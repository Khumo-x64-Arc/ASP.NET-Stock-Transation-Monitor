using Microsoft.AspNetCore.Mvc;

namespace StockMonitor.Web.Controllers;

// Pages are thin shells; data arrives from /api/* and the SignalR hub so the same endpoints serve any client.

public sealed class HomeController : Controller
{
    [HttpGet("/")]
    public IActionResult Index() { ViewData["Title"] = "Dashboard"; ViewData["Nav"] = "dashboard"; return View(); }
}

public sealed class AnalyticsController : Controller
{
    [HttpGet("/analytics")]
    public IActionResult Index() { ViewData["Title"] = "Analytics"; ViewData["Nav"] = "analytics"; return View(); }
}

public sealed class PortfolioController : Controller
{
    [HttpGet("/portfolio")]
    public IActionResult Index() { ViewData["Title"] = "Portfolio"; ViewData["Nav"] = "portfolio"; return View(); }
}
