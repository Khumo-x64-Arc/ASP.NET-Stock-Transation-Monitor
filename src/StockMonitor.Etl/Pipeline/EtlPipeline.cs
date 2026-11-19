using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using StockMonitor.Core.Cleansing;
using StockMonitor.Core.Model;
using StockMonitor.Data.Services;
using StockMonitor.Etl.Extract;

namespace StockMonitor.Etl.Pipeline;

public sealed class EtlOptions
{
    public string SourceFile { get; set; } = "";
    public DateTime? ReplayCutoff { get; set; }
    public List<SheetConfig> Sheets { get; set; } = new();
    public bool Force { get; set; }
    public bool Reset { get; set; }
    public bool DryRun { get; set; }
}

/// <summary>
/// Orchestrates: Extract -> Land (raw) -> Cleanse/Validate -> Stage (clean | replay queue | rejects)
///            -> Load (usp_LoadWarehouse) -> Derive (positions, realised gains) -> Reconcile -> Close batch.
/// </summary>
public sealed class EtlPipeline
{
    private readonly string _cs;
    private readonly WarehouseLoader _loader;
    private readonly Func<PositionRebuildService> _rebuildFactory;
    private readonly ILogger<EtlPipeline> _log;

    public EtlPipeline(string connectionString, Func<PositionRebuildService> rebuildFactory, ILogger<EtlPipeline> log)
    {
        _cs = connectionString;
        _loader = new WarehouseLoader(connectionString);
        _rebuildFactory = rebuildFactory;
        _log = log;
    }

    public async Task<int> RunAsync(EtlOptions o, CancellationToken ct = default)
    {
        if (!File.Exists(o.SourceFile))
        {
            _log.LogError("Source file not found: {File}", Path.GetFullPath(o.SourceFile));
            return 2;
        }

        // ---- 1. Extract (no DB needed) -------------------------------------------------------------
        var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(o.SourceFile, ct)));
        _log.LogInformation("[1/7] Extracting {Sheets} sheets from {File} (sha256 {Hash})",
            o.Sheets.Count, Path.GetFileName(o.SourceFile), hash[..12]);
        var extract = new ExcelExtractor().Extract(o.SourceFile, o.Sheets);
        foreach (var (sheet, n) in extract.RowsPerSheet) _log.LogInformation("      {Sheet,-8} {Rows,5} rows", sheet, n);
        if (extract.Findings.Any(f => f.Severity == Severity.Error))
        {
            foreach (var f in extract.Findings.Where(f => f.Severity == Severity.Error)) _log.LogError("{Rule}: {Detail}", f.RuleCode, f.Detail);
            return 3;
        }

        // ---- 2. Cleanse ------------------------------------------------------------------------------
        _log.LogInformation("[2/7] Cleansing {Rows} rows", extract.Rows.Count);
        var cleansed = new TransactionCleanser().Cleanse(extract.Rows);
        var findings = extract.Findings.Concat(cleansed.Findings).ToList();

        var cutoff = o.ReplayCutoff;
        var historical = cutoff is null ? cleansed.Clean : cleansed.Clean.Where(c => c.TxTimestamp < cutoff).ToList();
        var replay = cutoff is null ? new List<CleanTransaction>() : cleansed.Clean.Where(c => c.TxTimestamp >= cutoff).OrderBy(c => c.TxTimestamp).ToList();

        PrintProfile(extract, cleansed, findings, historical.Count, replay.Count, cutoff);
        if (o.DryRun)
        {
            _log.LogInformation("Dry run - nothing written.");
            return cleansed.Rejected.Count == 0 ? 0 : 1;
        }

        // ---- 3. Batch control -------------------------------------------------------------------------
        if (o.Reset)
        {
            _log.LogWarning("Reset requested: clearing facts, positions and real-time tables");
            await _loader.ExecAsync("EXEC etl.usp_ResetWarehouse;", ct);
        }
        else if (!o.Force && await _loader.FileAlreadyLoadedAsync(hash, ct))
        {
            _log.LogInformation("This exact file was already loaded successfully - skipping (use --force or --reset).");
            return 0;
        }

        var batchId = await _loader.StartBatchAsync("FILE", Path.GetFileName(o.SourceFile), hash, ct);
        try
        {
            // ---- 4. Land + stage ------------------------------------------------------------------------
            _log.LogInformation("[3/7] Landing raw rows (batch {Batch})", batchId);
            await _loader.BulkInsertRawAsync(batchId, extract.Rows, ct);

            _log.LogInformation("[4/7] Staging {Hist} clean rows, queueing {Replay} for live replay, {Rej} rejects",
                historical.Count, replay.Count, cleansed.Rejected.Count);
            await _loader.BulkInsertCleanAsync(batchId, historical, ct);
            if (replay.Count > 0)
            {
                // released rows are already facts; re-queue only what the warehouse has not seen yet
                await _loader.ExecAsync("DELETE FROM stg.ReplayQueue;", ct);
                var existing = await _loader.GetFactKeysAsync(ct);
                replay = replay.Where(r => !existing.Contains(r.TransactionKey)).ToList();
                await _loader.BulkInsertReplayAsync(batchId, replay, ct);
            }
            await _loader.BulkInsertRejectsAsync(batchId, cleansed.Rejected, ct);
            await _loader.BulkInsertFindingsAsync(batchId, findings, ct);

            // ---- 5. Load warehouse ----------------------------------------------------------------------
            _log.LogInformation("[5/7] Loading star schema (etl.usp_LoadWarehouse)");
            var loaded = await _loader.LoadWarehouseAsync(batchId, null, "FILE", ct);
            _log.LogInformation("      {Loaded} new fact rows ({Skipped} already present)", loaded, historical.Count - loaded);

            // ---- 6. Derive positions --------------------------------------------------------------------
            _log.LogInformation("[6/7] Rebuilding positions and realised gains");
            var summary = await _rebuildFactory().RebuildAsync(batchId, ct);

            // ---- 7. Reconcile ---------------------------------------------------------------------------
            _log.LogInformation("[7/7] Reconciling control totals");
            var ok = await ReconcileAsync(batchId, extract.Rows.Count, cleansed.Clean.Count, cleansed.Rejected.Count, ct);

            await _loader.CompleteBatchAsync(batchId, ok ? "SUCCEEDED" : "FAILED",
                $"{summary.OpenPositions} open positions, {summary.Disposals} disposals, {summary.Findings} engine findings",
                extract.Rows.Count, cleansed.Clean.Count, cleansed.Rejected.Count, replay.Count, ct);

            _log.LogInformation("Batch {Batch} {Status}.", batchId, ok ? "SUCCEEDED" : "FAILED reconciliation");
            return ok ? 0 : 4;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Batch {Batch} failed", batchId);
            await _loader.CompleteBatchAsync(batchId, "FAILED", ex.Message.Length > 3900 ? ex.Message[..3900] : ex.Message, ct: CancellationToken.None);
            return 1;
        }
    }

    /// <summary>
    /// Control checks: (a) every extracted row is either cleansed or rejected, (b) per-action totals in
    /// staging equal the totals in the fact table for this batch's keys.
    /// </summary>
    private async Task<bool> ReconcileAsync(int batchId, int extracted, int cleansed, int rejected, CancellationToken ct)
    {
        bool ok = true;
        if (extracted != cleansed + rejected)
        {
            _log.LogError("Row count mismatch: extracted {E} != cleansed {C} + rejected {R}", extracted, cleansed, rejected);
            ok = false;
        }

        const string sql = @"
SELECT s.ActionCode, s.Cnt AS StgRows, s.Total AS StgTotal, ISNULL(f.Cnt,0) AS DwRows, ISNULL(f.Total,0) AS DwTotal
FROM (SELECT ActionCode, COUNT(*) AS Cnt, SUM(TotalGbp) AS Total FROM stg.CleanTransaction WHERE BatchId = @b GROUP BY ActionCode) s
LEFT JOIN (
    SELECT tt.ActionCode, COUNT(*) AS Cnt, SUM(ft.TotalGbp) AS Total
    FROM dw.FactTransaction ft
    JOIN dw.DimTransactionType tt ON tt.TransactionTypeKey = ft.TransactionTypeKey
    WHERE ft.TransactionKey IN (SELECT TransactionKey FROM stg.CleanTransaction WHERE BatchId = @b)
    GROUP BY tt.ActionCode) f ON f.ActionCode = s.ActionCode
ORDER BY s.ActionCode;";

        await using var cn = new SqlConnection(_cs);
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@b", batchId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        _log.LogInformation("      {A,-11} {SR,6} {ST,14} {DR,6} {DT,14}", "Action", "stg#", "stg £", "dw#", "dw £");
        while (await rd.ReadAsync(ct))
        {
            var a = rd.GetString(0);
            int sr = rd.GetInt32(1), dr = rd.GetInt32(3);
            decimal st = rd.GetDecimal(2), dt = rd.GetDecimal(4);
            var match = sr == dr && st == dt;
            ok &= match;
            _log.LogInformation("      {A,-11} {SR,6} {ST,14:N2} {DR,6} {DT,14:N2} {M}", a, sr, st, dr, dt, match ? "OK" : "MISMATCH");
        }
        return ok;
    }

    private void PrintProfile(ExtractResult x, CleanseResult c, List<DataQualityFinding> findings, int hist, int replay, DateTime? cutoff)
    {
        _log.LogInformation("      extracted {E}, cleansed {C}, rejected {R}, blank rows skipped {B}",
            x.Rows.Count, c.Clean.Count, c.Rejected.Count, x.BlankRowsSkipped);
        foreach (var g in c.Clean.GroupBy(r => r.ActionCode).OrderBy(g => g.Key))
            _log.LogInformation("      {Action,-11} {Rows,5} rows  £{Total,14:N2}", g.Key, g.Count(), g.Sum(r => r.TotalGbp));
        foreach (var g in findings.GroupBy(f => (f.Severity, f.RuleCode)).OrderBy(g => g.Key.RuleCode))
            _log.LogInformation("      DQ {Sev,-4} {Rule,-34} x{N}", g.Key.Severity, g.Key.RuleCode, g.Count());
        foreach (var r in c.Rejected.Take(20))
            _log.LogWarning("      REJECT {Sheet}!{Row}: {Reason}", r.Raw.SourceSheet, r.Raw.SourceRow, r.Reason);
        _log.LogInformation("      synthetic keys {S}; cutoff {Cut}: {H} historical / {Rp} replay",
            c.Clean.Count(r => r.IsSyntheticKey), cutoff?.ToString("yyyy-MM-dd") ?? "none", hist, replay);
    }
}
