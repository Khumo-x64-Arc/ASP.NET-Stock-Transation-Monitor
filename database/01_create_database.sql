/* =====================================================================
   StockMonitor - 01: database + schemas
   Target: SQL Server 2019+ / Azure SQL (compat level 150+)
   Run with sqlcmd:  sqlcmd -S localhost -U sa -P <pwd> -i 01_create_database.sql
   ===================================================================== */
IF DB_ID(N'StockMonitor') IS NULL
BEGIN
    CREATE DATABASE StockMonitor;
END
GO

ALTER DATABASE StockMonitor SET RECOVERY SIMPLE;   -- dev default; use FULL in prod
GO
ALTER DATABASE StockMonitor SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;  -- readers don't block the live ingester
GO

USE StockMonitor;
GO

-- stg : landing + cleansed staging (truncate/reload per batch)
-- etl : pipeline control, lineage, rejects, data-quality log
-- dw  : conformed star schema (dimensions + facts) and derived positions
-- rt  : real-time / operational tables (prices, ticks, snapshots, alerts)
IF SCHEMA_ID(N'stg') IS NULL EXEC (N'CREATE SCHEMA stg AUTHORIZATION dbo;');
IF SCHEMA_ID(N'etl') IS NULL EXEC (N'CREATE SCHEMA etl AUTHORIZATION dbo;');
IF SCHEMA_ID(N'dw')  IS NULL EXEC (N'CREATE SCHEMA dw  AUTHORIZATION dbo;');
IF SCHEMA_ID(N'rt')  IS NULL EXEC (N'CREATE SCHEMA rt  AUTHORIZATION dbo;');
GO
