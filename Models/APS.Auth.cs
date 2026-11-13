using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

public partial class APS
{
    private record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);

    public record UserInfo(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("email")] string? Email);

    /// <summary>Creates a PKCE code verifier and its S256 code challenge (RFC 7636).</summary>
    public static (string Verifier, string Challenge) CreatePkcePair()
    {
        const string allowed = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";
        var verifier = RandomNumberGenerator.GetString(allowed, 64);
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    public static string CreateState() => Base64Url(RandomNumberGenerator.GetBytes(16));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // The Autodesk.Authentication SDK's Scopes enum does not include the tmadmapi:* scopes,
    // so the authorize URL and token requests are built by hand.
    public string GetAuthorizationURL(string state, string codeChallenge)
    {
        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = _clientId,
            ["redirect_uri"] = _callbackUri,
            ["scope"] = string.Join(' ', Scopes),
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256"
        };
        return $"{_baseUrl}/authentication/v2/authorize?" +
            string.Join('&', query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
    }

    public Task<Tokens> GenerateTokens(string code, string codeVerifier) =>
        RequestTokens(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["code_verifier"] = codeVerifier,
            ["redirect_uri"] = _callbackUri
        });

    public Task<Tokens> RefreshTokens(Tokens tokens) =>
        RequestTokens(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = tokens.RefreshToken ?? throw new ApsApiException(401, "No refresh token available."),
            ["scope"] = string.Join(' ', Scopes)
        });

    private async Task<Tokens> RequestTokens(Dictionary<string, string> form)
    {
        // Public (PKCE) clients identify themselves with client_id only.
        // Confidential clients may still use PKCE and send their secret as well.
        form["client_id"] = _clientId;
        if (_clientSecret != null)
        {
            form["client_secret"] = _clientSecret;
        }
        var client = CreateClient();
        using var response = await client.PostAsync($"{_baseUrl}/authentication/v2/token", new FormUrlEncodedContent(form));
        var token = await ReadJson<TokenResponse>(response);
        return new Tokens
        {
            AccessToken = token.AccessToken,
            RefreshToken = token.RefreshToken,
            ExpiresAt = DateTime.UtcNow.AddSeconds(token.ExpiresIn)
        };
    }

    public async Task<UserInfo> GetUserProfile(Tokens tokens)
    {
        var client = CreateClient(tokens);
        using var response = await client.GetAsync($"{_baseUrl}/userinfo");
        return await ReadJson<UserInfo>(response);
    }
}
