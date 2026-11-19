# ETL and data pipelines

StockMonitor has **two ingestion paths that share one transformation core**:

```mermaid
flowchart TB
  subgraph Batch["Batch pipeline - StockMonitor.Etl (console, scheduled or on demand)"]
    B1[1 Extract<br/>ExcelExtractor] --> B2[2 Land<br/>stg.RawTransaction]
    B2 --> B3[3 Cleanse / validate<br/>TransactionCleanser]
    B3 -->|valid, before cutoff| B4[4 Stage<br/>stg.CleanTransaction]
    B3 -->|valid, on/after cutoff| RQ[stg.ReplayQueue]
    B3 -->|invalid| RJ[etl.RejectedRow]
    B3 -->|findings| DQ[etl.DataQualityIssue]
    B4 --> B5[5 Load<br/>etl.usp_LoadWarehouse]
    B5 --> B6[6 Derive<br/>PositionRebuildService]
    B6 --> B7[7 Reconcile<br/>control totals]
  end
  subgraph Stream["Real-time pipeline - StockMonitor.Web"]
    S0[POST /api/transactions] --> S1[TransactionCleanser<br/>same rules]
    RQ --> RS[ReplayService<br/>every N s] --> S2
    S1 --> S2[stg.CleanTransaction<br/>LIVE batch]
    S2 --> S3[etl.usp_LoadWarehouse<br/>@TransactionKey]
    S3 --> S4[PositionRebuildService]
    S4 --> S5[AlertService]
    S5 --> S6[SignalR push]
    PF[PriceFeedService] --> RT[rt.SecurityPrice<br/>rt.PriceTick] --> S6
    SN[SnapshotService] --> RS2[rt.PortfolioSnapshot] --> S6
  end
  B5 -.same proc.- S3
  B3 -.same class.- S1
```

## 1. Batch pipeline (`src/StockMonitor.Etl`)

| Step | Component | What happens | Output |
|---|---|---|---|
| 0 | `Program` | Reads config (`appsettings.json`, then env `STOCKMONITOR_*`, then CLI). Hashes the file (SHA-256). If the same hash already loaded successfully, the run is skipped unless `--force`/`--reset` | — |
| 1 Extract | `ExcelExtractor` | Streams the configured sheets with ExcelDataReader. Maps columns by header name and alias. Renders each cell to an invariant string (dates as `yyyy-MM-dd HH:mm:ss.fff`, doubles as round-trip `R`). Skips blank rows. Flags unmapped columns | `List<RawTransactionRow>` |
| 2 Land | `WarehouseLoader.BulkInsertRawAsync` | `SqlBulkCopy` of every source cell plus `SourceSheet`, `SourceRow`, `BatchId` and a SHA-256 `RowHash` | `stg.RawTransaction` |
| 3 Cleanse | `TransactionCleanser` | Applies R01–R15 (see DATA_PREPARATION.md): parsing, typing, sign/currency normalisation, key generation, duplicate detection | clean rows, rejects, findings |
| 4 Stage | `BulkInsertClean/Replay/Rejects/Findings` | Rows before the cutoff go to `stg.CleanTransaction`. Rows on or after it go to `stg.ReplayQueue` as JSON. Rejects (with raw JSON) and findings go to `etl.*` | staging + control tables |
| 5 Load | `etl.usp_LoadWarehouse @BatchId` | One transaction: fill `DimDate` (400 days ahead), add unknown accounts as `UNK` (WARN), **MERGE `DimSecurity`** (SCD1, back-fill ISIN, keep the latest name, widen the first/last seen dates), add new transaction types, **insert facts where the key is not present**, compute signed `CashImpactGbp`, seed `rt.SecurityPrice` from the last trade for new securities | `dw.*` |
| 6 Derive | `PositionRebuildService` + `PositionEngine` | Replays all trades, dividends and corporate actions in time order. Builds Section 104 average-cost pools per account × security. Fully rewrites `dw.Position` and `dw.FactRealizedGain` in one transaction and logs `ENGINE_*` findings | positions, realised gains |
| 7 Reconcile | `EtlPipeline.ReconcileAsync` | (a) extracted = cleansed + rejected; (b) for each action code, staging row count and £ total equal the fact table. Any mismatch marks the batch FAILED (exit code 4) | `etl.BatchLog.Status` |

Exit codes: `0` success or skipped, `1` exception, `2` file not found, `3` extract error, `4` reconciliation failed.
A scheduler (SQL Agent CmdExec, Windows Task Scheduler, cron, Azure Container Apps job, or ADF custom activity) can act on these codes.

### Typical console output
```
[1/7] Extracting 9 sheets from Open Stock Transaction Data 2020-2024.xlsx (sha256 …)
      2020       157 rows
      Inv2021    492 rows
      …
[2/7] Cleansing 3299 rows
      extracted 3299, cleansed 3299, rejected 0, blank rows skipped 0
      BUY          1365 rows  £    404,132.84
      DIVIDEND     1207 rows  £     14,408.61
      …
      DQ Info R07 WITHDRAWAL_SIGN_NORMALISED      x3
      DQ Warn R14 CORPORATE_ACTION_ARTIFACT       x2
      DQ Info PARTIAL_FILL_PAIR                   x7
      synthetic keys 1207; cutoff 2024-01-01: 2771 historical / 528 replay
[3/7] Landing raw rows (batch 1)
[4/7] Staging 2771 clean rows, queueing 528 for live replay, 0 rejects
[5/7] Loading star schema (etl.usp_LoadWarehouse)
[6/7] Rebuilding positions and realised gains
[7/7] Reconciling control totals
      Action        stg#          stg £    dw#           dw £
      BUY           1131     322,215.38   1131     322,215.38 OK
      DEPOSIT        240     219,807.98    240     219,807.98 OK
      DIVIDEND      1085      11,877.30   1085      11,877.30 OK
      INTEREST        88           1.28     88           1.28 OK
      SELL           169     148,411.67    169     148,411.67 OK
      WITHDRAWAL      58      58,503.52     58      58,503.52 OK
```
(These figures are for the default `--cutoff 2024-01-01`, from the profiling run during development.)

## 2. Real-time pipeline (`src/StockMonitor.Web`)

**Sources of events**
1. `POST /api/transactions`: the production entry point (broker webhook, message-queue consumer, or upstream service).
2. `ReplayService`: for the demo, it releases rows from `stg.ReplayQueue` in timestamp order every
   `Monitor:Replay:IntervalSeconds` (default 4 s). It can be paused or resumed from the dashboard or via `/api/replay/*`.

**Per event (`IngestionService`)**
1. **Validate**: `TransactionCleanser.CleanseOne`, the same rules as the batch. A rejection returns `422` with the rule code.
2. **Idempotency**: if `dw.FactTransaction` already has the key, it returns `200 duplicate` and nothing else happens.
3. **Stage**: insert into `stg.CleanTransaction` under the day's `LIVE` batch (one `etl.BatchLog` row per UTC day).
4. **Load**: `etl.usp_LoadWarehouse @BatchId, @TransactionKey, 'LIVE'`, the same proc as the batch, limited to one row.
   If two identical requests race, the proc's `NOT EXISTS` makes the second a no-op.
5. **Derive**: rebuild positions (only for BUY/SELL/DIVIDEND). The rebuild runs behind a semaphore and takes well under 1 s at this volume.
6. **Alert** (`AlertService`): LARGE_TRADE (≥ £5k), LARGE_CASH_MOVEMENT, CONCENTRATION (a holding ≥ 12% of the portfolio after a buy),
   ISA_ALLOWANCE (≥ 80% / ≥ 100% of £20k in the UK tax year), OVERSELL (engine finding on this sale). Thresholds are in `Monitor:Alerts`.
7. **Push** over SignalR: `transaction` (feed row), `summary` (KPIs), `alert`.

**Background workers**
| Worker | Cadence | Writes | Pushes |
|---|---|---|---|
| `PriceFeedService` | `Prices:TickSeconds` (3 s) | `rt.SecurityPrice` (upsert), `rt.PriceTick` (append). Rolls `PrevCloseGbp` at the UTC day boundary. Purges hourly via `rt.usp_PurgeRealtime` | `prices`, `alert` (PRICE_MOVE ≥ 4%) |
| `SnapshotService` | `Snapshots:IntervalSeconds` (15 s) | `rt.PortfolioSnapshot` for all accounts and for each account | `snapshot`, `summary` |
| `ReplayService` | `Replay:IntervalSeconds` (4 s) | via `IngestionService` | `replay` |

The price source is `IMarketDataProvider`. The default `SimulatedMarketDataProvider` uses geometric Brownian motion (μ 7%,
σ from config, time accelerated so movement is visible). A vendor implementation (Polygon, Alpha Vantage, IEX, an LSEG feed and so on) can
be swapped in through DI without changing any consumer.

## 3. Cross-cutting properties

**Idempotency and re-runs**
- File level: SHA-256 of the workbook is stored in `etl.BatchLog.SourceFileHash`. An identical file is skipped.
- Row level: `dw.FactTransaction.TransactionKey` is `UNIQUE`, and the proc inserts only missing keys. `--force` re-stages and
  re-checks the file without duplicating facts.
- Synthetic keys are deterministic, so dividend rows without a broker ID get the same key on every run.
- Derived tables are rebuilt completely each time, so they never drift from the facts.

**Lineage**
Each fact carries `BatchId` and `Source` (`FILE`/`LIVE`). `stg.CleanTransaction` and `etl.DataQualityIssue` keep
`SourceSheet` + `SourceRow`, and `stg.RawTransaction` holds the original cell text and row hash. Any number in the UI can be
traced back to a cell in the workbook.

**Failure handling**
- Extract errors (missing sheet) abort before any DB write.
- Exceptions after the batch starts mark it `FAILED` with the message. Staging is per batch, so a re-run starts clean,
  and the load proc is transactional (`XACT_ABORT ON`).
- Workers catch and log exceptions per tick, so one bad tick does not stop the feed.
- Rejected rows are stored with their raw JSON, ready to fix and replay.

**Concurrency**
`READ_COMMITTED_SNAPSHOT` is on, so dashboards reading views are never blocked by the ingester or the price writer.
Position rebuilds are serialised with a process-wide semaphore.

**Performance at this scale and beyond**
3.3k rows load in seconds (bulk copy + one set-based proc). The full position rebuild is O(n log n) over trades. Above roughly
10^6 trades, switch the rebuild to incremental (only the affected account × security, from its last snapshot) and partition
`dw.FactTransaction` by `DateKey`. The interfaces already pass the affected key.

**Housekeeping**
`rt.usp_PurgeRealtime` keeps 24 h of ticks and 30 days of snapshots, deleting ticks in batches of 5,000 to keep the log small.

## 4. Operating the pipeline

| Task | Command |
|---|---|
| Profile a new export without touching the DB | `dotnet run --project src/StockMonitor.Etl -- --file new.xlsx --dry-run` |
| Incremental load of a newer export | `dotnet run --project src/StockMonitor.Etl -- --file new.xlsx --cutoff none` (only new keys are inserted) |
| Full rebuild | `… -- --reset --cutoff none` |
| Demo reset with live replay | `… -- --reset --cutoff 2024-01-01`, then restart the web app |
| Add a new account/sheet | add `{ "Name": "ISA2025", "AccountNumber": 2129 }` to `Etl:Sheets` |
| Add a split / spin-off | insert into `dw.CorporateAction` (or extend `05_seed_reference.sql`), then re-run the ETL with `--force`. Positions rebuild automatically |
| Check pipeline health | Analytics page → *Pipeline & data quality*, or `SELECT * FROM etl.vw_BatchSummary` |
