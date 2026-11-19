namespace StockMonitor.Core.Model;

/// <summary>Canonical action codes used everywhere downstream of the cleanser.</summary>
public static class ActionCodes
{
    public const string Buy = "BUY";
    public const string Sell = "SELL";
    public const string Dividend = "DIVIDEND";
    public const string Deposit = "DEPOSIT";
    public const string Withdrawal = "WITHDRAWAL";
    public const string Interest = "INTEREST";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string> { Buy, Sell, Dividend, Deposit, Withdrawal, Interest };

    public static bool IsTrade(string code) => code is Buy or Sell;
    public static bool RequiresSecurity(string code) => code is Buy or Sell or Dividend;
}

/// <summary>
/// One source row exactly as extracted, every value rendered as an invariant string.
/// Strings (not typed values) keep the landing layer lossless and let the batch
/// pipeline and the live API share one cleanser.
/// </summary>
public sealed class RawTransactionRow
{
    public string SourceSheet { get; set; } = "";
    public int SourceRow { get; set; }
    public string? AccountNumber { get; set; }
    public string? Action { get; set; }
    public string? TxDate { get; set; }
    public string? TxTime { get; set; }
    public string? Isin { get; set; }
    public string? Ticker { get; set; }
    public string? SecurityName { get; set; }
    public string? Shares { get; set; }
    public string? PricePerShare { get; set; }
    public string? PriceCurrency { get; set; }
    public string? ExchangeRate { get; set; }
    public string? Result { get; set; }
    public string? Total { get; set; }
    public string? TotalCurrency { get; set; }
    public string? WithholdingTax { get; set; }
    public string? WithholdingTaxCurrency { get; set; }
    public string? ChargeAmount { get; set; }
    public string? DepositFee { get; set; }
    public string? TransactionFee { get; set; }
    public string? FinraFee { get; set; }
    public string? CurrencyConversionFee { get; set; }
    public string? StampDutyReserveTax { get; set; }
    public string? Notes { get; set; }
    public string? BrokerId { get; set; }

    /// <summary>Values in a fixed order - used for the row hash and reject payloads.</summary>
    public IEnumerable<string?> Values() => new[]
    {
        AccountNumber, Action, TxDate, TxTime, Isin, Ticker, SecurityName, Shares, PricePerShare,
        PriceCurrency, ExchangeRate, Result, Total, TotalCurrency, WithholdingTax, WithholdingTaxCurrency,
        ChargeAmount, DepositFee, TransactionFee, FinraFee, CurrencyConversionFee, StampDutyReserveTax,
        Notes, BrokerId
    };
}

/// <summary>Typed, validated, GBP-normalised transaction. Mirrors stg.CleanTransaction.</summary>
public sealed record CleanTransaction
{
    public required string TransactionKey { get; init; }
    public bool IsSyntheticKey { get; init; }
    public required int AccountNumber { get; init; }
    public required DateTime TxTimestamp { get; init; }
    public required string ActionCode { get; init; }
    public string? ActionSubType { get; init; }
    public string? Ticker { get; init; }
    public string? Isin { get; init; }
    public string? SecurityName { get; init; }
    public decimal? Shares { get; init; }
    public decimal? PriceLocal { get; init; }
    public string? PriceCurrency { get; init; }
    public decimal? FxRate { get; init; }
    public decimal? PriceGbp { get; init; }
    public required decimal TotalGbp { get; init; }
    public decimal? BrokerResultGbp { get; init; }
    public decimal? WithholdingTax { get; init; }
    public string? WithholdingTaxCurrency { get; init; }
    public decimal? FxFeeGbp { get; init; }
    public decimal? StampDutyGbp { get; init; }
    public decimal? TransactionFeeGbp { get; init; }
    public decimal? FinraFeeGbp { get; init; }
    public decimal? DepositFeeGbp { get; init; }
    public string? Notes { get; init; }
    public bool IsCorporateActionArtifact { get; init; }
    public string? SourceSheet { get; init; }
    public int? SourceRow { get; init; }
}

public enum Severity { Info, Warn, Error }

public sealed record DataQualityFinding(
    Severity Severity,
    string RuleCode,
    string? TransactionKey,
    string? SourceSheet,
    int? SourceRow,
    string Detail);

public sealed record RejectedRow(RawTransactionRow Raw, string Reason);
