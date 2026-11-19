using Microsoft.EntityFrameworkCore;
using StockMonitor.Core.Model;
using StockMonitor.Data;
using StockMonitor.Web.Models;

namespace StockMonitor.Web.Services;

/// <summary>Read models for the dashboard and portfolio pages.</summary>
public sealed class PortfolioQueryService
{
    private readonly StockMonitorDbContext _db;
    public PortfolioQueryService(StockMonitorDbContext db) => _db = db;

    public static DateTime UkTaxYearStart(DateTime d)
    {
        var start = new DateTime(d.Year, 4, 6);
        return d >= start ? start : start.AddYears(-1);
    }

    public async Task<PortfolioSummary> GetSummaryAsync(int? accountKey, CancellationToken ct = default)
    {
        var holdings = await _db.Holdings.AsNoTracking()
            .Where(h => accountKey == null || h.AccountKey == accountKey).ToListAsync(ct);

        var cash = await _db.CashBalances.AsNoTracking()
            .Where(c => accountKey == null || c.AccountKey == accountKey).SumAsync(c => c.CashGbp, ct);

        var tx = _db.Transactions.AsNoTracking().Where(t => accountKey == null || t.AccountKey == accountKey);
        var lastTx = await tx.MaxAsync(t => (DateTime?)t.TxTimestamp, ct);
        var asOf = lastTx ?? DateTime.UtcNow;     // "this tax year" is relative to the data's clock, not the wall clock
        var taxYearStart = UkTaxYearStart(asOf);
        var yearAgo = asOf.AddYears(-1);

        var flows = await tx
            .GroupBy(t => t.TransactionType!.ActionCode)
            .Select(g => new { Code = g.Key, Total = g.Sum(x => x.TotalGbp) })
            .ToListAsync(ct);
        decimal Flow(string code) => flows.FirstOrDefault(f => f.Code == code)?.Total ?? 0m;

        var dividends12m = await tx.Where(t => t.TransactionType!.ActionCode == ActionCodes.Dividend && t.TxTimestamp > yearAgo)
            .SumAsync(t => t.TotalGbp, ct);

        var gains = _db.RealizedGains.AsNoTracking().Where(g => accountKey == null || g.AccountKey == accountKey);
        var realized = await gains.SumAsync(g => g.GainGbp, ct);
        var realizedTy = await gains.Where(g => g.DisposedAt >= taxYearStart).SumAsync(g => g.GainGbp, ct);

        var mv = holdings.Sum(h => h.MarketValueGbp ?? 0);
        var cost = holdings.Sum(h => h.CostBasisGbp);
        var day = holdings.Sum(h => h.DayChangeGbp ?? 0);

        return new PortfolioSummary(
            MarketValueGbp: R(mv),
            CostBasisGbp: R(cost),
            UnrealizedGainGbp: R(mv - cost),
            UnrealizedPct: cost == 0 ? 0 : R((mv - cost) / cost * 100),
            DayChangeGbp: R(day),
            DayChangePct: mv - day == 0 ? 0 : R(day / (mv - day) * 100),
            CashGbp: R(cash),
            TotalValueGbp: R(mv + cash),
            RealizedGainGbp: R(realized),
            RealizedThisTaxYearGbp: R(realizedTy),
            DividendsGbp: R(Flow(ActionCodes.Dividend)),
            DividendsLast12mGbp: R(dividends12m),
            NetContributionsGbp: R(Flow(ActionCodes.Deposit) - Flow(ActionCodes.Withdrawal)),
            OpenPositions: holdings.Select(h => h.SecurityKey).Distinct().Count(),
            LastTransactionAt: lastTx,
            AsOf: DateTime.UtcNow);
    }

    /// <summary>Holdings; when no account is selected, lines for the same security are combined across accounts.</summary>
    public async Task<List<HoldingRow>> GetHoldingsAsync(int? accountKey, CancellationToken ct = default)
    {
        var rows = await _db.Holdings.AsNoTracking()
            .Where(h => accountKey == null || h.AccountKey == accountKey).ToListAsync(ct);

        var total = rows.Sum(r => r.MarketValueGbp ?? 0);
        return rows.GroupBy(r => r.SecurityKey).Select(g =>
        {
            var f = g.First();
            var qty = g.Sum(x => x.Quantity);
            var cost = g.Sum(x => x.CostBasisGbp);
            var mv = g.Sum(x => x.MarketValueGbp ?? 0);
            var day = g.Sum(x => x.DayChangeGbp ?? 0);
            var price = f.PriceGbp ?? 0;
            return new HoldingRow(
                f.SecurityKey, f.Ticker, f.SecurityName,
                string.Join(" + ", g.Select(x => x.AccountType).Distinct().OrderBy(x => x)),
                R(qty, 6), qty == 0 ? 0 : R(cost / qty, 4), R(price, 4), R(mv), R(cost), R(mv - cost),
                cost == 0 ? 0 : R((mv - cost) / cost * 100),
                R(day), mv - day == 0 ? 0 : R(day / (mv - day) * 100),
                total == 0 ? 0 : R(mv / total * 100),
                R(g.Sum(x => x.DividendsGbp)), R(g.Sum(x => x.RealizedGainGbp)),
                g.Min(x => x.FirstBuyAt));
        }).OrderByDescending(h => h.MarketValueGbp).ToList();
    }

    public async Task<object> GetClosedPositionsAsync(int? accountKey, CancellationToken ct = default)
    {
        var q = from p in _db.Positions.AsNoTracking()
                join s in _db.Securities on p.SecurityKey equals s.SecurityKey
                join a in _db.Accounts on p.AccountKey equals a.AccountKey
                where p.Quantity == 0 && (accountKey == null || p.AccountKey == accountKey)
                orderby p.LastActivityAt descending
                select new
                {
                    s.SecurityKey, s.Ticker, s.SecurityName, a.AccountType,
                    p.RealizedGainGbp, p.DividendsGbp,
                    TotalReturnGbp = p.RealizedGainGbp + p.DividendsGbp,
                    p.FirstBuyAt, ClosedAt = p.LastActivityAt
                };
        return await q.ToListAsync(ct);
    }

    public async Task<object> GetSecurityDetailAsync(int securityKey, CancellationToken ct = default)
    {
        var sec = await _db.Securities.AsNoTracking().FirstOrDefaultAsync(s => s.SecurityKey == securityKey, ct);
        var txs = await _db.TransactionDetails.AsNoTracking()
            .Where(t => t.SecurityKey == securityKey)
            .OrderByDescending(t => t.TxTimestamp)
            .Select(t => new { t.TxTimestamp, t.AccountType, t.ActionName, t.ActionCode, t.Shares, t.PriceLocal, t.PriceCurrency, t.PriceGbp, t.TotalGbp, t.BrokerResultGbp, t.Notes })
            .ToListAsync(ct);
        var ticker = sec?.Ticker ?? "";
        var cas = await _db.CorporateActions.AsNoTracking()
            .Where(c => c.Ticker == ticker || c.NewTicker == ticker).ToListAsync(ct);
        var ticks = await _db.PriceTicks.AsNoTracking()
            .Where(p => p.SecurityKey == securityKey && p.TickTime > DateTime.UtcNow.AddHours(-2))
            .OrderBy(p => p.TickTime).Select(p => new { t = p.TickTime, p = p.PriceGbp }).ToListAsync(ct);
        return new { security = sec, transactions = txs, corporateActions = cas, ticks };
    }

    public async Task<List<FeedRow>> GetRecentTransactionsAsync(int take, CancellationToken ct = default) =>
        await _db.TransactionDetails.AsNoTracking()
            .OrderByDescending(t => t.InsertedAt).ThenByDescending(t => t.TxTimestamp)
            .Take(take)
            .Select(t => new FeedRow(t.TransactionKey, t.TxTimestamp, t.InsertedAt, t.AccountType, t.ActionCode, t.ActionName,
                t.Ticker, t.SecurityName, t.Shares, t.PriceGbp, t.TotalGbp, t.CashImpactGbp, t.Source))
            .ToListAsync(ct);

    public async Task<FeedRow?> GetFeedRowAsync(string key, CancellationToken ct = default) =>
        await _db.TransactionDetails.AsNoTracking().Where(t => t.TransactionKey == key)
            .Select(t => new FeedRow(t.TransactionKey, t.TxTimestamp, t.InsertedAt, t.AccountType, t.ActionCode, t.ActionName,
                t.Ticker, t.SecurityName, t.Shares, t.PriceGbp, t.TotalGbp, t.CashImpactGbp, t.Source))
            .FirstOrDefaultAsync(ct);

    public async Task<List<AlertRow>> GetAlertsAsync(int take, CancellationToken ct = default) =>
        await _db.Alerts.AsNoTracking().OrderByDescending(a => a.CreatedAt).Take(take)
            .Select(a => new AlertRow(a.AlertId, a.CreatedAt, a.Severity, a.AlertType, a.Message, a.IsAcknowledged))
            .ToListAsync(ct);

    public async Task<List<PriceRow>> GetPricesAsync(CancellationToken ct = default)
    {
        var held = _db.Positions.Where(p => p.Quantity > 0).Select(p => p.SecurityKey).Distinct();
        return await (from p in _db.Prices.AsNoTracking()
                      join s in _db.Securities on p.SecurityKey equals s.SecurityKey
                      where held.Contains(p.SecurityKey)
                      orderby s.Ticker
                      select new PriceRow(p.SecurityKey, s.Ticker, p.PriceGbp,
                          p.PrevCloseGbp == 0 ? 0 : (p.PriceGbp - p.PrevCloseGbp) / p.PrevCloseGbp * 100, p.UpdatedAt))
                     .ToListAsync(ct);
    }

    public async Task<object> GetSnapshotsAsync(int? accountKey, int hours, CancellationToken ct = default)
    {
        var since = DateTime.UtcNow.AddHours(-hours);
        return await _db.Snapshots.AsNoTracking()
            .Where(s => s.SnapshotTime >= since && s.AccountKey == accountKey)
            .OrderBy(s => s.SnapshotTime)
            .Select(s => new { t = s.SnapshotTime, value = s.MarketValueGbp + s.CashGbp, mv = s.MarketValueGbp, cost = s.CostBasisGbp })
            .ToListAsync(ct);
    }

    public async Task<object> GetAccountsAsync(CancellationToken ct = default) =>
        await _db.Accounts.AsNoTracking().OrderBy(a => a.AccountNumber)
            .Select(a => new { a.AccountKey, a.AccountNumber, a.AccountType, a.AccountName, a.IsTaxSheltered })
            .ToListAsync(ct);

    public async Task<object> GetCorporateActionsAsync(CancellationToken ct = default) =>
        await _db.CorporateActions.AsNoTracking().OrderBy(c => c.EffectiveDate).ToListAsync(ct);

    private static decimal R(decimal v, int dp = 2) => Math.Round(v, dp);
}
