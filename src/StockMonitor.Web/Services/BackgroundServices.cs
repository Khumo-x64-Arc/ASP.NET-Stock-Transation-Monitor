using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StockMonitor.Core.Model;
using StockMonitor.Data;
using StockMonitor.Data.Entities;
using StockMonitor.Web.Hubs;
using StockMonitor.Web.Models;

namespace StockMonitor.Web.Services;

/// <summary>Shared pause/resume switch for the replay stream (toggled from the dashboard).</summary>
public sealed class ReplayControl
{
    public ReplayControl(IOptions<MonitorOptions> o) => Paused = o.Value.Replay.StartPaused;
    public volatile bool Paused;
    public int Released;
}

/// <summary>
/// Streams the transactions the ETL held back (stg.ReplayQueue, dated after the cutoff) through the live
/// ingestion path at a fixed cadence - this is the "real-time" feed for the demo. In production the same
/// IngestionService is fed by POST /api/transactions (broker webhook / message consumer) instead.
/// </summary>
public sealed class ReplayService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ReplayControl _control;
    private readonly IHubContext<MarketHub> _hub;
    private readonly MonitorOptions.ReplayOptions _o;
    private readonly ILogger<ReplayService> _log;

    public ReplayService(IServiceScopeFactory scopes, ReplayControl control, IHubContext<MarketHub> hub,
        IOptions<MonitorOptions> o, ILogger<ReplayService> log)
    {
        _scopes = scopes; _control = control; _hub = hub; _o = o.Value.Replay; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_o.Enabled) { _log.LogInformation("Replay disabled"); return; }
        await Task.Delay(TimeSpan.FromSeconds(5), ct);   // let the app warm up

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_control.Paused && await ReleaseNextAsync(ct))
                    _control.Released++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "Replay step failed - will retry");
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0.5, _o.IntervalSeconds)), ct);
        }
    }

    private async Task<bool> ReleaseNextAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockMonitorDbContext>();
        var next = await db.ReplayQueue.Where(r => r.ReleasedAt == null)
            .OrderBy(r => r.TxTimestamp).ThenBy(r => r.ReplayId).FirstOrDefaultAsync(ct);
        if (next is null) return false;

        var clean = JsonSerializer.Deserialize<CleanTransaction>(next.Payload)!;
        var ingest = scope.ServiceProvider.GetRequiredService<IngestionService>();
        var result = await ingest.IngestCleanAsync(clean, null, ct);

        next.ReleasedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        var pending = await db.ReplayQueue.CountAsync(r => r.ReleasedAt == null, ct);
        await _hub.Clients.All.SendAsync("replay", new { paused = _control.Paused, pending, lastKey = result.TransactionKey }, ct);
        return true;
    }
}

// ------------------------------------------------------------------------------------------------------------

/// <summary>Source of live prices. Swap the simulated implementation for a vendor feed without touching consumers.</summary>
public interface IMarketDataProvider
{
    string Name { get; }
    decimal NextPrice(string ticker, decimal current, TimeSpan elapsed);
}

/// <summary>Geometric Brownian motion around the last traded price - deterministic per ticker seed, no external calls.</summary>
public sealed class SimulatedMarketDataProvider : IMarketDataProvider
{
    private readonly MonitorOptions.PriceOptions _o;
    private readonly Random _rng = new(20240701);
    private readonly object _lock = new();

    public SimulatedMarketDataProvider(IOptions<MonitorOptions> o) => _o = o.Value.Prices;
    public string Name => "SIMULATED";

    public decimal NextPrice(string ticker, decimal current, TimeSpan elapsed)
    {
        // accelerate time so moves are visible on screen; 252 trading days * 6.5h
        var years = elapsed.TotalSeconds * _o.TimeAcceleration / (252 * 6.5 * 3600);
        double z;
        lock (_lock)
        {
            // Box-Muller
            var u1 = 1.0 - _rng.NextDouble();
            var u2 = _rng.NextDouble();
            z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
        }
        var sigma = _o.AnnualVolatility;
        const double mu = 0.07;
        var factor = Math.Exp((mu - 0.5 * sigma * sigma) * years + sigma * Math.Sqrt(years) * z);
        return Math.Round(current * (decimal)factor, 6);
    }
}

/// <summary>Ticks prices for held securities, persists them to rt.*, and pushes them to clients.</summary>
public sealed class PriceFeedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IMarketDataProvider _provider;
    private readonly IHubContext<MarketHub> _hub;
    private readonly MonitorOptions _o;
    private readonly ILogger<PriceFeedService> _log;
    private readonly HashSet<(int, DateOnly)> _moveAlerted = new();
    private DateTime _lastPurge = DateTime.MinValue;

    public PriceFeedService(IServiceScopeFactory scopes, IMarketDataProvider provider, IHubContext<MarketHub> hub,
        IOptions<MonitorOptions> o, ILogger<PriceFeedService> log)
    {
        _scopes = scopes; _provider = provider; _hub = hub; _o = o.Value; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_o.Prices.Enabled) return;
        var interval = TimeSpan.FromSeconds(Math.Max(1, _o.Prices.TickSeconds));
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(ct))
        {
            try { await TickAsync(interval, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogError(ex, "Price tick failed"); }
        }
    }

    private async Task TickAsync(TimeSpan elapsed, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockMonitorDbContext>();

        var held = await db.Positions.Where(p => p.Quantity > 0).Select(p => p.SecurityKey).Distinct().ToListAsync(ct);
        if (held.Count == 0) return;

        var prices = await db.Prices.Where(p => held.Contains(p.SecurityKey)).ToDictionaryAsync(p => p.SecurityKey, ct);
        var tickers = await db.Securities.Where(s => held.Contains(s.SecurityKey)).ToDictionaryAsync(s => s.SecurityKey, s => s.Ticker, ct);
        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);
        var rows = new List<PriceRow>();

        foreach (var key in held)
        {
            if (!prices.TryGetValue(key, out var px))
            {
                // position created by a corporate action (e.g. a spin-off) has never traded: seed from average cost
                var avg = await db.Positions.Where(p => p.SecurityKey == key && p.Quantity > 0)
                    .Select(p => p.AvgCostGbp).FirstOrDefaultAsync(ct) ?? 1m;
                px = new SecurityPrice { SecurityKey = key, PriceGbp = avg, PrevCloseGbp = avg, Source = "SEED", UpdatedAt = now };
                db.Prices.Add(px);
            }

            if (DateOnly.FromDateTime(px.UpdatedAt) < today) px.PrevCloseGbp = px.PriceGbp;   // new trading day

            px.PriceGbp = _provider.NextPrice(tickers.GetValueOrDefault(key, "?"), px.PriceGbp, elapsed);
            px.Source = _provider.Name;
            px.UpdatedAt = now;
            db.PriceTicks.Add(new PriceTick { SecurityKey = key, PriceGbp = px.PriceGbp, TickTime = now });

            var chg = px.PrevCloseGbp == 0 ? 0 : (px.PriceGbp - px.PrevCloseGbp) / px.PrevCloseGbp * 100;
            rows.Add(new PriceRow(key, tickers.GetValueOrDefault(key, "?"), Math.Round(px.PriceGbp, 4), Math.Round(chg, 2), now));

            if (Math.Abs(chg) >= _o.Alerts.PriceMovePct && _moveAlerted.Add((key, today)))
            {
                var alerts = scope.ServiceProvider.GetRequiredService<AlertService>();
                var a = await alerts.RaiseAsync("INFO", "PRICE_MOVE", key,
                    $"{tickers.GetValueOrDefault(key)} moved {chg:+0.0;-0.0}% vs previous close", ct);
                await _hub.Clients.All.SendAsync("alert", a, ct);
            }
        }
        await db.SaveChangesAsync(ct);
        await _hub.Clients.All.SendAsync("prices", rows, ct);

        if (now - _lastPurge > TimeSpan.FromHours(1))
        {
            await db.Database.ExecuteSqlRawAsync("EXEC rt.usp_PurgeRealtime;", ct);
            _lastPurge = now;
        }
    }
}

/// <summary>Writes a portfolio valuation point at a fixed interval (intraday chart) and pushes KPIs.</summary>
public sealed class SnapshotService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IHubContext<MarketHub> _hub;
    private readonly MonitorOptions.SnapshotOptions _o;
    private readonly ILogger<SnapshotService> _log;

    public SnapshotService(IServiceScopeFactory scopes, IHubContext<MarketHub> hub, IOptions<MonitorOptions> o, ILogger<SnapshotService> log)
    {
        _scopes = scopes; _hub = hub; _o = o.Value.Snapshots; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, _o.IntervalSeconds)));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<StockMonitorDbContext>();
                var holdings = await db.Holdings.AsNoTracking().ToListAsync(ct);
                var cash = await db.CashBalances.AsNoTracking().ToListAsync(ct);
                var now = DateTime.UtcNow;

                PortfolioSnapshot Snap(int? account) => new()
                {
                    SnapshotTime = now,
                    AccountKey = account,
                    MarketValueGbp = Math.Round(holdings.Where(h => account == null || h.AccountKey == account).Sum(h => h.MarketValueGbp ?? 0), 4),
                    CostBasisGbp = Math.Round(holdings.Where(h => account == null || h.AccountKey == account).Sum(h => h.CostBasisGbp), 4),
                    CashGbp = Math.Round(cash.Where(c => account == null || c.AccountKey == account).Sum(c => c.CashGbp), 4)
                };

                var all = Snap(null);
                db.Snapshots.Add(all);
                foreach (var a in cash.Select(c => c.AccountKey).Distinct()) db.Snapshots.Add(Snap(a));
                await db.SaveChangesAsync(ct);

                var queries = scope.ServiceProvider.GetRequiredService<PortfolioQueryService>();
                await _hub.Clients.All.SendAsync("snapshot", new { t = now, value = all.MarketValueGbp + all.CashGbp, mv = all.MarketValueGbp, cost = all.CostBasisGbp }, ct);
                await _hub.Clients.All.SendAsync("summary", await queries.GetSummaryAsync(null, ct), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "Snapshot failed");
            }
        }
    }
}
