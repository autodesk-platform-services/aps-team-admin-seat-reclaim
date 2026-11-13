using System.Text.Json.Serialization;

public class Tokens
{
    public string AccessToken { get; set; } = "";
    public string? RefreshToken { get; set; }
    public DateTime ExpiresAt { get; set; }
}

// Team Admin API shapes (https://developer.api.autodesk.com/team-admin/v1)

public record Team(string Id, string Name);

public record TeamUser(string Id, string? FirstName, string? LastName, string? Email, string? Role, bool? IsEmailVerified);

public record ProductPool(
    string ProductId,
    string TeamId,
    string? ProductName,
    int? TotalQuantity,
    int? AssignedQuantity,
    bool? Unlimited,
    DateTime? StartDate,
    DateTime? EndDate);

public record Assignment(
    string UserId,
    string? Email,
    string TeamId,
    string ProductId,
    string? ProductName,
    string AssignmentType);

public record Pagination(string? Cursor, bool HasMore);

public record PagedResponse<T>(List<T> Data, Pagination? Pagination);

public record DataResponse<T>(List<T> Data);

// Insights (Business Success Plan Reporting) API shapes

public record InsightsContext(string ContextId, string? Alias, List<string>? ReportingBenefits)
{
    public bool HasReportingApiAccess => ReportingBenefits?.Contains("ReportingAPIAccess") == true;
}

/// <summary>
/// Latest usage of one product (offering) by one user, from the API or an uploaded report.
/// The user is identified by email and the team by name; both are resolved to Team Admin ids
/// through GET /teams and GET /teams/{teamId}/users.
/// </summary>
public record UsageRecord(string Email, string Product, DateOnly LastUsed, string? Team = null);

// Application shapes returned to the browser

public record TeamSummary(string Id, string Name, bool ReportingApiAccess);

public record InactiveRow(
    string UserId,
    string? Name,
    string? Email,
    string ProductId,
    string? ProductName,
    string AssignmentType,
    bool Removable,
    DateOnly? LastUsed,
    int? DaysInactive);

public record PoolSummary(
    string ProductId,
    string? ProductName,
    int? Total,
    int? Assigned,
    int? Available,
    bool Unlimited,
    DateTime? EndDate);

public record ProductMatch(string ProductId, string? ProductName, string? UsageProduct, bool Manual);

public record InactiveUsersResult(
    DateOnly AsOf,
    DateOnly Cutoff,
    int Days,
    string Source,
    List<InactiveRow> Rows,
    List<ProductMatch> ProductMatches,
    List<string> UsageProducts,
    List<string> Warnings);

public class InactiveUsersRequest
{
    public int Days { get; set; } = 60;
    /// <summary>Optional manual mapping: Team Admin productId → usage product (offering) name.</summary>
    public Dictionary<string, string>? ProductMap { get; set; }
}

public record RemovalItem(string UserId, string ProductId);

public class RemovalRequest
{
    public List<RemovalItem> Items { get; set; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter<RemovalStatus>))]
public enum RemovalStatus { Removed, Inherited, Failed }

public record RemovalResult(string UserId, string ProductId, RemovalStatus Status, string? Message);

public class ApsApiException : Exception
{
    public int StatusCode { get; }
    public ApsApiException(int statusCode, string message) : base(message) { StatusCode = statusCode; }
}
