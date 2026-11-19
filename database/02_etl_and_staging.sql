/* =====================================================================
   StockMonitor - 02: ETL control tables + staging
   ===================================================================== */
USE StockMonitor;
GO

/* ---------- ETL control ---------- */
IF OBJECT_ID(N'etl.BatchLog') IS NULL
BEGIN
CREATE TABLE etl.BatchLog
(
    BatchId         INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_BatchLog PRIMARY KEY,
    BatchType       VARCHAR(10)    NOT NULL CONSTRAINT DF_BatchLog_Type DEFAULT ('FILE'),  -- FILE | LIVE
    SourceFile      NVARCHAR(400)  NULL,
    SourceFileHash  CHAR(64)       NULL,          -- SHA-256 of the workbook, used to skip identical re-runs
    StartedAt       DATETIME2(3)   NOT NULL CONSTRAINT DF_BatchLog_Start DEFAULT (SYSUTCDATETIME()),
    CompletedAt     DATETIME2(3)   NULL,
    Status          VARCHAR(20)    NOT NULL CONSTRAINT DF_BatchLog_Status DEFAULT ('RUNNING'), -- RUNNING|SUCCEEDED|FAILED|SKIPPED
    RowsExtracted   INT NOT NULL CONSTRAINT DF_BatchLog_RE DEFAULT (0),
    RowsCleansed    INT NOT NULL CONSTRAINT DF_BatchLog_RC DEFAULT (0),
    RowsRejected    INT NOT NULL CONSTRAINT DF_BatchLog_RR DEFAULT (0),
    RowsLoaded      INT NOT NULL CONSTRAINT DF_BatchLog_RL DEFAULT (0),
    RowsQueued      INT NOT NULL CONSTRAINT DF_BatchLog_RQ DEFAULT (0),   -- rows held back for live replay
    Message         NVARCHAR(4000) NULL
);
END
GO

IF OBJECT_ID(N'etl.RejectedRow') IS NULL
BEGIN
CREATE TABLE etl.RejectedRow
(
    RejectedRowId   BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_RejectedRow PRIMARY KEY,
    BatchId         INT            NOT NULL CONSTRAINT FK_RejectedRow_Batch REFERENCES etl.BatchLog(BatchId),
    SourceSheet     NVARCHAR(50)   NULL,
    SourceRow       INT            NULL,
    Reason          NVARCHAR(400)  NOT NULL,
    RawPayload      NVARCHAR(MAX)  NULL,          -- JSON of the raw row for replay/fix
    CreatedAt       DATETIME2(3)   NOT NULL CONSTRAINT DF_RejectedRow_Created DEFAULT (SYSUTCDATETIME())
);
END
GO

IF OBJECT_ID(N'etl.DataQualityIssue') IS NULL
BEGIN
CREATE TABLE etl.DataQualityIssue
(
    IssueId         BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_DataQualityIssue PRIMARY KEY,
    BatchId         INT            NULL,
    Severity        VARCHAR(10)    NOT NULL,      -- INFO | WARN | ERROR
    RuleCode        VARCHAR(50)    NOT NULL,      -- e.g. WITHDRAWAL_SIGN_FLIPPED, OVERSELL_CLAMPED
    TransactionKey  NVARCHAR(64)   NULL,
    SourceSheet     NVARCHAR(50)   NULL,
    SourceRow       INT            NULL,
    Detail          NVARCHAR(1000) NULL,
    CreatedAt       DATETIME2(3)   NOT NULL CONSTRAINT DF_DQI_Created DEFAULT (SYSUTCDATETIME())
);
CREATE INDEX IX_DQI_Batch ON etl.DataQualityIssue(BatchId, RuleCode);
END
GO

/* ---------- Landing: raw, untyped copy of every source cell ---------- */
IF OBJECT_ID(N'stg.RawTransaction') IS NULL
BEGIN
CREATE TABLE stg.RawTransaction
(
    RawId                   BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_RawTransaction PRIMARY KEY,
    BatchId                 INT            NOT NULL,
    SourceSheet             NVARCHAR(50)   NOT NULL,
    SourceRow               INT            NOT NULL,
    AccountNumber           NVARCHAR(20)   NULL,
    [Action]                NVARCHAR(200)  NULL,
    TxDate                  NVARCHAR(50)   NULL,
    TxTime                  NVARCHAR(50)   NULL,
    Isin                    NVARCHAR(50)   NULL,
    Ticker                  NVARCHAR(50)   NULL,
    SecurityName            NVARCHAR(200)  NULL,
    Shares                  NVARCHAR(50)   NULL,
    PricePerShare           NVARCHAR(50)   NULL,
    PriceCurrency           NVARCHAR(10)   NULL,
    ExchangeRate            NVARCHAR(50)   NULL,
    Result                  NVARCHAR(50)   NULL,
    Total                   NVARCHAR(50)   NULL,
    TotalCurrency           NVARCHAR(10)   NULL,
    WithholdingTax          NVARCHAR(50)   NULL,
    WithholdingTaxCurrency  NVARCHAR(10)   NULL,
    ChargeAmount            NVARCHAR(400)  NULL,
    DepositFee              NVARCHAR(50)   NULL,
    TransactionFee          NVARCHAR(50)   NULL,
    FinraFee                NVARCHAR(50)   NULL,
    CurrencyConversionFee   NVARCHAR(50)   NULL,
    StampDutyReserveTax     NVARCHAR(50)   NULL,
    Notes                   NVARCHAR(1000) NULL,
    BrokerId                NVARCHAR(100)  NULL,
    RowHash                 BINARY(32)     NOT NULL,   -- SHA-256 over the raw values (change detection)
    LoadedAt                DATETIME2(3)   NOT NULL CONSTRAINT DF_Raw_Loaded DEFAULT (SYSUTCDATETIME())
);
CREATE INDEX IX_Raw_Batch ON stg.RawTransaction(BatchId);
END
GO

/* ---------- Cleansed, typed staging (output of the C# cleanser) ---------- */
IF OBJECT_ID(N'stg.CleanTransaction') IS NULL
BEGIN
CREATE TABLE stg.CleanTransaction
(
    CleanId                 BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CleanTransaction PRIMARY KEY,
    BatchId                 INT            NOT NULL,
    TransactionKey          NVARCHAR(64)   NOT NULL,   -- broker ID, or SYN-<sha1> for rows without one
    IsSyntheticKey          BIT            NOT NULL,
    AccountNumber           INT            NOT NULL,
    TxTimestamp             DATETIME2(3)   NOT NULL,
    ActionCode              VARCHAR(20)    NOT NULL,   -- BUY|SELL|DIVIDEND|DEPOSIT|WITHDRAWAL|INTEREST
    ActionSubType           NVARCHAR(80)   NULL,       -- dividend flavour, e.g. 'Ordinary', 'Return of capital non us'
    Ticker                  NVARCHAR(20)   NULL,
    Isin                    CHAR(12)       NULL,
    SecurityName            NVARCHAR(200)  NULL,
    Shares                  DECIMAL(28,10) NULL,
    PriceLocal              DECIMAL(19,6)  NULL,
    PriceCurrency           CHAR(3)        NULL,       -- USD | GBX | GBP
    FxRate                  DECIMAL(19,8)  NULL,       -- local units per 1 GBP (GBX = 100)
    PriceGbp                DECIMAL(19,6)  NULL,
    TotalGbp                DECIMAL(19,4)  NOT NULL,   -- always positive; direction comes from ActionCode
    BrokerResultGbp         DECIMAL(19,4)  NULL,
    WithholdingTax          DECIMAL(19,4)  NULL,
    WithholdingTaxCurrency  CHAR(3)        NULL,
    FxFeeGbp                DECIMAL(19,4)  NULL,
    StampDutyGbp            DECIMAL(19,4)  NULL,
    TransactionFeeGbp       DECIMAL(19,4)  NULL,
    FinraFeeGbp             DECIMAL(19,4)  NULL,
    DepositFeeGbp           DECIMAL(19,4)  NULL,
    Notes                   NVARCHAR(1000) NULL,
    IsCorporateActionArtifact BIT          NOT NULL CONSTRAINT DF_Clean_Artifact DEFAULT (0),
    SourceSheet             NVARCHAR(50)   NULL,
    SourceRow               INT            NULL
);
CREATE INDEX IX_Clean_Batch ON stg.CleanTransaction(BatchId) INCLUDE (TransactionKey);
END
GO

/* ---------- Replay queue: cleansed rows after the cutoff, released live by the web app ---------- */
IF OBJECT_ID(N'stg.ReplayQueue') IS NULL
BEGIN
CREATE TABLE stg.ReplayQueue
(
    ReplayId                BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ReplayQueue PRIMARY KEY,
    BatchId                 INT            NOT NULL,
    TransactionKey          NVARCHAR(64)   NOT NULL CONSTRAINT UQ_Replay_Key UNIQUE,
    TxTimestamp             DATETIME2(3)   NOT NULL,
    Payload                 NVARCHAR(MAX)  NOT NULL,   -- JSON of the CleanTransaction
    ReleasedAt              DATETIME2(3)   NULL
);
CREATE INDEX IX_Replay_Pending ON stg.ReplayQueue(ReleasedAt, TxTimestamp);
END
GO
