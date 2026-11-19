using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using StockMonitor.Data;
using StockMonitor.Web.Hubs;
using StockMonitor.Web.Models;
using StockMonitor.Web.Services;

namespace StockMonitor.Web.Controllers.Api;

[ApiController]
[Route("api/dashboard")]
public sealed class DashboardApiController : ControllerBase
{
    private readonly PortfolioQueryService _q;
    public DashboardApiController(PortfolioQueryService q) => _q = q;

    [HttpGet("summary")]   public async Task<IActionResult> Summary(int? account, CancellationToken ct) => Ok(await _q.GetSummaryAsync(account, ct));
    [HttpGet("recent")]    public async Task<IActionResult> Recent(int take = 25, CancellationToken ct = default) => Ok(await _q.GetRecentTransactionsAsync(Math.Clamp(take, 1, 200), ct));
    [HttpGet("alerts")]    public async Task<IActionResult> Alerts(int take = 20, CancellationToken ct = default) => Ok(await _q.GetAlertsAsync(Math.Clamp(take, 1, 200), ct));
    [HttpGet("prices")]    public async Task<IActionResult> Prices(CancellationToken ct) => Ok(await _q.GetPricesAsync(ct));
    [HttpGet("snapshots")] public async Task<IActionResult> Snapshots(int? account, int hours = 2, CancellationToken ct = default) => Ok(await _q.GetSnapshotsAsync(account, Math.Clamp(hours, 1, 72), ct));
    [HttpGet("/api/accounts")] public async Task<IActionResult> Accounts(CancellationToken ct) => Ok(await _q.GetAccountsAsync(ct));
}

[ApiController]
[Route("api/portfolio")]
public sealed class PortfolioApiController : ControllerBase
{
    private readonly PortfolioQueryService _q;
    public PortfolioApiController(PortfolioQueryService q) => _q = q;

    [HttpGet("holdings")]               public async Task<IActionResult> Holdings(int? account, CancellationToken ct) => Ok(await _q.GetHoldingsAsync(account, ct));
    [HttpGet("closed")]                 public async Task<IActionResult> Closed(int? account, CancellationToken ct) => Ok(await _q.GetClosedPositionsAsync(account, ct));
    [HttpGet("security/{securityKey:int}")] public async Task<IActionResult> Security(int securityKey, CancellationToken ct) => Ok(await _q.GetSecurityDetailAsync(securityKey, ct));
    [HttpGet("corporate-actions")]      public async Task<IActionResult> CorporateActions(CancellationToken ct) => Ok(await _q.GetCorporateActionsAsync(ct));
}

[ApiController]
[Route("api/analytics")]
public sealed class AnalyticsApiController : ControllerBase
{
    private readonly AnalyticsQueryService _q;
    public AnalyticsApiController(AnalyticsQueryService q) => _q = q;

    [HttpGet("cashflow")]   public async Task<IActionResult> CashFlow(int? account, string? from, string? to, CancellationToken ct) => Ok(await _q.GetCashFlowAsync(account, from, to, ct));
    [HttpGet("dividends")]  public async Task<IActionResult> Dividends(int? account, int take = 12, CancellationToken ct = default) => Ok(await _q.GetTopDividendPayersAsync(account, Math.Clamp(take, 1, 100), ct));
    [HttpGet("realized")]   public async Task<IActionResult> Realized(CancellationToken ct) => Ok(await _q.GetRealizedByTaxYearAsync(ct));
    [HttpGet("isa")]        public async Task<IActionResult> Isa(CancellationToken ct) => Ok(await _q.GetIsaAllowanceAsync(ct));
    [HttpGet("mix")]        public async Task<IActionResult> Mix(int? account, CancellationToken ct) => Ok(await _q.GetActionMixAsync(account, ct));
    [HttpGet("fees")]       public async Task<IActionResult> Fees(int? account, CancellationToken ct) => Ok(await _q.GetFeesAndTaxesAsync(account, ct));
    [HttpGet("allocation")] public async Task<IActionResult> Allocation(CancellationToken ct) => Ok(await _q.GetAllocationAsync(ct));
    [HttpGet("pipeline")]   public async Task<IActionResult> Pipeline(CancellationToken ct) => Ok(await _q.GetPipelineAsync(ct));
}

/// <summary>Write side of the real-time pipeline.</summary>
[ApiController]
[Route("api")]
public sealed class IngestApiController : ControllerBase
{
    private readonly IngestionService _ingest;
    private readonly AlertService _alerts;
    private readonly ReplayControl _replay;
    private readonly StockMonitorDbContext _db;
    private readonly IHubContext<MarketHub> _hub;

    public IngestApiController(IngestionService ingest, AlertService alerts, ReplayControl replay, StockMonitorDbContext db, IHubContext<MarketHub> hub)
    {
        _ingest = ingest; _alerts = alerts; _replay = replay; _db = db; _hub = hub;
    }

    /// <summary>Ingest one transaction (broker webhook / upstream feed). Idempotent on BrokerId.</summary>
    [HttpPost("transactions")]
    [ProducesResponseType(typeof(IngestResult), 201)]
    [ProducesResponseType(typeof(IngestResult), 200)]
    [ProducesResponseType(typeof(IngestResult), 422)]
    public async Task<IActionResult> Post([FromBody] LiveTransactionRequest request, CancellationToken ct)
    {
        var r = await _ingest.IngestRequestAsync(request, ct);
        if (!r.Accepted) return UnprocessableEntity(r);
        return r.Duplicate ? Ok(r) : StatusCode(201, r);
    }

    [HttpPost("alerts/{id:long}/ack")]
    public async Task<IActionResult> Ack(long id, CancellationToken ct) => await _alerts.AcknowledgeAsync(id, ct) ? NoContent() : NotFound();

    [HttpGet("replay")]
    public async Task<IActionResult> ReplayStatus(CancellationToken ct) =>
        Ok(new { paused = _replay.Paused, released = _replay.Released, pending = await _db.ReplayQueue.CountAsync(r => r.ReleasedAt == null, ct) });

    [HttpPost("replay/{op:regex(^(pause|resume)$)}")]
    public async Task<IActionResult> ReplayToggle(string op, CancellationToken ct)
    {
        _replay.Paused = op == "pause";
        var pending = await _db.ReplayQueue.CountAsync(r => r.ReleasedAt == null, ct);
        await _hub.Clients.All.SendAsync("replay", new { paused = _replay.Paused, pending }, ct);
        return Ok(new { paused = _replay.Paused, pending });
    }
}
