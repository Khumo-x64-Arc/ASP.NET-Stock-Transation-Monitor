# Data preparation

This document records what the source workbook contains, the problems profiling found, and every rule the
pipeline applies to fix them. Each rule has a code (`R01`…), and that code is written to
`etl.DataQualityIssue` / `etl.RejectedRow`, so any value in the warehouse can be traced back to the rule that
changed it and to the original sheet and row.

## 1. Source inventory

`data/Open Stock Transaction Data 2020-2024.xlsx` has **20 sheets**:

| Sheet(s) | Rows | Role | Used? |
|---|---|---|---|
| `Description` | 54 | Data dictionary (column meanings, account mapping 2129 = ISA, 2131 = GIA) | Documentation only |
| `2020` | 157 | Broker export, GIA (the ISA opens in 2021) | **Yes** → account 2131 |
| `Inv2021`–`Inv2024` | 492 / 406 / 466 / 236 | Broker export, GIA | **Yes** → account 2131 |
| `ISA2021`–`ISA2024` | 339 / 351 / 560 / 292 | Broker export, ISA | **Yes** → account 2129 |
| `2021`–`2024` | — | Per-year union of ISA + GIA, **no account column** | No (duplicates) |
| `2020-2023`, `2020-2023-V2`, `2020-2024`, `2020-2024 with Splits` | 2,771–3,299 | Analyst-consolidated copies, some with extra hand-made columns | No (duplicates); used to cross-check split factors |
| `Company MetaData-2023/2024` | 86 | Per-ticker summary incl. split dates/ratios | Reference for `dw.CorporateAction` (after repair, §3.6) |

**Decision:** load only the 9 per-account sheets. They are the most granular source, they are the only sheets that
identify the account without guessing, and they add up exactly to the 3,299 rows in the consolidated
`2020-2024` sheet (157 + 3,142). Loading the consolidated sheets as well would double-count every transaction.
The sheet-to-account mapping is configuration (`Etl:Sheets` in `appsettings.json`), not code.

## 2. Profile (3,299 rows, 30 Apr 2020 – 1 Jul 2024)

| Action | Rows | Total (GBP) |
|---|---:|---:|
| Market buy | 1,365 | 404,132.84 |
| Dividend (8 sub-types) | 1,207 | 14,408.61 |
| Market sell | 287 | 215,115.72 |
| Deposit | 268 | 252,007.76 |
| Interest on cash | 110 | 1.50 |
| Withdrawal | 62 | 78,024.17 (after sign fix) |

- 85 tickers. ISIN is present only in the 2024 exports (474 rows, 35 distinct ISINs, all pass the ISO 6166 check digit).
- Price currency: USD 2,855 rows, GBX (pence) 4 rows (National Grid), none 440 (cash movements).
- Fees: FX conversion £728.23, deposit fees £12.25, transaction + FINRA fees £1.19, stamp duty £0.05.
- Dividend sub-types: Ordinary 889, Dividend 154, *Dividends paid by us corporations* 138, Return of capital 9,
  Bonus 7, *Dividends paid by foreign corporations* 6, Demerger 2, Ordinary manufactured payment 2.
- Cash check (Σ signed cash impact): ISA **+£117.82**, GIA **−£741.24**. The GIA shortfall is the same gap
  found by the position engine's oversell check (§3.8): some 2024 GIA buys are missing from the export.

## 3. Quality issues found and how each is handled

### 3.1 Layout drift between years
The broker changed the export format over time, so columns are matched by **header name with aliases**, never by position:

| Feature | 2020–2023 sheets | 2024 sheets |
|---|---|---|
| Date | `Date` column (date) | *no Date column* |
| Time | time-of-day only | full timestamp **with milliseconds** |
| ISIN | absent | present |
| Fee columns | `Charge amount`, `Deposit fee`, `Transaction fee`, `Finra fee`, `Stamp duty…` (varies by year) | only `Currency conversion fee` |
| Total header | `Total` | `Total` (consolidated sheets use `Total (GBP)` / `Total GBP`, also aliased) |

Rule **R03** builds the timestamp: if `Time` contains a full date (year > 1900) it is used as is (2024 layout).
Otherwise `Date` + time-of-day is used. A time-only string is never parsed as "today at 17:20" (there is a unit test for this).
Excel time cells arrive as `1899-12-30 hh:mm:ss`, and the time-of-day is taken from them.

Unknown columns that contain data are reported as **X01 UNMAPPED_COLUMN** (INFO). A configured sheet that does not exist is
**X02 SHEET_NOT_FOUND** (ERROR, which aborts the run).

### 3.2 Action text → canonical codes (R01, R02)
`Market buy/sell` (and `Limit`/`Stop` variants) map to `BUY`/`SELL`, `Deposit`, `Withdrawal`, `Interest on cash`
map to `DEPOSIT`/`WITHDRAWAL`/`INTEREST`, and `Dividend (<x>)` maps to `DIVIDEND` with `<x>` kept as `ActionSubType`. A missing
action is rejected (R01). An unrecognised action is rejected (R02) instead of being guessed. New dividend
sub-types are added to `dw.DimTransactionType` automatically at load.

### 3.3 Placeholders and text in numeric columns (R06, R13)
- `Exchange rate` holds the string **"Not available"** on all 1,207 dividend rows. It is a recognised null token
  (`null`, `n/a`, `-`, `not available`, …) and becomes `NULL` with no warning.
- Any other non-numeric value in a numeric field becomes `NULL` with a **R06 NON_NUMERIC** warning, so nothing is
  dropped silently.
- In the analyst-edited copies, one `Charge amount` cell holds an analyst comment ("This was not a market sell but rather a
  company acquisition…"). **R13** moves free text from that column to `Notes`.

### 3.4 Sign conventions (R07, R08)
The 2024 GIA export records **3 withdrawals as negative** amounts, while every earlier year records them as positive.
All amounts are stored as magnitudes (`TotalGbp ≥ 0`), and direction comes from the transaction type's
`CashDirection` (+1 or −1). Withdrawals are logged as **R07** (INFO). Any other negative total is logged as **R08** (WARN).
Withholding tax and fees are also stored as absolute values. Without this rule the GIA cash balance would be off
by about £39k.

### 3.5 Currency normalisation (R12)
- All `Total`, `Result` and fee columns are already in GBP.
- `PriceGbp` is derived: for **GBX** it is price ÷ 100 (pence to pounds), for **GBP** it is the price as is, and for
  **USD** it is price ÷ exchange rate (the broker quotes the rate as USD per GBP).
- If a USD trade has no rate, the effective price is `Total ÷ Shares` and **R12 FX_DERIVED** is logged. (This does not happen in this
  file. The only two trades without a rate are the zero-value PBCT rows in §3.7.)
- Withholding tax is kept in its own currency (USD, or GBX in two cases) because no rate is supplied for dividends. Analytics
  reports it per currency and does not mix it into GBP.

### 3.6 Corporate actions: repairing the metadata sheet
The broker export has no event type for splits, spin-offs or mergers, so these come from `Company MetaData-2024`.
That sheet was **damaged by Excel's type inference**:

| Symptom | Example | Repair |
|---|---|---|
| Ratio "1/4" or "4/1" turned into a date | NLY ratio shows `2024-01-04`, AAPL shows `2024-04-01` | Read back as 1-for-4 / 4-for-1 |
| `dd/mm/yyyy` text read as `mm/dd` | AT&T `11/04/2022` became 4 Nov 2022 | 11 Apr 2022 (WarnerMedia spin-off) |
| Same | NYMT `09/03/2023` became 3 Sep 2023 | 9 Mar 2023 |

Each repaired date was checked against the `2020-2024 with Splits` sheet: the split adjustment factor changes exactly at
these boundaries (for example, AAPL rows are ×4 up to Jul 2020 and ×1 from Feb 2021). The result is `05_seed_reference.sql`:

| Date | Type | Terms |
|---|---|---|
| 2020-08-31 | SPLIT | AAPL 4:1 |
| 2020-10-27 | SPLIT | NEE 4:1 |
| 2020-10-30 | CASH_ACQUISITION | LVGO (Teladoc). The cash leg arrived as a *Return of capital* row |
| 2021-11-12 | SPINOFF | O → 0.1 ONL, 3.1% of cost (dataset factor 1.032) |
| 2022-04-01 | MERGER | PBCT → 0.118 MTB, 100% of cost rolls over |
| 2022-04-11 | SPINOFF | T → 0.241917 WBD, 24.5% of cost (dataset factor 1.324) |
| 2022-07-18 | SPLIT | GOOGL 20:1 |
| 2022-09-26 | SPLIT | NLY 1:4 (reverse) |
| 2023-03-09 | SPLIT | NYMT 1:4 (reverse) |
| 2024-07-15 | SPLIT | AVGO 10:1 (after the last transaction) |

Evidence that these are needed: MTB, WBD and ONL are **sold** in the export but never bought. Without these events the
engine would produce negative holdings. With them, the sold quantities match exactly (for example, 181 PBCT × 0.118 = 21.358 MTB =
0.812 + 0.546 + 5 + 15 sold).

The MRK → Organon spin-off appears in the metadata, but **no OGN shares ever appear in the export**, so it is not
seeded. Adding it would create a holding the broker never reported.

### 3.7 Corporate-action artefacts (R14)
The broker recorded the PBCT acquisition as two **"Market sell" rows with price 0 and total 0** (134 shares in the GIA and
47 in the ISA, on 5 Apr 2022). Treating these as real sales would book a fictitious loss of about £1.9k. They are
loaded (for lineage) with `IsCorporateActionArtifact = 1`, and the position engine skips them because the MERGER
event has already moved the shares into MTB.

### 3.8 Keys, duplicates and missing history (R15, PARTIAL_FILL_PAIR, ENGINE_OVERSELL_CLAMPED)
- **All 1,207 dividend rows have no broker ID.** They get a deterministic surrogate key:
  `SYN-` + SHA-1(account | timestamp | action | sub-type | ticker | shares | total). Re-running produces the same key,
  and there are no collisions in this file.
- **7 pairs of rows** have identical account, time, ticker, quantity and total but **different broker IDs** (for example,
  four 1-share GMRE buys at 13:30:01). These are partial fills, not duplicates, so they are kept and logged as INFO.
  A repeated *broker ID* inside a batch is a true duplicate and is rejected (`DUPLICATE_KEY`).
- **4 GIA sells in 2024 exceed the recorded holding** (GMRE, AMAT, COST, HD by 1–19 shares). The export is
  missing some earlier GIA acquisitions, which matches the −£741 cash gap in §2. The engine clamps the pool at zero, treats
  the unmatched shares as zero cost, and logs **ENGINE_OVERSELL_CLAMPED** (WARN). The live path also raises an `OVERSELL` alert.

### 3.9 Text hygiene and identifiers (R09, R10)
Tickers are trimmed and upper-cased, and names have whitespace collapsed. Security fields are removed from cash rows. Trades
and dividends without a ticker are rejected (R09). ISINs are validated with the ISO 6166 Luhn check. An invalid ISIN is
dropped with a warning (R10) rather than polluting `DimSecurity`.

### 3.10 Range checks (R04, R05, R11)
The account must be an integer (R04), `Total` must be present (R05), and trades need shares > 0 and price ≥ 0 (R11). A
timestamp more than a day in the future is rejected (R03 FUTURE_TIMESTAMP).

## 4. Rule catalogue

| Code | Severity / outcome | Rule |
|---|---|---|
| X01 | INFO | Unmapped non-empty column in a source sheet |
| X02 | ERROR → abort | Configured sheet missing |
| R01 | Reject | Missing action |
| R02 | Reject | Unknown action |
| R03 | Reject | Unparseable or future timestamp |
| R04 | Reject | Non-integer account |
| R05 | Reject | Missing total |
| R06 | WARN, value → NULL | Non-numeric value in numeric column |
| R07 | INFO, abs() | Negative withdrawal |
| R08 | WARN, abs() | Other negative total |
| R09 | Reject | Trade/dividend without ticker |
| R10 | WARN, value → NULL | Invalid ISIN |
| R11 | Reject | Trade with qty ≤ 0 or negative price |
| R12 | WARN | GBP price derived from Total / Shares |
| R13 | WARN | Free text in ChargeAmount moved to Notes |
| R14 | WARN, flagged | Zero-value sell = corporate-action artefact |
| DUPLICATE_KEY | Reject | Same broker ID twice in a batch |
| PARTIAL_FILL_PAIR | INFO | Same economics, different broker IDs (kept) |
| ENGINE_OVERSELL_CLAMPED | WARN | Sale exceeds recorded holding |
| ENGINE_UNKNOWN_CA | ERROR | Unknown corporate action type |

## 5. Expected result of a full load (`--cutoff none`)

| Check | Value |
|---|---|
| Extracted / cleansed / rejected | 3,299 / 3,299 / 0 |
| Synthetic keys | 1,207 |
| R07 withdrawal sign fixes | 3 |
| R14 artefacts | 2 |
| PARTIAL_FILL_PAIR | 7 |
| ENGINE_OVERSELL_CLAMPED | 4 |
| Control totals stg = dw per action | all equal |
| Open holdings at 1 Jul 2024 | 24 tickers |

**Validation against the dataset's own notes.** The Description sheet lists 22 tickers held on 17 Sep 2024.
At the last transaction in the file (1 Jul 2024), the engine holds 21 of them. The 22nd, BKNG, was bought after the export
ends. It also holds AOS, DLR and MRK, which the same sheet lists as "sold by 17 Sep 2024", so they were sold
after the export ends. Every difference is explained by the gap between the two dates.

With the default `--cutoff 2024-01-01`, 2,771 rows load in the batch and 528 are queued in `stg.ReplayQueue` for the live feed.

## 6. Known limitations
- Realised gains use Section 104 pooling only. HMRC's same-day and 30-day ("bed and breakfast") matching rules
  are not applied. This matters for 2024, when the GIA sold and the ISA immediately re-bought the same stocks
  ("Bed & ISA"), although those are different accounts and so outside the 30-day rule. The broker's own `Result` is kept
  next to the computed gain for comparison.
- Spin-off cost allocations come from the dataset's own adjustment factors, not from official HMRC or issuer
  apportionment notices.
- ISA deposits above £20k in a tax year usually reflect transfers-in from another ISA. The export cannot tell the
  two apart, so the ISA-allowance alert says to check.
