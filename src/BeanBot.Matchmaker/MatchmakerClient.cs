using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using BeanBot.Protocol;
using BeanBot.Transport;
using Microsoft.Extensions.Logging;

namespace BeanBot.Matchmaker;

public enum JoinLookupStatus
{
    Ok = 1,
    Full = 2,
    NotFound = 3,
    Error = 4,
    RateLimited = 5,
}

public sealed record GameListingInfo(
    GameCode Code,
    IPEndPoint RelayEndPoint,
    string HostName,
    int PlayerCount,
    int MaxPlayers,
    int NumImpostors,
    MapTypes Map,
    QuickChatModes QuickChat,
    GameKeywords Language = GameKeywords.All);

public sealed record JoinLookupResult(
    JoinLookupStatus Status,
    IPEndPoint? RelayEndPoint,
    GameListingInfo? Listing,
    string? ErrorMessage = null);

public sealed class RoomLookupException : InvalidOperationException
{
    public JoinLookupStatus Status { get; }

    public RoomLookupException(JoinLookupStatus status, string message)
        : base(message)
    {
        Status = status;
    }
}

/// <summary>
/// HTTP matchmaker client speaking /api/user, /api/games, /api/games/{code}, and /api/games/filtered (5.1, F2, F3).
/// Works identically against Local Impostor and Live Innersloth matchmakers via ServerProfile.MatchmakerBaseUrl.
/// Never caches relay IP addresses (5.1, F2).
/// </summary>
public sealed class MatchmakerClient
{
    private readonly HttpClient _http;
    private readonly ILogger<MatchmakerClient>? _logger;
    private readonly int _maxRetries;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    public MatchmakerClient(
        HttpClient? httpClient = null,
        ILogger<MatchmakerClient>? logger = null,
        int maxRetries = 3,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _http = httpClient ?? new HttpClient();
        _logger = logger;
        _maxRetries = maxRetries;
        _delayAsync = delayAsync ?? Task.Delay;
    }

    public async Task<string> GetMatchmakerTokenAsync(
        ServerProfile profile,
        string puid,
        string username,
        Language language = Language.English,
        string? eosIdToken = null,
        CancellationToken ct = default)
    {
        var url = $"{profile.MatchmakerBaseUrl.TrimEnd('/')}/api/user";
        var payload = new
        {
            Puid = puid,
            Username = username,
            ClientVersion = ProtocolVersion.CLIENT_VERSION.Value,
            Language = (uint)language,
        };

        using var resp = await SendWithBackoffAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(payload) };
            if (!string.IsNullOrWhiteSpace(eosIdToken))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", eosIdToken);
            }

            return req;
        }, ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException(
                $"Matchmaker /api/user failed ({(int)resp.StatusCode} {resp.ReasonPhrase}): {errBody}",
                null,
                resp.StatusCode);
        }

        var raw = (await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim();
        if (raw.StartsWith('"') && raw.EndsWith('"') && raw.Length >= 2)
        {
            raw = JsonSerializer.Deserialize<string>(raw) ?? raw;
        }

        return raw;
    }

    public async Task<IPEndPoint> HostRoomAsync(
        ServerProfile profile,
        string matchmakerToken,
        CancellationToken ct = default)
    {
        var url = $"{profile.MatchmakerBaseUrl.TrimEnd('/')}/api/games";
        using var postResp = await SendWithBackoffAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", matchmakerToken);
            return req;
        }, ct).ConfigureAwait(false);

        HttpResponseMessage resp = postResp;
        if (postResp.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound)
        {
            postResp.Dispose();
            resp = await SendWithBackoffAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Put, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", matchmakerToken);
                return req;
            }, ct).ConfigureAwait(false);
        }

        using (resp)
        {
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            return ParseRelayEndPoint(doc.RootElement);
        }
    }

    public async Task<JoinLookupResult> JoinByCodeAsync(
        ServerProfile profile,
        string matchmakerToken,
        GameCode code,
        CancellationToken ct = default)
    {
        var baseUrl = profile.MatchmakerBaseUrl.TrimEnd('/');
        var getUrl = $"{baseUrl}/api/games/{code.Value}";
        var postUrl = $"{baseUrl}/api/games?gameId={code.Value}";

        if (profile.Mode == ServerProfileMode.Live)
        {
            // Real Among Us CoJoinOnlineGameFromCode sends POST /api/games?gameId={int32};
            // CoFindGameInfo sends GET /api/games/{int32}. Try POST first on live, and only fall back to GET when
            // POST did not return an explicit matchmaker Errors[] verdict (avoids doubling requests against rate limits).
            var (postResult, _, postBody) = await SendJoinLookupAsync(HttpMethod.Post, postUrl, matchmakerToken, ct).ConfigureAwait(false);
            if (postResult.Status is JoinLookupStatus.Ok or JoinLookupStatus.Full or JoinLookupStatus.RateLimited)
            {
                return postResult;
            }

            if (postResult.Status == JoinLookupStatus.NotFound && !ContainsJsonErrorsArray(postBody))
            {
                var (getResult, _, _) = await SendJoinLookupAsync(HttpMethod.Get, getUrl, matchmakerToken, ct).ConfigureAwait(false);
                if (getResult.Status is JoinLookupStatus.Ok or JoinLookupStatus.Full)
                {
                    return getResult;
                }

                return new JoinLookupResult(
                    JoinLookupStatus.NotFound,
                    null,
                    null,
                    getResult.ErrorMessage ?? postResult.ErrorMessage ?? "Game not found");
            }

            return postResult;
        }

        var (localGetResult, localStatus, localBody) = await SendJoinLookupAsync(HttpMethod.Get, getUrl, matchmakerToken, ct).ConfigureAwait(false);
        if (localGetResult.Status == JoinLookupStatus.NotFound &&
            (localStatus == HttpStatusCode.MethodNotAllowed || string.IsNullOrWhiteSpace(localBody)))
        {
            var (localPostResult, _, _) = await SendJoinLookupAsync(HttpMethod.Post, postUrl, matchmakerToken, ct).ConfigureAwait(false);
            if (localPostResult.Status is JoinLookupStatus.Ok or JoinLookupStatus.Full)
            {
                return localPostResult;
            }
        }

        return localGetResult;
    }

    private async Task<(JoinLookupResult Result, HttpStatusCode StatusCode, string Body)> SendJoinLookupAsync(
        HttpMethod method,
        string url,
        string matchmakerToken,
        CancellationToken ct)
    {
        using var resp = await SendWithBackoffAsync(() =>
        {
            var req = new HttpRequestMessage(method, url);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", matchmakerToken);
            if (method == HttpMethod.Post)
            {
                req.Content = new StringContent(string.Empty, System.Text.Encoding.UTF8, "application/json");
            }

            return req;
        }, ct).ConfigureAwait(false);

        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if ((int)resp.StatusCode == 429)
        {
            return (new JoinLookupResult(JoinLookupStatus.RateLimited, null, null, "TooManyRequests (HTTP 429)"), resp.StatusCode, body);
        }

        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new HttpRequestException(
                $"Matchmaker join lookup unauthorized ({(int)resp.StatusCode} {resp.ReasonPhrase}).",
                null,
                resp.StatusCode);
        }

        if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.InternalServerError)
        {
            return (new JoinLookupResult(JoinLookupStatus.NotFound, null, null, ParseErrorMessage(body) ?? "Game not found"), resp.StatusCode, body);
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            resp.EnsureSuccessStatusCode();
            return (new JoinLookupResult(JoinLookupStatus.NotFound, null, null, "Game not found"), resp.StatusCode, body);
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            resp.EnsureSuccessStatusCode();
            return (new JoinLookupResult(JoinLookupStatus.NotFound, null, null, "Game not found"), resp.StatusCode, body);
        }

        // Handle spec 5.1 numeric status codes (1 = ok, 2 = full, 3 = not found)
        if (TryGetPropertyIgnoreCase(root, "Status", out var statusProp) && statusProp.ValueKind == JsonValueKind.Number)
        {
            var statusCode = statusProp.GetInt32();
            if (statusCode == 2) return (new JoinLookupResult(JoinLookupStatus.Full, null, null, "Lobby full (2)"), resp.StatusCode, body);
            if (statusCode == 3) return (new JoinLookupResult(JoinLookupStatus.NotFound, null, null, "Lobby not found (3)"), resp.StatusCode, body);
        }

        if (TryGetPropertyIgnoreCase(root, "Errors", out var errorsProp) &&
            errorsProp.ValueKind == JsonValueKind.Array &&
            errorsProp.GetArrayLength() > 0)
        {
            var first = errorsProp[0];
            var reason = TryGetPropertyIgnoreCase(first, "Reason", out var rProp) && rProp.ValueKind == JsonValueKind.Number
                ? rProp.GetInt32()
                : (int)DisconnectReason.Unknown;
            var msg = TryGetPropertyIgnoreCase(first, "Message", out var mProp) ? mProp.GetString() : null;
            var mapped = reason switch
            {
                (int)DisconnectReason.GameFull => JoinLookupStatus.Full,
                (int)DisconnectReason.GameNotFound => JoinLookupStatus.NotFound,
                (int)DisconnectReason.TooManyRequests => JoinLookupStatus.RateLimited,
                _ => JoinLookupStatus.Error,
            };
            var reasonName = reason is >= 0 and <= 255 && Enum.IsDefined(typeof(DisconnectReason), (byte)reason)
                ? $"{(DisconnectReason)(byte)reason} ({reason})"
                : $"Reason={reason}";
            var detail = string.IsNullOrWhiteSpace(msg) ? reasonName : $"{msg} [{reasonName}]";
            return (new JoinLookupResult(mapped, null, null, detail), resp.StatusCode, body);
        }

        if (TryGetPropertyIgnoreCase(root, "Game", out var gameProp) && gameProp.ValueKind == JsonValueKind.Object)
        {
            var listing = ParseListing(gameProp);
            return (new JoinLookupResult(JoinLookupStatus.Ok, listing.RelayEndPoint, listing), resp.StatusCode, body);
        }

        // Direct { Ip, Port } response fallback (5.1)
        if (TryParseRelayEndPoint(root, out var directEp))
        {
            return (new JoinLookupResult(JoinLookupStatus.Ok, directEp, null), resp.StatusCode, body);
        }

        resp.EnsureSuccessStatusCode();
        return (new JoinLookupResult(JoinLookupStatus.Error, null, null, "Unrecognized matchmaker join payload."), resp.StatusCode, body);
    }

    public async Task<IReadOnlyList<GameListingInfo>> BrowseByFilterAsync(
        ServerProfile profile,
        string matchmakerToken,
        MapTypes? map = null,
        int numImpostors = 0,
        GameKeywords lang = GameKeywords.English,
        QuickChatModes chatMode = QuickChatModes.QuickChatOnly,
        CancellationToken ct = default)
    {
        var filterObj = new
        {
            FilterSets = new[]
            {
                new
                {
                    GameMode = (int)GameModes.Normal,
                    Filters = Array.Empty<object>(),
                },
            },
        };

        var encodedFilter = HttpUtility.UrlEncode(JsonSerializer.Serialize(filterObj));
        var filteredUrl = $"{profile.MatchmakerBaseUrl.TrimEnd('/')}/api/games/filtered?filter={encodedFilter}";
        using var resp = await SendWithBackoffAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, filteredUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", matchmakerToken);
            return req;
        }, ct).ConfigureAwait(false);

        IReadOnlyList<GameListingInfo> listings;
        if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.InternalServerError)
        {
            var mapMask = map.HasValue ? 1 << (int)map.Value : 0x3F;
            var legacyUrl = $"{profile.MatchmakerBaseUrl.TrimEnd('/')}/api/games?mapId={mapMask}&lang={(uint)lang}&numImpostors={numImpostors}";
            using var legacyResp = await SendWithBackoffAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, legacyUrl);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", matchmakerToken);
                return req;
            }, ct).ConfigureAwait(false);
            legacyResp.EnsureSuccessStatusCode();
            var legacyJson = await legacyResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var legacyDoc = JsonDocument.Parse(legacyJson);
            listings = ParseListingArray(legacyDoc.RootElement);
        }
        else
        {
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("games", out var gamesArray))
            {
                listings = ParseListingArray(gamesArray);
            }
            else
            {
                listings = ParseListingArray(doc.RootElement);
            }
        }

        return ApplyListingFilter(listings, map, numImpostors, lang, chatMode);
    }

    private static IReadOnlyList<GameListingInfo> ApplyListingFilter(
        IReadOnlyList<GameListingInfo> listings,
        MapTypes? map,
        int numImpostors,
        GameKeywords lang,
        QuickChatModes chatMode)
    {
        return listings
            .Where(g => !map.HasValue || g.Map == map.Value)
            .Where(g => numImpostors <= 0 || g.NumImpostors == numImpostors)
            .Where(g => lang == GameKeywords.All || g.Language == GameKeywords.All || g.Language == lang)
            .Where(g => (byte)chatMode == 0 || g.QuickChat == chatMode)
            .ToArray();
    }

    private static IReadOnlyList<GameListingInfo> ParseListingArray(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<GameListingInfo>();
        }

        var list = new List<GameListingInfo>(array.GetArrayLength());
        foreach (var item in array.EnumerateArray())
        {
            list.Add(ParseListing(item));
        }

        return list;
    }

    private static GameListingInfo ParseListing(JsonElement item)
    {
        var ep = ParseRelayEndPoint(item);
        var gameId = TryGetPropertyIgnoreCase(item, "GameId", out var gid) && gid.ValueKind == JsonValueKind.Number ? gid.GetInt32() : 0;
        var hostName = TryGetPropertyIgnoreCase(item, "HostName", out var hn) ? hn.GetString() ?? string.Empty : string.Empty;
        var players = TryGetPropertyIgnoreCase(item, "PlayerCount", out var pc) && pc.ValueKind == JsonValueKind.Number ? pc.GetInt32() : 0;
        var maxPlayers = TryGetPropertyIgnoreCase(item, "MaxPlayers", out var mp) && mp.ValueKind == JsonValueKind.Number ? mp.GetInt32() : 15;
        var imps = TryGetPropertyIgnoreCase(item, "NumImpostors", out var ni) && ni.ValueKind == JsonValueKind.Number ? ni.GetInt32() : 1;
        var map = TryGetPropertyIgnoreCase(item, "MapId", out var mid) && mid.ValueKind == JsonValueKind.Number ? (MapTypes)mid.GetInt32() : MapTypes.Skeld;
        var qc = TryGetPropertyIgnoreCase(item, "QuickChat", out var qcp) && qcp.ValueKind == JsonValueKind.Number ? (QuickChatModes)qcp.GetInt32() : QuickChatModes.QuickChatOnly;
        var language = TryGetPropertyIgnoreCase(item, "Language", out var langProp) && langProp.ValueKind == JsonValueKind.Number
            ? (GameKeywords)langProp.GetUInt32()
            : GameKeywords.All;
        return new GameListingInfo(new GameCode(gameId), ep, hostName, players, maxPlayers, imps, map, qc, language);
    }

    public static IPEndPoint ParseRelayEndPoint(JsonElement element)
    {
        if (TryParseRelayEndPoint(element, out var ep) && ep is not null)
        {
            return ep;
        }

        throw new InvalidDataException($"Matchmaker response missing Ip/Port: {element.GetRawText()}");
    }

    private static bool TryParseRelayEndPoint(JsonElement element, out IPEndPoint? endPoint)
    {
        endPoint = null;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!TryGetPropertyIgnoreCase(element, "Ip", out var ipElem) ||
            !TryGetPropertyIgnoreCase(element, "Port", out var portElem) ||
            portElem.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        IPAddress address;
        if (ipElem.ValueKind == JsonValueKind.Number)
        {
            var rawIp = ipElem.GetInt64();
            address = new IPAddress(BitConverter.GetBytes((uint)rawIp));
        }
        else if (ipElem.ValueKind == JsonValueKind.String && IPAddress.TryParse(ipElem.GetString(), out var parsed))
        {
            address = parsed;
        }
        else
        {
            return false;
        }

        endPoint = new IPEndPoint(address, portElem.GetInt32());
        return true;
    }

    private static bool ContainsJsonErrorsArray(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   TryGetPropertyIgnoreCase(doc.RootElement, "Errors", out var errs) &&
                   errs.ValueKind == JsonValueKind.Array &&
                   errs.GetArrayLength() > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ParseErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                TryGetPropertyIgnoreCase(doc.RootElement, "Errors", out var errs) &&
                errs.ValueKind == JsonValueKind.Array &&
                errs.GetArrayLength() > 0)
            {
                if (TryGetPropertyIgnoreCase(errs[0], "Message", out var msg))
                {
                    return msg.GetString();
                }
            }
        }
        catch
        {
            // Non-JSON error body
        }

        return null;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(propertyName, out value))
            {
                return true;
            }

            foreach (var prop in element.EnumerateObject())
            {
                if (prop.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = prop.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private async Task<HttpResponseMessage> SendWithBackoffAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken ct)
    {
        var backoff = new ReconnectBackoff(baseDelayMs: 500, maxDelayMs: 60_000);
        for (var attempt = 0; ; attempt++)
        {
            using var req = requestFactory();
            HttpResponseMessage resp;
            try
            {
                resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Matchmaker HTTP request timed out ({req.RequestUri?.AbsolutePath}).", ex);
            }

            var code = (int)resp.StatusCode;
            if ((code == 429 || code >= 500) && attempt < _maxRetries)
            {
                TimeSpan? retryAfterDelay = null;
                if (code == 429 && resp.Headers.RetryAfter is { } retryAfter)
                {
                    retryAfterDelay = retryAfter.Delta ?? (retryAfter.Date.HasValue ? retryAfter.Date.Value - DateTimeOffset.UtcNow : null);
                }

                if (retryAfterDelay.HasValue && retryAfterDelay.Value > TimeSpan.FromSeconds(10))
                {
                    _logger?.LogWarning(
                        "Matchmaker HTTP 429 with Retry-After={RetryAfterSeconds:F0}s; stopping retries to avoid extending rate limit.",
                        retryAfterDelay.Value.TotalSeconds);
                    return resp;
                }

                resp.Dispose();
                var delay = retryAfterDelay.HasValue && retryAfterDelay.Value > TimeSpan.Zero
                    ? retryAfterDelay.Value
                    : backoff.NextDelay();
                _logger?.LogWarning("Matchmaker HTTP {StatusCode}; retrying in {DelayMs}ms.", code, delay.TotalMilliseconds);
                await _delayAsync(delay, ct).ConfigureAwait(false);
                continue;
            }

            return resp;
        }
    }
}
