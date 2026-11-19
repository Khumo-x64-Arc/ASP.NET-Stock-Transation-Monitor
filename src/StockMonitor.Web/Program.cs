using Microsoft.EntityFrameworkCore;
using StockMonitor.Data;
using StockMonitor.Data.Services;
using StockMonitor.Web.Hubs;
using StockMonitor.Web.Models;
using StockMonitor.Web.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables(prefix: "STOCKMONITOR_");

// PaaS hosts (Render, Heroku-style) tell the app which port to bind via $PORT.
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(port)) builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var cs = builder.Configuration.GetConnectionString("StockMonitor")
         ?? throw new InvalidOperationException("ConnectionStrings:StockMonitor is not configured");

builder.Services.Configure<MonitorOptions>(builder.Configuration.GetSection("Monitor"));

// data
builder.Services.AddDbContext<StockMonitorDbContext>(o => o.UseSqlServer(cs));
builder.Services.AddSingleton(new WarehouseLoader(cs));
builder.Services.AddScoped<PositionRebuildService>();

// application services
builder.Services.AddScoped<PortfolioQueryService>();
builder.Services.AddScoped<AnalyticsQueryService>();
builder.Services.AddScoped<AlertService>();
builder.Services.AddScoped<IngestionService>();
builder.Services.AddSingleton<ReplayControl>();
builder.Services.AddSingleton<IMarketDataProvider, SimulatedMarketDataProvider>();

// real-time workers
builder.Services.AddHostedService<ReplayService>();
builder.Services.AddHostedService<PriceFeedService>();
builder.Services.AddHostedService<SnapshotService>();

builder.Services.AddSignalR();
builder.Services.AddControllersWithViews()
    .AddJsonOptions(o => o.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);
builder.Services.AddHealthChecks().AddDbContextCheck<StockMonitorDbContext>("sql");

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
    app.UseHsts();
}

app.UseStaticFiles();
app.UseRouting();

app.MapControllers();
app.MapHub<MarketHub>("/hubs/market");
app.MapHealthChecks("/health");                       // readiness: includes SQL Server
app.MapGet("/healthz", () => Results.Ok("ok"));          // liveness: used by Render so a cold DB doesn't fail the deploy
app.Map("/error", () => Results.Problem("An unexpected error occurred."));

app.Run();
