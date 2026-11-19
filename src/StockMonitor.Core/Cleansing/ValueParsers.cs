using System.Globalization;

namespace StockMonitor.Core.Cleansing;

/// <summary>Tolerant parsers for the messy values found in broker exports.</summary>
public static class ValueParsers
{
    private static readonly string[] NullTokens = { "", "null", "nan", "n/a", "na", "-", "not available", "none" };

    private static readonly string[] DateFormats =
    {
        "yyyy-MM-dd", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.fff", "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss.fff", "yyyy-MM-ddTHH:mm:ss.fffK", "dd/MM/yyyy", "dd/MM/yyyy HH:mm:ss"
    };

    private static readonly string[] TimeFormats = { @"hh\:mm\:ss", @"hh\:mm\:ss\.fff", @"hh\:mm" };

    public static bool IsNullToken(string? s) =>
        s is null || NullTokens.Contains(s.Trim().ToLowerInvariant());

    public enum NumberParse { Empty, Ok, Invalid }

    /// <summary>Parses a decimal; distinguishes "empty/placeholder" from "garbage" so garbage can be logged.</summary>
    public static NumberParse TryDecimal(string? s, out decimal? value)
    {
        value = null;
        if (IsNullToken(s)) return NumberParse.Empty;
        var t = s!.Trim().Replace(",", "").Replace("£", "").Replace("$", "");
        if (decimal.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        {
            value = d;
            return NumberParse.Ok;
        }
        // Excel occasionally stores doubles like 1.2345E-05
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var dbl) && !double.IsNaN(dbl))
        {
            value = (decimal)dbl;
            return NumberParse.Ok;
        }
        return NumberParse.Invalid;
    }

    public static DateTime? TryDateTime(string? s)
    {
        if (IsNullToken(s)) return null;
        var t = s!.Trim();
        if (DateTime.TryParseExact(t, DateFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
            return dt;
        // Free-form fallback only when a date part is present - otherwise "17:20:49" would parse as *today* at 17:20.
        if ((t.Contains('-') || t.Contains('/')) &&
            DateTime.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out dt))
            return dt;
        // Excel serial date (e.g. "44562" or "44562.5")
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) && serial is > 20000 and < 80000)
            return DateTime.FromOADate(serial);
        return null;
    }

    public static TimeSpan? TryTimeOfDay(string? s)
    {
        if (IsNullToken(s)) return null;
        var t = s!.Trim();
        if (TimeSpan.TryParseExact(t, TimeFormats, CultureInfo.InvariantCulture, out var ts)) return ts;
        if (TimeSpan.TryParse(t, CultureInfo.InvariantCulture, out ts) && ts < TimeSpan.FromDays(1)) return ts;
        // a time cell rendered as datetime ("1899-12-30 17:20:49.000")
        var dt = TryDateTime(t);
        if (dt is not null) return dt.Value.TimeOfDay;
        // fraction-of-day ("0.7227")
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var frac) && frac is >= 0 and < 1)
            return TimeSpan.FromDays(frac);
        return null;
    }

    /// <summary>
    /// Builds the transaction timestamp. Two export layouts exist:
    /// 2020-2023 sheets have separate Date + Time columns; 2024 sheets have no Date column and a full
    /// timestamp (with milliseconds) in Time.
    /// </summary>
    public static DateTime? CombineTimestamp(string? date, string? time)
    {
        var timeAsDate = TryDateTime(time);
        if (timeAsDate is { Year: > 1900 }) return timeAsDate;     // 2024 layout: Time carries the full timestamp

        var d = TryDateTime(date);
        if (d is null) return null;
        var tod = TryTimeOfDay(time) ?? TimeSpan.Zero;
        return d.Value.Date + tod;
    }

    /// <summary>ISO 6166 validation: 2-letter country, 9 alphanumerics, Luhn check digit over the expanded string.</summary>
    public static bool IsValidIsin(string? isin)
    {
        if (string.IsNullOrWhiteSpace(isin)) return false;
        var s = isin.Trim().ToUpperInvariant();
        if (s.Length != 12 || !char.IsLetter(s[0]) || !char.IsLetter(s[1]) || !char.IsDigit(s[11])) return false;
        if (!s.All(char.IsLetterOrDigit)) return false;

        var digits = new System.Text.StringBuilder();
        foreach (var c in s[..11])
            digits.Append(char.IsDigit(c) ? (c - '0').ToString() : (c - 'A' + 10).ToString());

        int sum = 0;
        bool dbl = true; // rightmost digit of the payload is doubled
        for (int i = digits.Length - 1; i >= 0; i--)
        {
            int n = digits[i] - '0';
            if (dbl) { n *= 2; if (n > 9) n -= 9; }
            sum += n;
            dbl = !dbl;
        }
        int check = (10 - sum % 10) % 10;
        return check == s[11] - '0';
    }

    public static string? CleanText(string? s)
    {
        if (IsNullToken(s)) return null;
        var t = string.Join(' ', s!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return t.Length == 0 ? null : t;
    }
}
