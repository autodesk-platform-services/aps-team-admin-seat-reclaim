using System.Net;

public partial class APS
{
    private string TeamAdminUrl => $"{_baseUrl}/team-admin/v1";

    public Task<List<Team>> GetTeams(Tokens tokens) =>
        GetAllPages<Team>(tokens, $"{TeamAdminUrl}/teams");

    public Task<List<TeamUser>> GetTeamUsers(string teamId, Tokens tokens, List<string>? warnings = null) =>
        GetAllPages<TeamUser>(tokens, $"{TeamAdminUrl}/teams/{Uri.EscapeDataString(teamId)}/users", "team users", warnings);

    public Task<List<ProductPool>> GetProductPools(string teamId, Tokens tokens, List<string>? warnings = null) =>
        GetAllPages<ProductPool>(tokens, $"{TeamAdminUrl}/teams/{Uri.EscapeDataString(teamId)}/product-pools", "product pools", warnings);

    /// <summary>Assignment lists are returned in one response, without pagination.</summary>
    public async Task<List<Assignment>> GetAssignments(string teamId, Tokens tokens)
    {
        var client = CreateClient(tokens);
        using var response = await client.GetAsync($"{TeamAdminUrl}/teams/{Uri.EscapeDataString(teamId)}/assignments");
        var result = await ReadJson<DataResponse<Assignment>>(response);
        return result.Data ?? [];
    }

    /// <summary>
    /// Removes a DIRECT product assignment. 204 means the seat was released (or no grant existed);
    /// 422 means only inherited (TEAM) grants remain, which this API cannot remove.
    /// </summary>
    public async Task<RemovalResult> RemoveAssignment(string teamId, string userId, string productId, Tokens tokens)
    {
        var client = CreateClient(tokens);
        var url = $"{TeamAdminUrl}/teams/{Uri.EscapeDataString(teamId)}/assignments" +
            $"?userId={Uri.EscapeDataString(userId)}&productId={Uri.EscapeDataString(productId)}";
        using var response = await client.DeleteAsync(url);
        if (response.StatusCode == HttpStatusCode.NoContent || response.IsSuccessStatusCode)
        {
            return new RemovalResult(userId, productId, RemovalStatus.Removed, null);
        }
        var message = ExtractErrorMessage(await response.Content.ReadAsStringAsync());
        return response.StatusCode == HttpStatusCode.UnprocessableEntity
            ? new RemovalResult(userId, productId, RemovalStatus.Inherited, message)
            : new RemovalResult(userId, productId, RemovalStatus.Failed, $"{(int)response.StatusCode}: {message}");
    }

    /// <summary>
    /// Follows Team Admin cursors: omit cursor on the first request, then pass the returned cursor
    /// (URL-encoded) with the same limit until hasMore is false.
    /// </summary>
    private async Task<List<T>> GetAllPages<T>(Tokens tokens, string url, string label = "items", List<string>? warnings = null)
    {
        const int limit = 100;
        var client = CreateClient(tokens);
        var results = new List<T>();
        string? cursor = null;
        var pages = 0;
        do
        {
            pages++;
            var pageUrl = $"{url}?limit={limit}" + (cursor != null ? $"&cursor={Uri.EscapeDataString(cursor)}" : "");
            using var response = await client.GetAsync(pageUrl);
            var page = await ReadJson<PagedResponse<T>>(response);
            results.AddRange(page.Data ?? []);
            cursor = page.Pagination is { HasMore: true } ? page.Pagination.Cursor : null;
        } while (cursor != null && pages < MaxPages);
        if (cursor != null)
        {
            AddWarning(warnings, $"Only the first {results.Count} {label} were loaded (sample limit of {MaxPages} pages). " +
                "Users beyond that limit are missing from the results.");
        }
        return results;
    }
}
