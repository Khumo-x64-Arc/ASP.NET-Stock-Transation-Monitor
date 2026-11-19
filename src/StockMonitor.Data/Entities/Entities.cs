namespace StockMonitor.Data.Entities;

// ---------------- etl ----------------
public class BatchLog
{
    public int BatchId { get; set; }
    public string BatchType { get; set; } = "FILE";
    public string? SourceFile { get; set; }
    public string? SourceFileHash { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string Status { get; set; } = "RUNNING";
    public int RowsExtracted { get; set; }
    public int RowsCleansed { get; set; }
    public int RowsRejected { get; set; }
    public int RowsLoaded { get; set; }
    public int RowsQueued { get; set; }
    public string? Message { get; set; }
}

public class DataQualityIssue
{
    public long IssueId { get; set; }
    public int? BatchId { get; set; }
    public string Severity { get; set; } = "INFO";
    public string RuleCode { get; set; } = "";
    public string? TransactionKey { get; set; }
    public string? SourceSheet { get; set; }
    public int? SourceRow { get; set; }
    public string? Detail { get; set; }
    public DateTime CreatedAt { get; set; }
}

// ---------------- stg ----------------
public class CleanTransactionRow
{
    public long CleanId { get; set; }
    public int BatchId { get; set; }
    public string TransactionKey { get; set; } = "";
    public bool IsSyntheticKey { get; set; }
    public int AccountNumber { get; set; }
    public DateTime TxTimestamp { get; set; }
    public string ActionCode { get; set; } = "";
    public string? ActionSubType { get; set; }
    public string? Ticker { get; set; }
    public string? Isin { get; set; }
    public string? SecurityName { get; set; }
    public decimal? Shares { get; set; }
    public decimal? PriceLocal { get; set; }
    public string? PriceCurrency { get; set; }
    public decimal? FxRate { get; set; }
    public decimal? PriceGbp { get; set; }
    public decimal TotalGbp { get; set; }
    public decimal? BrokerResultGbp { get; set; }
    public decimal? WithholdingTax { get; set; }
    public string? WithholdingTaxCurrency { get; set; }
    public decimal? FxFeeGbp { get; set; }
    public decimal? StampDutyGbp { get; set; }
    public decimal? TransactionFeeGbp { get; set; }
    public decimal? FinraFeeGbp { get; set; }
    public decimal? DepositFeeGbp { get; set; }
    public string? Notes { get; set; }
    public bool IsCorporateActionArtifact { get; set; }
    public string? SourceSheet { get; set; }
    public int? SourceRow { get; set; }
}

public class ReplayQueueItem
{
    public long ReplayId { get; set; }
    public int BatchId { get; set; }
    public string TransactionKey { get; set; } = "";
    public DateTime TxTimestamp { get; set; }
    public string Payload { get; set; } = "";
    public DateTime? ReleasedAt { get; set; }
}

// ---------------- dw ----------------
public class DimAccount
{
    public int AccountKey { get; set; }
    public int AccountNumber { get; set; }
    public string AccountType { get; set; } = "";
    public string AccountName { get; set; } = "";
    public bool IsTaxSheltered { get; set; }
    public string BaseCurrency { get; set; } = "GBP";
}

public class DimSecurity
{
    public int SecurityKey { get; set; }
    public string Ticker { get; set; } = "";
    public string? Isin { get; set; }
    public string SecurityName { get; set; } = "";
    public string? QuoteCurrency { get; set; }
    public DateOnly? FirstSeenDate { get; set; }
    public DateOnly? LastSeenDate { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class DimTransactionType
{
    public int TransactionTypeKey { get; set; }
    public string ActionCode { get; set; } = "";
    public string ActionSubType { get; set; } = "";
    public string Category { get; set; } = "";
    public short CashDirection { get; set; }
    public string DisplayName { get; set; } = "";
}

public class FactTransaction
{
    public long TransactionId { get; set; }
    public string TransactionKey { get; set; } = "";
    public int AccountKey { get; set; }
    public int? SecurityKey { get; set; }
    public int TransactionTypeKey { get; set; }
    public int DateKey { get; set; }
    public DateTime TxTimestamp { get; set; }
    public decimal? Shares { get; set; }
    public decimal? PriceLocal { get; set; }
    public string? PriceCurrency { get; set; }
    public decimal? FxRate { get; set; }
    public decimal? PriceGbp { get; set; }
    public decimal TotalGbp { get; set; }
    public decimal CashImpactGbp { get; set; }
    public decimal? BrokerResultGbp { get; set; }
    public decimal? WithholdingTax { get; set; }
    public string? WithholdingTaxCurrency { get; set; }
    public decimal? FxFeeGbp { get; set; }
    public decimal? StampDutyGbp { get; set; }
    public decimal? TransactionFeeGbp { get; set; }
    public decimal? FinraFeeGbp { get; set; }
    public decimal? DepositFeeGbp { get; set; }
    public string? Notes { get; set; }
    public bool IsSyntheticKey { get; set; }
    public bool IsCorporateActionArtifact { get; set; }
    public string Source { get; set; } = "FILE";
    public int BatchId { get; set; }
    public DateTime InsertedAt { get; set; }

    public DimAccount? Account { get; set; }
    public DimSecurity? Security { get; set; }
    public DimTransactionType? TransactionType { get; set; }
}

public class CorporateAction
{
    public int CorporateActionId { get; set; }
    public DateOnly EffectiveDate { get; set; }
    public string ActionType { get; set; } = "";
    public string Ticker { get; set; } = "";
    public decimal Ratio { get; set; }
    public string? NewTicker { get; set; }
    public string? NewSecurityName { get; set; }
    public decimal CostBasisAllocation { get; set; }
    public string? Source { get; set; }
    public string? Notes { get; set; }
}

public class Position
{
    public int AccountKey { get; set; }
    public int SecurityKey { get; set; }
    public decimal Quantity { get; set; }
    public decimal CostBasisGbp { get; set; }
    public decimal? AvgCostGbp { get; set; }
    public decimal RealizedGainGbp { get; set; }
    public decimal DividendsGbp { get; set; }
    public DateTime? FirstBuyAt { get; set; }
    public DateTime? LastActivityAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class FactRealizedGain
{
    public long RealizedGainId { get; set; }
    public long? TransactionId { get; set; }
    public int AccountKey { get; set; }
    public int SecurityKey { get; set; }
    public int DateKey { get; set; }
    public DateTime DisposedAt { get; set; }
    public decimal SharesDisposed { get; set; }
    public decimal ProceedsGbp { get; set; }
    public decimal CostBasisGbp { get; set; }
    public decimal GainGbp { get; set; }
    public decimal? BrokerResultGbp { get; set; }
    public string Method { get; set; } = "S104_AVERAGE";
}

// ---------------- rt ----------------
public class SecurityPrice
{
    public int SecurityKey { get; set; }
    public decimal PriceGbp { get; set; }
    public decimal PrevCloseGbp { get; set; }
    public string Source { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
}

public class PriceTick
{
    public long TickId { get; set; }
    public int SecurityKey { get; set; }
    public decimal PriceGbp { get; set; }
    public DateTime TickTime { get; set; }
}

public class PortfolioSnapshot
{
    public long SnapshotId { get; set; }
    public DateTime SnapshotTime { get; set; }
    public int? AccountKey { get; set; }
    public decimal MarketValueGbp { get; set; }
    public decimal CostBasisGbp { get; set; }
    public decimal CashGbp { get; set; }
}

public class Alert
{
    public long AlertId { get; set; }
    public DateTime CreatedAt { get; set; }
    public string Severity { get; set; } = "INFO";
    public string AlertType { get; set; } = "";
    public int? AccountKey { get; set; }
    public int? SecurityKey { get; set; }
    public string? TransactionKey { get; set; }
    public string Message { get; set; } = "";
    public bool IsAcknowledged { get; set; }
}
