using System.Globalization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private const string PkceCookie = "aps_pkce";
    private const string AccessTokenCookie = "aps_access_token";
    private const string RefreshTokenCookie = "aps_refresh_token";
    private const string ExpiresAtCookie = "aps_expires_at";
    private static readonly TimeSpan RefreshBuffer = TimeSpan.FromSeconds(60);

    private readonly APS _aps;
    private readonly IDataProtector _protector;

    public AuthController(APS aps, IDataProtectionProvider dataProtection)
    {
        _aps = aps;
        _protector = CreateProtector(dataProtection);
    }

    public static IDataProtector CreateProtector(IDataProtectionProvider dataProtection) =>
        dataProtection.CreateProtector("ApsTeamAdmin.Auth");

    /// <summary>
    /// Reads the tokens from the (encrypted, HttpOnly) cookies and refreshes them when they are about to expire.
    /// Returns null when the user is not logged in or the session can no longer be refreshed.
    /// </summary>
    public static async Task<Tokens?> PrepareTokens(HttpRequest request, HttpResponse response, APS aps, IDataProtector protector)
    {
        Tokens tokens;
        try
        {
            if (!request.Cookies.TryGetValue(AccessTokenCookie, out var access) ||
                !request.Cookies.TryGetValue(ExpiresAtCookie, out var expiresAt))
            {
                return null;
            }
            tokens = new Tokens
            {
                AccessToken = protector.Unprotect(access),
                RefreshToken = request.Cookies.TryGetValue(RefreshTokenCookie, out var refresh) ? protector.Unprotect(refresh) : null,
                ExpiresAt = DateTime.Parse(expiresAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            };
        }
        catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException)
        {
            ClearTokens(response);
            return null;
        }

        if (tokens.ExpiresAt - RefreshBuffer < DateTime.UtcNow)
        {
            if (tokens.RefreshToken == null)
            {
                ClearTokens(response);
                return null;
            }
            try
            {
                tokens = await aps.RefreshTokens(tokens);
            }
            catch (ApsApiException)
            {
                ClearTokens(response);
                return null;
            }
            StoreTokens(request, response, protector, tokens);
        }
        return tokens;
    }

    [HttpGet("login")]
    public ActionResult Login()
    {
        var (verifier, challenge) = APS.CreatePkcePair();
        var state = APS.CreateState();
        // Keep the verifier server-side (encrypted cookie) until the callback; the browser never sees it in clear text.
        Response.Cookies.Append(PkceCookie, _protector.Protect($"{state}|{verifier}"),
            CookieOptions(Request, TimeSpan.FromMinutes(10)));
        return Redirect(_aps.GetAuthorizationURL(state, challenge));
    }

    [HttpGet("callback")]
    public async Task<ActionResult> Callback(string? code, string? state, string? error, [FromQuery(Name = "error_description")] string? errorDescription)
    {
        if (!string.IsNullOrEmpty(error))
        {
            return BadRequest($"Authorization failed: {error} {errorDescription}");
        }
        if (string.IsNullOrEmpty(code) || !Request.Cookies.TryGetValue(PkceCookie, out var pkce))
        {
            return BadRequest("Missing authorization code or login session. Start again from /api/auth/login.");
        }
        Response.Cookies.Delete(PkceCookie);

        string[] parts;
        try
        {
            parts = _protector.Unprotect(pkce).Split('|', 2);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return BadRequest("Invalid login session. Start again from /api/auth/login.");
        }
        if (parts.Length != 2 || parts[0] != state)
        {
            return BadRequest("Authorization state mismatch.");
        }

        var tokens = await _aps.GenerateTokens(code, codeVerifier: parts[1]);
        StoreTokens(Request, Response, _protector, tokens);
        return Redirect("/");
    }

    [HttpGet("logout")]
    public ActionResult Logout()
    {
        ClearTokens(Response);
        return Redirect("/");
    }

    [HttpGet("profile")]
    public async Task<ActionResult> GetProfile()
    {
        var tokens = await PrepareTokens(Request, Response, _aps, _protector);
        if (tokens == null)
        {
            return Unauthorized();
        }
        var profile = await _aps.GetUserProfile(tokens);
        return Ok(new { name = profile.Name, email = profile.Email });
    }

    private static void StoreTokens(HttpRequest request, HttpResponse response, IDataProtector protector, Tokens tokens)
    {
        // Session cookies; the refresh token keeps the session alive while the browser is open.
        var options = CookieOptions(request, null);
        response.Cookies.Append(AccessTokenCookie, protector.Protect(tokens.AccessToken), options);
        if (tokens.RefreshToken != null)
        {
            response.Cookies.Append(RefreshTokenCookie, protector.Protect(tokens.RefreshToken), options);
        }
        response.Cookies.Append(ExpiresAtCookie, tokens.ExpiresAt.ToString("O", CultureInfo.InvariantCulture), options);
    }

    private static void ClearTokens(HttpResponse response)
    {
        response.Cookies.Delete(AccessTokenCookie);
        response.Cookies.Delete(RefreshTokenCookie);
        response.Cookies.Delete(ExpiresAtCookie);
    }

    private static CookieOptions CookieOptions(HttpRequest request, TimeSpan? maxAge) => new()
    {
        HttpOnly = true,
        Secure = request.IsHttps,
        // Lax so the cookies are sent on the top-level redirect back from the Autodesk sign-in page.
        SameSite = SameSiteMode.Lax,
        MaxAge = maxAge
    };
}
