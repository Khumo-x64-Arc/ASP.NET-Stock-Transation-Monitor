using Microsoft.EntityFrameworkCore;
using StockMonitor.Core.Model;
using StockMonitor.Data;

namespace StockMonitor.Web.Services;

/// <summary>Aggregations for the analytics page. Heavy lifting is in the dw views; this class filters and shapes.</summary>
public sealed class AnalyticsQueryService
{
    private readonly StockMonitorDbContext _db;
    public AnalyticsQueryService(StockMonitorDbContext db) => _db = db;

    /// <summary>Monthly flows (deposits, withdrawals, buys, sells, dividends, fees) plus cumulative contributions.</summary>
    public async Task<object> GetCashFlowAsync(int? accountKey, string? from, string? to, CancellationToken ct = default)
    {
        var rows = await _db.MonthlyCashFlows.AsNoTracking()
            .Where(m => (accountKey == null || m.AccountKey == accountKey)
                        && (from == null || string.Compare(m.YearMonth, from) >= 0)
                        && (to == null || string.Compare(m.YearMonth, to) <= 0))
            .ToListAsync(ct);

        var months = rows.GroupBy(r => r.YearMonth).OrderBy(g => g.Key).Select(g => new
        {
            month = g.Key,
            deposits = g.Sum(x => x.DepositsGbp),
            withdrawals = g.Sum(x => x.WithdrawalsGbp),
            buys = g.Sum(x => x.BuysGbp),
            sells = g.Sum(x => x.SellsGbp),
            dividends = g.Sum(x => x.DividendsGbp),
            dividendsIsa = g.Where(x => x.AccountType == "ISA").Sum(x => x.DividendsGbp),
            dividendsInv = g.Where(x => x.AccountType != "ISA").Sum(x => x.DividendsGbp),
            interest = g.Sum(x => x.InterestGbp),
            fees = g.Sum(x => x.FeesGbp),
            trades = g.Sum(x => x.TradeCount)
        }).ToList();

        decimal contrib = 0, invested = 0;
        var cumulative = months.Select(m =>
        {
            contrib += m.deposits - m.withdrawals;
            invested += m.buys - m.sells;
            return new { m.month, netContributions = contrib, netInvested = invested };
        }).ToList();

        return new { months, cumulative };
    }

    public async Task<object> GetTopDividendPayersAsync(int? accountKey, int take, CancellationToken ct = default)
    {
        var rows = await _db.DividendsBySecurity.AsNoTracking()
            .Where(d => accountKey == null || d.AccountKey == accountKey).ToListAsync(ct);
        return rows.GroupBy(r => new { r.Ticker, r.SecurityName })
            .Select(g => new { g.Key.Ticker, g.Key.SecurityName, payments = g.Sum(x => x.Payments), dividends = g.Sum(x => x.DividendsGbp) })
            .OrderByDescending(x => x.dividends).Take(take).ToList();
    }

    public async Task<object> GetRealizedByTaxYearAsync(CancellationToken ct = default) =>
        await _db.RealizedByTaxYear.AsNoTracking().OrderBy(r => r.UkTaxYear).ThenBy(r => r.AccountType).ToListAsync(ct);

    public async Task<object> GetIsaAllowanceAsync(CancellationToken ct = default) =>
        await _db.IsaAllowance.AsNoTracking().OrderBy(r => r.UkTaxYear).ToListAsync(ct);

    public async Task<object> GetActionMixAsync(int? accountKey, CancellationToken ct = default) =>
        await _db.TransactionDetails.AsNoTracking()
            .Where(t => accountKey == null || t.AccountKey == accountKey)
            .GroupBy(t => t.ActionName)
            .Select(g => new { action = g.Key, count = g.Count(), total = g.Sum(x => x.TotalGbp) })
            .OrderByDescending(x => x.count)
            .ToListAsync(ct);

    /// <summary>Costs of investing: FX conversion fees, stamp duty, other fees (GBP) and withholding tax by currency.</summary>
    public async Task<object> GetFeesAndTaxesAsync(int? accountKey, CancellationToken ct = default)
    {
        var tx = _db.Transactions.AsNoTracking().Where(t => accountKey == null || t.AccountKey == accountKey);
        var fees = await tx.GroupBy(_ => 1).Select(g => new
        {
            fxFees = g.Sum(x => x.FxFeeGbp ?? 0),
            stampDuty = g.Sum(x => x.StampDutyGbp ?? 0),
            transactionFees = g.Sum(x => (x.TransactionFeeGbp ?? 0) + (x.FinraFeeGbp ?? 0)),
            depositFees = g.Sum(x => x.DepositFeeGbp ?? 0)
        }).FirstOrDefaultAsync(ct);

        var wht = await tx.Where(t => t.WithholdingTax != null && t.WithholdingTax > 0)
            .GroupBy(t => new { t.WithholdingTaxCurrency, Year = t.TxTimestamp.Year })
            .Select(g => new { currency = g.Key.WithholdingTaxCurrency, year = g.Key.Year, amount = g.Sum(x => x.WithholdingTax ?? 0) })
            .OrderBy(x => x.year).ToListAsync(ct);

        return new { fees, withholding = wht };
    }

    /// <summary>Concentration by holding and by account (from the live valuation view).</summary>
    public async Task<object> GetAllocationAsync(CancellationToken ct = default)
    {
        var h = await _db.Holdings.AsNoTracking().ToListAsync(ct);
        var byAccount = h.GroupBy(x => x.AccountType).Select(g => new { account = g.Key, value = g.Sum(x => x.MarketValueGbp ?? 0) });
        return new { byAccount };
    }

    /// <summary>ETL health: recent batches and data-quality findings by rule.</summary>
    public async Task<object> GetPipelineAsync(CancellationToken ct = default)
    {
        var batches = await _db.BatchSummaries.AsNoTracking().OrderByDescending(b => b.BatchId).Take(10).ToListAsync(ct);
        var rules = await _db.DataQualityIssues.AsNoTracking()
            .GroupBy(i => new { i.Severity, i.RuleCode })
            .Select(g => new { g.Key.Severity, g.Key.RuleCode, count = g.Count() })
            .OrderBy(x => x.RuleCode).ToListAsync(ct);
        var recentIssues = await _db.DataQualityIssues.AsNoTracking()
            .Where(i => i.Severity != "INFO")
            .OrderByDescending(i => i.IssueId).Take(25)
            .Select(i => new { i.Severity, i.RuleCode, i.SourceSheet, i.SourceRow, i.TransactionKey, i.Detail })
            .ToListAsync(ct);
        var rejected = await _db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM etl.RejectedRow").FirstAsync(ct);
        var pendingReplay = await _db.ReplayQueue.CountAsync(r => r.ReleasedAt == null, ct);
        var live = await _db.Transactions.CountAsync(t => t.Source == "LIVE", ct);
        return new { batches, rules, recentIssues, rejected, pendingReplay, liveTransactions = live };
    }
}
