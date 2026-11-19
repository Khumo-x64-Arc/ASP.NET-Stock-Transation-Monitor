/* =====================================================================
   StockMonitor - 06: semantic views consumed by the web app (EF keyless entities)
   ===================================================================== */
USE StockMonitor;
GO

/* Flattened transaction feed */
CREATE OR ALTER VIEW dw.vw_TransactionDetail
AS
SELECT  f.TransactionId, f.TransactionKey, f.TxTimestamp, d.[Date] AS TxDate, d.YearMonth, d.UkTaxYear,
        a.AccountKey, a.AccountNumber, a.AccountType,
        tt.ActionCode, tt.ActionSubType, tt.Category, tt.DisplayName AS ActionName,
        s.SecurityKey, s.Ticker, s.SecurityName,
        f.Shares, f.PriceLocal, f.PriceCurrency, f.PriceGbp, f.FxRate,
        f.TotalGbp, f.CashImpactGbp, f.BrokerResultGbp,
        f.WithholdingTax, f.WithholdingTaxCurrency,
        ISNULL(f.FxFeeGbp,0) + ISNULL(f.StampDutyGbp,0) + ISNULL(f.TransactionFeeGbp,0)
            + ISNULL(f.FinraFeeGbp,0) + ISNULL(f.DepositFeeGbp,0) AS TotalFeesGbp,
        f.Notes, f.Source, f.InsertedAt
FROM dw.FactTransaction f
JOIN dw.DimAccount a          ON a.AccountKey = f.AccountKey
JOIN dw.DimTransactionType tt ON tt.TransactionTypeKey = f.TransactionTypeKey
JOIN dw.DimDate d             ON d.DateKey = f.DateKey
LEFT JOIN dw.DimSecurity s    ON s.SecurityKey = f.SecurityKey;
GO

/* Monthly cash flows per account */
CREATE OR ALTER VIEW dw.vw_MonthlyCashFlow
AS
SELECT  d.YearMonth, a.AccountKey, a.AccountType,
        SUM(CASE WHEN tt.ActionCode = 'DEPOSIT'    THEN f.TotalGbp ELSE 0 END) AS DepositsGbp,
        SUM(CASE WHEN tt.ActionCode = 'WITHDRAWAL' THEN f.TotalGbp ELSE 0 END) AS WithdrawalsGbp,
        SUM(CASE WHEN tt.ActionCode = 'BUY'        THEN f.TotalGbp ELSE 0 END) AS BuysGbp,
        SUM(CASE WHEN tt.ActionCode = 'SELL'       THEN f.TotalGbp ELSE 0 END) AS SellsGbp,
        SUM(CASE WHEN tt.ActionCode = 'DIVIDEND'   THEN f.TotalGbp ELSE 0 END) AS DividendsGbp,
        SUM(CASE WHEN tt.ActionCode = 'INTEREST'   THEN f.TotalGbp ELSE 0 END) AS InterestGbp,
        SUM(ISNULL(f.FxFeeGbp,0) + ISNULL(f.StampDutyGbp,0) + ISNULL(f.TransactionFeeGbp,0)
            + ISNULL(f.FinraFeeGbp,0) + ISNULL(f.DepositFeeGbp,0))           AS FeesGbp,
        SUM(CASE WHEN tt.ActionCode IN ('BUY','SELL') THEN 1 ELSE 0 END)       AS TradeCount
FROM dw.FactTransaction f
JOIN dw.DimDate d             ON d.DateKey = f.DateKey
JOIN dw.DimAccount a          ON a.AccountKey = f.AccountKey
JOIN dw.DimTransactionType tt ON tt.TransactionTypeKey = f.TransactionTypeKey
GROUP BY d.YearMonth, a.AccountKey, a.AccountType;
GO

/* Dividend income by security and account (net of withholding, as booked) */
CREATE OR ALTER VIEW dw.vw_DividendBySecurity
AS
SELECT  s.SecurityKey, s.Ticker, s.SecurityName, a.AccountKey, a.AccountType,
        COUNT(*)                 AS Payments,
        SUM(f.TotalGbp)          AS DividendsGbp,
        MIN(f.TxTimestamp)       AS FirstPaidAt,
        MAX(f.TxTimestamp)       AS LastPaidAt
FROM dw.FactTransaction f
JOIN dw.DimTransactionType tt ON tt.TransactionTypeKey = f.TransactionTypeKey AND tt.ActionCode = 'DIVIDEND'
JOIN dw.DimSecurity s         ON s.SecurityKey = f.SecurityKey
JOIN dw.DimAccount a          ON a.AccountKey = f.AccountKey
GROUP BY s.SecurityKey, s.Ticker, s.SecurityName, a.AccountKey, a.AccountType;
GO

/* Cash balance per account = running sum of signed cash impact */
CREATE OR ALTER VIEW dw.vw_CashBalance
AS
SELECT a.AccountKey, a.AccountNumber, a.AccountType, ISNULL(SUM(f.CashImpactGbp), 0) AS CashGbp
FROM dw.DimAccount a
LEFT JOIN dw.FactTransaction f ON f.AccountKey = a.AccountKey
GROUP BY a.AccountKey, a.AccountNumber, a.AccountType;
GO

/* Realised gains by UK tax year (GIA matters for CGT; ISA shown for completeness) */
CREATE OR ALTER VIEW dw.vw_RealizedGainByTaxYear
AS
SELECT  d.UkTaxYear, a.AccountKey, a.AccountType, a.IsTaxSheltered,
        COUNT(*)              AS Disposals,
        SUM(g.ProceedsGbp)    AS ProceedsGbp,
        SUM(g.CostBasisGbp)   AS CostBasisGbp,
        SUM(g.GainGbp)        AS GainGbp,
        SUM(ISNULL(g.BrokerResultGbp, 0)) AS BrokerResultGbp
FROM dw.FactRealizedGain g
JOIN dw.DimDate d    ON d.DateKey = g.DateKey
JOIN dw.DimAccount a ON a.AccountKey = g.AccountKey
GROUP BY d.UkTaxYear, a.AccountKey, a.AccountType, a.IsTaxSheltered;
GO

/* ISA subscriptions per tax year vs the GBP 20,000 allowance */
CREATE OR ALTER VIEW dw.vw_IsaAllowance
AS
SELECT  d.UkTaxYear, a.AccountKey,
        SUM(f.TotalGbp)                AS SubscribedGbp,
        CAST(20000 AS DECIMAL(19,4))   AS AllowanceGbp,
        CAST(SUM(f.TotalGbp) / 20000.0 * 100 AS DECIMAL(9,2)) AS UsedPct
FROM dw.FactTransaction f
JOIN dw.DimAccount a          ON a.AccountKey = f.AccountKey AND a.AccountType = 'ISA'
JOIN dw.DimTransactionType tt ON tt.TransactionTypeKey = f.TransactionTypeKey AND tt.ActionCode = 'DEPOSIT'
JOIN dw.DimDate d             ON d.DateKey = f.DateKey
GROUP BY d.UkTaxYear, a.AccountKey;
GO

/* Holdings valued at the latest real-time price */
CREATE OR ALTER VIEW dw.vw_Holding
AS
SELECT  p.AccountKey, a.AccountNumber, a.AccountType,
        s.SecurityKey, s.Ticker, s.SecurityName, s.QuoteCurrency,
        p.Quantity, p.CostBasisGbp, p.AvgCostGbp,
        ISNULL(px.PriceGbp, p.AvgCostGbp)                          AS PriceGbp,
        ISNULL(px.PrevCloseGbp, p.AvgCostGbp)                      AS PrevCloseGbp,
        p.Quantity * ISNULL(px.PriceGbp, p.AvgCostGbp)             AS MarketValueGbp,
        p.Quantity * ISNULL(px.PriceGbp, p.AvgCostGbp) - p.CostBasisGbp AS UnrealizedGainGbp,
        p.Quantity * (ISNULL(px.PriceGbp, 0) - ISNULL(px.PrevCloseGbp, 0)) AS DayChangeGbp,
        p.RealizedGainGbp, p.DividendsGbp, p.FirstBuyAt, p.LastActivityAt,
        px.Source AS PriceSource, px.UpdatedAt AS PriceUpdatedAt
FROM dw.Position p
JOIN dw.DimAccount a     ON a.AccountKey = p.AccountKey
JOIN dw.DimSecurity s    ON s.SecurityKey = p.SecurityKey
LEFT JOIN rt.SecurityPrice px ON px.SecurityKey = p.SecurityKey
WHERE p.Quantity > 0.0000001;
GO

/* Pipeline health for the ops panel */
CREATE OR ALTER VIEW etl.vw_BatchSummary
AS
SELECT  b.BatchId, b.BatchType, b.SourceFile, b.StartedAt, b.CompletedAt, b.Status,
        b.RowsExtracted, b.RowsCleansed, b.RowsRejected, b.RowsLoaded, b.RowsQueued,
        (SELECT COUNT(*) FROM etl.DataQualityIssue q WHERE q.BatchId = b.BatchId AND q.Severity = 'WARN')  AS Warnings,
        (SELECT COUNT(*) FROM etl.DataQualityIssue q WHERE q.BatchId = b.BatchId AND q.Severity = 'ERROR') AS Errors
FROM etl.BatchLog b;
GO
