/* =====================================================================
   StockMonitor - 05: reference data
   ===================================================================== */
USE StockMonitor;
GO

/* Accounts (from the dataset's Description sheet) */
MERGE dw.DimAccount AS t
USING (VALUES
    (2129, 'ISA', N'Stocks & Shares ISA (2129)', 1),
    (2131, 'INV', N'General Investment Account (2131)', 0)
) AS s (AccountNumber, AccountType, AccountName, IsTaxSheltered)
ON t.AccountNumber = s.AccountNumber
WHEN MATCHED THEN UPDATE SET AccountType = s.AccountType, AccountName = s.AccountName, IsTaxSheltered = s.IsTaxSheltered
WHEN NOT MATCHED THEN INSERT (AccountNumber, AccountType, AccountName, IsTaxSheltered)
                      VALUES (s.AccountNumber, s.AccountType, s.AccountName, s.IsTaxSheltered);
GO

/* Transaction types (new dividend flavours are added automatically by usp_LoadWarehouse) */
MERGE dw.DimTransactionType AS t
USING (VALUES
    ('BUY',        N'',                                      'TRADE',  -1, N'Market buy'),
    ('SELL',       N'',                                      'TRADE',   1, N'Market sell'),
    ('DEPOSIT',    N'',                                      'CASH',    1, N'Deposit'),
    ('WITHDRAWAL', N'',                                      'CASH',   -1, N'Withdrawal'),
    ('INTEREST',   N'',                                      'INCOME',  1, N'Interest on cash'),
    ('DIVIDEND',   N'Ordinary',                              'INCOME',  1, N'Dividend (Ordinary)'),
    ('DIVIDEND',   N'Dividend',                              'INCOME',  1, N'Dividend'),
    ('DIVIDEND',   N'Dividends paid by us corporations',     'INCOME',  1, N'Dividend (US corporations)'),
    ('DIVIDEND',   N'Dividends paid by foreign corporations','INCOME',  1, N'Dividend (Foreign corporations)'),
    ('DIVIDEND',   N'Return of capital non us',              'INCOME',  1, N'Dividend (Return of capital)'),
    ('DIVIDEND',   N'Bonus',                                 'INCOME',  1, N'Dividend (Bonus)'),
    ('DIVIDEND',   N'Demerger',                              'INCOME',  1, N'Dividend (Demerger)'),
    ('DIVIDEND',   N'Ordinary manufactured payment',         'INCOME',  1, N'Dividend (Manufactured payment)')
) AS s (ActionCode, ActionSubType, Category, CashDirection, DisplayName)
ON t.ActionCode = s.ActionCode AND t.ActionSubType = s.ActionSubType
WHEN MATCHED THEN UPDATE SET Category = s.Category, CashDirection = s.CashDirection, DisplayName = s.DisplayName
WHEN NOT MATCHED THEN INSERT (ActionCode, ActionSubType, Category, CashDirection, DisplayName)
                      VALUES (s.ActionCode, s.ActionSubType, s.Category, s.CashDirection, s.DisplayName);
GO

/* Corporate actions.
   The Company MetaData sheets record these, but Excel corrupted many cells on import:
     - ratios such as "1/4" and "4/1" were converted to dates (shown as 2024-01-04 / 2024-04-01)
     - dd/mm/yyyy strings were read as mm/dd (e.g. AT&T "11/04/2022" became 2022-11-04)
   The values below were repaired and cross-checked against the "with Splits" sheet, where the
   split adjustment factor changes exactly at these dates. CostBasisAllocation for spin-offs is
   derived from the dataset's own price-adjustment factors (1 - 1/factor). */
MERGE dw.CorporateAction AS t
USING (VALUES
    (CAST('2020-08-31' AS DATE), 'SPLIT',            N'AAPL',  4.0,       NULL,   NULL,                        0.000000, N'4-for-1 split'),
    (CAST('2020-10-27' AS DATE), 'SPLIT',            N'NEE',   4.0,       NULL,   NULL,                        0.000000, N'4-for-1 split'),
    (CAST('2020-10-30' AS DATE), 'CASH_ACQUISITION', N'LVGO',  0.0,       NULL,   NULL,                        0.000000, N'Acquired by Teladoc; cash leg arrived as a Return-of-capital row on 2020-11-04'),
    (CAST('2021-11-12' AS DATE), 'SPINOFF',          N'O',     0.1,       N'ONL', N'Orion Office REIT',        0.031008, N'1 ONL per 10 O (Realty Income / VEREIT merger); factor 1.032'),
    (CAST('2022-04-01' AS DATE), 'MERGER',           N'PBCT',  0.118,     N'MTB', N'M&T Bank',                 1.000000, N'People''s United acquired by M&T: 0.118 MTB per PBCT. Broker booked it as zero-value sells on 2022-04-05'),
    (CAST('2022-04-11' AS DATE), 'SPINOFF',          N'T',     0.241917,  N'WBD', N'Warner Bros Discovery',    0.244713, N'AT&T WarnerMedia spin-off; factor 1.324'),
    (CAST('2022-07-18' AS DATE), 'SPLIT',            N'GOOGL', 20.0,      NULL,   NULL,                        0.000000, N'20-for-1 split'),
    (CAST('2022-09-26' AS DATE), 'SPLIT',            N'NLY',   0.25,      NULL,   NULL,                        0.000000, N'1-for-4 reverse split'),
    (CAST('2023-03-09' AS DATE), 'SPLIT',            N'NYMT',  0.25,      NULL,   NULL,                        0.000000, N'1-for-4 reverse split'),
    (CAST('2024-07-15' AS DATE), 'SPLIT',            N'AVGO',  10.0,      NULL,   NULL,                        0.000000, N'10-for-1 split (after the last transaction in the file)')
) AS s (EffectiveDate, ActionType, Ticker, Ratio, NewTicker, NewSecurityName, CostBasisAllocation, Notes)
ON t.EffectiveDate = s.EffectiveDate AND t.ActionType = s.ActionType AND t.Ticker = s.Ticker
WHEN MATCHED THEN UPDATE SET Ratio = s.Ratio, NewTicker = s.NewTicker, NewSecurityName = s.NewSecurityName,
                             CostBasisAllocation = s.CostBasisAllocation, Notes = s.Notes
WHEN NOT MATCHED THEN INSERT (EffectiveDate, ActionType, Ticker, Ratio, NewTicker, NewSecurityName, CostBasisAllocation, Source, Notes)
                      VALUES (s.EffectiveDate, s.ActionType, s.Ticker, s.Ratio, s.NewTicker, s.NewSecurityName,
                              s.CostBasisAllocation, N'Company MetaData-2024 (repaired)', s.Notes);
GO

EXEC etl.usp_EnsureDimDate @From = '2020-01-01', @To = '2027-12-31';
GO
