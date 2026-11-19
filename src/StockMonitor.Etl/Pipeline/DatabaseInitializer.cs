using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace StockMonitor.Etl.Pipeline;

/// <summary>
/// Applies database/*.sql without sqlcmd (useful in containers / PaaS where sqlcmd isn't installed).
/// Scripts are split on "GO" lines and executed in order on one connection, so "USE StockMonitor" carries over.
///  - SQL Server (on-prem / container): connects to master, runs every script (01 creates the database).
///  - Azure SQL Database: CREATE DATABASE / USE / ALTER DATABASE SET RECOVERY aren't supported there, so
///    01_create_database.sql is replaced by the schema creation only and "USE" batches are skipped.
///    Create the empty database in the Azure portal first.
/// All scripts are idempotent, so running this on every deploy is safe.
/// </summary>
public sealed class DatabaseInitializer
{
    private static readonly Regex GoSplitter = new(@"^\s*GO\s*;?\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
    private readonly string _cs;
    private readonly ILogger _log;

    public DatabaseInitializer(string connectionString, ILogger log) { _cs = connectionString; _log = log; }

    public async Task RunAsync(string scriptsDir, CancellationToken ct = default)
    {
        var files = Directory.GetFiles(scriptsDir, "*.sql").OrderBy(f => f, StringComparer.Ordinal).ToList();
        if (files.Count == 0) throw new DirectoryNotFoundException($"No .sql files in {Path.GetFullPath(scriptsDir)}");

        var target = new SqlConnectionStringBuilder(_cs);
        var dbName = string.IsNullOrEmpty(target.InitialCatalog) ? "StockMonitor" : target.InitialCatalog;

        bool isAzure = await IsAzureSqlDatabaseAsync(ct);
        _log.LogInformation("Initialising database '{Db}' on {Kind}", dbName, isAzure ? "Azure SQL Database" : "SQL Server");

        // SQL Server: start in master so 01 can create the database. Azure: work inside the target database.
        var cb = new SqlConnectionStringBuilder(_cs);
        if (!isAzure) cb.InitialCatalog = "master";
        await using var cn = new SqlConnection(cb.ConnectionString);
        await cn.OpenAsync(ct);

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            var sql = (await File.ReadAllTextAsync(file, ct)).Replace("StockMonitor", dbName, StringComparison.Ordinal);

            if (isAzure && name.StartsWith("01_", StringComparison.Ordinal))
                sql = AzureSchemaOnly;

            int n = 0;
            // Azure SQL Database can't switch databases - drop "USE x;" lines (they sit after the header comments)
            if (isAzure)
                sql = Regex.Replace(sql, @"^\s*USE\s+\[?\w+\]?\s*;?\s*$", "", RegexOptions.Multiline | RegexOptions.IgnoreCase);

            foreach (var batch in GoSplitter.Split(sql).Select(b => b.Trim()).Where(b => b.Length > 0))
            {
                if (IsCommentOnly(batch)) continue;
                await using var cmd = new SqlCommand(batch, cn) { CommandTimeout = 600 };
                await cmd.ExecuteNonQueryAsync(ct);
                n++;
            }
            _log.LogInformation("  {File}: {Batches} batches", name, n);
        }
        _log.LogInformation("Database ready.");
    }

    private static bool IsCommentOnly(string batch) =>
        Regex.Replace(Regex.Replace(batch, @"/\*.*?\*/", "", RegexOptions.Singleline), @"--[^\n]*", "").Trim().Length == 0;

    private async Task<bool> IsAzureSqlDatabaseAsync(CancellationToken ct)
    {
        // Connect to whatever the connection string names; master always exists on SQL Server,
        // and the target database must already exist on Azure.
        var cb = new SqlConnectionStringBuilder(_cs);
        try
        {
            await using var cn = new SqlConnection(cb.ConnectionString);
            await cn.OpenAsync(ct);
            return await EngineIsAzure(cn, ct);
        }
        catch (SqlException)
        {
            cb.InitialCatalog = "master";               // target DB doesn't exist yet (fresh SQL Server)
            await using var cn = new SqlConnection(cb.ConnectionString);
            await cn.OpenAsync(ct);
            return await EngineIsAzure(cn, ct);
        }
    }

    private static async Task<bool> EngineIsAzure(SqlConnection cn, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("SELECT CAST(SERVERPROPERTY('EngineEdition') AS int);", cn);
        return (int)(await cmd.ExecuteScalarAsync(ct))! == 5;   // 5 = Azure SQL Database
    }

    private const string AzureSchemaOnly = @"
IF SCHEMA_ID(N'stg') IS NULL EXEC (N'CREATE SCHEMA stg AUTHORIZATION dbo;');
IF SCHEMA_ID(N'etl') IS NULL EXEC (N'CREATE SCHEMA etl AUTHORIZATION dbo;');
IF SCHEMA_ID(N'dw')  IS NULL EXEC (N'CREATE SCHEMA dw  AUTHORIZATION dbo;');
IF SCHEMA_ID(N'rt')  IS NULL EXEC (N'CREATE SCHEMA rt  AUTHORIZATION dbo;');";
}
