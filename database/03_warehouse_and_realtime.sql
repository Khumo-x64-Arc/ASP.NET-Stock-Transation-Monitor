/* =====================================================================
   StockMonitor - 03: star schema (dw) + real-time tables (rt)
   ===================================================================== */
USE StockMonitor;
GO

/* =========================== DIMENSIONS =========================== */
IF OBJECT_ID(N'dw.DimDate') IS NULL
BEGIN
CREATE TABLE dw.DimDate
(
    DateKey         INT          NOT NULL CONSTRAINT PK_DimDate PRIMARY KEY,   -- yyyymmdd
    [Date]          DATE         NOT NULL CONSTRAINT UQ_DimDate_Date UNIQUE,
    [Year]          SMALLINT     NOT NULL,
    [Quarter]       TINYINT      NOT NULL,
    [Month]         TINYINT      NOT NULL,
    MonthName       VARCHAR(10)  NOT NULL,
    YearMonth       CHAR(7)      NOT NULL,      -- '2023-04'
    [Day]           TINYINT      NOT NULL,
    DayOfWeek       TINYINT      NOT NULL,      -- 1 = Monday
    IsWeekend       BIT          NOT NULL,
    UkTaxYear       CHAR(7)      NOT NULL,      -- '2023/24' (6 Apr - 5 Apr); drives ISA allowance + CGT reporting
    UkTaxYearStart  SMALLINT     NOT NULL
);
END
GO

IF OBJECT_ID(N'dw.DimAccount') IS NULL
BEGIN
CREATE TABLE dw.DimAccount
(
    AccountKey      INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_DimAccount PRIMARY KEY,
    AccountNumber   INT          NOT NULL CONSTRAINT UQ_DimAccount_Number UNIQUE,
    AccountType     VARCHAR(5)   NOT NULL,      -- ISA | INV
    AccountName     NVARCHAR(100) NOT NULL,
    IsTaxSheltered  BIT          NOT NULL,
    BaseCurrency    CHAR(3)      NOT NULL CONSTRAINT DF_DimAccount_Ccy DEFAULT ('GBP')
);
END
GO

IF OBJECT_ID(N'dw.DimSecurity') IS NULL
BEGIN
CREATE TABLE dw.DimSecurity
(
    SecurityKey     INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_DimSecurity PRIMARY KEY,
    Ticker          NVARCHAR(20)  NOT NULL CONSTRAINT UQ_DimSecurity_Ticker UNIQUE,
    Isin            CHAR(12)      NULL,
    SecurityName    NVARCHAR(200) NOT NULL,
    QuoteCurrency   CHAR(3)       NULL,         -- USD / GBX
    FirstSeenDate   DATE          NULL,
    LastSeenDate    DATE          NULL,
    UpdatedAt       DATETIME2(3)  NOT NULL CONSTRAINT DF_DimSecurity_Upd DEFAULT (SYSUTCDATETIME())
);
END
GO

IF OBJECT_ID(N'dw.DimTransactionType') IS NULL
BEGIN
CREATE TABLE dw.DimTransactionType
(
    TransactionTypeKey  INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_DimTransactionType PRIMARY KEY,
    ActionCode          VARCHAR(20)   NOT NULL,
    ActionSubType       NVARCHAR(80)  NOT NULL CONSTRAINT DF_DimTT_Sub DEFAULT (N''),
    Category            VARCHAR(10)   NOT NULL,   -- TRADE | INCOME | CASH
    CashDirection       SMALLINT      NOT NULL,   -- +1 money into the account, -1 money out
    DisplayName         NVARCHAR(120) NOT NULL,
    CONSTRAINT UQ_DimTT UNIQUE (ActionCode, ActionSubType)
);
END
GO

/* =========================== FACTS =========================== */
IF OBJECT_ID(N'dw.FactTransaction') IS NULL
BEGIN
CREATE TABLE dw.FactTransaction
(
    TransactionId           BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_FactTransaction PRIMARY KEY,
    TransactionKey          NVARCHAR(64)   NOT NULL CONSTRAINT UQ_FactTransaction_Key UNIQUE,  -- idempotency key
    AccountKey              INT            NOT NULL CONSTRAINT FK_FT_Account REFERENCES dw.DimAccount(AccountKey),
    SecurityKey             INT            NULL     CONSTRAINT FK_FT_Security REFERENCES dw.DimSecurity(SecurityKey),
    TransactionTypeKey      INT            NOT NULL CONSTRAINT FK_FT_Type REFERENCES dw.DimTransactionType(TransactionTypeKey),
    DateKey                 INT            NOT NULL CONSTRAINT FK_FT_Date REFERENCES dw.DimDate(DateKey),
    TxTimestamp             DATETIME2(3)   NOT NULL,
    Shares                  DECIMAL(28,10) NULL,
    PriceLocal              DECIMAL(19,6)  NULL,
    PriceCurrency           CHAR(3)        NULL,
    FxRate                  DECIMAL(19,8)  NULL,
    PriceGbp                DECIMAL(19,6)  NULL,
    TotalGbp                DECIMAL(19,4)  NOT NULL,
    CashImpactGbp           DECIMAL(19,4)  NOT NULL,   -- signed: TotalGbp * CashDirection
    BrokerResultGbp         DECIMAL(19,4)  NULL,
    WithholdingTax          DECIMAL(19,4)  NULL,
    WithholdingTaxCurrency  CHAR(3)        NULL,
    FxFeeGbp                DECIMAL(19,4)  NULL,
    StampDutyGbp            DECIMAL(19,4)  NULL,
    TransactionFeeGbp       DECIMAL(19,4)  NULL,
    FinraFeeGbp             DECIMAL(19,4)  NULL,
    DepositFeeGbp           DECIMAL(19,4)  NULL,
    Notes                   NVARCHAR(1000) NULL,
    IsSyntheticKey          BIT            NOT NULL,
    IsCorporateActionArtifact BIT          NOT NULL,
    Source                  VARCHAR(10)    NOT NULL,   -- FILE | LIVE
    BatchId                 INT            NOT NULL,
    InsertedAt              DATETIME2(3)   NOT NULL CONSTRAINT DF_FT_Inserted DEFAULT (SYSUTCDATETIME())
);
CREATE INDEX IX_FT_Account_Time  ON dw.FactTransaction(AccountKey, TxTimestamp) INCLUDE (TransactionTypeKey, SecurityKey, TotalGbp, CashImpactGbp);
CREATE INDEX IX_FT_Security_Time ON dw.FactTransaction(SecurityKey, TxTimestamp) INCLUDE (Shares, TotalGbp);
CREATE INDEX IX_FT_Date          ON dw.FactTransaction(DateKey) INCLUDE (TransactionTypeKey, AccountKey, TotalGbp);
CREATE INDEX IX_FT_Inserted      ON dw.FactTransaction(InsertedAt DESC);
END
GO

/* Corporate actions the broker export cannot express (splits, spin-offs, mergers).
   Seeded in 05_seed_reference.sql; the position engine replays them in time order. */
IF OBJECT_ID(N'dw.CorporateAction') IS NULL
BEGIN
CREATE TABLE dw.CorporateAction
(
    CorporateActionId   INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CorporateAction PRIMARY KEY,
    EffectiveDate       DATE          NOT NULL,
    ActionType          VARCHAR(20)   NOT NULL,   -- SPLIT | SPINOFF | MERGER | CASH_ACQUISITION
    Ticker              NVARCHAR(20)  NOT NULL,
    Ratio               DECIMAL(19,8) NOT NULL,   -- SPLIT: new/old shares; SPINOFF/MERGER: new shares per old share
    NewTicker           NVARCHAR(20)  NULL,
    NewSecurityName     NVARCHAR(200) NULL,
    CostBasisAllocation DECIMAL(9,6)  NOT NULL CONSTRAINT DF_CA_Alloc DEFAULT (0), -- share of old cost moved to new line
    Source              NVARCHAR(200) NULL,
    Notes               NVARCHAR(1000) NULL,
    CONSTRAINT CK_CA_Type CHECK (ActionType IN ('SPLIT','SPINOFF','MERGER','CASH_ACQUISITION')),
    CONSTRAINT UQ_CA UNIQUE (EffectiveDate, ActionType, Ticker)
);
END
GO

/* Current position per account x security (rebuilt by the position engine). Closed lines kept (Quantity = 0). */
IF OBJECT_ID(N'dw.Position') IS NULL
BEGIN
CREATE TABLE dw.Position
(
    AccountKey          INT            NOT NULL CONSTRAINT FK_Pos_Account REFERENCES dw.DimAccount(AccountKey),
    SecurityKey         INT            NOT NULL CONSTRAINT FK_Pos_Security REFERENCES dw.DimSecurity(SecurityKey),
    Quantity            DECIMAL(28,10) NOT NULL,
    CostBasisGbp        DECIMAL(19,4)  NOT NULL,
    AvgCostGbp          DECIMAL(19,6)  NULL,
    RealizedGainGbp     DECIMAL(19,4)  NOT NULL,
    DividendsGbp        DECIMAL(19,4)  NOT NULL,
    FirstBuyAt          DATETIME2(3)   NULL,
    LastActivityAt      DATETIME2(3)   NULL,
    UpdatedAt           DATETIME2(3)   NOT NULL CONSTRAINT DF_Pos_Upd DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_Position PRIMARY KEY (AccountKey, SecurityKey)
);
END
GO

IF OBJECT_ID(N'dw.FactRealizedGain') IS NULL
BEGIN
CREATE TABLE dw.FactRealizedGain
(
    RealizedGainId      BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_FactRealizedGain PRIMARY KEY,
    TransactionId       BIGINT         NULL CONSTRAINT FK_RG_Tx REFERENCES dw.FactTransaction(TransactionId), -- NULL for corporate-action disposals
    AccountKey          INT            NOT NULL,
    SecurityKey         INT            NOT NULL,
    DateKey             INT            NOT NULL,
    DisposedAt          DATETIME2(3)   NOT NULL,
    SharesDisposed      DECIMAL(28,10) NOT NULL,
    ProceedsGbp         DECIMAL(19,4)  NOT NULL,
    CostBasisGbp        DECIMAL(19,4)  NOT NULL,
    GainGbp             DECIMAL(19,4)  NOT NULL,
    BrokerResultGbp     DECIMAL(19,4)  NULL,
    Method              VARCHAR(20)    NOT NULL CONSTRAINT DF_RG_Method DEFAULT ('S104_AVERAGE')
);
CREATE INDEX IX_RG_Date ON dw.FactRealizedGain(DateKey) INCLUDE (AccountKey, GainGbp);
END
GO

/* =========================== REAL-TIME =========================== */
IF OBJECT_ID(N'rt.SecurityPrice') IS NULL
BEGIN
CREATE TABLE rt.SecurityPrice
(
    SecurityKey     INT           NOT NULL CONSTRAINT PK_SecurityPrice PRIMARY KEY
                                  CONSTRAINT FK_SP_Security REFERENCES dw.DimSecurity(SecurityKey),
    PriceGbp        DECIMAL(19,6) NOT NULL,
    PrevCloseGbp    DECIMAL(19,6) NOT NULL,     -- reference for day change
    Source          VARCHAR(20)   NOT NULL,     -- LAST_TRADE | SIMULATED | <provider>
    UpdatedAt       DATETIME2(3)  NOT NULL
);
END
GO

IF OBJECT_ID(N'rt.PriceTick') IS NULL
BEGIN
CREATE TABLE rt.PriceTick
(
    TickId          BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PriceTick PRIMARY KEY NONCLUSTERED,
    SecurityKey     INT           NOT NULL,
    PriceGbp        DECIMAL(19,6) NOT NULL,
    TickTime        DATETIME2(3)  NOT NULL
);
CREATE CLUSTERED INDEX CIX_PriceTick ON rt.PriceTick(TickTime, SecurityKey);
END
GO

IF OBJECT_ID(N'rt.PortfolioSnapshot') IS NULL
BEGIN
CREATE TABLE rt.PortfolioSnapshot
(
    SnapshotId      BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PortfolioSnapshot PRIMARY KEY,
    SnapshotTime    DATETIME2(3)  NOT NULL,
    AccountKey      INT           NULL,          -- NULL = all accounts
    MarketValueGbp  DECIMAL(19,4) NOT NULL,
    CostBasisGbp    DECIMAL(19,4) NOT NULL,
    CashGbp         DECIMAL(19,4) NOT NULL
);
CREATE INDEX IX_Snapshot_Time ON rt.PortfolioSnapshot(SnapshotTime) INCLUDE (AccountKey, MarketValueGbp);
END
GO

IF OBJECT_ID(N'rt.Alert') IS NULL
BEGIN
CREATE TABLE rt.Alert
(
    AlertId         BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Alert PRIMARY KEY,
    CreatedAt       DATETIME2(3)   NOT NULL CONSTRAINT DF_Alert_Created DEFAULT (SYSUTCDATETIME()),
    Severity        VARCHAR(10)    NOT NULL,    -- INFO | WARN | CRITICAL
    AlertType       VARCHAR(40)    NOT NULL,    -- LARGE_TRADE | CONCENTRATION | ISA_ALLOWANCE | OVERSELL | PRICE_MOVE
    AccountKey      INT            NULL,
    SecurityKey     INT            NULL,
    TransactionKey  NVARCHAR(64)   NULL,
    Message         NVARCHAR(500)  NOT NULL,
    IsAcknowledged  BIT            NOT NULL CONSTRAINT DF_Alert_Ack DEFAULT (0)
);
CREATE INDEX IX_Alert_Created ON rt.Alert(CreatedAt DESC);
END
GO
