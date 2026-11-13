using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Autodesk.Authentication;
using Autodesk.Authentication.Model;

public partial class APS
{
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
    // so the authorize URL is built by hand (everything after it goes through the SDK).
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

    // Token exchange, refresh and userinfo use the official Autodesk.Authentication SDK.
    public async Task<Tokens> GenerateTokens(string code, string codeVerifier) =>
        ToTokens(await CallSdk(() => _authClient.GetThreeLeggedTokenAsync(
            _clientId, code, _callbackUri, clientSecret: _clientSecret, codeVerifier: codeVerifier)));

    public async Task<Tokens> RefreshTokens(Tokens tokens)
    {
        var refreshToken = tokens.RefreshToken ?? throw new ApsApiException(401, "No refresh token available.");
        // Scopes are omitted: the refreshed token keeps the scopes granted at sign-in
        // (the SDK's Scopes enum cannot express the tmadmapi:* scopes).
        return ToTokens(await CallSdk(() => _authClient.RefreshTokenAsync(
            _clientId, refreshToken, clientSecret: _clientSecret)));
    }

    public async Task<UserInfo> GetUserProfile(Tokens tokens)
    {
        var info = await CallSdk(() => _authClient.GetUserInfoAsync(tokens.AccessToken));
        return new UserInfo(info.Name, info.Email);
    }

    private static Tokens ToTokens(ThreeLeggedToken token) => new()
    {
        AccessToken = token.AccessToken,
        RefreshToken = token.RefreshToken,
        ExpiresAt = DateTime.UtcNow.AddSeconds(token.ExpiresIn ?? 0)
    };

    private static async Task<T> CallSdk<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (AuthenticationApiException ex)
        {
            throw new ApsApiException((int)ex.HttpResponseMessage.StatusCode, $"Authentication failed: {ex.Message}");
        }
    }
}
