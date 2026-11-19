/* =====================================================================
   StockMonitor - 04: transform/load stored procedures
   The C# ETL lands + cleanses; these procs conform and load the star schema.
   The same proc is used by the batch pipeline AND the live ingestion API,
   so both paths apply identical load rules.
   ===================================================================== */
USE StockMonitor;
GO

/* ---------- Date dimension (idempotent range fill) ---------- */
CREATE OR ALTER PROCEDURE etl.usp_EnsureDimDate
    @From DATE,
    @To   DATE
AS
BEGIN
    SET NOCOUNT ON;
    SET DATEFIRST 1;  -- Monday = 1

    ;WITH n AS (
        SELECT TOP (DATEDIFF(DAY, @From, @To) + 1)
               ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS i
        FROM sys.all_objects a CROSS JOIN sys.all_objects b
    ), d AS (
        SELECT DATEADD(DAY, i, @From) AS dt FROM n
    )
    INSERT dw.DimDate (DateKey, [Date], [Year], [Quarter], [Month], MonthName, YearMonth, [Day],
                       DayOfWeek, IsWeekend, UkTaxYear, UkTaxYearStart)
    SELECT  CONVERT(INT, FORMAT(dt, 'yyyyMMdd')),
            dt,
            YEAR(dt),
            DATEPART(QUARTER, dt),
            MONTH(dt),
            DATENAME(MONTH, dt),
            FORMAT(dt, 'yyyy-MM'),
            DAY(dt),
            DATEPART(WEEKDAY, dt),
            CASE WHEN DATEPART(WEEKDAY, dt) IN (6, 7) THEN 1 ELSE 0 END,
            -- UK tax year runs 6 April -> 5 April
            CONCAT(ty.StartYear, '/', RIGHT(CONVERT(CHAR(4), ty.StartYear + 1), 2)),
            ty.StartYear
    FROM d
    CROSS APPLY (SELECT CASE WHEN dt >= DATEFROMPARTS(YEAR(dt), 4, 6) THEN YEAR(dt) ELSE YEAR(dt) - 1 END AS StartYear) ty
    WHERE NOT EXISTS (SELECT 1 FROM dw.DimDate x WHERE x.[Date] = d.dt);
END
GO

/* ---------- Main load: stg.CleanTransaction -> dims + dw.FactTransaction ---------- */
CREATE OR ALTER PROCEDURE etl.usp_LoadWarehouse
    @BatchId         INT,
    @TransactionKey  NVARCHAR(64) = NULL,     -- set by the live path to load a single row
    @Source          VARCHAR(10)  = 'FILE',
    @RowsLoaded      INT          = NULL OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @src TABLE (CleanId BIGINT PRIMARY KEY);
    INSERT @src (CleanId)
    SELECT c.CleanId
    FROM stg.CleanTransaction c
    WHERE c.BatchId = @BatchId
      AND (@TransactionKey IS NULL OR c.TransactionKey = @TransactionKey);

    DECLARE @minD DATE, @maxD DATE;
    SELECT @minD = MIN(CONVERT(DATE, c.TxTimestamp)), @maxD = MAX(CONVERT(DATE, c.TxTimestamp))
    FROM stg.CleanTransaction c JOIN @src s ON s.CleanId = c.CleanId;

    IF @minD IS NULL BEGIN SET @RowsLoaded = 0; RETURN; END

    -- keep the date dimension a little ahead so live rows never miss a key
    DECLARE @ahead DATE = DATEADD(DAY, 400, @maxD);
    EXEC etl.usp_EnsureDimDate @From = @minD, @To = @ahead;

    BEGIN TRAN;

    /* 1. Accounts: unknown account numbers are created as 'UNK' and flagged */
    INSERT dw.DimAccount (AccountNumber, AccountType, AccountName, IsTaxSheltered)
    SELECT DISTINCT c.AccountNumber, 'UNK', CONCAT(N'Account ', c.AccountNumber), 0
    FROM stg.CleanTransaction c JOIN @src s ON s.CleanId = c.CleanId
    WHERE NOT EXISTS (SELECT 1 FROM dw.DimAccount a WHERE a.AccountNumber = c.AccountNumber);

    IF @@ROWCOUNT > 0
        INSERT etl.DataQualityIssue (BatchId, Severity, RuleCode, Detail)
        VALUES (@BatchId, 'WARN', 'UNKNOWN_ACCOUNT', N'New account number(s) auto-created with type UNK - classify in dw.DimAccount.');

    /* 2. Securities (SCD type 1): latest non-null attributes win, ISIN back-filled when it first appears */
    ;WITH sec AS (
        SELECT  c.Ticker,
                MAX(c.Isin)                                  AS Isin,
                MIN(CONVERT(DATE, c.TxTimestamp))            AS FirstSeen,
                MAX(CONVERT(DATE, c.TxTimestamp))            AS LastSeen
        FROM stg.CleanTransaction c JOIN @src s ON s.CleanId = c.CleanId
        WHERE c.Ticker IS NOT NULL
        GROUP BY c.Ticker
    ), latest AS (
        SELECT c.Ticker, c.SecurityName, c.PriceCurrency,
               ROW_NUMBER() OVER (PARTITION BY c.Ticker ORDER BY c.TxTimestamp DESC) AS rn
        FROM stg.CleanTransaction c JOIN @src s ON s.CleanId = c.CleanId
        WHERE c.Ticker IS NOT NULL AND c.SecurityName IS NOT NULL
    )
    MERGE dw.DimSecurity AS t
    USING (
        SELECT sec.Ticker, sec.Isin, COALESCE(l.SecurityName, sec.Ticker) AS SecurityName,
               l.PriceCurrency, sec.FirstSeen, sec.LastSeen
        FROM sec LEFT JOIN latest l ON l.Ticker = sec.Ticker AND l.rn = 1
    ) AS s
    ON t.Ticker = s.Ticker
    WHEN MATCHED THEN UPDATE SET
        t.Isin          = COALESCE(s.Isin, t.Isin),
        t.SecurityName  = CASE WHEN s.LastSeen >= ISNULL(t.LastSeenDate, '19000101') THEN s.SecurityName ELSE t.SecurityName END,
        t.QuoteCurrency = COALESCE(s.PriceCurrency, t.QuoteCurrency),
        t.FirstSeenDate = CASE WHEN t.FirstSeenDate IS NULL OR s.FirstSeen < t.FirstSeenDate THEN s.FirstSeen ELSE t.FirstSeenDate END,
        t.LastSeenDate  = CASE WHEN t.LastSeenDate  IS NULL OR s.LastSeen  > t.LastSeenDate  THEN s.LastSeen  ELSE t.LastSeenDate  END,
        t.UpdatedAt     = SYSUTCDATETIME()
    WHEN NOT MATCHED THEN
        INSERT (Ticker, Isin, SecurityName, QuoteCurrency, FirstSeenDate, LastSeenDate)
        VALUES (s.Ticker, s.Isin, s.SecurityName, s.PriceCurrency, s.FirstSeen, s.LastSeen);

    /* 3. Transaction types: new dividend flavours etc. are added automatically */
    INSERT dw.DimTransactionType (ActionCode, ActionSubType, Category, CashDirection, DisplayName)
    SELECT DISTINCT c.ActionCode, ISNULL(c.ActionSubType, N''),
           CASE c.ActionCode WHEN 'BUY' THEN 'TRADE' WHEN 'SELL' THEN 'TRADE'
                             WHEN 'DIVIDEND' THEN 'INCOME' WHEN 'INTEREST' THEN 'INCOME' ELSE 'CASH' END,
           CASE WHEN c.ActionCode IN ('BUY', 'WITHDRAWAL') THEN -1 ELSE 1 END,
           CASE WHEN c.ActionSubType IS NULL THEN c.ActionCode ELSE CONCAT(c.ActionCode, N' (', c.ActionSubType, N')') END
    FROM stg.CleanTransaction c JOIN @src s ON s.CleanId = c.CleanId
    WHERE NOT EXISTS (SELECT 1 FROM dw.DimTransactionType tt
                      WHERE tt.ActionCode = c.ActionCode AND tt.ActionSubType = ISNULL(c.ActionSubType, N''));

    /* 4. Facts: insert-only on the idempotency key -> re-running a batch never duplicates */
    INSERT dw.FactTransaction
        (TransactionKey, AccountKey, SecurityKey, TransactionTypeKey, DateKey, TxTimestamp,
         Shares, PriceLocal, PriceCurrency, FxRate, PriceGbp, TotalGbp, CashImpactGbp, BrokerResultGbp,
         WithholdingTax, WithholdingTaxCurrency, FxFeeGbp, StampDutyGbp, TransactionFeeGbp, FinraFeeGbp,
         DepositFeeGbp, Notes, IsSyntheticKey, IsCorporateActionArtifact, Source, BatchId)
    SELECT  c.TransactionKey, a.AccountKey, sec.SecurityKey, tt.TransactionTypeKey,
            CONVERT(INT, FORMAT(c.TxTimestamp, 'yyyyMMdd')), c.TxTimestamp,
            c.Shares, c.PriceLocal, c.PriceCurrency, c.FxRate, c.PriceGbp, c.TotalGbp,
            c.TotalGbp * tt.CashDirection, c.BrokerResultGbp,
            c.WithholdingTax, c.WithholdingTaxCurrency, c.FxFeeGbp, c.StampDutyGbp, c.TransactionFeeGbp,
            c.FinraFeeGbp, c.DepositFeeGbp, c.Notes, c.IsSyntheticKey, c.IsCorporateActionArtifact, @Source, @BatchId
    FROM stg.CleanTransaction c
    JOIN @src s                     ON s.CleanId = c.CleanId
    JOIN dw.DimAccount a            ON a.AccountNumber = c.AccountNumber
    JOIN dw.DimTransactionType tt   ON tt.ActionCode = c.ActionCode AND tt.ActionSubType = ISNULL(c.ActionSubType, N'')
    LEFT JOIN dw.DimSecurity sec    ON sec.Ticker = c.Ticker
    WHERE NOT EXISTS (SELECT 1 FROM dw.FactTransaction f WHERE f.TransactionKey = c.TransactionKey);

    SET @RowsLoaded = @@ROWCOUNT;

    /* 5. Seed a price for any newly seen security (last traded price) so valuation never has gaps */
    ;WITH lastTrade AS (
        SELECT f.SecurityKey, f.PriceGbp, f.TxTimestamp,
               ROW_NUMBER() OVER (PARTITION BY f.SecurityKey ORDER BY f.TxTimestamp DESC) AS rn
        FROM dw.FactTransaction f
        JOIN dw.DimTransactionType tt ON tt.TransactionTypeKey = f.TransactionTypeKey
        WHERE tt.ActionCode IN ('BUY', 'SELL') AND f.PriceGbp > 0
    )
    -- Splits after the last trade (e.g. AVGO 10:1 on 2024-07-15) must be applied to the seed price,
    -- otherwise the post-split quantity would be valued at the pre-split price.
    INSERT rt.SecurityPrice (SecurityKey, PriceGbp, PrevCloseGbp, Source, UpdatedAt)
    SELECT lt.SecurityKey,
           CAST(lt.PriceGbp / ISNULL(adj.Factor, 1) AS DECIMAL(19,6)),
           CAST(lt.PriceGbp / ISNULL(adj.Factor, 1) AS DECIMAL(19,6)),
           'LAST_TRADE', SYSUTCDATETIME()
    FROM lastTrade lt
    JOIN dw.DimSecurity s ON s.SecurityKey = lt.SecurityKey
    OUTER APPLY (
        SELECT CAST(EXP(SUM(LOG(CAST(ca.Ratio AS FLOAT)))) AS DECIMAL(19,8)) AS Factor
        FROM dw.CorporateAction ca
        WHERE ca.Ticker = s.Ticker AND ca.ActionType = 'SPLIT'
          AND ca.EffectiveDate > CONVERT(DATE, lt.TxTimestamp)
    ) adj
    WHERE lt.rn = 1
      AND NOT EXISTS (SELECT 1 FROM rt.SecurityPrice p WHERE p.SecurityKey = lt.SecurityKey);

    IF @Source = 'FILE'
        UPDATE etl.BatchLog SET RowsLoaded = RowsLoaded + @RowsLoaded WHERE BatchId = @BatchId;

    COMMIT;
END
GO

/* ---------- Housekeeping for the real-time tables ---------- */
CREATE OR ALTER PROCEDURE rt.usp_PurgeRealtime
    @KeepTickHours      INT = 24,
    @KeepSnapshotDays   INT = 30
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @tickCutoff DATETIME2(3) = DATEADD(HOUR, -@KeepTickHours, SYSUTCDATETIME());
    DECLARE @snapCutoff DATETIME2(3) = DATEADD(DAY,  -@KeepSnapshotDays, SYSUTCDATETIME());

    WHILE 1 = 1
    BEGIN
        DELETE TOP (5000) FROM rt.PriceTick WHERE TickTime < @tickCutoff;   -- batched to keep the log small
        IF @@ROWCOUNT < 5000 BREAK;
    END
    DELETE FROM rt.PortfolioSnapshot WHERE SnapshotTime < @snapCutoff;
END
GO

/* ---------- Full reset (dev only): clears facts but keeps reference data ---------- */
CREATE OR ALTER PROCEDURE etl.usp_ResetWarehouse
AS
BEGIN
    SET NOCOUNT ON;
    DELETE rt.Alert; DELETE rt.PortfolioSnapshot; DELETE rt.PriceTick; DELETE rt.SecurityPrice;
    DELETE dw.FactRealizedGain; DELETE dw.Position; DELETE dw.FactTransaction;
    TRUNCATE TABLE stg.ReplayQueue; TRUNCATE TABLE stg.CleanTransaction; TRUNCATE TABLE stg.RawTransaction;
END
GO
