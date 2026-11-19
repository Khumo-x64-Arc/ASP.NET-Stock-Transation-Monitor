using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StockMonitor.Core.Model;
using StockMonitor.Core.Portfolio;
using StockMonitor.Data.Entities;

namespace StockMonitor.Data.Services;

public sealed record RebuildSummary(int OpenPositions, int ClosedPositions, int Disposals, int Findings, TimeSpan Elapsed);

/// <summary>
/// Derives dw.Position and dw.FactRealizedGain from dw.FactTransaction + dw.CorporateAction using <see cref="PositionEngine"/>.
/// A full rebuild of ~3.3k rows takes well under a second, which keeps the logic simple and always consistent
/// (no incremental drift). Calls are serialised because the batch ETL and the live API can both trigger it.
/// </summary>
public sealed class PositionRebuildService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly StockMonitorDbContext _db;
    private readonly ILogger<PositionRebuildService> _log;

    public PositionRebuildService(StockMonitorDbContext db, ILogger<PositionRebuildService> log)
    {
        _db = db;
        _log = log;
    }

    public async Task<RebuildSummary> RebuildAsync(int? batchId = null, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var txs = await _db.Transactions.AsNoTracking()
                .Where(t => t.SecurityKey != null &&
                            (t.TransactionType!.ActionCode == ActionCodes.Buy ||
                             t.TransactionType.ActionCode == ActionCodes.Sell ||
                             t.TransactionType.ActionCode == ActionCodes.Dividend))
                .Select(t => new EngineTransaction(
                    t.TransactionId, t.TransactionKey, t.AccountKey, t.Security!.Ticker, t.TxTimestamp,
                    t.TransactionType!.ActionCode, t.Shares ?? 0m, t.TotalGbp, t.BrokerResultGbp, t.IsCorporateActionArtifact))
                .ToListAsync(ct);

            var cas = await _db.CorporateActions.AsNoTracking().ToListAsync(ct);
            var caInfos = cas.Select(c => new CorporateActionInfo(
                c.EffectiveDate.ToDateTime(TimeOnly.MinValue), c.ActionType, c.Ticker, c.Ratio, c.NewTicker, c.CostBasisAllocation))
                .ToList();

            // Corporate actions can create lines in securities that never traded in the file (e.g. a spin-off kept to date)
            await EnsureSecuritiesAsync(cas, ct);

            var result = new PositionEngine().Run(txs, caInfos);

            var keyByTicker = await _db.Securities.AsNoTracking().ToDictionaryAsync(s => s.Ticker, s => s.SecurityKey, ct);
            var now = DateTime.UtcNow;

            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            await _db.RealizedGains.ExecuteDeleteAsync(ct);
            await _db.Positions.ExecuteDeleteAsync(ct);
            await _db.DataQualityIssues.Where(i => i.RuleCode.StartsWith("ENGINE_")).ExecuteDeleteAsync(ct);

            _db.Positions.AddRange(result.Positions.Values
                .Where(p => keyByTicker.ContainsKey(p.Ticker))
                .Select(p => new Position
                {
                    AccountKey = p.AccountKey,
                    SecurityKey = keyByTicker[p.Ticker],
                    Quantity = Math.Round(p.Quantity, 10),
                    CostBasisGbp = Math.Round(p.CostBasisGbp, 4),
                    AvgCostGbp = p.AvgCostGbp is null ? null : Math.Round(p.AvgCostGbp.Value, 6),
                    RealizedGainGbp = Math.Round(p.RealizedGainGbp, 4),
                    DividendsGbp = Math.Round(p.DividendsGbp, 4),
                    FirstBuyAt = p.FirstBuyAt,
                    LastActivityAt = p.LastActivityAt,
                    UpdatedAt = now
                }));

            _db.RealizedGains.AddRange(result.Realized
                .Where(g => keyByTicker.ContainsKey(g.Ticker))
                .Select(g => new FactRealizedGain
                {
                    TransactionId = g.TransactionId,
                    AccountKey = g.AccountKey,
                    SecurityKey = keyByTicker[g.Ticker],
                    DateKey = g.DisposedAt.Year * 10000 + g.DisposedAt.Month * 100 + g.DisposedAt.Day,
                    DisposedAt = g.DisposedAt,
                    SharesDisposed = Math.Round(g.SharesDisposed, 10),
                    ProceedsGbp = g.ProceedsGbp,
                    CostBasisGbp = g.CostBasisGbp,
                    GainGbp = g.GainGbp,
                    BrokerResultGbp = g.BrokerResultGbp,
                    Method = "S104_AVERAGE"
                }));

            _db.DataQualityIssues.AddRange(result.Findings.Select(f => new DataQualityIssue
            {
                BatchId = batchId,
                Severity = f.Severity.ToString().ToUpperInvariant(),
                RuleCode = f.RuleCode,
                TransactionKey = f.TransactionKey,
                Detail = f.Detail.Length > 1000 ? f.Detail[..1000] : f.Detail,
                CreatedAt = now
            }));

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            _db.ChangeTracker.Clear();

            var summary = new RebuildSummary(
                result.Positions.Values.Count(p => p.Quantity > 0),
                result.Positions.Values.Count(p => p.Quantity == 0),
                result.Realized.Count, result.Findings.Count, sw.Elapsed);
            _log.LogInformation("Positions rebuilt: {Open} open, {Closed} closed, {Disposals} disposals, {Findings} findings in {Ms} ms",
                summary.OpenPositions, summary.ClosedPositions, summary.Disposals, summary.Findings, sw.ElapsedMilliseconds);
            return summary;
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task EnsureSecuritiesAsync(IEnumerable<CorporateAction> cas, CancellationToken ct)
    {
        var wanted = cas.Where(c => c.NewTicker != null)
            .GroupBy(c => c.NewTicker!)
            .Select(g => g.First())
            .ToList();
        if (wanted.Count == 0) return;

        var names = wanted.Select(w => w.NewTicker!).ToList();
        var existing = await _db.Securities.Where(s => names.Contains(s.Ticker)).Select(s => s.Ticker).ToListAsync(ct);
        foreach (var w in wanted.Where(w => !existing.Contains(w.NewTicker!)))
        {
            _db.Securities.Add(new DimSecurity
            {
                Ticker = w.NewTicker!,
                SecurityName = w.NewSecurityName ?? w.NewTicker!,
                FirstSeenDate = w.EffectiveDate,
                UpdatedAt = DateTime.UtcNow
            });
        }
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
    }
}
