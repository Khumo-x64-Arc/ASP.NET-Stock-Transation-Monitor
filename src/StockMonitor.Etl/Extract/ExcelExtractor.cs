using System.Globalization;
using ExcelDataReader;
using StockMonitor.Core.Model;

namespace StockMonitor.Etl.Extract;

public sealed class SheetConfig
{
    public string Name { get; set; } = "";
    public int AccountNumber { get; set; }
}

public sealed class ExtractResult
{
    public List<RawTransactionRow> Rows { get; } = new();
    public List<DataQualityFinding> Findings { get; } = new();
    public Dictionary<string, int> RowsPerSheet { get; } = new();
    public int BlankRowsSkipped { get; set; }
}

/// <summary>
/// Reads the per-account broker export sheets. Columns are located by header name (with aliases), never by
/// position, because the layout drifts between years (2024 drops "Date", adds "ISIN", removes several fee columns).
/// Every cell is rendered to an invariant string so the landing table is a faithful copy of the source.
/// </summary>
public sealed class ExcelExtractor
{
    private static readonly Dictionary<string, Action<RawTransactionRow, string?>> Map =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Account Number"] = (r, v) => r.AccountNumber = v,
            ["Action"] = (r, v) => r.Action = v,
            ["Date"] = (r, v) => r.TxDate = v,
            ["Transaction Date"] = (r, v) => r.TxDate = v,
            ["Time"] = (r, v) => r.TxTime = v,
            ["ISIN"] = (r, v) => r.Isin = v,
            ["Ticker"] = (r, v) => r.Ticker = v,
            ["Name"] = (r, v) => r.SecurityName = v,
            ["No. of shares"] = (r, v) => r.Shares = v,
            ["Price / share"] = (r, v) => r.PricePerShare = v,
            ["Price / share (USD)"] = (r, v) => r.PricePerShare = v,
            ["Currency (Price / share)"] = (r, v) => r.PriceCurrency = v,
            ["Exchange rate"] = (r, v) => r.ExchangeRate = v,
            ["Result"] = (r, v) => r.Result = v,
            ["Result (GBP)"] = (r, v) => r.Result = v,
            ["Result of Sell"] = (r, v) => r.Result = v,
            ["Total"] = (r, v) => r.Total = v,
            ["Total (GBP)"] = (r, v) => r.Total = v,
            ["Total GBP"] = (r, v) => r.Total = v,
            ["Currency (Total)"] = (r, v) => r.TotalCurrency = v,
            ["Withholding tax"] = (r, v) => r.WithholdingTax = v,
            ["Currency (Withholding tax)"] = (r, v) => r.WithholdingTaxCurrency = v,
            ["Charge amount"] = (r, v) => r.ChargeAmount = v,
            ["Charge amount (GBP)"] = (r, v) => r.ChargeAmount = v,
            ["Deposit fee"] = (r, v) => r.DepositFee = v,
            ["Transaction fee"] = (r, v) => r.TransactionFee = v,
            ["Finra fee"] = (r, v) => r.FinraFee = v,
            ["Currency conversion fee"] = (r, v) => r.CurrencyConversionFee = v,
            ["Currency conversion fee (GBP)"] = (r, v) => r.CurrencyConversionFee = v,
            ["Stamp duty reserve tax"] = (r, v) => r.StampDutyReserveTax = v,
            ["Notes"] = (r, v) => r.Notes = v,
            ["ID"] = (r, v) => r.BrokerId = v,
        };

    // Currency columns for fees are constant GBP in this dataset and are intentionally not landed.
    private static readonly HashSet<string> KnownIgnored = new(StringComparer.OrdinalIgnoreCase)
    {
        "Currency (Result)", "Currency (Charge amount)", "Currency (Deposit fee)", "Currency (Transaction fee)",
        "Currency (Finra fee)", "Currency (Currency conversion fee)", "Currency (Stamp duty reserve tax)"
    };

    static ExcelExtractor() => System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

    public ExtractResult Extract(string path, IReadOnlyList<SheetConfig> sheets)
    {
        var result = new ExtractResult();
        var wanted = sheets.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = ExcelReaderFactory.CreateReader(stream);

        do
        {
            if (!wanted.TryGetValue(reader.Name, out var cfg)) continue;
            found.Add(reader.Name);

            if (!reader.Read()) continue;                       // header row
            var headers = new string?[reader.FieldCount];
            for (int i = 0; i < reader.FieldCount; i++)
                headers[i] = reader.GetValue(i)?.ToString()?.Trim().Replace("\n", " ");

            var unmapped = new Dictionary<int, int>();          // column index -> non-empty cell count
            int excelRow = 1, count = 0;

            while (reader.Read())
            {
                excelRow++;
                var row = new RawTransactionRow
                {
                    SourceSheet = reader.Name,
                    SourceRow = excelRow,
                    AccountNumber = cfg.AccountNumber.ToString(CultureInfo.InvariantCulture)
                };

                bool any = false;
                for (int i = 0; i < reader.FieldCount && i < headers.Length; i++)
                {
                    var text = Render(reader.GetValue(i));
                    if (text is null) continue;
                    any = true;

                    var h = headers[i];
                    if (h is not null && Map.TryGetValue(h, out var set)) set(row, text);
                    else if (h is null || !KnownIgnored.Contains(h)) unmapped[i] = unmapped.GetValueOrDefault(i) + 1;
                }

                if (!any) { result.BlankRowsSkipped++; continue; }
                result.Rows.Add(row);
                count++;
            }

            result.RowsPerSheet[reader.Name] = count;
            foreach (var (col, n) in unmapped)
                result.Findings.Add(new DataQualityFinding(Severity.Info, "X01 UNMAPPED_COLUMN", null, reader.Name, null,
                    $"Column {col + 1} '{headers[col] ?? "(no header)"}' has {n} non-empty cells and is not landed"));
        } while (reader.NextResult());

        foreach (var missing in wanted.Keys.Where(k => !found.Contains(k)))
            result.Findings.Add(new DataQualityFinding(Severity.Error, "X02 SHEET_NOT_FOUND", null, missing, null,
                $"Configured sheet '{missing}' does not exist in the workbook"));

        return result;
    }

    /// <summary>Invariant rendering of Excel cell values.</summary>
    public static string? Render(object? v) => v switch
    {
        null => null,
        DBNull => null,
        string s => string.IsNullOrWhiteSpace(s) ? null : s.Trim(),
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
        TimeSpan ts => ts.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture),
        double d => double.IsNaN(d) ? null : d.ToString("R", CultureInfo.InvariantCulture),
        bool b => b ? "TRUE" : "FALSE",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString()
    };
}
