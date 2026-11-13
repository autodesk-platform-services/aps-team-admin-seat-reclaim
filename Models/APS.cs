using System.Net.Http.Headers;
using System.Text.Json;
using Autodesk.Authentication;
using Autodesk.SDKManager;

public partial class APS
{
    public const string HttpClientName = "aps";

    // Team Admin scopes + Insights (Business Success Plan Reporting) scopes.
    // data:write is required because usage queries are submitted with POST.
    // GET /userinfo (User Profile API v2) needs openid, and profapi:core-std-profile:read for the standard profile fields.
    private static readonly string[] Scopes =
    [
        "openid",
        "profapi:core-std-profile:read",
        "tmadmapi:team:read",
        "tmadmapi:team-users:read",
        "tmadmapi:team-products:read",
        "tmadmapi:team-products:write",
        "data:read",
        "data:write"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _clientId;
    private readonly string? _clientSecret;
    private readonly string _callbackUri;
    private readonly string _baseUrl;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AuthenticationClient _authClient = new(SdkManagerBuilder.Create().Build());

    public APS(string clientId, string? clientSecret, string callbackUri, string? baseUrl, IHttpClientFactory httpClientFactory)
    {
        _clientId = clientId;
        _clientSecret = string.IsNullOrWhiteSpace(clientSecret) ? null : clientSecret;
        _callbackUri = callbackUri;
        _baseUrl = (string.IsNullOrWhiteSpace(baseUrl) ? "https://developer.api.autodesk.com" : baseUrl).TrimEnd('/');
        _httpClientFactory = httpClientFactory;
    }

    private HttpClient CreateClient(Tokens? tokens = null)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        if (tokens != null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        }
        return client;
    }

    private static async Task<T> ReadJson<T>(HttpResponseMessage response)
    {
        await EnsureSuccess(response);
        var result = await response.Content.ReadFromJsonAsync<T>(JsonOptions);
        return result ?? throw new ApsApiException((int)response.StatusCode, "Empty response from APS.");
    }

    private static async Task EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }
        var body = await response.Content.ReadAsStringAsync();
        var request = response.RequestMessage;
        throw new ApsApiException((int)response.StatusCode,
            $"{(int)response.StatusCode} {response.ReasonPhrase} - {ExtractErrorMessage(body)} ({request?.Method} {request?.RequestUri?.AbsolutePath})");
    }

    /// <summary>
    /// Pulls a readable message out of RFC 7807 problem details, APS errors ({developerMessage, errorCode})
    /// or Insights errors ({Code, Message} / {title, detail}).
    /// </summary>
    internal static string ExtractErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "no details";
        }
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                string? Get(string name)
                {
                    foreach (var p in root.EnumerateObject())
                    {
                        if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                        {
                            return p.Value.GetString();
                        }
                    }
                    return null;
                }
                var message = Get("detail") ?? Get("developerMessage") ?? Get("message") ?? Get("title") ?? Get("error_description");
                var code = Get("errorCode") ?? Get("code") ?? Get("type");
                if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
                {
                    var details = string.Join("; ", errors.EnumerateArray()
                        .Select(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("detail", out var d) ? d.GetString() : null)
                        .Where(d => !string.IsNullOrEmpty(d)));
                    if (details.Length > 0)
                    {
                        message = $"{message} ({details})";
                    }
                }
                if (message != null)
                {
                    return code != null ? $"{message} [{code}]" : message;
                }
            }
        }
        catch (JsonException)
        {
        }
        return body.Length > 300 ? body[..300] : body;
    }
}
