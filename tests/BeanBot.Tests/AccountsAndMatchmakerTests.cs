using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;
using BeanBot.Accounts;
using BeanBot.Matchmaker;
using BeanBot.Observability;
using BeanBot.Protocol;
using BeanBot.Transport;
using Xunit;

namespace BeanBot.Tests;

public sealed class AccountsAndMatchmakerTests
{
    [Fact]
    public void Vault_PersistsDeviceIdBeforeLogin_AndSealsWithChaCha20Poly1305()
    {
        var dir = CreateTempDir();
        var vaultPath = Path.Combine(dir, "accounts.json.age");

        var vault1 = new Vault(vaultPath, "test-secret-pass");
        var first = vault1.GetOrCreate("bot-00", "BeanZero");
        Assert.False(string.IsNullOrWhiteSpace(first.DeviceId));
        Assert.True(Guid.TryParse(first.DeviceId, out _));

        var raw = File.ReadAllBytes(vaultPath);
        Assert.DoesNotContain("bot-00", Encoding.UTF8.GetString(raw));

        var vault2 = new Vault(vaultPath, "test-secret-pass");
        var second = vault2.GetOrCreate("bot-00", "BeanZero");
        Assert.Equal(first.DeviceId, second.DeviceId);

        Assert.ThrowsAny<Exception>(() => new Vault(vaultPath, "wrong-pass"));
    }

    [Fact]
    public void SessionLock_EnforcesSingleLiveConnectionPerAccount()
    {
        var dir = CreateTempDir();
        using var lock1 = SessionLock.Acquire("bot-01", dir);

        Assert.Throws<AccountAlreadyActiveException>(() => SessionLock.Acquire("bot-01", dir));

        lock1.Dispose();
        using var lock2 = SessionLock.Acquire("bot-01", dir);
        Assert.Equal("bot-01", lock2.AccountId);
    }

    [Fact]
    public void VerifyDistinctPuids_RejectsSharedPuidAndCountsCollision()
    {
        var dir = CreateTempDir();
        var vault = new Vault(Path.Combine(dir, "accounts.json.age"), "pass");
        var a1 = vault.GetOrCreate("a1", "Bot1");
        a1.Puid = "shared-puid-123";
        vault.SaveRecord(a1);

        var a2 = vault.GetOrCreate("a2", "Bot2");
        a2.Puid = "shared-puid-123";
        vault.SaveRecord(a2);

        var counters = new Counters();
        Assert.Throws<InvalidOperationException>(() => Vault.VerifyDistinctPuids(new[] { a1, a2 }, counters));
        Assert.Equal(1, counters.PuidCollisions);
    }

    [Fact]
    public async Task DeviceAuthSource_AndroidDeviceId_RefreshAnd401Reauth_WorkPerUpdatedP1_1()
    {
        var dir = CreateTempDir();
        var vault = new Vault(Path.Combine(dir, "accounts.json.age"), "pass");

        // Local profile derives deterministic PUID without hitting external HTTP
        var localAcc = vault.GetOrCreate("local-00", "LocalBean");
        var localAuth = new DeviceAuthSource(vault, new HttpClient());
        var localResult = await localAuth.AuthenticateAsync(localAcc, ServerProfile.Local());
        Assert.False(string.IsNullOrWhiteSpace(localResult.Puid));

        // Live Android device auth: POST /auth/v1/accounts/deviceid once, external_auth_type=deviceid_access_token, nonce (32 hex), display_name, account_id -> PUID, refresh_token, 429 backoff, refresh < 300s, expired refresh fallback
        var deviceIdCalls = 0;
        var tokenCalls = 0;
        var seenForms = new List<Dictionary<string, string>>();
        var handler = new StubHttpHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/auth/v1/accounts/deviceid")
            {
                deviceIdCalls++;
                var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                var nvc = HttpUtility.ParseQueryString(body);
                Assert.Equal("Android", nvc["deviceModel"]);
                Assert.Equal("dep123", nvc["deployment_id"]);
                return JsonResp(HttpStatusCode.OK, new { access_token = "dev-access-tok-001" });
            }

            if (path == "/auth/v1/oauth/token")
            {
                tokenCalls++;
                var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                var nvc = HttpUtility.ParseQueryString(body);
                var form = nvc.AllKeys.Where(k => k is not null).ToDictionary(k => k!, k => nvc[k]!);
                seenForms.Add(form);

                if (tokenCalls == 1)
                {
                    return new HttpResponseMessage((HttpStatusCode)429);
                }

                if (form["grant_type"] == "refresh_token")
                {
                    if (form["refresh_token"] == "rt-expired")
                    {
                        return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                    }

                    return JsonResp(HttpStatusCode.OK, new
                    {
                        access_token = "eos-access-refreshed",
                        refresh_token = "rt-2",
                        expires_in = 3600,
                        account_id = "puid-should-never-rotate",
                    });
                }

                return JsonResp(HttpStatusCode.OK, new
                {
                    access_token = "eos-access-initial",
                    refresh_token = "rt-1",
                    expires_in = 3600,
                    account_id = "puid-eos-999",
                });
            }

            if (path == "/api/user")
            {
                return JsonResp(HttpStatusCode.OK, new { username = "IgnoredBackendName", friendCode = "live#1234" });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var delays = new List<TimeSpan>();
        var liveAcc = vault.GetOrCreate("live-00", "RosterBean");
        var originalDeviceId = liveAcc.DeviceId;
        var eosCfg = new EosClientConfig("cid", "csec", "dep123", "https://eos.test", "https://backend.test");
        var liveAuth = new DeviceAuthSource(vault, new HttpClient(handler), eosCfg, delayAsync: (ts, _) => { delays.Add(ts); return Task.CompletedTask; });
        var liveProfile = ServerProfile.Private("https://mm.test", useDtls: false, useDeviceAuth: true);

        // 1. Initial login (retried after 429 backoff >= 8s)
        var initial = await liveAuth.AuthenticateAsync(liveAcc, liveProfile);
        Assert.Equal("puid-eos-999", initial.Puid);
        Assert.Equal("RosterBean", initial.DisplayName); // Preserved roster name
        Assert.Equal("live#1234", initial.FriendCode);
        Assert.Equal("eos-access-initial", initial.AccessToken);
        Assert.Equal("dev-access-tok-001", initial.Account.DeviceAccessToken);
        Assert.Equal("rt-1", initial.Account.RefreshToken);
        Assert.Equal(1, deviceIdCalls);
        Assert.Single(delays);
        Assert.True(delays[0] >= TimeSpan.FromSeconds(8));

        var authForm = seenForms.Last();
        Assert.Equal("external_auth", authForm["grant_type"]);
        Assert.Equal("deviceid_access_token", authForm["external_auth_type"]);
        Assert.Equal("dev-access-tok-001", authForm["external_auth_token"]);
        Assert.Equal("RosterBean", authForm["display_name"]);
        Assert.Equal("dep123", authForm["deployment_id"]);
        Assert.Equal(32, authForm["nonce"].Length);

        // 2. Expiring within <300s triggers refresh_token and NEVER rotates PUID
        initial.Account.TokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(120);
        var refreshed = await liveAuth.AuthenticateAsync(initial.Account, liveProfile);
        Assert.Equal("eos-access-refreshed", refreshed.AccessToken);
        Assert.Equal("puid-eos-999", refreshed.Puid); // Never rotates!
        Assert.Equal("rt-2", refreshed.Account.RefreshToken);
        Assert.Equal("refresh_token", seenForms.Last()["grant_type"]);

        // 3. Expired refresh_token (401) falls back to persisted DeviceAccessToken (NEVER regenerates deviceId or DeviceAccessToken)
        refreshed.Account.RefreshToken = "rt-expired";
        refreshed.Account.TokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(60);
        var reauthed = await liveAuth.AuthenticateAsync(refreshed.Account, liveProfile);
        Assert.Equal("eos-access-initial", reauthed.AccessToken);
        Assert.Equal(originalDeviceId, reauthed.Account.DeviceId);
        Assert.Equal("dev-access-tok-001", reauthed.Account.DeviceAccessToken);
        Assert.Equal(1, deviceIdCalls);
        Assert.Equal("external_auth", seenForms.Last()["grant_type"]);

        // 4. Banned account fails cleanly
        vault.Ban("live-00");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            liveAuth.AuthenticateAsync(vault.Get("live-00")!, liveProfile));
    }

    [Fact]
    public async Task MatchmakerClient_HandlesUserHostJoinBrowseAnd429Backoff()
    {
        var ipBytes = BitConverter.ToUInt32(IPAddress.Parse("10.20.30.40").GetAddressBytes(), 0);
        var attempts = 0;

        var handler = new StubHttpHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/user")
            {
                attempts++;
                if (attempts == 1)
                {
                    return new HttpResponseMessage((HttpStatusCode)429);
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("\"mm-bearer-tok\"", Encoding.UTF8, "application/json"),
                };
            }

            if (path == "/api/games")
            {
                return JsonResp(HttpStatusCode.OK, new { Ip = ipBytes, Port = 22055 });
            }

            if (path.StartsWith("/api/games/", StringComparison.Ordinal))
            {
                var codeStr = path["/api/games/".Length..];
                if (codeStr == "111")
                {
                    return JsonResp(HttpStatusCode.OK, new { Status = 2 });
                }

                if (codeStr == "222")
                {
                    return JsonResp(HttpStatusCode.NotFound, new { Status = 3 });
                }

                if (codeStr == "filtered")
                {
                    return JsonResp(HttpStatusCode.OK, new
                    {
                        games = new[]
                        {
                            new
                            {
                                GameId = GameCode.From("ABCDEF").Value,
                                IP = ipBytes,
                                Port = (ushort)22055,
                                HostName = "HostOne",
                                PlayerCount = 4,
                                MaxPlayers = 15,
                                NumImpostors = 2,
                                MapId = (byte)0,
                                QuickChat = (byte)2,
                            },
                        },
                    });
                }

                return JsonResp(HttpStatusCode.OK, new { Status = 1, Ip = ipBytes, Port = 22055 });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var client = new MatchmakerClient(new HttpClient(handler), maxRetries: 3, delayAsync: (_, _) => Task.CompletedTask);
        var profile = ServerProfile.Private("https://mm.test");

        var token = await client.GetMatchmakerTokenAsync(profile, "puid-1", "BotZero");
        Assert.Equal("mm-bearer-tok", token);
        Assert.Equal(2, attempts);

        var hostEp = await client.HostRoomAsync(profile, token);
        Assert.Equal(IPAddress.Parse("10.20.30.40"), hostEp.Address);
        Assert.Equal(22055, hostEp.Port);

        var ok = await client.JoinByCodeAsync(profile, token, GameCode.From("ABCDEF"));
        Assert.Equal(JoinLookupStatus.Ok, ok.Status);
        Assert.Equal(22055, ok.RelayEndPoint!.Port);

        var full = await client.JoinByCodeAsync(profile, token, GameCode.From(111));
        Assert.Equal(JoinLookupStatus.Full, full.Status);

        var missing = await client.JoinByCodeAsync(profile, token, GameCode.From(222));
        Assert.Equal(JoinLookupStatus.NotFound, missing.Status);

        var list = await client.BrowseByFilterAsync(profile, token, MapTypes.Skeld);
        Assert.Single(list);
        Assert.Equal("ABCDEF", list[0].Code.Code);
        Assert.Equal("HostOne", list[0].HostName);
    }

    [Fact]
    public async Task Matchmaker_LiveJoinByCode_UsesPostGameIdAndFallsBackToGetAndCaseInsensitiveJson()
    {
        var ipBytes = IPAddress.Parse("10.20.30.40").GetAddressBytes();
        var codePost = GameCode.From("KUBVAF");
        var codeGetFallback = GameCode.From("PQRSWX");
        var requests = new List<string>();

        var handler = new StubHttpHandler(req =>
        {
            var pq = $"{req.Method} {req.RequestUri!.AbsolutePath}{req.RequestUri.Query}";
            requests.Add(pq);

            // Code #1 succeeds on CoFindGameCode POST /api/games?gameId={int} with lowercase JSON keys
            if (req.Method == HttpMethod.Post && req.RequestUri.AbsolutePath == "/api/games" &&
                req.RequestUri.Query == $"?gameId={codePost.Value}")
            {
                return JsonResp(HttpStatusCode.OK, new { ip = "10.20.30.40", port = 22055 });
            }

            // Code #2 returns 404 null body on POST and succeeds on GET /api/games/{int} fallback
            if (req.Method == HttpMethod.Post && req.RequestUri.AbsolutePath == "/api/games" &&
                req.RequestUri.Query == $"?gameId={codeGetFallback.Value}")
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("null", Encoding.UTF8, "application/json"),
                };
            }

            if (req.Method == HttpMethod.Get && req.RequestUri.AbsolutePath == $"/api/games/{codeGetFallback.Value}")
            {
                return JsonResp(HttpStatusCode.OK, new { status = 1, ip = BitConverter.ToUInt32(ipBytes), port = 22056 });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent(string.Empty, Encoding.UTF8, "application/json"),
            };
        });

        var client = new MatchmakerClient(new HttpClient(handler), maxRetries: 1, delayAsync: (_, _) => Task.CompletedTask);
        var liveProfile = ServerProfile.Live("na");

        var postResult = await client.JoinByCodeAsync(liveProfile, "mm-tok", codePost);
        Assert.Equal(JoinLookupStatus.Ok, postResult.Status);
        Assert.Equal(22055, postResult.RelayEndPoint!.Port);

        var getResult = await client.JoinByCodeAsync(liveProfile, "mm-tok", codeGetFallback);
        Assert.Equal(JoinLookupStatus.Ok, getResult.Status);
        Assert.Equal(22056, getResult.RelayEndPoint!.Port);

        var missing = await client.JoinByCodeAsync(liveProfile, "mm-tok", GameCode.From("ABCDEF"));
        Assert.Equal(JoinLookupStatus.NotFound, missing.Status);

        Assert.Contains($"POST /api/games?gameId={codePost.Value}", requests);
        Assert.Contains($"POST /api/games?gameId={codeGetFallback.Value}", requests);
        Assert.Contains($"GET /api/games/{codeGetFallback.Value}", requests);
    }

    [Fact]
    public async Task Matchmaker_LiveJoinByCode_MapsReason116ToRateLimited_WithoutExtraGetFallback()
    {
        var requests = new List<string>();
        var handler = new StubHttpHandler(req =>
        {
            requests.Add($"{req.Method} {req.RequestUri!.AbsolutePath}");
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{\"Errors\":[{\"Reason\":116}]}", Encoding.UTF8, "application/json"),
            };
        });

        var client = new MatchmakerClient(new HttpClient(handler), maxRetries: 1, delayAsync: (_, _) => Task.CompletedTask);
        var result = await client.JoinByCodeAsync(ServerProfile.Live("na"), "mm-tok", GameCode.From("PLIHRG"));

        Assert.Equal(JoinLookupStatus.RateLimited, result.Status);
        Assert.Contains("TooManyRequests", result.ErrorMessage);
        Assert.Single(requests); // POST only; no GET fallback that would extend the rate limit.
    }

    [Fact]
    public void GroundRule_G1_NoWireIdsOutsideProtocolAndTransport()
    {
        var repoSrc = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src"));
        var restrictedProjects = new[]
        {
            "BeanBot.Accounts",
            "BeanBot.Matchmaker",
            "BeanBot.World",
            "BeanBot.Nav",
            "BeanBot.Behaviors",
            "BeanBot.Host",
            "BeanBot.Commands",
            "BeanBot.Orchestrator",
            "BeanBot.Cli",
            "BeanBot.Gui",
        };

        var forbiddenTokens = new[] { "RootMessageId.", "RpcId.", "GameDataTag.", "UdpSendOption." };

        foreach (var proj in restrictedProjects)
        {
            var projDir = Path.Combine(repoSrc, proj);
            foreach (var cs in Directory.EnumerateFiles(projDir, "*.cs", SearchOption.AllDirectories))
            {
                if (cs.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
                    cs.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var text = File.ReadAllText(cs);
                foreach (var token in forbiddenTokens)
                {
                    Assert.DoesNotContain(token, text, StringComparison.Ordinal);
                }
            }
        }
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "beanbot-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static HttpResponseMessage JsonResp(HttpStatusCode status, object payload) =>
        new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> fn) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(fn(request));
    }
}
