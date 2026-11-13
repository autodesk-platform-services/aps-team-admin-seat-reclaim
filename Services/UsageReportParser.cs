using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using CsvHelper;
using CsvHelper.Configuration;

public record ParsedUsageReport(
    List<UsageRecord> Records,
    DateOnly? AsOf,
    DateOnly? FirstDate,
    bool IsDailyUsage,
    List<string> Tables,
    List<string> Warnings);

/// <summary>
/// Reads Business Success Plan usage reports into <see cref="UsageRecord"/>s (latest use per email and product).
/// Supported inputs (.csv, .xlsx, or a .zip of them):
/// <list type="bullet">
/// <item>Export Usage API / Autodesk Account exports: usage.csv (team_alias, email, offering_name, day_used),
/// the 2026 "Seat usage" layout (Team, Email, Product, Day used) and the Usage report (last_accessed).</item>
/// <item>Tables exported from aps-business-success-plan-report (Name, Email, Product, Earliest, Latest, ...),
/// as described in its DATA-TRANSFORMATION.md.</item>
/// </list>
/// </summary>
public static partial class UsageReportParser
{
    private const int HeaderSearchRows = 10;

    // Normalized header names (lowercase, letters and digits only), most specific first.
    private static readonly string[] EmailHeaders = ["email", "emailaddress", "useremail"];
    // The subscription offering is what Team Admin product pools represent. Legacy exports call it
    // offering_name (product_name is the feature); 2026 exports call it Product (and the feature Feature).
    private static readonly string[] ProductHeaders = ["offeringname", "offering", "product", "productname"];
    private static readonly string[] DailyDateHeaders = ["dayused", "usagedate"];
    private static readonly string[] LatestDateHeaders = ["lastaccessed", "latest", "latestusagedate", "latestusage", "lastused", "lastusagedate"];
    private static readonly string[] TeamHeaders = ["teamalias", "team", "teamname"];
    private static readonly string[] HashedIdHeaders = ["hashedautodeskid", "hasheduserid"];

    private record ColumnMap(int Email, int Product, int Date, int Team, bool Daily);

    public static ParsedUsageReport Parse(Stream stream, string fileName)
    {
        var tables = new List<(string Name, List<string?[]> Rows)>();
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        switch (extension)
        {
            case ".zip":
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    foreach (var entry in zip.Entries.Where(e => e.Length > 0))
                    {
                        var entryExt = Path.GetExtension(entry.Name).ToLowerInvariant();
                        if (entryExt is not (".csv" or ".xlsx"))
                        {
                            continue;
                        }
                        using var entryStream = entry.Open();
                        using var buffer = new MemoryStream();
                        entryStream.CopyTo(buffer);
                        buffer.Position = 0;
                        tables.AddRange(entryExt == ".csv"
                            ? [(entry.FullName, ReadCsv(buffer))]
                            : ReadXlsx(buffer, entry.FullName));
                    }
                }
                break;
            case ".csv":
                tables.Add((fileName, ReadCsv(stream)));
                break;
            case ".xlsx":
                tables.AddRange(ReadXlsx(stream, fileName));
                break;
            default:
                throw new InvalidDataException("Unsupported file type. Upload a .csv, .xlsx or .zip report.");
        }
        return Parse(tables);
    }

    /// <summary>Parses already-loaded tables (each a list of rows of cell text).</summary>
    public static ParsedUsageReport Parse(IEnumerable<(string Name, List<string?[]> Rows)> tables)
    {
        var latest = new Dictionary<(string, string, string?), UsageRecord>();
        var usedTables = new List<string>();
        var warnings = new List<string>();
        DateOnly? first = null, last = null;
        var daily = false;
        var sawHashedOnly = false;
        var maskedRows = 0;

        foreach (var (name, rows) in tables)
        {
            var (headerRow, map, hashedOnly) = FindHeader(rows);
            sawHashedOnly |= hashedOnly;
            if (map == null)
            {
                continue;
            }
            usedTables.Add(name);
            daily |= map.Daily;
            foreach (var row in rows.Skip(headerRow + 1))
            {
                var email = Cell(row, map.Email)?.Trim();
                var product = Cell(row, map.Product)?.Trim();
                if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(product))
                {
                    continue;
                }
                if (!email.Contains('@'))
                {
                    maskedRows++; // "*****" or a hash when personal data is turned off
                    continue;
                }
                if (!TryParseDate(Cell(row, map.Date), out var date))
                {
                    continue; // e.g. assigned users who never used the product
                }
                var team = map.Team >= 0 ? Cell(row, map.Team)?.Trim() : null;
                var key = (email.ToLowerInvariant(), product, team);
                if (!latest.TryGetValue(key, out var existing) || existing.LastUsed < date)
                {
                    latest[key] = new UsageRecord(email, product, date, team);
                }
                first = first == null || date < first ? date : first;
                last = last == null || date > last ? date : last;
            }
        }

        if (usedTables.Count == 0)
        {
            throw new InvalidDataException(sawHashedOnly
                ? "The report only contains hashed user ids. Turn on personal data in Autodesk Account and export again so users can be matched by email."
                : "No usage table found. The report needs an email column, a product column (offering_name / Product) and a usage date column (day_used / Day used / last_accessed / Latest). "
                  + "Columns found: " + DescribeHeaders(tables));
        }
        if (latest.Count == 0 && maskedRows > 0)
        {
            throw new InvalidDataException("Emails in the report are masked or hashed. Turn on personal data in Autodesk Account and export again so users can be matched by email.");
        }
        if (latest.Count == 0)
        {
            throw new InvalidDataException("The report has no usage rows with an email, a product and a usage date.");
        }
        if (maskedRows > 0)
        {
            warnings.Add($"{maskedRows} row(s) with masked or hashed emails were ignored.");
        }
        return new ParsedUsageReport([.. latest.Values], last, first, daily, usedTables, warnings);
    }

    /// <summary>Lists the first non-empty row of each table, to show what was actually uploaded.</summary>
    private static string DescribeHeaders(IEnumerable<(string Name, List<string?[]> Rows)> tables) =>
        string.Join(" | ", tables.Select(t =>
        {
            var header = t.Rows.FirstOrDefault(r => r.Any(c => !string.IsNullOrWhiteSpace(c)));
            var cols = header == null ? "(empty)" : string.Join(", ", header.Where(c => !string.IsNullOrWhiteSpace(c)).Take(20));
            return $"{t.Name}: [{cols}]";
        }));

    private static (int HeaderRow, ColumnMap? Map, bool HashedOnly) FindHeader(List<string?[]> rows)
    {
        var hashedOnly = false;
        for (var r = 0; r < Math.Min(HeaderSearchRows, rows.Count); r++)
        {
            var headers = rows[r].Select(h => Normalize(h)).ToList();
            var email = IndexOf(headers, EmailHeaders);
            var product = IndexOf(headers, ProductHeaders);
            var dailyDate = IndexOf(headers, DailyDateHeaders);
            var latestDate = IndexOf(headers, LatestDateHeaders);
            if (product >= 0 && email < 0 && IndexOf(headers, HashedIdHeaders) >= 0)
            {
                hashedOnly = true;
            }
            if (email >= 0 && product >= 0 && (dailyDate >= 0 || latestDate >= 0))
            {
                var daily = dailyDate >= 0;
                return (r, new ColumnMap(email, product, daily ? dailyDate : latestDate, IndexOf(headers, TeamHeaders), daily), false);
            }
        }
        return (-1, null, hashedOnly);
    }

    private static int IndexOf(List<string> headers, string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var i = headers.IndexOf(candidate);
            if (i >= 0)
            {
                return i;
            }
        }
        return -1;
    }

    private static string? Cell(string?[] row, int index) => index >= 0 && index < row.Length ? row[index] : null;

    private static string Normalize(string? header) =>
        NonAlphanumeric().Replace((header ?? "").ToLowerInvariant(), "");

    [GeneratedRegex("[^a-z0-9]")]
    private static partial Regex NonAlphanumeric();

    private static readonly string[] DateFormats =
    [
        "yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ssZ", "yyyy-MM-ddTHH:mm:ss.fffZ", "yyyy-MM-dd HH:mm:ss", "yyyy/MM/dd",
        "M/d/yyyy", "M/d/yyyy H:mm", "M/d/yyyy h:mm:ss tt", "d.M.yyyy"
    ];

    public static bool TryParseDate(string? text, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        text = text.Trim();
        if (DateTime.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            || DateTime.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out parsed))
        {
            date = DateOnly.FromDateTime(parsed);
            return true;
        }
        // Excel serial dates that were exported as numbers
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) && serial is > 20000 and < 80000)
        {
            date = DateOnly.FromDateTime(DateTime.FromOADate(serial));
            return true;
        }
        return false;
    }

    private static List<string?[]> ReadCsv(Stream stream)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = false,
            DetectDelimiter = true,
            BadDataFound = null,
            MissingFieldFound = null
        };
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        using var csv = new CsvReader(reader, config);
        var rows = new List<string?[]>();
        while (csv.Read())
        {
            rows.Add(csv.Parser.Record ?? []);
        }
        return rows;
    }

    private static IEnumerable<(string, List<string?[]>)> ReadXlsx(Stream stream, string fileName)
    {
        using var workbook = new XLWorkbook(stream);
        var tables = new List<(string, List<string?[]>)>();
        foreach (var sheet in workbook.Worksheets)
        {
            var used = sheet.RangeUsed();
            if (used == null)
            {
                continue;
            }
            var rows = new List<string?[]>();
            foreach (var row in used.Rows())
            {
                rows.Add(row.Cells().Select(c => c.Value.Type switch
                {
                    XLDataType.DateTime => c.Value.GetDateTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    XLDataType.Blank => null,
                    _ => c.Value.ToString(CultureInfo.InvariantCulture)
                }).ToArray());
            }
            tables.Add(($"{fileName}/{sheet.Name}", rows));
        }
        return tables;
    }
}
