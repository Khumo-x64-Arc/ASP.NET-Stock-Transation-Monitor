namespace StockMonitor.Web.Models;

public sealed class MonitorOptions
{
    public ReplayOptions Replay { get; set; } = new();
    public PriceOptions Prices { get; set; } = new();
    public SnapshotOptions Snapshots { get; set; } = new();
    public AlertOptions Alerts { get; set; } = new();

    public sealed class ReplayOptions { public bool Enabled { get; set; } = true; public double IntervalSeconds { get; set; } = 4; public bool StartPaused { get; set; } }
    public sealed class PriceOptions { public bool Enabled { get; set; } = true; public double TickSeconds { get; set; } = 3; public double AnnualVolatility { get; set; } = 0.3; public double TimeAcceleration { get; set; } = 600; }
    public sealed class SnapshotOptions { public double IntervalSeconds { get; set; } = 15; }
    public sealed class AlertOptions { public decimal LargeTradeGbp { get; set; } = 5000; public decimal ConcentrationPct { get; set; } = 12; public decimal IsaAllowanceGbp { get; set; } = 20000; public decimal PriceMovePct { get; set; } = 4; }
}

/// <summary>Body of POST /api/transactions - the shape a broker webhook / upstream system would send.</summary>
public sealed class LiveTransactionRequest
{
    public int AccountNumber { get; set; }
    public string Action { get; set; } = "";          // "Market buy", "BUY", "Dividend (Ordinary)", ...
    public DateTime? Timestamp { get; set; }           // defaults to now (UTC)
    public string? Ticker { get; set; }
    public string? Name { get; set; }
    public string? Isin { get; set; }
    public decimal? Shares { get; set; }
    public decimal? PricePerShare { get; set; }
    public string? PriceCurrency { get; set; }         // USD | GBX | GBP
    public decimal? ExchangeRate { get; set; }         // local per GBP
    public decimal Total { get; set; }                 // GBP
    public decimal? Result { get; set; }
    public decimal? WithholdingTax { get; set; }
    public string? WithholdingTaxCurrency { get; set; }
    public decimal? CurrencyConversionFee { get; set; }
    public string? Notes { get; set; }
    public string? BrokerId { get; set; }              // idempotency key - resend safely
}

public sealed record IngestResult(bool Accepted, bool Duplicate, string? TransactionKey, string? Error, IReadOnlyList<string> Warnings);

public sealed record PortfolioSummary(
    decimal MarketValueGbp, decimal CostBasisGbp, decimal UnrealizedGainGbp, decimal UnrealizedPct,
    decimal DayChangeGbp, decimal DayChangePct, decimal CashGbp, decimal TotalValueGbp,
    decimal RealizedGainGbp, decimal RealizedThisTaxYearGbp, decimal DividendsGbp, decimal DividendsLast12mGbp,
    decimal NetContributionsGbp, int OpenPositions, DateTime? LastTransactionAt, DateTime AsOf);

public sealed record HoldingRow(
    int SecurityKey, string Ticker, string SecurityName, string Accounts, decimal Quantity, decimal AvgCostGbp,
    decimal PriceGbp, decimal MarketValueGbp, decimal CostBasisGbp, decimal UnrealizedGainGbp, decimal UnrealizedPct,
    decimal DayChangeGbp, decimal DayChangePct, decimal WeightPct, decimal DividendsGbp, decimal RealizedGainGbp, DateTime? FirstBuyAt);

public sealed record FeedRow(
    string TransactionKey, DateTime TxTimestamp, DateTime InsertedAt, string AccountType, string ActionCode, string ActionName,
    string? Ticker, string? SecurityName, decimal? Shares, decimal? PriceGbp, decimal TotalGbp, decimal CashImpactGbp, string Source);

public sealed record PriceRow(int SecurityKey, string Ticker, decimal PriceGbp, decimal ChangePct, DateTime UpdatedAt);

public sealed record AlertRow(long AlertId, DateTime CreatedAt, string Severity, string AlertType, string Message, bool IsAcknowledged);
