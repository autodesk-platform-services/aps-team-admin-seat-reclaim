using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/[controller]")]
public class TeamsController : ControllerBase
{
    private const int MaxDays = 365;
    private const long MaxReportBytes = 500L * 1024 * 1024;

    private readonly APS _aps;
    private readonly IDataProtector _protector;

    public TeamsController(APS aps, IDataProtectionProvider dataProtection)
    {
        _aps = aps;
        _protector = AuthController.CreateProtector(dataProtection);
    }

    /// <summary>
    /// Teams the user administers (Team Admin API), flagged with whether the matching Insights context
    /// has the Business Success Plan Reporting API benefit.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult> GetTeams()
    {
        var tokens = await AuthController.PrepareTokens(Request, Response, _aps, _protector);
        if (tokens == null)
        {
            return Unauthorized();
        }
        var teams = await GetTeamsWithContexts(tokens);
        return Ok(teams.Select(t => new TeamSummary(t.Team.Id, t.Team.Name, t.Context?.HasReportingApiAccess == true)));
    }

    /// <summary>Current license pools of a team (total, assigned and available seats), read live from Team Admin.</summary>
    [HttpGet("{teamId}/product-pools")]
    public async Task<ActionResult> GetProductPools(string teamId)
    {
        var tokens = await AuthController.PrepareTokens(Request, Response, _aps, _protector);
        if (tokens == null)
        {
            return Unauthorized();
        }
        var (team, _) = await GetEligibleTeam(teamId, tokens, requireReportingApi: false);
        if (team == null)
        {
            return Forbid403();
        }
        var pools = await _aps.GetProductPools(teamId, tokens);
        return Ok(pools
            .OrderBy(p => p.ProductName ?? p.ProductId)
            .Select(p => new PoolSummary(
                p.ProductId,
                p.ProductName,
                p.TotalQuantity,
                p.AssignedQuantity,
                p.TotalQuantity.HasValue && p.AssignedQuantity.HasValue ? p.TotalQuantity - p.AssignedQuantity : null,
                p.Unlimited == true,
                p.EndDate)));
    }

    /// <summary>Inactive users based on the Business Success Plan Reporting (Insights) Usage Query API.</summary>
    [HttpPost("{teamId}/inactive-users")]
    public async Task<ActionResult> GetInactiveUsersFromApi(string teamId, [FromBody] InactiveUsersRequest request)
    {
        var tokens = await AuthController.PrepareTokens(Request, Response, _aps, _protector);
        if (tokens == null)
        {
            return Unauthorized();
        }
        if (request.Days is < 1 or > MaxDays)
        {
            return BadRequest($"days must be between 1 and {MaxDays}.");
        }
        var (team, context) = await GetEligibleTeam(teamId, tokens, requireReportingApi: true);
        if (team == null || context == null)
        {
            return Forbid403();
        }

        var asOf = DateOnly.FromDateTime(DateTime.UtcNow);
        var warnings = new List<string>();
        var usageTask = _aps.QueryLatestUsage(context.ContextId, asOf.AddDays(-request.Days), tokens, warnings);
        var (users, pools, assignments) = await GetTeamData(teamId, tokens, warnings);
        var usage = await usageTask;

        return Ok(InactiveUserAnalyzer.Analyze(users, pools, assignments, usage, asOf, request.Days,
            "Business Success Plan Reporting API", request.ProductMap, warnings));
    }

    /// <summary>Inactive users based on an uploaded Business Success Plan report (.csv, .xlsx or .zip).</summary>
    [HttpPost("{teamId}/inactive-users/report")]
    [RequestSizeLimit(MaxReportBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxReportBytes)]
    public async Task<ActionResult> GetInactiveUsersFromReport(string teamId, [FromForm] IFormFile file, [FromForm] int days, [FromForm] string? productMap)
    {
        var tokens = await AuthController.PrepareTokens(Request, Response, _aps, _protector);
        if (tokens == null)
        {
            return Unauthorized();
        }
        if (days is < 1 or > MaxDays)
        {
            return BadRequest($"days must be between 1 and {MaxDays}.");
        }
        var (team, context) = await GetEligibleTeam(teamId, tokens, requireReportingApi: false);
        if (team == null)
        {
            return Forbid403();
        }

        ParsedUsageReport report;
        await using (var stream = file.OpenReadStream())
        {
            // ZipArchive needs a seekable stream.
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            buffer.Position = 0;
            report = UsageReportParser.Parse(buffer, file.FileName);
        }

        var warnings = new List<string>(report.Warnings);
        var records = FilterToTeam(report.Records, team, context, warnings);
        var asOf = report.AsOf ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var cutoff = asOf.AddDays(-days);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (today.DayNumber - asOf.DayNumber > 7)
        {
            warnings.Add($"The latest usage in the report is from {asOf:yyyy-MM-dd}, {today.DayNumber - asOf.DayNumber} days ago. Inactivity is measured up to that date.");
        }
        if (report.IsDailyUsage && report.FirstDate > cutoff)
        {
            warnings.Add($"The report starts on {report.FirstDate:yyyy-MM-dd}, after the start of the {days}-day window ({cutoff:yyyy-MM-dd}). " +
                "Users who only used a product before the report starts are listed as inactive. Export a longer period for accurate results.");
        }
        if (!report.IsDailyUsage)
        {
            warnings.Add("The report contains summarized latest-usage dates. Make sure it covers at least the selected number of days.");
        }

        Dictionary<string, string>? map = null;
        if (!string.IsNullOrWhiteSpace(productMap))
        {
            map = JsonSerializer.Deserialize<Dictionary<string, string>>(productMap);
        }

        var (users, pools, assignments) = await GetTeamData(teamId, tokens, warnings);
        return Ok(InactiveUserAnalyzer.Analyze(users, pools, assignments, records, asOf, days,
            $"Report: {file.FileName}", map, warnings));
    }

    /// <summary>
    /// Removes the selected DIRECT assignments one by one (Team Admin allows 60 requests per minute;
    /// throttled calls are retried by <see cref="ApsRetryHandler"/>).
    /// </summary>
    [HttpPost("{teamId}/assignments/remove")]
    public async Task<ActionResult> RemoveAssignments(string teamId, [FromBody] RemovalRequest request)
    {
        var tokens = await AuthController.PrepareTokens(Request, Response, _aps, _protector);
        if (tokens == null)
        {
            return Unauthorized();
        }
        if (request.Items.Count == 0)
        {
            return BadRequest("No assignments selected.");
        }
        var (team, _) = await GetEligibleTeam(teamId, tokens, requireReportingApi: false);
        if (team == null)
        {
            return Forbid403();
        }

        // Only remove assignments that currently exist as DIRECT grants on this team.
        var direct = (await _aps.GetAssignments(teamId, tokens))
            .Where(a => string.Equals(a.AssignmentType, "DIRECT", StringComparison.OrdinalIgnoreCase))
            .Select(a => (a.UserId, a.ProductId))
            .ToHashSet();

        var results = new List<RemovalResult>();
        foreach (var item in request.Items.Distinct())
        {
            if (!direct.Contains((item.UserId, item.ProductId)))
            {
                results.Add(new RemovalResult(item.UserId, item.ProductId, RemovalStatus.Failed,
                    "Not a direct assignment on this team (it may be inherited or already removed)."));
                continue;
            }
            results.Add(await _aps.RemoveAssignment(teamId, item.UserId, item.ProductId, tokens));
        }
        return Ok(results);
    }

    private async Task<(List<TeamUser>, List<ProductPool>, List<Assignment>)> GetTeamData(string teamId, Tokens tokens, List<string> warnings)
    {
        var users = _aps.GetTeamUsers(teamId, tokens, warnings);
        var pools = _aps.GetProductPools(teamId, tokens, warnings);
        var assignments = _aps.GetAssignments(teamId, tokens);
        await Task.WhenAll(users, pools, assignments);
        return (users.Result, pools.Result, assignments.Result);
    }

    private async Task<List<(Team Team, InsightsContext? Context)>> GetTeamsWithContexts(Tokens tokens)
    {
        var teamsTask = _aps.GetTeams(tokens);
        var contextsTask = _aps.GetContexts(tokens);
        var teams = await teamsTask;
        List<InsightsContext> contexts;
        try
        {
            contexts = await contextsTask;
        }
        catch (ApsApiException ex) when (ex.StatusCode is 403 or 404)
        {
            contexts = []; // not a Business Success Plan admin anywhere
        }

        // An Insights context is a team, so contextId should equal the Team Admin team id;
        // fall back to matching the alias against the team name.
        return teams.Select(t => (t,
                contexts.FirstOrDefault(c => c.ContextId == t.Id)
                ?? contexts.FirstOrDefault(c => string.Equals(c.Alias, t.Name, StringComparison.OrdinalIgnoreCase))))
            .OrderBy(t => t.t.Name)
            .ToList();
    }

    /// <summary>
    /// A team the user administers (listed by Team Admin GET /teams). The Business Success Plan Reporting API
    /// benefit is only required when <paramref name="requireReportingApi"/> is set (the API usage source);
    /// uploading a report and removing licenses only need Team Admin access.
    /// </summary>
    private async Task<(Team? Team, InsightsContext? Context)> GetEligibleTeam(string teamId, Tokens tokens, bool requireReportingApi)
    {
        var match = (await GetTeamsWithContexts(tokens)).FirstOrDefault(t => t.Team.Id == teamId);
        if (match.Team == null || (requireReportingApi && match.Context?.HasReportingApiAccess != true))
        {
            return (null, null);
        }
        return (match.Team, match.Context);
    }

    private static List<UsageRecord> FilterToTeam(List<UsageRecord> records, Team team, InsightsContext? context, List<string> warnings)
    {
        if (!records.Any(r => r.Team != null))
        {
            return records;
        }
        bool IsTeam(string? name) => name == null
            || string.Equals(name, team.Name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, context?.Alias, StringComparison.OrdinalIgnoreCase)
            || name == team.Id;
        var filtered = records.Where(r => IsTeam(r.Team)).ToList();
        if (filtered.Count == 0)
        {
            var found = string.Join(", ", records.Select(r => r.Team).Distinct().Take(10));
            throw new InvalidDataException($"The report has no usage for team \"{team.Name}\". Teams found in the report: {found}.");
        }
        var skipped = records.Count - filtered.Count;
        if (skipped > 0)
        {
            warnings.Add($"{skipped} usage record(s) for other teams were ignored.");
        }
        return filtered;
    }

    private ObjectResult Forbid403() =>
        StatusCode(403, "You must be an administrator of this team (and, to query usage with the API, the team must have Business Success Plan Reporting API access).");
}
