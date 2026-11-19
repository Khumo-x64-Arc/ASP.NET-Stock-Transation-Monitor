using StockMonitor.Core.Portfolio;
using Xunit;

namespace StockMonitor.Tests;

public class PositionEngineTests
{
    private static long _id;
    private static EngineTransaction T(string date, string action, string ticker, decimal shares, decimal total, int account = 1, bool artifact = false) =>
        new(++_id, $"K{_id}", account, ticker, DateTime.Parse(date), action, shares, total, null, artifact);

    [Fact]
    public void Average_cost_pool_and_realised_gain()
    {
        var r = new PositionEngine().Run(new[]
        {
            T("2021-01-01", "BUY", "AAA", 10, 100),
            T("2021-02-01", "BUY", "AAA", 10, 300),     // pool: 20 sh, £400, avg £20
            T("2021-03-01", "SELL", "AAA", 5, 150),     // cost 5*20 = 100, gain 50
        }, Array.Empty<CorporateActionInfo>());

        var p = r.Positions[(1, "AAA")];
        Assert.Equal(15m, p.Quantity);
        Assert.Equal(300m, p.CostBasisGbp);
        Assert.Equal(50m, Assert.Single(r.Realized).GainGbp);
    }

    [Fact]
    public void Split_scales_quantity_but_not_cost()
    {
        var r = new PositionEngine().Run(
            new[] { T("2020-07-01", "BUY", "AAPL", 2, 600), T("2021-02-17", "BUY", "AAPL", 20, 2000) },
            new[] { new CorporateActionInfo(new DateTime(2020, 8, 31), "SPLIT", "AAPL", 4, null, 0) });
        var p = r.Positions[(1, "AAPL")];
        Assert.Equal(28m, p.Quantity);
        Assert.Equal(2600m, p.CostBasisGbp);
    }

    [Fact]
    public void Merger_rolls_cost_into_new_line_and_ignores_zero_value_artifact_sell()
    {
        var r = new PositionEngine().Run(new[]
        {
            T("2020-05-01", "BUY", "PBCT", 100, 1500),
            T("2022-04-05", "SELL", "PBCT", 100, 0, artifact: true),
            T("2022-04-11", "SELL", "MTB", 5, 600),
        }, new[] { new CorporateActionInfo(new DateTime(2022, 4, 1), "MERGER", "PBCT", 0.118m, "MTB", 1m) });

        Assert.Equal(0m, r.Positions[(1, "PBCT")].Quantity);
        var mtb = r.Positions[(1, "MTB")];
        Assert.Equal(11.8m - 5m, mtb.Quantity);
        var g = Assert.Single(r.Realized);
        Assert.Equal(Math.Round(600m - 1500m * 5m / 11.8m, 4), g.GainGbp);
        Assert.Empty(r.Findings);
    }

    [Fact]
    public void Spinoff_moves_allocated_cost()
    {
        var r = new PositionEngine().Run(new[] { T("2021-01-01", "BUY", "T", 100, 2000) },
            new[] { new CorporateActionInfo(new DateTime(2022, 4, 11), "SPINOFF", "T", 0.25m, "WBD", 0.2m) });
        Assert.Equal(1600m, r.Positions[(1, "T")].CostBasisGbp);
        Assert.Equal(25m, r.Positions[(1, "WBD")].Quantity);
        Assert.Equal(400m, r.Positions[(1, "WBD")].CostBasisGbp);
    }

    [Fact]
    public void Oversell_is_clamped_and_reported()
    {
        var r = new PositionEngine().Run(new[] { T("2024-01-01", "BUY", "HD", 2, 500), T("2024-05-22", "SELL", "HD", 3, 780) },
            Array.Empty<CorporateActionInfo>());
        Assert.Equal(0m, r.Positions[(1, "HD")].Quantity);
        Assert.Contains(r.Findings, f => f.RuleCode == "ENGINE_OVERSELL_CLAMPED");
        Assert.Equal(280m, r.Realized[0].GainGbp);   // missing share treated as zero cost
    }

    [Fact]
    public void Accounts_are_pooled_separately()
    {
        var r = new PositionEngine().Run(new[]
        {
            T("2021-01-01", "BUY", "X", 1, 100, account: 1),
            T("2021-01-01", "BUY", "X", 1, 200, account: 2),
        }, Array.Empty<CorporateActionInfo>());
        Assert.Equal(100m, r.Positions[(1, "X")].CostBasisGbp);
        Assert.Equal(200m, r.Positions[(2, "X")].CostBasisGbp);
    }
}
