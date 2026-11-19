using System.Data;
using Microsoft.Data.SqlClient;
using StockMonitor.Core.Model;

namespace StockMonitor.Data.Services;

/// <summary>
/// Thin ADO.NET layer for the high-volume / set-based parts of the pipeline:
/// SqlBulkCopy into staging and execution of the load procedure. Shared by the batch ETL and the live ingestion path.
/// </summary>
public sealed class WarehouseLoader
{
    private readonly string _cs;
    public WarehouseLoader(string connectionString) => _cs = connectionString;

    public async Task<int> StartBatchAsync(string batchType, string? sourceFile, string? fileHash, CancellationToken ct = default)
    {
        await using var cn = new SqlConnection(_cs);
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            "INSERT etl.BatchLog (BatchType, SourceFile, SourceFileHash) OUTPUT inserted.BatchId VALUES (@t, @f, @h);", cn);
        cmd.Parameters.AddWithValue("@t", batchType);
        cmd.Parameters.AddWithValue("@f", (object?)sourceFile ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@h", (object?)fileHash ?? DBNull.Value);
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<bool> FileAlreadyLoadedAsync(string fileHash, CancellationToken ct = default)
    {
        await using var cn = new SqlConnection(_cs);
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            "SELECT COUNT(*) FROM etl.BatchLog WHERE SourceFileHash = @h AND Status = 'SUCCEEDED';", cn);
        cmd.Parameters.AddWithValue("@h", fileHash);
        return (int)(await cmd.ExecuteScalarAsync(ct))! > 0;
    }

    public async Task CompleteBatchAsync(int batchId, string status, string? message,
        int? extracted = null, int? cleansed = null, int? rejected = null, int? queued = null, CancellationToken ct = default)
    {
        await using var cn = new SqlConnection(_cs);
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand(@"
UPDATE etl.BatchLog SET
    Status        = @s,
    Message       = @m,
    CompletedAt   = SYSUTCDATETIME(),
    RowsExtracted = ISNULL(@e, RowsExtracted),
    RowsCleansed  = ISNULL(@c, RowsCleansed),
    RowsRejected  = ISNULL(@r, RowsRejected),
    RowsQueued    = ISNULL(@q, RowsQueued)
WHERE BatchId = @id;", cn);
        cmd.Parameters.AddWithValue("@id", batchId);
        cmd.Parameters.AddWithValue("@s", status);
        cmd.Parameters.AddWithValue("@m", (object?)message ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@e", (object?)extracted ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@c", (object?)cleansed ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@r", (object?)rejected ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@q", (object?)queued ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // --------------------------------------------------------------------------------------------
    public async Task BulkInsertRawAsync(int batchId, IReadOnlyList<RawTransactionRow> rows, CancellationToken ct = default)
    {
        var t = new DataTable();
        string[] cols =
        {
            "BatchId", "SourceSheet", "SourceRow", "AccountNumber", "Action", "TxDate", "TxTime", "Isin", "Ticker",
            "SecurityName", "Shares", "PricePerShare", "PriceCurrency", "ExchangeRate", "Result", "Total", "TotalCurrency",
            "WithholdingTax", "WithholdingTaxCurrency", "ChargeAmount", "DepositFee", "TransactionFee", "FinraFee",
            "CurrencyConversionFee", "StampDutyReserveTax", "Notes", "BrokerId", "RowHash"
        };
        foreach (var c in cols)
            t.Columns.Add(c, c switch { "BatchId" or "SourceRow" => typeof(int), "RowHash" => typeof(byte[]), _ => typeof(string) });

        foreach (var r in rows)
        {
            var hash = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(string.Join('\u001f', r.Values().Select(v => v ?? ""))));
            static object S(string? v, int max = 50) => (object?)Cap(v, max) ?? DBNull.Value;
            t.Rows.Add(batchId, r.SourceSheet, r.SourceRow, S(r.AccountNumber, 20), S(r.Action, 200), S(r.TxDate), S(r.TxTime),
                S(r.Isin), S(r.Ticker), S(r.SecurityName, 200), S(r.Shares), S(r.PricePerShare), S(r.PriceCurrency, 10),
                S(r.ExchangeRate), S(r.Result), S(r.Total), S(r.TotalCurrency, 10), S(r.WithholdingTax),
                S(r.WithholdingTaxCurrency, 10), S(r.ChargeAmount, 400), S(r.DepositFee), S(r.TransactionFee), S(r.FinraFee),
                S(r.CurrencyConversionFee), S(r.StampDutyReserveTax), S(r.Notes, 1000), S(r.BrokerId, 100), hash);
        }
        await BulkCopyAsync("stg.RawTransaction", t, ct);
    }

    public async Task BulkInsertCleanAsync(int batchId, IEnumerable<CleanTransaction> rows, CancellationToken ct = default)
    {
        var t = CleanTable();
        foreach (var c in rows) AddClean(t, batchId, c);
        await BulkCopyAsync("stg.CleanTransaction", t, ct);
    }

    public async Task BulkInsertFindingsAsync(int? batchId, IEnumerable<DataQualityFinding> findings, CancellationToken ct = default)
    {
        var t = new DataTable();
        t.Columns.Add("BatchId", typeof(int));
        t.Columns.Add("Severity", typeof(string));
        t.Columns.Add("RuleCode", typeof(string));
        t.Columns.Add("TransactionKey", typeof(string));
        t.Columns.Add("SourceSheet", typeof(string));
        t.Columns.Add("SourceRow", typeof(int));
        t.Columns.Add("Detail", typeof(string));
        t.Columns.Add("CreatedAt", typeof(DateTime));
        var now = DateTime.UtcNow;
        foreach (var f in findings)
            t.Rows.Add((object?)batchId ?? DBNull.Value, f.Severity.ToString().ToUpperInvariant(), Cap(f.RuleCode, 50),
                (object?)f.TransactionKey ?? DBNull.Value, (object?)f.SourceSheet ?? DBNull.Value,
                (object?)f.SourceRow ?? DBNull.Value, Cap(f.Detail, 1000), now);
        if (t.Rows.Count > 0) await BulkCopyAsync("etl.DataQualityIssue", t, ct);
    }

    public async Task BulkInsertRejectsAsync(int batchId, IEnumerable<RejectedRow> rejects, CancellationToken ct = default)
    {
        var t = new DataTable();
        t.Columns.Add("BatchId", typeof(int));
        t.Columns.Add("SourceSheet", typeof(string));
        t.Columns.Add("SourceRow", typeof(int));
        t.Columns.Add("Reason", typeof(string));
        t.Columns.Add("RawPayload", typeof(string));
        foreach (var r in rejects)
            t.Rows.Add(batchId, r.Raw.SourceSheet, r.Raw.SourceRow, Cap(r.Reason, 400),
                System.Text.Json.JsonSerializer.Serialize(r.Raw));
        if (t.Rows.Count > 0) await BulkCopyAsync("etl.RejectedRow", t, ct);
    }

    public async Task BulkInsertReplayAsync(int batchId, IEnumerable<CleanTransaction> rows, CancellationToken ct = default)
    {
        var t = new DataTable();
        t.Columns.Add("BatchId", typeof(int));
        t.Columns.Add("TransactionKey", typeof(string));
        t.Columns.Add("TxTimestamp", typeof(DateTime));
        t.Columns.Add("Payload", typeof(string));
        foreach (var c in rows)
            t.Rows.Add(batchId, c.TransactionKey, c.TxTimestamp, System.Text.Json.JsonSerializer.Serialize(c));
        if (t.Rows.Count > 0) await BulkCopyAsync("stg.ReplayQueue", t, ct);
    }

    /// <summary>Runs etl.usp_LoadWarehouse (batch: all rows; live: a single TransactionKey). Returns rows inserted.</summary>
    public async Task<int> LoadWarehouseAsync(int batchId, string? transactionKey, string source, CancellationToken ct = default)
    {
        await using var cn = new SqlConnection(_cs);
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand("etl.usp_LoadWarehouse", cn) { CommandType = CommandType.StoredProcedure, CommandTimeout = 600 };
        cmd.Parameters.AddWithValue("@BatchId", batchId);
        cmd.Parameters.AddWithValue("@TransactionKey", (object?)transactionKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Source", source);
        var outp = cmd.Parameters.Add("@RowsLoaded", SqlDbType.Int);
        outp.Direction = ParameterDirection.Output;
        await cmd.ExecuteNonQueryAsync(ct);
        return outp.Value is int i ? i : 0;
    }

    public async Task<HashSet<string>> GetFactKeysAsync(CancellationToken ct = default)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        await using var cn = new SqlConnection(_cs);
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand("SELECT TransactionKey FROM dw.FactTransaction;", cn);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) keys.Add(rd.GetString(0));
        return keys;
    }

    public async Task ExecAsync(string sql, CancellationToken ct = default)
    {
        await using var cn = new SqlConnection(_cs);
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn) { CommandTimeout = 600 };
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // --------------------------------------------------------------------------------------------
    private async Task BulkCopyAsync(string table, DataTable data, CancellationToken ct)
    {
        await using var cn = new SqlConnection(_cs);
        await cn.OpenAsync(ct);
        using var bulk = new SqlBulkCopy(cn, SqlBulkCopyOptions.CheckConstraints | SqlBulkCopyOptions.TableLock, null)
        {
            DestinationTableName = table,
            BatchSize = 5000,
            BulkCopyTimeout = 600
        };
        foreach (DataColumn c in data.Columns) bulk.ColumnMappings.Add(c.ColumnName, c.ColumnName);
        await bulk.WriteToServerAsync(data, ct);
    }

    private static DataTable CleanTable()
    {
        var t = new DataTable();
        void Col(string n, Type ty) => t.Columns.Add(n, ty);
        Col("BatchId", typeof(int)); Col("TransactionKey", typeof(string)); Col("IsSyntheticKey", typeof(bool));
        Col("AccountNumber", typeof(int)); Col("TxTimestamp", typeof(DateTime)); Col("ActionCode", typeof(string));
        Col("ActionSubType", typeof(string)); Col("Ticker", typeof(string)); Col("Isin", typeof(string));
        Col("SecurityName", typeof(string)); Col("Shares", typeof(decimal)); Col("PriceLocal", typeof(decimal));
        Col("PriceCurrency", typeof(string)); Col("FxRate", typeof(decimal)); Col("PriceGbp", typeof(decimal));
        Col("TotalGbp", typeof(decimal)); Col("BrokerResultGbp", typeof(decimal)); Col("WithholdingTax", typeof(decimal));
        Col("WithholdingTaxCurrency", typeof(string)); Col("FxFeeGbp", typeof(decimal)); Col("StampDutyGbp", typeof(decimal));
        Col("TransactionFeeGbp", typeof(decimal)); Col("FinraFeeGbp", typeof(decimal)); Col("DepositFeeGbp", typeof(decimal));
        Col("Notes", typeof(string)); Col("IsCorporateActionArtifact", typeof(bool)); Col("SourceSheet", typeof(string));
        Col("SourceRow", typeof(int));
        return t;
    }

    private static void AddClean(DataTable t, int batchId, CleanTransaction c)
    {
        static object N(object? v) => v ?? DBNull.Value;
        // round to the column scale - SqlBulkCopy rejects decimals whose scale exceeds the target
        static object R(decimal? v, int scale) => v is null ? DBNull.Value : Math.Round(v.Value, scale);
        t.Rows.Add(batchId, c.TransactionKey, c.IsSyntheticKey, c.AccountNumber, c.TxTimestamp, c.ActionCode,
            N(c.ActionSubType), N(c.Ticker), N(c.Isin), N(Cap(c.SecurityName, 200)), R(c.Shares, 10), R(c.PriceLocal, 6),
            N(c.PriceCurrency), R(c.FxRate, 8), R(c.PriceGbp, 6), Math.Round(c.TotalGbp, 4), R(c.BrokerResultGbp, 4),
            R(c.WithholdingTax, 4), N(c.WithholdingTaxCurrency), R(c.FxFeeGbp, 4), R(c.StampDutyGbp, 4),
            R(c.TransactionFeeGbp, 4), R(c.FinraFeeGbp, 4), R(c.DepositFeeGbp, 4), N(Cap(c.Notes, 1000)), c.IsCorporateActionArtifact, N(c.SourceSheet), N(c.SourceRow));
    }

    private static string? Cap(string? s, int n) => s is null || s.Length <= n ? s : s[..n];
}
