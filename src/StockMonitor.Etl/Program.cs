using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using StockMonitor.Data;
using StockMonitor.Data.Services;
using StockMonitor.Etl.Extract;
using StockMonitor.Etl.Pipeline;

// Usage:
//   dotnet run --project src/StockMonitor.Etl -- [--file <xlsx>] [--cutoff yyyy-MM-dd|none] [--reset] [--force] [--dry-run]
//   --cutoff   rows on/after this date go to stg.ReplayQueue and are streamed live by the web app (default from appsettings)
//   --reset    wipe facts/positions/real-time tables first (reference data kept)
//   --force    reload even if this exact file (SHA-256) was already loaded
//   --dry-run  extract + cleanse + profile only; no database access
//   --init-db  apply database/*.sql first (idempotent; no sqlcmd needed) - used by the Docker/Render deploy
//   --init-only  apply the scripts and exit

var flags = new HashSet<string>(args.Where(a => a is "--reset" or "--force" or "--dry-run" or "--init-db" or "--init-only"));
var kv = args.Where(a => !flags.Contains(a)).ToArray();

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddEnvironmentVariables(prefix: "STOCKMONITOR_")
    .AddCommandLine(kv, new Dictionary<string, string> { ["--file"] = "Etl:SourceFile", ["--cutoff"] = "Etl:ReplayCutoff", ["--scripts"] = "Etl:ScriptsDir" })
    .Build();

using var loggerFactory = LoggerFactory.Create(b => b
    .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
    .SetMinimumLevel(LogLevel.Information)
    .AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning));

var cs = config.GetConnectionString("StockMonitor") ?? throw new InvalidOperationException("ConnectionStrings:StockMonitor missing");

var cutoffText = config["Etl:ReplayCutoff"];
DateTime? cutoff = string.IsNullOrWhiteSpace(cutoffText) || cutoffText.Equals("none", StringComparison.OrdinalIgnoreCase)
    ? null
    : DateTime.ParseExact(cutoffText, "yyyy-MM-dd", CultureInfo.InvariantCulture);

var sourceFile = config["Etl:SourceFile"] ?? "";
if (!Path.IsPathRooted(sourceFile))
{
    // resolve relative to the current directory first, then to the project folder
    var candidates = new[] { Path.GetFullPath(sourceFile), Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../", sourceFile)) };
    sourceFile = candidates.FirstOrDefault(File.Exists) ?? candidates[0];
}

var options = new EtlOptions
{
    SourceFile = sourceFile,
    ReplayCutoff = cutoff,
    Sheets = config.GetSection("Etl:Sheets").Get<List<SheetConfig>>() ?? new(),
    Force = flags.Contains("--force"),
    Reset = flags.Contains("--reset"),
    DryRun = flags.Contains("--dry-run"),
};

PositionRebuildService RebuildFactory()
{
    var db = new StockMonitorDbContext(new DbContextOptionsBuilder<StockMonitorDbContext>()
        .UseSqlServer(cs, sql => sql.CommandTimeout(600))
        .UseLoggerFactory(loggerFactory)
        .Options);
    return new PositionRebuildService(db, loggerFactory.CreateLogger<PositionRebuildService>());
}

if (flags.Contains("--init-db") || flags.Contains("--init-only"))
{
    var dirs = new[]
    {
        config["Etl:ScriptsDir"],
        Path.Combine(Directory.GetCurrentDirectory(), "database"),
        Path.Combine(AppContext.BaseDirectory, "database"),
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../database")),
    }.Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => d!).ToList();
    var scriptsDir = dirs.FirstOrDefault(Directory.Exists)
        ?? throw new DirectoryNotFoundException("database/ scripts not found; pass --scripts <dir>");
    await new DatabaseInitializer(cs, loggerFactory.CreateLogger("DbInit")).RunAsync(scriptsDir);
    if (flags.Contains("--init-only")) return 0;
}

var pipeline = new EtlPipeline(cs, RebuildFactory, loggerFactory.CreateLogger<EtlPipeline>());
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
return await pipeline.RunAsync(options, cts.Token);
