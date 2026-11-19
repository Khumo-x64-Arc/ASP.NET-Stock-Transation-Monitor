using StockMonitor.Core.Cleansing;
using StockMonitor.Core.Model;
using Xunit;

namespace StockMonitor.Tests;

public class CleanserTests
{
    private static RawTransactionRow Buy(Action<RawTransactionRow>? tweak = null)
    {
        var r = new RawTransactionRow
        {
            SourceSheet = "Inv2021", SourceRow = 2, AccountNumber = "2131", Action = "Market buy",
            TxDate = "2021-01-04 00:00:00.000", TxTime = "1899-12-30 14:31:00.000",
            Ticker = "riot", SecurityName = "Riot  Platforms", Shares = "10", PricePerShare = "18.79",
            PriceCurrency = "USD", ExchangeRate = "1.36275", Total = "137.88", BrokerId = "EOF738679570"
        };
        tweak?.Invoke(r);
        return r;
    }

    private static (CleanTransaction? c, string? reason, List<DataQualityFinding> f) Run(RawTransactionRow r)
    {
        var f = new List<DataQualityFinding>();
        var (c, reason) = new TransactionCleanser().CleanseOne(r, f);
        return (c, reason, f);
    }

    [Fact]
    public void Buy_is_normalised_to_gbp_and_canonical_codes()
    {
        var (c, reason, _) = Run(Buy());
        Assert.Null(reason);
        Assert.Equal("BUY", c!.ActionCode);
        Assert.Equal("RIOT", c.Ticker);
        Assert.Equal("Riot Platforms", c.SecurityName);           // whitespace collapsed
        Assert.Equal(new DateTime(2021, 1, 4, 14, 31, 0), c.TxTimestamp);
        Assert.Equal(Math.Round(18.79m / 1.36275m, 6), c.PriceGbp);
        Assert.False(c.IsSyntheticKey);
    }

    [Fact]
    public void Layout_2024_uses_full_timestamp_in_time_column()
    {
        var (c, _, _) = Run(Buy(r => { r.TxDate = null; r.TxTime = "2024-01-04 12:34:34.123"; }));
        Assert.Equal(new DateTime(2024, 1, 4, 12, 34, 34, 123), c!.TxTimestamp);
    }

    [Fact]
    public void Gbx_prices_are_converted_from_pence()
    {
        var (c, _, _) = Run(Buy(r => { r.PriceCurrency = "GBX"; r.PricePerShare = "929.8"; r.ExchangeRate = "100"; }));
        Assert.Equal(9.298m, c!.PriceGbp);
    }

    [Fact]
    public void Dividend_subtype_is_extracted_and_not_available_fx_is_null()
    {
        var (c, reason, f) = Run(Buy(r => { r.Action = "Dividend (Dividends paid by us corporations)"; r.ExchangeRate = "Not available"; r.BrokerId = null; r.Total = "11.70"; }));
        Assert.Null(reason);
        Assert.Equal("DIVIDEND", c!.ActionCode);
        Assert.Equal("Dividends paid by us corporations", c.ActionSubType);
        Assert.Null(c.FxRate);
        Assert.True(c.IsSyntheticKey);
        Assert.StartsWith("SYN-", c.TransactionKey);
        Assert.DoesNotContain(f, x => x.RuleCode.Contains("NON_NUMERIC"));
    }

    [Fact]
    public void Synthetic_key_is_deterministic()
    {
        var a = Run(Buy(r => r.BrokerId = null)).c!.TransactionKey;
        var b = Run(Buy(r => r.BrokerId = null)).c!.TransactionKey;
        Assert.Equal(a, b);
    }

    [Fact]
    public void Negative_withdrawal_is_normalised_with_info_finding()
    {
        var (c, _, f) = Run(Buy(r => { r.Action = "Withdrawal"; r.Total = "-3268"; r.Ticker = null; r.Shares = null; r.PricePerShare = null; }));
        Assert.Equal(3268m, c!.TotalGbp);
        Assert.Null(c.Ticker);
        Assert.Contains(f, x => x.RuleCode == "R07 WITHDRAWAL_SIGN_NORMALISED");
    }

    [Fact]
    public void Text_in_charge_amount_moves_to_notes()
    {
        var (c, _, f) = Run(Buy(r => r.ChargeAmount = "This was not a market sell but rather a company acquisition"));
        Assert.Contains("company acquisition", c!.Notes);
        Assert.Contains(f, x => x.RuleCode == "R13 TEXT_IN_NUMERIC");
    }

    [Fact]
    public void Zero_value_sell_is_flagged_as_corporate_action_artifact()
    {
        var (c, _, _) = Run(Buy(r => { r.Action = "Market sell"; r.Ticker = "PBCT"; r.Shares = "134"; r.PricePerShare = "0"; r.Total = "0"; }));
        Assert.True(c!.IsCorporateActionArtifact);
    }

    [Theory]
    [InlineData(null, "R01")]
    [InlineData("Stock lending", "R02")]
    public void Bad_actions_are_rejected(string? action, string code)
    {
        var (c, reason, _) = Run(Buy(r => r.Action = action));
        Assert.Null(c);
        Assert.StartsWith(code, reason);
    }

    [Fact]
    public void Trade_without_quantity_is_rejected()
    {
        var (c, reason, _) = Run(Buy(r => r.Shares = "0"));
        Assert.Null(c);
        Assert.StartsWith("R11", reason);
    }

    [Fact]
    public void Duplicate_broker_ids_in_a_batch_are_rejected_after_first()
    {
        var res = new TransactionCleanser().Cleanse(new[] { Buy(), Buy(r => r.SourceRow = 3) });
        Assert.Single(res.Clean);
        Assert.Single(res.Rejected);
        Assert.StartsWith("DUPLICATE_KEY", res.Rejected[0].Reason);
    }

    [Fact]
    public void Split_fills_with_distinct_ids_are_kept()
    {
        var res = new TransactionCleanser().Cleanse(new[] { Buy(), Buy(r => { r.SourceRow = 3; r.BrokerId = "EOF738679571"; }) });
        Assert.Equal(2, res.Clean.Count);
        Assert.Contains(res.Findings, f => f.RuleCode == "PARTIAL_FILL_PAIR");
    }

    [Theory]
    [InlineData("US0378331005", true)]   // Apple
    [InlineData("US9256521090", true)]   // VICI
    [InlineData("US0378331006", false)]
    [InlineData("XX123", false)]
    public void Isin_check_digit(string isin, bool valid) => Assert.Equal(valid, ValueParsers.IsValidIsin(isin));

    [Fact]
    public void Time_only_string_is_not_mistaken_for_a_full_timestamp()
    {
        var ts = ValueParsers.CombineTimestamp("2020-04-30", "17:20:49");
        Assert.Equal(new DateTime(2020, 4, 30, 17, 20, 49), ts);
    }
}
