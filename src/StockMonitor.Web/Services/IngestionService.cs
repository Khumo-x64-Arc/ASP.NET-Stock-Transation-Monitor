using System.Globalization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StockMonitor.Core.Cleansing;
using StockMonitor.Core.Model;
using StockMonitor.Data;
using StockMonitor.Data.Entities;
using StockMonitor.Data.Services;
using StockMonitor.Web.Hubs;
using StockMonitor.Web.Models;

namespace StockMonitor.Web.Services;

/// <summary>
/// Real-time path. Each event goes through the SAME cleanser and the SAME load procedure as the batch ETL:
///   validate/cleanse -> stg.CleanTransaction (LIVE batch) -> etl.usp_LoadWarehouse(@TransactionKey)
///   -> rebuild positions -> evaluate alerts -> push to dashboards over SignalR.
/// Idempotent on TransactionKey: resending the same broker ID returns Duplicate without side effects.
/// </summary>
public sealed class IngestionService
{
    private static int _liveBatchId;
    private static DateOnly _liveBatchDay;
    private static readonly SemaphoreSlim BatchGate = new(1, 1);

    private readonly StockMonitorDbContext _db;
    private readonly WarehouseLoader _loader;
    private readonly PositionRebuildService _positions;
    private readonly AlertService _alerts;
    private readonly PortfolioQueryService _queries;
    private readonly IHubContext<MarketHub> _hub;
    private readonly ILogger<IngestionService> _log;

    public IngestionService(StockMonitorDbContext db, WarehouseLoader loader, PositionRebuildService positions, AlertService alerts,
        PortfolioQueryService queries, IHubContext<MarketHub> hub, ILogger<IngestionService> log)
    {
        _db = db; _loader = loader; _positions = positions; _alerts = alerts; _queries = queries; _hub = hub; _log = log;
    }

    /// <summary>Entry point for the REST API: raw request -> cleanser -> ingest.</summary>
    public async Task<IngestResult> IngestRequestAsync(LiveTransactionRequest r, CancellationToken ct = default)
    {
        var inv = CultureInfo.InvariantCulture;
        string? S(decimal? d) => d?.ToString(inv);

        var raw = new RawTransactionRow
        {
            SourceSheet = "API",
            AccountNumber = r.AccountNumber.ToString(inv),
            Action = r.Action,
            TxTime = (r.Timestamp ?? DateTime.UtcNow).ToString("yyyy-MM-dd HH:mm:ss.fff", inv),
            Isin = r.Isin, Ticker = r.Ticker, SecurityName = r.Name,
            Shares = S(r.Shares), PricePerShare = S(r.PricePerShare), PriceCurrency = r.PriceCurrency,
            ExchangeRate = S(r.ExchangeRate), Total = S(r.Total), Result = S(r.Result),
            WithholdingTax = S(r.WithholdingTax), WithholdingTaxCurrency = r.WithholdingTaxCurrency,
            CurrencyConversionFee = S(r.CurrencyConversionFee), Notes = r.Notes, BrokerId = r.BrokerId
        };

        var findings = new List<DataQualityFinding>();
        var (clean, reason) = new TransactionCleanser().CleanseOne(raw, findings);
        if (clean is null) return new IngestResult(false, false, null, reason, findings.Select(f => f.Detail).ToList());

        if (!await _db.Accounts.AnyAsync(a => a.AccountNumber == clean.AccountNumber, ct))
            return new IngestResult(false, false, null, $"Unknown account {clean.AccountNumber}", Array.Empty<string>());

        return await IngestCleanAsync(clean, findings, ct);
    }

    /// <summary>Entry point for already-cleansed events (replay queue).</summary>
    public async Task<IngestResult> IngestCleanAsync(CleanTransaction clean, IReadOnlyCollection<DataQualityFinding>? findings = null,
        CancellationToken ct = default)
    {
        if (await _db.Transactions.AnyAsync(t => t.TransactionKey == clean.TransactionKey, ct))
            return new IngestResult(true, true, clean.TransactionKey, null, Array.Empty<string>());

        var batchId = await GetLiveBatchAsync(ct);

        _db.CleanTransactions.Add(ToRow(batchId, clean));
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();

        if (findings is { Count: > 0 })
            await _loader.BulkInsertFindingsAsync(batchId, findings.Select(f => f with { TransactionKey = f.TransactionKey ?? clean.TransactionKey }), ct);

        var loaded = await _loader.LoadWarehouseAsync(batchId, clean.TransactionKey, "LIVE", ct);
        if (loaded == 0)   // lost a race with a concurrent identical request
            return new IngestResult(true, true, clean.TransactionKey, null, Array.Empty<string>());

        await _db.BatchLogs.Where(b => b.BatchId == batchId)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.RowsLoaded, b => b.RowsLoaded + 1)
                                      .SetProperty(b => b.RowsCleansed, b => b.RowsCleansed + 1), ct);

        if (ActionCodes.RequiresSecurity(clean.ActionCode))
            await _positions.RebuildAsync(batchId, ct);

        var fired = await _alerts.EvaluateTransactionAsync(clean, ct);

        // ---- push ----
        var feed = await _queries.GetFeedRowAsync(clean.TransactionKey, ct);
        if (feed is not null) await _hub.Clients.All.SendAsync("transaction", feed, ct);
        await _hub.Clients.All.SendAsync("summary", await _queries.GetSummaryAsync(null, ct), ct);
        foreach (var a in fired) await _hub.Clients.All.SendAsync("alert", a, ct);

        _log.LogInformation("Ingested {Key} {Action} {Ticker} £{Total}", clean.TransactionKey, clean.ActionCode, clean.Ticker, clean.TotalGbp);
        return new IngestResult(true, false, clean.TransactionKey, null,
            findings?.Select(f => $"{f.RuleCode}: {f.Detail}").ToList() ?? new List<string>());
    }

    /// <summary>One LIVE batch per UTC day keeps lineage readable in etl.BatchLog.</summary>
    private async Task<int> GetLiveBatchAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (_liveBatchId != 0 && _liveBatchDay == today) return _liveBatchId;

        await BatchGate.WaitAsync(ct);
        try
        {
            if (_liveBatchId != 0 && _liveBatchDay == today) return _liveBatchId;
            var start = today.ToDateTime(TimeOnly.MinValue);
            var existing = await _db.BatchLogs.Where(b => b.BatchType == "LIVE" && b.StartedAt >= start)
                .OrderByDescending(b => b.BatchId).Select(b => (int?)b.BatchId).FirstOrDefaultAsync(ct);
            _liveBatchId = existing ?? await _loader.StartBatchAsync("LIVE", "api/replay", null, ct);
            _liveBatchDay = today;
            return _liveBatchId;
        }
        finally { BatchGate.Release(); }
    }

    private static CleanTransactionRow ToRow(int batchId, CleanTransaction c) => new()
    {
        BatchId = batchId, TransactionKey = c.TransactionKey, IsSyntheticKey = c.IsSyntheticKey, AccountNumber = c.AccountNumber,
        TxTimestamp = c.TxTimestamp, ActionCode = c.ActionCode, ActionSubType = c.ActionSubType, Ticker = c.Ticker, Isin = c.Isin,
        SecurityName = c.SecurityName, Shares = c.Shares, PriceLocal = c.PriceLocal, PriceCurrency = c.PriceCurrency, FxRate = c.FxRate,
        PriceGbp = c.PriceGbp, TotalGbp = c.TotalGbp, BrokerResultGbp = c.BrokerResultGbp, WithholdingTax = c.WithholdingTax,
        WithholdingTaxCurrency = c.WithholdingTaxCurrency, FxFeeGbp = c.FxFeeGbp, StampDutyGbp = c.StampDutyGbp,
        TransactionFeeGbp = c.TransactionFeeGbp, FinraFeeGbp = c.FinraFeeGbp, DepositFeeGbp = c.DepositFeeGbp, Notes = c.Notes,
        IsCorporateActionArtifact = c.IsCorporateActionArtifact, SourceSheet = c.SourceSheet, SourceRow = c.SourceRow
    };
}
