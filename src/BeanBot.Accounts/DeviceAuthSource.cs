using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeanBot.Transport;
using Microsoft.Extensions.Logging;

namespace BeanBot.Accounts;

public sealed record EosClientConfig(
    string ClientId,
    string ClientSecret,
    string DeploymentId,
    string EosBaseUrl = "https://api.epicgames.dev",
    string InnerslothBackendUrl = "https://backend.innersloth.com",
    string ExternalAuthType = "deviceid_access_token")
{
    public static EosClientConfig? FromEnvironment()
    {
        var id = Environment.GetEnvironmentVariable("BEANBOT_EOS_CLIENT_ID");
        var secret = Environment.GetEnvironmentVariable("BEANBOT_EOS_CLIENT_SECRET");
        var deployment = Environment.GetEnvironmentVariable("BEANBOT_EOS_DEPLOYMENT_ID");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(deployment))
        {
            return null;
        }

        var authType = Environment.GetEnvironmentVariable("BEANBOT_EOS_EXTERNAL_AUTH_TYPE");
        return new EosClientConfig(
            id,
            secret,
            deployment,
            ExternalAuthType: string.IsNullOrWhiteSpace(authType) ? "deviceid_access_token" : authType.Trim());
    }
}

public sealed record AuthSessionResult(
    AccountRecord Account,
    string AccessToken,
    string Puid,
    string DisplayName,
    string FriendCode,
    string? IdToken = null);

public interface IDeviceAuthSource
{
    Task<AuthSessionResult> AuthenticateAsync(
        AccountRecord account,
        ServerProfile profile,
        CancellationToken ct = default);
}

/// <summary>
/// P1.1 Android Device Auth (platform tag 7, external_auth_type=deviceid).
/// - UseDeviceAuth == false (Local Impostor): derives a stable local PUID from the persisted DeviceId.
/// - UseDeviceAuth == true (Live / authenticated Private):
///   1) POST api.epicgames.dev/auth/v1/oauth/token (grant_type=external_auth, deviceid, nonce)
///   2) Persist account_id as PUID (never rotated on refresh)
///   3) Auto-refresh on &lt;300s remaining or 401; fallback to persisted deviceid on expired refresh token
///   4) POST backend.innersloth.com/api/user (persist friend code, preserve roster DisplayName)
///   5) 429 exponential backoff; never logs access/refresh tokens (logs only PUID + expiry).
/// </summary>
public sealed class DeviceAuthSource : IDeviceAuthSource
{
    public const int RefreshThresholdSeconds = 300;

    private readonly Vault _vault;
    private readonly HttpClient _http;
    private readonly EosClientConfig? _eosConfig;
    private readonly ILogger<DeviceAuthSource>? _logger;
    private readonly int _maxRetries;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    public DeviceAuthSource(
        Vault vault,
        HttpClient? httpClient = null,
        EosClientConfig? eosConfig = null,
        ILogger<DeviceAuthSource>? logger = null,
        int maxRetries = 4,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _vault = vault;
        _http = httpClient ?? new HttpClient();
        _eosConfig = eosConfig ?? EosClientConfig.FromEnvironment();
        _logger = logger;
        _maxRetries = maxRetries;
        _delayAsync = delayAsync ?? Task.Delay;
    }

    public Task<AuthSessionResult> AuthenticateAsync(
        AccountRecord account,
        ServerProfile profile,
        CancellationToken ct = default)
        => AuthenticateInternalAsync(account, profile, forceRefresh: false, ct);

    public Task<AuthSessionResult> RefreshOrReauthenticateAsync(
        AccountRecord account,
        ServerProfile profile,
        CancellationToken ct = default)
        => AuthenticateInternalAsync(account, profile, forceRefresh: true, ct);

    public static bool HasValidCachedToken(AccountRecord account, DateTimeOffset utcNow) =>
        !string.IsNullOrWhiteSpace(account.CachedToken) &&
        account.TokenExpiresAt.HasValue &&
        account.TokenExpiresAt.Value > utcNow.AddSeconds(RefreshThresholdSeconds) &&
        !string.IsNullOrWhiteSpace(account.Puid);

    private async Task<AuthSessionResult> AuthenticateInternalAsync(
        AccountRecord account,
        ServerProfile profile,
        bool forceRefresh,
        CancellationToken ct)
    {
        if (account.State == AccountState.Banned)
        {
            throw new InvalidOperationException($"Account '{account.AccountId}' is marked Banned.");
        }

        var now = DateTimeOffset.UtcNow;
        if (account.IsCooling(now))
        {
            throw new InvalidOperationException(
                $"Account '{account.AccountId}' is in cooldown until {account.CooldownUntil:O}.");
        }

        if (string.IsNullOrWhiteSpace(account.DeviceId))
        {
            throw new InvalidOperationException(
                $"Account '{account.AccountId}' is missing persisted DeviceId (must be persisted before login per G6).");
        }

        if (!forceRefresh && HasValidCachedToken(account, now))
        {
            if (account.State == AccountState.New)
            {
                account.State = AccountState.LoggedIn;
                _vault.SaveRecord(account);
            }

            return new AuthSessionResult(
                account,
                account.CachedToken!,
                account.Puid!,
                account.DisplayName,
                account.FriendCode ?? string.Empty,
                account.CachedIdToken ?? account.CachedToken);
        }

        if (!profile.UseDeviceAuth)
        {
            var localPuid = DeriveStableLocalPuid(account.DeviceId);
            account.Puid ??= localPuid;
            account.FriendCode ??= $"{account.AccountId}#0001";
            account.CachedToken = $"local-{account.Puid}";
            account.RefreshToken = $"local-refresh-{account.Puid}";
            account.TokenExpiresAt = now.AddHours(12);
            account.State = AccountState.LoggedIn;
            _vault.SaveRecord(account);

            _logger?.LogDebug(
                "Local auth completed for {AccountId} (PUID={Puid}, ExpiresAt={ExpiresAt:O}).",
                account.AccountId, account.Puid, account.TokenExpiresAt);
            return new AuthSessionResult(account, account.CachedToken, account.Puid, account.DisplayName, account.FriendCode, account.CachedToken);
        }

        var cfg = _eosConfig ?? throw new InvalidOperationException(
            "EOS client credentials are required for ServerProfile.UseDeviceAuth=true. " +
            "Set BEANBOT_EOS_CLIENT_ID, BEANBOT_EOS_CLIENT_SECRET, and BEANBOT_EOS_DEPLOYMENT_ID.");

        EosTokenResponse? tokenResp = null;

        // Refresh path if a refresh token is already persisted (expires_in < 300s or 401).
        if (!string.IsNullOrWhiteSpace(account.RefreshToken))
        {
            tokenResp = await TryRefreshTokenAsync(cfg, account.RefreshToken, ct).ConfigureAwait(false);
            if (tokenResp is null)
            {
                _logger?.LogInformation(
                    "Refresh token rejected/expired (401) for {AccountId}; re-authenticating from persisted device id.",
                    account.AccountId);
                account.RefreshToken = null;
                account.CachedToken = null;
            }
        }

        tokenResp ??= await RequestExternalDeviceAuthAsync(cfg, account, ct).ConfigureAwait(false);

        // Refresh never rotates the persisted PUID; initial login captures account_id as PUID.
        var resolvedPuid = string.IsNullOrWhiteSpace(account.Puid) ? tokenResp.Puid : account.Puid;
        if (string.IsNullOrWhiteSpace(resolvedPuid))
        {
            throw new InvalidDataException("EOS token response missing account_id / product_user_id.");
        }

        account.Puid = resolvedPuid;
        account.CachedToken = tokenResp.AccessToken;
        account.CachedIdToken = tokenResp.IdToken ?? tokenResp.AccessToken;
        account.RefreshToken = tokenResp.RefreshToken ?? account.RefreshToken;
        account.TokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(tokenResp.ExpiresIn);

        // 3. Innersloth account backend: persist friend code if supported; otherwise retain cached friend code.
        var friendCode = await TryFetchInnerslothAccountAsync(cfg, account, ct).ConfigureAwait(false);
        account.FriendCode = friendCode;
        account.State = AccountState.LoggedIn;
        _vault.SaveRecord(account);

        _logger?.LogInformation(
            "Authenticated {AccountId} via Android EOS deviceid (PUID={Puid}, ExpiresAt={ExpiresAt:O}).",
            account.AccountId, account.Puid, account.TokenExpiresAt);

        return new AuthSessionResult(
            account,
            account.CachedToken!,
            account.Puid!,
            account.DisplayName,
            account.FriendCode ?? string.Empty,
            account.CachedIdToken);
    }

    private async Task<EosTokenResponse> RequestExternalDeviceAuthAsync(
        EosClientConfig cfg,
        AccountRecord account,
        CancellationToken ct)
    {
        var externalToken = await EnsureExternalAuthTokenAsync(cfg, account, ct).ConfigureAwait(false);
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "external_auth",
            ["external_auth_type"] = cfg.ExternalAuthType,
            ["external_auth_token"] = externalToken,
            ["deployment_id"] = cfg.DeploymentId,
            ["nonce"] = nonce,
        };
        if (string.Equals(cfg.ExternalAuthType, "deviceid_access_token", StringComparison.Ordinal))
        {
            form["display_name"] = string.IsNullOrWhiteSpace(account.DisplayName) ? "BeanBot" : account.DisplayName;
        }

        using var resp = await SendEosFormWithBackoffAsync(cfg, "/auth/v1/oauth/token", form, ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException(
                $"EOS external_auth failed ({(int)resp.StatusCode} {resp.ReasonPhrase}): {errBody}",
                null,
                resp.StatusCode);
        }

        return await ParseEosTokenResponseAsync(resp, ct).ConfigureAwait(false);
    }

    private async Task<string> EnsureExternalAuthTokenAsync(
        EosClientConfig cfg,
        AccountRecord account,
        CancellationToken ct)
    {
        if (!string.Equals(cfg.ExternalAuthType, "deviceid_access_token", StringComparison.Ordinal))
        {
            return account.DeviceId;
        }

        if (!string.IsNullOrWhiteSpace(account.DeviceAccessToken))
        {
            return account.DeviceAccessToken;
        }

        using var resp = await SendEosFormWithBackoffAsync(cfg, "/auth/v1/accounts/deviceid", new Dictionary<string, string>
        {
            ["deviceModel"] = "Android",
            ["deployment_id"] = cfg.DeploymentId,
        }, ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException(
                $"EOS accounts/deviceid failed ({(int)resp.StatusCode} {resp.ReasonPhrase}): {errBody}",
                null,
                resp.StatusCode);
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var devToken = doc.RootElement.TryGetProperty("access_token", out var atProp) ? atProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(devToken))
        {
            throw new InvalidDataException("EOS accounts/deviceid response missing access_token.");
        }

        // Persist device credential before minting so subsequent logins reuse the same PUID.
        account.DeviceAccessToken = devToken;
        _vault.SaveRecord(account);
        return devToken;
    }

    private async Task<EosTokenResponse?> TryRefreshTokenAsync(
        EosClientConfig cfg,
        string refreshToken,
        CancellationToken ct)
    {
        using var resp = await SendEosFormWithBackoffAsync(cfg, "/auth/v1/oauth/token", new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["deployment_id"] = cfg.DeploymentId,
        }, ct).ConfigureAwait(false);

        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            return null;
        }

        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException(
                $"EOS refresh_token failed ({(int)resp.StatusCode} {resp.ReasonPhrase}): {errBody}",
                null,
                resp.StatusCode);
        }

        return await ParseEosTokenResponseAsync(resp, ct).ConfigureAwait(false);
    }

    private async Task<string> TryFetchInnerslothAccountAsync(
        EosClientConfig cfg,
        AccountRecord account,
        CancellationToken ct)
    {
        var url = $"{cfg.InnerslothBackendUrl.TrimEnd('/')}/api/user";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.CachedIdToken ?? account.CachedToken);
        req.Content = JsonContent.Create(new { Puid = account.Puid, DisplayName = account.DisplayName });

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound &&
                !string.IsNullOrWhiteSpace(account.CachedIdToken))
            {
                _logger?.LogDebug(
                    "Innersloth backend /api/user returned {StatusCode} for {AccountId}; continuing with EOS id_token for matchmaker.",
                    (int)resp.StatusCode, account.AccountId);
                return account.FriendCode ?? string.Empty;
            }

            var errBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException(
                $"Innersloth backend /api/user failed ({(int)resp.StatusCode} {resp.ReasonPhrase}): {errBody}",
                null,
                resp.StatusCode);
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var root = doc.RootElement;
        if (string.IsNullOrWhiteSpace(account.DisplayName))
        {
            if (root.TryGetProperty("username", out var uProp) && !string.IsNullOrWhiteSpace(uProp.GetString()))
            {
                account.DisplayName = uProp.GetString()!;
            }
            else if (root.TryGetProperty("name", out var nProp) && !string.IsNullOrWhiteSpace(nProp.GetString()))
            {
                account.DisplayName = nProp.GetString()!;
            }
        }

        return root.TryGetProperty("friendCode", out var fcProp)
            ? fcProp.GetString() ?? string.Empty
            : string.Empty;
    }

    private async Task<HttpResponseMessage> SendEosFormWithBackoffAsync(
        EosClientConfig cfg,
        string relativePath,
        Dictionary<string, string> formFields,
        CancellationToken ct)
    {
        var basicAuth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{cfg.ClientId}:{cfg.ClientSecret}"));
        var url = $"{cfg.EosBaseUrl.TrimEnd('/')}{relativePath}";

        for (var attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicAuth);
            req.Content = new FormUrlEncodedContent(formFields);

            var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var statusCode = (int)resp.StatusCode;
            if ((statusCode == 429 || statusCode >= 500) && attempt < _maxRetries)
            {
                resp.Dispose();
                var baseMs = 8000 * (1 << Math.Min(attempt, 3)); // >= 8s spacing + exponential backoff on 429
                var jitterMs = Random.Shared.Next(0, 1500);
                var delay = TimeSpan.FromMilliseconds(baseMs + jitterMs);
                _logger?.LogWarning("EOS endpoint {Path} returned {StatusCode}; retrying in {DelayMs}ms (attempt {Attempt}).",
                    relativePath, statusCode, delay.TotalMilliseconds, attempt + 1);
                await _delayAsync(delay, ct).ConfigureAwait(false);
                continue;
            }

            return resp;
        }
    }

    private static async Task<EosTokenResponse> ParseEosTokenResponseAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var root = doc.RootElement;
        var accessToken = root.TryGetProperty("access_token", out var atProp) ? atProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidDataException("EOS response missing access_token.");
        }

        var idToken = root.TryGetProperty("id_token", out var idProp) ? idProp.GetString() : null;
        var refreshToken = root.TryGetProperty("refresh_token", out var rtProp) ? rtProp.GetString() : null;
        var puid = root.TryGetProperty("account_id", out var acctProp) && !string.IsNullOrWhiteSpace(acctProp.GetString())
            ? acctProp.GetString()
            : root.TryGetProperty("product_user_id", out var puidProp)
                ? puidProp.GetString()
                : null;
        var expiresIn = root.TryGetProperty("expires_in", out var expProp) ? expProp.GetInt32() : 3600;

        return new EosTokenResponse(accessToken, idToken, refreshToken, puid, expiresIn);
    }

    private static string DeriveStableLocalPuid(string deviceId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("beanbot-local:" + deviceId));
        return Convert.ToHexString(hash[..16]).ToLowerInvariant();
    }

    private sealed record EosTokenResponse(string AccessToken, string? IdToken, string? RefreshToken, string? Puid, int ExpiresIn);
}
