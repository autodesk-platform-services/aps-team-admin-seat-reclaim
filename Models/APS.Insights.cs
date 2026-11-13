using System.Globalization;
using System.Net;
using System.Text.Json;

public partial class APS
{
    private record UsageQueryPage(
        string? Id,
        string? Status,
        List<string>? Columns,
        List<List<JsonElement>>? Rows,
        UsageQueryPagination? Pagination,
        string? Error,
        string? Message);

    private record UsageQueryPagination(int? TotalResults, int? Offset, int? Limit);

    private const int InsightsPageSize = 100;

    /// <summary>
    /// Sample limit: at most this many pages (of 100 items) are read per list, in Team Admin and Insights.
    /// Larger teams are truncated. Remove or raise this for production use.
    /// </summary>
    private const int MaxPages = 5;

    /// <summary>Thread-safe: team data and usage are loaded concurrently into the same list.</summary>
    private static void AddWarning(List<string>? warnings, string message)
    {
        if (warnings == null)
        {
            return;
        }
        lock (warnings)
        {
            warnings.Add(message);
        }
    }
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromMinutes(5);

    // The usageDate filter syntax is not consistently documented, so each form is tried in order
    // (only a 400 moves to the next one) and the first one accepted is reused, as in
    // aps-business-success-plan-report.
    private static readonly Func<string, string>[] SinceWhereForms =
    [
        since => $"usageDate >= '{since}'",
        since => $"usageDate >= '{since}T00:00:00Z'",
        since => $"usageDate BETWEEN '{since}' AND '{DateTime.UtcNow:yyyy-MM-dd}'"
    ];
    private static int _acceptedWhereForm = -1;

    private string InsightsUrl => $"{_baseUrl}/insights/v1";

    /// <summary>Teams (contexts) the user can report on, with their reporting benefits.</summary>
    public async Task<List<InsightsContext>> GetContexts(Tokens tokens)
    {
        var client = CreateClient(tokens);
        using var response = await client.GetAsync($"{InsightsUrl}/contexts");
        return await ReadJson<List<InsightsContext>>(response);
    }

    /// <summary>
    /// Latest usage date per (email, product) since the given date, using the Usage Query API.
    /// The "fields" list acts as the GROUP BY, so one row is returned per user and product.
    /// </summary>
    public async Task<List<UsageRecord>> QueryLatestUsage(string contextId, DateOnly since, Tokens tokens, List<string>? warnings = null)
    {
        var sinceText = since.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var client = CreateClient(tokens);

        string? queryId = null;
        var start = Math.Max(_acceptedWhereForm, 0);
        for (var i = start; i < SinceWhereForms.Length && queryId == null; i++)
        {
            var body = new
            {
                fields = new[] { "emailAddress", "productName" },
                metrics = new[] { "latestUsageDate" },
                where = SinceWhereForms[i](sinceText),
                orderBy = ""
                // The context is passed in the query string only; the API rejects it in the body
                // with InvalidQueryError.
            };
            var id = Uri.EscapeDataString(contextId);
            using var response = await client.PostAsJsonAsync(
                $"{InsightsUrl}/usage-queries?offset=0&limit={InsightsPageSize}&contextId={id}", body, JsonOptions);
            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                var message = ExtractErrorMessage(await response.Content.ReadAsStringAsync());
                if (LooksLikePersonalDataError(message))
                {
                    throw PersonalDataHashedException(message);
                }
                if (i == SinceWhereForms.Length - 1)
                {
                    throw new ApsApiException(400, $"The Usage Query API rejected the request: {message}");
                }
                continue;
            }
            var submitted = await ReadJson<UsageQueryPage>(response);
            queryId = submitted.Id ?? throw new ApsApiException(502, "The Usage Query API did not return a query id.");
            _acceptedWhereForm = i;
        }

        var page = await WaitForQuery(client, queryId!);
        var records = new List<UsageRecord>();
        var seen = 0;
        var pageCount = 0;
        while (true)
        {
            pageCount++;
            var rows = page.Rows ?? [];
            records.AddRange(ToUsageRecords(page.Columns ?? [], rows));
            // Advance by the rows actually returned, not by the page size.
            seen += rows.Count;
            var total = page.Pagination?.TotalResults ?? 0;
            if (rows.Count == 0 || seen >= total)
            {
                break;
            }
            if (pageCount >= MaxPages)
            {
                AddWarning(warnings, $"Only the first {seen} of {total} usage rows were loaded (sample limit of {MaxPages} pages). " +
                    "Users whose usage is beyond that limit are listed as inactive.");
                break;
            }
            using var response = await client.GetAsync($"{InsightsUrl}/usage-queries/{Uri.EscapeDataString(queryId!)}?offset={seen}&limit={InsightsPageSize}");
            page = await ReadJson<UsageQueryPage>(response);
        }
        return records;
    }

    private async Task<UsageQueryPage> WaitForQuery(HttpClient client, string queryId)
    {
        var deadline = DateTime.UtcNow + QueryTimeout;
        for (var attempt = 1; ; attempt++)
        {
            using var response = await client.GetAsync($"{InsightsUrl}/usage-queries/{Uri.EscapeDataString(queryId)}?offset=0&limit={InsightsPageSize}");
            var page = await ReadJson<UsageQueryPage>(response);
            switch (page.Status?.ToUpperInvariant())
            {
                case "COMPLETED":
                    return page; // the completed response doubles as the first page
                case "ERROR":
                case "EXPIRED":
                    var message = page.Error ?? page.Message ?? "no details";
                    if (LooksLikePersonalDataError(message))
                    {
                        throw PersonalDataHashedException(message);
                    }
                    throw new ApsApiException(502, $"Usage query {queryId} ended with status {page.Status}: {message}");
            }
            if (DateTime.UtcNow > deadline)
            {
                throw new ApsApiException(504, $"Usage query {queryId} did not complete within {QueryTimeout.TotalMinutes} minutes.");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(1000 * attempt, 10000)));
        }
    }

    private static IEnumerable<UsageRecord> ToUsageRecords(List<string> columns, List<List<JsonElement>> rows)
    {
        // Look columns up by name, falling back to the requested order.
        int Index(string name, int fallback)
        {
            var i = columns.FindIndex(c => c.Equals(name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 ? i : fallback;
        }
        if (columns.Count > 0 && !columns.Any(c => c.Equals("emailAddress", StringComparison.OrdinalIgnoreCase)))
        {
            // Personal data fields are omitted entirely when the admin chose to hash personal data.
            throw PersonalDataHashedException("emailAddress was not returned");
        }
        int emailIdx = Index("emailAddress", 0), productIdx = Index("productName", 1), latestIdx = Index("latestUsageDate", 2);

        foreach (var row in rows)
        {
            string? Cell(int i) => i < row.Count && row[i].ValueKind != JsonValueKind.Null ? row[i].ToString() : null;
            var email = Cell(emailIdx);
            var product = Cell(productIdx);
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(product)
                || !UsageReportParser.TryParseDate(Cell(latestIdx), out var latest))
            {
                continue;
            }
            yield return new UsageRecord(email.Trim(), product.Trim(), latest);
        }
    }

    private static bool LooksLikePersonalDataError(string message) =>
        message.Contains("emailAddress", StringComparison.OrdinalIgnoreCase)
        || message.Contains("personal", StringComparison.OrdinalIgnoreCase)
        || message.Contains("hash", StringComparison.OrdinalIgnoreCase);

    private static ApsApiException PersonalDataHashedException(string detail) =>
        new(409, "This team hashes personal data, so usage cannot be matched to Team Admin users by email. " +
            "Enable personal data in Autodesk Account (Reporting > Settings) or upload a report that includes emails. " +
            $"Details: {detail}");
}
