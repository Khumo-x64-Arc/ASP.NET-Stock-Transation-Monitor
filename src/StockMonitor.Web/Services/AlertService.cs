using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StockMonitor.Core.Model;
using StockMonitor.Data;
using StockMonitor.Data.Entities;
using StockMonitor.Web.Models;

namespace StockMonitor.Web.Services;

/// <summary>Rule engine evaluated on every ingested transaction (and by the price feed for price moves).</summary>
public sealed class AlertService
{
    private readonly StockMonitorDbContext _db;
    private readonly MonitorOptions.AlertOptions _o;

    public AlertService(StockMonitorDbContext db, IOptions<MonitorOptions> options)
    {
        _db = db;
        _o = options.Value.Alerts;
    }

    public async Task<List<AlertRow>> EvaluateTransactionAsync(CleanTransaction t, CancellationToken ct = default)
    {
        var account = await _db.Accounts.AsNoTracking().FirstAsync(a => a.AccountNumber == t.AccountNumber, ct);
        int? secKey = t.Ticker is null ? null
            : await _db.Securities.Where(s => s.Ticker == t.Ticker).Select(s => (int?)s.SecurityKey).FirstOrDefaultAsync(ct);
        var alerts = new List<Alert>();

        Alert New(string sev, string type, string msg) => new()
        {
            CreatedAt = DateTime.UtcNow, Severity = sev, AlertType = type, AccountKey = account.AccountKey,
            SecurityKey = secKey, TransactionKey = t.TransactionKey, Message = msg
        };

        // 1. Large trade / cash movement
        if (ActionCodes.IsTrade(t.ActionCode) && t.TotalGbp >= _o.LargeTradeGbp)
            alerts.Add(New("WARN", "LARGE_TRADE",
                $"{account.AccountType}: {t.ActionCode} {t.Shares:0.####} {t.Ticker} for £{t.TotalGbp:N2} (threshold £{_o.LargeTradeGbp:N0})"));
        if ((t.ActionCode is ActionCodes.Deposit or ActionCodes.Withdrawal) && t.TotalGbp >= _o.LargeTradeGbp)
            alerts.Add(New("INFO", "LARGE_CASH_MOVEMENT", $"{account.AccountType}: {t.ActionCode} of £{t.TotalGbp:N2}"));

        // 2. Concentration after a buy
        if (t.ActionCode == ActionCodes.Buy && secKey is not null)
        {
            var holdings = await _db.Holdings.AsNoTracking().ToListAsync(ct);
            var total = holdings.Sum(h => h.MarketValueGbp ?? 0);
            var line = holdings.Where(h => h.SecurityKey == secKey).Sum(h => h.MarketValueGbp ?? 0);
            if (total > 0 && line / total * 100 >= _o.ConcentrationPct)
                alerts.Add(New("WARN", "CONCENTRATION",
                    $"{t.Ticker} is now {line / total * 100:N1}% of the portfolio (limit {_o.ConcentrationPct}%)"));
        }

        // 3. ISA subscription allowance (UK tax year 6 Apr - 5 Apr)
        if (t.ActionCode == ActionCodes.Deposit && account.AccountType == "ISA")
        {
            var start = PortfolioQueryService.UkTaxYearStart(t.TxTimestamp);
            var end = start.AddYears(1);
            var subscribed = await _db.Transactions
                .Where(x => x.AccountKey == account.AccountKey && x.TransactionType!.ActionCode == ActionCodes.Deposit
                            && x.TxTimestamp >= start && x.TxTimestamp < end)
                .SumAsync(x => x.TotalGbp, ct);
            var pct = subscribed / _o.IsaAllowanceGbp * 100;
            var before = (subscribed - t.TotalGbp) / _o.IsaAllowanceGbp * 100;   // only alert when a threshold is crossed
            if (pct >= 100 && before < 100)
                alerts.Add(New("CRITICAL", "ISA_ALLOWANCE",
                    $"ISA deposits for {start.Year}/{(start.Year + 1) % 100:00} are £{subscribed:N0} - over the £{_o.IsaAllowanceGbp:N0} allowance (check for transfers-in)"));
            else if (pct >= 80 && before < 80)
                alerts.Add(New("INFO", "ISA_ALLOWANCE", $"ISA allowance {pct:N0}% used for {start.Year}/{(start.Year + 1) % 100:00}"));
        }

        // 4. Engine reconciliation finding for this transaction (sell without enough recorded shares)
        if (t.ActionCode == ActionCodes.Sell &&
            await _db.DataQualityIssues.AnyAsync(i => i.TransactionKey == t.TransactionKey && i.RuleCode == "ENGINE_OVERSELL_CLAMPED", ct))
            alerts.Add(New("WARN", "OVERSELL", $"{t.Ticker}: sale exceeds recorded holding in {account.AccountType} - missing acquisition history"));

        if (alerts.Count == 0) return new();
        _db.Alerts.AddRange(alerts);
        await _db.SaveChangesAsync(ct);
        return alerts.Select(ToRow).ToList();
    }

    public async Task<AlertRow> RaiseAsync(string severity, string type, int? securityKey, string message, CancellationToken ct = default)
    {
        var a = new Alert { CreatedAt = DateTime.UtcNow, Severity = severity, AlertType = type, SecurityKey = securityKey, Message = message };
        _db.Alerts.Add(a);
        await _db.SaveChangesAsync(ct);
        return ToRow(a);
    }

    public async Task<bool> AcknowledgeAsync(long id, CancellationToken ct = default) =>
        await _db.Alerts.Where(a => a.AlertId == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.IsAcknowledged, true), ct) > 0;

    private static AlertRow ToRow(Alert a) => new(a.AlertId, a.CreatedAt, a.Severity, a.AlertType, a.Message, a.IsAcknowledged);
}
