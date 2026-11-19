using Microsoft.AspNetCore.SignalR;

namespace StockMonitor.Web.Hubs;

/// <summary>
/// Server -> client push channel. Events:
///   "transaction" (FeedRow)            a transaction was ingested (API or replay)
///   "prices"      (PriceRow[])         a price tick batch
///   "summary"     (PortfolioSummary)   KPIs recalculated (all accounts)
///   "snapshot"    ({time, value})      portfolio value point for the intraday chart
///   "alert"       (AlertRow)           a rule fired
///   "replay"      ({paused, pending})  replay state changed
/// Clients only listen; all writes go through the REST API so they are validated and audited.
/// </summary>
public sealed class MarketHub : Hub
{
}
