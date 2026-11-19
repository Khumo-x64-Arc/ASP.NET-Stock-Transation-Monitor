namespace StockMonitor.Data.Entities;

// Keyless entities mapped to the semantic views in database/06_views.sql

public class VwTransactionDetail
{
    public long TransactionId { get; set; }
    public string TransactionKey { get; set; } = "";
    public DateTime TxTimestamp { get; set; }
    public DateOnly TxDate { get; set; }
    public string YearMonth { get; set; } = "";
    public string UkTaxYear { get; set; } = "";
    public int AccountKey { get; set; }
    public int AccountNumber { get; set; }
    public string AccountType { get; set; } = "";
    public string ActionCode { get; set; } = "";
    public string ActionSubType { get; set; } = "";
    public string Category { get; set; } = "";
    public string ActionName { get; set; } = "";
    public int? SecurityKey { get; set; }
    public string? Ticker { get; set; }
    public string? SecurityName { get; set; }
    public decimal? Shares { get; set; }
    public decimal? PriceLocal { get; set; }
    public string? PriceCurrency { get; set; }
    public decimal? PriceGbp { get; set; }
    public decimal? FxRate { get; set; }
    public decimal TotalGbp { get; set; }
    public decimal CashImpactGbp { get; set; }
    public decimal? BrokerResultGbp { get; set; }
    public decimal? WithholdingTax { get; set; }
    public string? WithholdingTaxCurrency { get; set; }
    public decimal TotalFeesGbp { get; set; }
    public string? Notes { get; set; }
    public string Source { get; set; } = "";
    public DateTime InsertedAt { get; set; }
}

public class VwMonthlyCashFlow
{
    public string YearMonth { get; set; } = "";
    public int AccountKey { get; set; }
    public string AccountType { get; set; } = "";
    public decimal DepositsGbp { get; set; }
    public decimal WithdrawalsGbp { get; set; }
    public decimal BuysGbp { get; set; }
    public decimal SellsGbp { get; set; }
    public decimal DividendsGbp { get; set; }
    public decimal InterestGbp { get; set; }
    public decimal FeesGbp { get; set; }
    public int TradeCount { get; set; }
}

public class VwDividendBySecurity
{
    public int SecurityKey { get; set; }
    public string Ticker { get; set; } = "";
    public string SecurityName { get; set; } = "";
    public int AccountKey { get; set; }
    public string AccountType { get; set; } = "";
    public int Payments { get; set; }
    public decimal DividendsGbp { get; set; }
    public DateTime FirstPaidAt { get; set; }
    public DateTime LastPaidAt { get; set; }
}

public class VwCashBalance
{
    public int AccountKey { get; set; }
    public int AccountNumber { get; set; }
    public string AccountType { get; set; } = "";
    public decimal CashGbp { get; set; }
}

public class VwRealizedGainByTaxYear
{
    public string UkTaxYear { get; set; } = "";
    public int AccountKey { get; set; }
    public string AccountType { get; set; } = "";
    public bool IsTaxSheltered { get; set; }
    public int Disposals { get; set; }
    public decimal ProceedsGbp { get; set; }
    public decimal CostBasisGbp { get; set; }
    public decimal GainGbp { get; set; }
    public decimal BrokerResultGbp { get; set; }
}

public class VwIsaAllowance
{
    public string UkTaxYear { get; set; } = "";
    public int AccountKey { get; set; }
    public decimal SubscribedGbp { get; set; }
    public decimal AllowanceGbp { get; set; }
    public decimal UsedPct { get; set; }
}

public class VwHolding
{
    public int AccountKey { get; set; }
    public int AccountNumber { get; set; }
    public string AccountType { get; set; } = "";
    public int SecurityKey { get; set; }
    public string Ticker { get; set; } = "";
    public string SecurityName { get; set; } = "";
    public string? QuoteCurrency { get; set; }
    public decimal Quantity { get; set; }
    public decimal CostBasisGbp { get; set; }
    public decimal? AvgCostGbp { get; set; }
    public decimal? PriceGbp { get; set; }
    public decimal? PrevCloseGbp { get; set; }
    public decimal? MarketValueGbp { get; set; }
    public decimal? UnrealizedGainGbp { get; set; }
    public decimal? DayChangeGbp { get; set; }
    public decimal RealizedGainGbp { get; set; }
    public decimal DividendsGbp { get; set; }
    public DateTime? FirstBuyAt { get; set; }
    public DateTime? LastActivityAt { get; set; }
    public string? PriceSource { get; set; }
    public DateTime? PriceUpdatedAt { get; set; }
}

public class VwBatchSummary
{
    public int BatchId { get; set; }
    public string BatchType { get; set; } = "";
    public string? SourceFile { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string Status { get; set; } = "";
    public int RowsExtracted { get; set; }
    public int RowsCleansed { get; set; }
    public int RowsRejected { get; set; }
    public int RowsLoaded { get; set; }
    public int RowsQueued { get; set; }
    public int Warnings { get; set; }
    public int Errors { get; set; }
}
