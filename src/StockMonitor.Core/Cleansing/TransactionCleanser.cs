using System.Security.Cryptography;
using System.Text;
using StockMonitor.Core.Model;

namespace StockMonitor.Core.Cleansing;

public sealed class CleanseResult
{
    public List<CleanTransaction> Clean { get; } = new();
    public List<RejectedRow> Rejected { get; } = new();
    public List<DataQualityFinding> Findings { get; } = new();
}

/// <summary>
/// Data-preparation rules for Trading 212 style exports (see docs/DATA_PREPARATION.md for the rule catalogue).
/// Stateless apart from the per-batch duplicate check, so it is used both by the batch ETL and by the live API.
/// </summary>
public sealed class TransactionCleanser
{
    private static readonly Dictionary<string, string> ActionMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Market buy"] = ActionCodes.Buy,
        ["Limit buy"] = ActionCodes.Buy,
        ["Stop buy"] = ActionCodes.Buy,
        ["Market sell"] = ActionCodes.Sell,
        ["Limit sell"] = ActionCodes.Sell,
        ["Stop sell"] = ActionCodes.Sell,
        ["Deposit"] = ActionCodes.Deposit,
        ["Withdrawal"] = ActionCodes.Withdrawal,
        ["Interest on cash"] = ActionCodes.Interest,
        // canonical codes are accepted too (live API callers)
        ["BUY"] = ActionCodes.Buy, ["SELL"] = ActionCodes.Sell, ["DEPOSIT"] = ActionCodes.Deposit,
        ["WITHDRAWAL"] = ActionCodes.Withdrawal, ["INTEREST"] = ActionCodes.Interest, ["DIVIDEND"] = ActionCodes.Dividend,
    };

    /// <summary>Cleanses a batch. Duplicate transaction keys inside the batch are rejected after the first occurrence.</summary>
    public CleanseResult Cleanse(IEnumerable<RawTransactionRow> rows)
    {
        var result = new CleanseResult();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var naturalKeys = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var raw in rows)
        {
            var (clean, reason) = CleanseOne(raw, result.Findings);
            if (clean is null)
            {
                result.Rejected.Add(new RejectedRow(raw, reason!));
                continue;
            }

            if (!seenKeys.Add(clean.TransactionKey))
            {
                result.Rejected.Add(new RejectedRow(raw, $"DUPLICATE_KEY: {clean.TransactionKey} already present in batch"));
                continue;
            }

            // Same account/time/ticker/qty/total but different broker IDs = split fills, not duplicates. Keep, but note it.
            var nk = NaturalKey(clean);
            if (!clean.IsSyntheticKey && naturalKeys.TryGetValue(nk, out var otherKey))
                result.Findings.Add(new(Severity.Info, "PARTIAL_FILL_PAIR", clean.TransactionKey, raw.SourceSheet, raw.SourceRow,
                    $"Identical economics to {otherKey}; distinct broker IDs so both kept."));
            else
                naturalKeys.TryAdd(nk, clean.TransactionKey);

            result.Clean.Add(clean);
        }
        return result;
    }

    /// <summary>Applies all row-level rules. Returns (null, reason) when the row must be rejected.</summary>
    public (CleanTransaction? Clean, string? RejectReason) CleanseOne(RawTransactionRow raw, ICollection<DataQualityFinding> findings)
    {
        void Flag(Severity sev, string rule, string detail, string? key = null) =>
            findings.Add(new DataQualityFinding(sev, rule, key, raw.SourceSheet, raw.SourceRow, detail));

        // R01/R02 action -------------------------------------------------------------------------------
        var actionText = ValueParsers.CleanText(raw.Action);
        if (actionText is null) return (null, "R01 MISSING_ACTION");
        var (actionCode, subType) = MapAction(actionText);
        if (actionCode is null) return (null, $"R02 UNKNOWN_ACTION: '{actionText}'");

        // R03 timestamp --------------------------------------------------------------------------------
        var ts = ValueParsers.CombineTimestamp(raw.TxDate, raw.TxTime);
        if (ts is null) return (null, $"R03 BAD_TIMESTAMP: date='{raw.TxDate}' time='{raw.TxTime}'");
        var tsValue = DateTime.SpecifyKind(ts.Value, DateTimeKind.Unspecified);
        if (tsValue > DateTime.UtcNow.AddDays(1)) return (null, $"R03 FUTURE_TIMESTAMP: {tsValue:O}");

        // account --------------------------------------------------------------------------------------
        if (!int.TryParse(raw.AccountNumber?.Trim(), out var account))
            return (null, $"R04 BAD_ACCOUNT: '{raw.AccountNumber}'");

        // numerics (R06 garbage -> null + warning) ------------------------------------------------------
        decimal? Num(string? value, string field)
        {
            var r = ValueParsers.TryDecimal(value, out var d);
            if (r == ValueParsers.NumberParse.Invalid)
                Flag(Severity.Warn, "R06 NON_NUMERIC", $"{field}='{Truncate(value, 120)}' set to NULL");
            return d;
        }

        var shares = Num(raw.Shares, "Shares");
        var price = Num(raw.PricePerShare, "PricePerShare");
        var total = Num(raw.Total, "Total");
        var result = Num(raw.Result, "Result");
        var wht = Num(raw.WithholdingTax, "WithholdingTax");
        var fxFee = Num(raw.CurrencyConversionFee, "CurrencyConversionFee");
        var stamp = Num(raw.StampDutyReserveTax, "StampDutyReserveTax");
        var txFee = Num(raw.TransactionFee, "TransactionFee");
        var finra = Num(raw.FinraFee, "FinraFee");
        var depFee = Num(raw.DepositFee, "DepositFee");

        // ChargeAmount: numeric for deposits; one row carries an analyst comment instead (R13)
        var notes = ValueParsers.CleanText(raw.Notes);
        if (ValueParsers.TryDecimal(raw.ChargeAmount, out _) == ValueParsers.NumberParse.Invalid)
        {
            notes = string.Join(" | ", new[] { notes, ValueParsers.CleanText(raw.ChargeAmount) }.Where(x => x != null));
            Flag(Severity.Warn, "R13 TEXT_IN_NUMERIC", "Free text in ChargeAmount moved to Notes");
        }

        // R05 total ------------------------------------------------------------------------------------
        if (total is null) return (null, "R05 MISSING_TOTAL");
        if (total < 0)
        {
            // 2024 exports switched withdrawals to negative amounts; everything downstream expects magnitudes
            Flag(actionCode == ActionCodes.Withdrawal ? Severity.Info : Severity.Warn,
                 actionCode == ActionCodes.Withdrawal ? "R07 WITHDRAWAL_SIGN_NORMALISED" : "R08 NEGATIVE_TOTAL_NORMALISED",
                 $"Total {total} stored as {Math.Abs(total.Value)}");
            total = Math.Abs(total.Value);
        }

        // security ---------------------------------------------------------------------------------------
        var ticker = ValueParsers.CleanText(raw.Ticker)?.ToUpperInvariant();
        var name = ValueParsers.CleanText(raw.SecurityName);
        if (ActionCodes.RequiresSecurity(actionCode) && ticker is null)
            return (null, "R09 MISSING_TICKER");
        if (!ActionCodes.RequiresSecurity(actionCode)) { ticker = null; name = null; }

        string? isin = ValueParsers.CleanText(raw.Isin)?.ToUpperInvariant();
        if (isin is not null && !ValueParsers.IsValidIsin(isin))
        {
            Flag(Severity.Warn, "R10 INVALID_ISIN", $"'{isin}' failed ISO 6166 check digit; dropped");
            isin = null;
        }

        if (ActionCodes.IsTrade(actionCode))
        {
            if (shares is null or <= 0m) return (null, $"R11 BAD_QUANTITY: {raw.Shares}");
            if (price is null or < 0m) return (null, $"R11 BAD_PRICE: {raw.PricePerShare}");
        }

        // currency normalisation (R12) -----------------------------------------------------------------
        var ccy = ValueParsers.CleanText(raw.PriceCurrency)?.ToUpperInvariant();
        var fx = Num(raw.ExchangeRate, "ExchangeRate");     // "Not available" on dividends -> NULL (null token, no warning)
        decimal? priceGbp = null;
        if (price is not null)
        {
            switch (ccy)
            {
                case "GBX": priceGbp = price / 100m; fx ??= 100m; break;    // pence -> pounds
                case "GBP": priceGbp = price; fx ??= 1m; break;
                default:
                    if (fx is > 0m) priceGbp = price / fx;
                    else if (ActionCodes.IsTrade(actionCode) && shares is > 0m && total > 0)
                    {
                        priceGbp = total / shares;                            // effective price incl. fees
                        Flag(Severity.Warn, "R12 FX_DERIVED", $"No FX rate for {ccy} trade; GBP price derived from Total/Shares");
                    }
                    break;
            }
        }

        // corporate-action artefact (R14): broker books acquisitions as zero-value sells -------------
        var isArtifact = actionCode == ActionCodes.Sell && total == 0m && (price ?? 0m) == 0m;

        // transaction key (R15) ------------------------------------------------------------------------
        var brokerId = ValueParsers.CleanText(raw.BrokerId);
        bool synthetic = brokerId is null;
        var key = brokerId ?? SyntheticKey(account, tsValue, actionCode, subType, ticker, shares, total.Value);
        if (key.Length > 64) key = "H-" + Sha1Hex(key)[..40];

        if (isArtifact)
            Flag(Severity.Warn, "R14 CORPORATE_ACTION_ARTIFACT",
                 $"Zero-value SELL of {shares} {ticker}: flagged; position handled by dw.CorporateAction", key);

        var clean = new CleanTransaction
        {
            TransactionKey = key,
            IsSyntheticKey = synthetic,
            AccountNumber = account,
            TxTimestamp = tsValue,
            ActionCode = actionCode,
            ActionSubType = subType,
            Ticker = ticker,
            Isin = isin,
            SecurityName = name,
            Shares = ActionCodes.RequiresSecurity(actionCode) ? shares : null,
            PriceLocal = price,
            PriceCurrency = ccy is { Length: 3 } ? ccy : null,
            FxRate = fx,
            PriceGbp = priceGbp is null ? null : Math.Round(priceGbp.Value, 6),
            TotalGbp = Math.Round(total.Value, 4),
            BrokerResultGbp = result,
            WithholdingTax = wht is null ? null : Math.Abs(wht.Value),
            WithholdingTaxCurrency = ValueParsers.CleanText(raw.WithholdingTaxCurrency)?.ToUpperInvariant() is { Length: 3 } w ? w : null,
            FxFeeGbp = fxFee is null ? null : Math.Abs(fxFee.Value),
            StampDutyGbp = stamp is null ? null : Math.Abs(stamp.Value),
            TransactionFeeGbp = txFee is null ? null : Math.Abs(txFee.Value),
            FinraFeeGbp = finra is null ? null : Math.Abs(finra.Value),
            DepositFeeGbp = depFee is null ? null : Math.Abs(depFee.Value),
            Notes = notes,
            IsCorporateActionArtifact = isArtifact,
            SourceSheet = raw.SourceSheet,
            SourceRow = raw.SourceRow,
        };
        return (clean, null);
    }

    /// <summary>"Dividend (Dividends paid by us corporations)" -> (DIVIDEND, "Dividends paid by us corporations").</summary>
    public static (string? Code, string? SubType) MapAction(string action)
    {
        var a = action.Trim();
        if (ActionMap.TryGetValue(a, out var code)) return (code, null);
        if (a.StartsWith("Dividend", StringComparison.OrdinalIgnoreCase))
        {
            var open = a.IndexOf('(');
            var close = a.LastIndexOf(')');
            var sub = open >= 0 && close > open ? a[(open + 1)..close].Trim() : "Ordinary";
            return (ActionCodes.Dividend, sub.Length == 0 ? "Ordinary" : sub);
        }
        return (null, null);
    }

    public static string SyntheticKey(int account, DateTime ts, string action, string? sub, string? ticker, decimal? shares, decimal total)
    {
        var nk = string.Join('|', account, ts.ToString("yyyy-MM-ddTHH:mm:ss.fff"), action, sub ?? "", ticker ?? "",
            shares?.ToString("0.##########", System.Globalization.CultureInfo.InvariantCulture) ?? "",
            total.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture));
        return "SYN-" + Sha1Hex(nk)[..24];
    }

    private static string NaturalKey(CleanTransaction c) =>
        $"{c.AccountNumber}|{c.TxTimestamp:O}|{c.ActionCode}|{c.Ticker}|{c.Shares}|{c.TotalGbp}";

    private static string Sha1Hex(string s) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s)));

    private static string? Truncate(string? s, int n) => s is null || s.Length <= n ? s : s[..n] + "...";
}
