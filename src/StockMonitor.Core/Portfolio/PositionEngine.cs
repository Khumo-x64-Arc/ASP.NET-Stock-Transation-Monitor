using StockMonitor.Core.Model;

namespace StockMonitor.Core.Portfolio;

public sealed record EngineTransaction(
    long? TransactionId,
    string TransactionKey,
    int AccountKey,
    string Ticker,
    DateTime Timestamp,
    string ActionCode,
    decimal Shares,
    decimal TotalGbp,
    decimal? BrokerResultGbp,
    bool IsCorporateActionArtifact);

public sealed record CorporateActionInfo(
    DateTime EffectiveDate,
    string ActionType,          // SPLIT | SPINOFF | MERGER | CASH_ACQUISITION
    string Ticker,
    decimal Ratio,
    string? NewTicker,
    decimal CostBasisAllocation);

public sealed class PositionState
{
    public int AccountKey { get; init; }
    public string Ticker { get; init; } = "";
    public decimal Quantity { get; set; }
    public decimal CostBasisGbp { get; set; }
    public decimal RealizedGainGbp { get; set; }
    public decimal DividendsGbp { get; set; }
    public DateTime? FirstBuyAt { get; set; }
    public DateTime? LastActivityAt { get; set; }
    public decimal? AvgCostGbp => Quantity > 0 ? CostBasisGbp / Quantity : null;
}

public sealed record RealizedGain(
    long? TransactionId,
    int AccountKey,
    string Ticker,
    DateTime DisposedAt,
    decimal SharesDisposed,
    decimal ProceedsGbp,
    decimal CostBasisGbp,
    decimal GainGbp,
    decimal? BrokerResultGbp);

public sealed class EngineResult
{
    public Dictionary<(int AccountKey, string Ticker), PositionState> Positions { get; } = new();
    public List<RealizedGain> Realized { get; } = new();
    public List<DataQualityFinding> Findings { get; } = new();
}

/// <summary>
/// Rebuilds positions by replaying transactions and corporate actions in time order.
/// Cost basis uses the UK Section 104 pooled (average-cost) method, per account and security.
/// Buy totals already include FX fees / stamp duty, so fees are capitalised into cost as HMRC requires.
/// (Same-day and 30-day matching rules are not applied - see docs/ETL_PIPELINE.md.)
/// </summary>
public sealed class PositionEngine
{
    private const decimal Epsilon = 0.000001m;

    public EngineResult Run(IEnumerable<EngineTransaction> transactions, IEnumerable<CorporateActionInfo> corporateActions,
                            DateTime? asOf = null)
    {
        var result = new EngineResult();
        var cutoff = asOf ?? DateTime.MaxValue;

        // Corporate actions apply at the very start of their effective date, i.e. before that day's trades.
        var events = transactions.Where(t => t.Timestamp <= cutoff)
            .Select(t => (Time: t.Timestamp, Order: 1, Tx: t, Ca: (CorporateActionInfo?)null))
            .Concat(corporateActions.Where(c => c.EffectiveDate <= cutoff)
                .Select(c => (Time: c.EffectiveDate.Date, Order: 0, Tx: (EngineTransaction?)null, Ca: (CorporateActionInfo?)c)))
            .OrderBy(e => e.Time).ThenBy(e => e.Order).ThenBy(e => e.Tx?.TransactionId ?? 0);

        foreach (var e in events)
        {
            if (e.Ca is not null) ApplyCorporateAction(result, e.Ca);
            else ApplyTransaction(result, e.Tx!);
        }

        foreach (var p in result.Positions.Values)
        {
            if (Math.Abs(p.Quantity) < Epsilon) { p.Quantity = 0; p.CostBasisGbp = 0; }
            p.CostBasisGbp = Math.Round(p.CostBasisGbp, 4);
        }
        return result;
    }

    private static PositionState Get(EngineResult r, int account, string ticker)
    {
        if (!r.Positions.TryGetValue((account, ticker), out var p))
        {
            p = new PositionState { AccountKey = account, Ticker = ticker };
            r.Positions[(account, ticker)] = p;
        }
        return p;
    }

    private static void ApplyTransaction(EngineResult r, EngineTransaction t)
    {
        var p = Get(r, t.AccountKey, t.Ticker);
        p.LastActivityAt = t.Timestamp;

        switch (t.ActionCode)
        {
            case ActionCodes.Buy:
                p.Quantity += t.Shares;
                p.CostBasisGbp += t.TotalGbp;
                p.FirstBuyAt ??= t.Timestamp;
                break;

            case ActionCodes.Dividend:
                p.DividendsGbp += t.TotalGbp;
                break;

            case ActionCodes.Sell:
                if (t.IsCorporateActionArtifact) return;   // handled by the MERGER corporate action

                var qty = t.Shares;
                if (qty > p.Quantity + Epsilon)
                {
                    // Source data is missing earlier buys for this line. Clamp so the pool never goes negative,
                    // and record the gap for reconciliation instead of silently producing negative holdings.
                    r.Findings.Add(new DataQualityFinding(Severity.Warn, "ENGINE_OVERSELL_CLAMPED", t.TransactionKey, null, null,
                        $"Account {t.AccountKey} sold {qty:0.######} {t.Ticker} but held {p.Quantity:0.######}; " +
                        $"{qty - p.Quantity:0.######} shares had no recorded acquisition (cost treated as 0)."));
                }

                var held = Math.Max(p.Quantity, 0);
                var matched = Math.Min(qty, held);
                var cost = held > 0 ? p.CostBasisGbp * (matched / held) : 0m;

                p.Quantity = Math.Max(held - qty, 0);
                p.CostBasisGbp -= cost;
                if (p.Quantity < Epsilon) { p.Quantity = 0; p.CostBasisGbp = 0; }

                var gain = t.TotalGbp - cost;
                p.RealizedGainGbp += gain;
                r.Realized.Add(new RealizedGain(t.TransactionId, t.AccountKey, t.Ticker, t.Timestamp, qty,
                    t.TotalGbp, Math.Round(cost, 4), Math.Round(gain, 4), t.BrokerResultGbp));
                break;
        }
    }

    private static void ApplyCorporateAction(EngineResult r, CorporateActionInfo ca)
    {
        var lines = r.Positions.Values
            .Where(p => p.Ticker.Equals(ca.Ticker, StringComparison.OrdinalIgnoreCase) && p.Quantity > Epsilon)
            .ToList();

        foreach (var p in lines)
        {
            switch (ca.ActionType)
            {
                case "SPLIT":                                  // quantity scales, cost unchanged
                    p.Quantity *= ca.Ratio;
                    break;

                case "SPINOFF":                                // new line created, part of the cost moves across
                {
                    var moved = p.CostBasisGbp * ca.CostBasisAllocation;
                    var n = Get(r, p.AccountKey, ca.NewTicker!);
                    n.Quantity += p.Quantity * ca.Ratio;
                    n.CostBasisGbp += moved;
                    n.FirstBuyAt ??= ca.EffectiveDate;
                    n.LastActivityAt = ca.EffectiveDate;
                    p.CostBasisGbp -= moved;
                    break;
                }

                case "MERGER":                                 // share-for-share: old line closes, cost rolls over (no disposal)
                {
                    var n = Get(r, p.AccountKey, ca.NewTicker!);
                    n.Quantity += p.Quantity * ca.Ratio;
                    n.CostBasisGbp += p.CostBasisGbp * ca.CostBasisAllocation;
                    n.FirstBuyAt ??= ca.EffectiveDate;
                    n.LastActivityAt = ca.EffectiveDate;
                    p.Quantity = 0; p.CostBasisGbp = 0;
                    p.LastActivityAt = ca.EffectiveDate;
                    break;
                }

                case "CASH_ACQUISITION":                       // line closes; Ratio = cash per share in GBP (0 if booked separately)
                {
                    var proceeds = p.Quantity * ca.Ratio;
                    var gain = proceeds - p.CostBasisGbp;
                    r.Realized.Add(new RealizedGain(null, p.AccountKey, p.Ticker, ca.EffectiveDate, p.Quantity,
                        Math.Round(proceeds, 4), Math.Round(p.CostBasisGbp, 4), Math.Round(gain, 4), null));
                    p.RealizedGainGbp += gain;
                    p.Quantity = 0; p.CostBasisGbp = 0;
                    p.LastActivityAt = ca.EffectiveDate;
                    break;
                }

                default:
                    r.Findings.Add(new DataQualityFinding(Severity.Error, "ENGINE_UNKNOWN_CA", null, null, null,
                        $"Unknown corporate action type {ca.ActionType} for {ca.Ticker}"));
                    break;
            }
        }
    }
}
