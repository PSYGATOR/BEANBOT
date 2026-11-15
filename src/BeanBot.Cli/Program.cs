using System.Net;
using BeanBot.Accounts;
using BeanBot.Commands;
using BeanBot.Host;
using BeanBot.Matchmaker;
using BeanBot.Observability;
using BeanBot.Orchestrator;
using BeanBot.Protocol;
using BeanBot.Transport;
using BeanBot.World;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeanBot.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        EnsureWritableWorkingDirectory();
        try
        {
            if (args.Length == 0)
            {
                if (!Console.IsInputRedirected && !Console.IsOutputRedirected)
                {
                    if (TryLaunchGui())
                    {
                        return 0;
                    }

                    return await RunInteractiveLauncherAsync().ConfigureAwait(false);
                }

                PrintUsage();
                return 0;
            }

            if (args[0] is "--cli" or "cli")
            {
                if (args.Length == 1)
                {
                    return await RunInteractiveLauncherAsync().ConfigureAwait(false);
                }

                return await DispatchCommandAsync(args[1..]).ConfigureAwait(false);
            }

            if (args[0] is "--gui" or "gui")
            {
                if (TryLaunchGui())
                {
                    return 0;
                }

                Console.Error.WriteLine($"GUI binary not found alongside {Environment.ProcessPath ?? "beanbot.exe"} (expected beanbot-gui.exe).");
                return 1;
            }

            return await DispatchCommandAsync(args).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            if (ex.InnerException is not null)
            {
                Console.Error.WriteLine($"  Details: {ex.InnerException.Message}");
            }

            if (args.Length == 0 && !Console.IsInputRedirected && !Console.IsOutputRedirected)
            {
                Console.WriteLine("Press Enter to exit...");
                Console.ReadLine();
            }

            return 1;
        }
    }

    private static void EnsureWritableWorkingDirectory()
    {
        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var cwd = Environment.CurrentDirectory;
        var isUnderWindowsDir = !string.IsNullOrWhiteSpace(winDir) &&
                                cwd.StartsWith(winDir, StringComparison.OrdinalIgnoreCase);
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrWhiteSpace(exeDir) &&
            Directory.Exists(exeDir) &&
            (isUnderWindowsDir ||
             (!Directory.Exists(Path.Combine(cwd, "data")) && Directory.Exists(Path.Combine(exeDir, "data")))))
        {
            try
            {
                Environment.CurrentDirectory = exeDir;
            }
            catch
            {
            }
        }
    }

    private static async Task<int> DispatchCommandAsync(string[] args)
    {
        if (args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return 0;
        }

        switch (args[0].ToLowerInvariant())
        {
            case "protocol-check":
                ProtocolRegistry.Default.AssertSpecCoverage();
                Console.WriteLine(
                    $"Protocol registry OK: {ProtocolRegistry.Default.RootMessages.Count} root messages, " +
                    $"{ProtocolRegistry.Default.Rpcs.Count} RPCs, CLIENT_VERSION={ProtocolVersion.CLIENT_VERSION} ({ProtocolVersion.CLIENT_SEMANTIC_VERSION}).");
                return 0;

            case "replay":
                return RunReplay(args[1..]);

            case "pin":
                return await RunPinAsync(args[1..]).ConfigureAwait(false);

            case "ban":
                return RunBan(args[1..]);

            case "host":
                return await RunHostAsync(args).ConfigureAwait(false);

            case "join":
            case "serve":
                return await RunSessionAsync(args).ConfigureAwait(false);

            default:
                Console.Error.WriteLine($"Unknown command: {args[0]}");
                PrintUsage();
                return 1;
        }
    }

    private static async Task<int> RunInteractiveLauncherAsync()
    {
        Console.WriteLine("BeanBot Interactive Mode (pass '--help' in a terminal for CLI flags).");
        while (true)
        {
            Console.WriteLine();
            Console.WriteLine("Select an action:");
            Console.WriteLine("  1) Self-host a lobby (host)");
            Console.WriteLine("  2) Join / auto-join a room (join)");
            Console.WriteLine("  3) Protocol registry check (protocol-check)");
            Console.WriteLine("  4) Show CLI usage (help)");
            Console.WriteLine("  5) Exit");
            Console.Write("Choice [1-5]: ");

            var choice = Console.ReadLine()?.Trim();
            if (choice is null or "5" || choice.Equals("exit", StringComparison.OrdinalIgnoreCase) || choice.Equals("quit", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            try
            {
                switch (choice)
                {
                    case "1":
                        var region = PromptWithDefault("Region (na/eu/as)", "na");
                        var profile = PromptWithDefault("Profile (live/private/local)", "live");
                        var bots = PromptWithDefault("Joiner bots (0-14, 0 for human play)", "0");
                        var isPublic = PromptWithDefault("Make lobby public once seated? (y/n)", "n");
                        var hostArgs = new List<string> { "host", "--bots", bots, "--map", "skeld", "--region", region, "--profile", profile };
                        if (isPublic.StartsWith("y", StringComparison.OrdinalIgnoreCase))
                        {
                            hostArgs.Add("--public");
                        }

                        await DispatchCommandAsync(hostArgs.ToArray()).ConfigureAwait(false);
                        break;

                    case "2":
                        var code = PromptWithDefault("Room code (6-letter code, 'auto', or 'host')", "auto");
                        var botCountStr = PromptWithDefault("How many bots to join (1-15)", "1");
                        if (!int.TryParse(botCountStr, out var botCount) || botCount < 1 || botCount > Roster.MaxTotalSeats)
                        {
                            Console.Error.WriteLine($"Invalid bot count '{botCountStr}'. Expected 1..{Roster.MaxTotalSeats}.");
                            continue;
                        }

                        var jRegion = PromptWithDefault("Region (na/eu/as)", "na");
                        var jProfile = PromptWithDefault("Profile (live/private/local)", "live");
                        var joinArgs = new List<string>
                        {
                            "join",
                            code,
                            "--bots",
                            botCount.ToString(),
                            "--region",
                            jRegion,
                            "--profile",
                            jProfile,
                        };
                        if (botCount == 1)
                        {
                            var account = PromptWithDefault("Account ID", "bot-00");
                            var name = PromptWithDefault("Display name", "BeanBot");
                            joinArgs.Add("--account");
                            joinArgs.Add(account);
                            joinArgs.Add("--name");
                            joinArgs.Add(name);
                        }

                        if (code.Equals("host", StringComparison.OrdinalIgnoreCase))
                        {
                            var jPublic = PromptWithDefault("Make lobby public? (y/n)", "n");
                            if (jPublic.StartsWith("y", StringComparison.OrdinalIgnoreCase))
                            {
                                joinArgs.Add("--public");
                            }
                        }

                        await DispatchCommandAsync(joinArgs.ToArray()).ConfigureAwait(false);
                        break;

                    case "3":
                        await DispatchCommandAsync(["protocol-check"]).ConfigureAwait(false);
                        break;

                    case "4":
                        PrintUsage();
                        break;

                    default:
                        Console.WriteLine("Please enter a number between 1 and 5.");
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                if (ex.InnerException is not null)
                {
                    Console.Error.WriteLine($"  Details: {ex.InnerException.Message}");
                }
            }
        }
    }

    private static string PromptWithDefault(string label, string defaultValue)
    {
        Console.Write($"{label} [{defaultValue}]: ");
        var line = Console.ReadLine()?.Trim();
        return string.IsNullOrWhiteSpace(line) ? defaultValue : line;
    }

    private static Vault OpenVaultOrPrompt(string vaultPath, string? vaultKey)
    {
        if (string.IsNullOrWhiteSpace(vaultKey) &&
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Vault.VaultKeyEnvVar)) &&
            !Console.IsInputRedirected)
        {
            Console.Write($"Vault passphrase ({Vault.VaultKeyEnvVar} not set; enter a passphrase for this session): ");
            vaultKey = ReadMaskedLine();
        }

        return new Vault(vaultPath, vaultKey);
    }

    private static string ReadMaskedLine()
    {
        var chars = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return new string(chars.ToArray());
            }

            if (key.Key == ConsoleKey.Backspace && chars.Count > 0)
            {
                chars.RemoveAt(chars.Count - 1);
                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                chars.Add(key.KeyChar);
            }
        }
    }

    private static void EnsureEosConfigIfRequired(ServerProfile profile)
    {
        if (!profile.UseDeviceAuth || EosClientConfig.FromEnvironment() is not null)
        {
            return;
        }

        if (Console.IsInputRedirected)
        {
            throw new InvalidOperationException(
                "EOS credentials missing for live profile. Set BEANBOT_EOS_CLIENT_ID, BEANBOT_EOS_CLIENT_SECRET, and BEANBOT_EOS_DEPLOYMENT_ID.");
        }

        Console.WriteLine("EOS credentials not set in environment (needed for --profile live):");
        Console.Write("  BEANBOT_EOS_CLIENT_ID: ");
        var id = Console.ReadLine()?.Trim();
        Console.Write("  BEANBOT_EOS_CLIENT_SECRET: ");
        var secret = ReadMaskedLine().Trim();
        Console.Write("  BEANBOT_EOS_DEPLOYMENT_ID: ");
        var dep = Console.ReadLine()?.Trim();

        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(dep))
        {
            throw new InvalidOperationException(
                "EOS credentials missing for live profile. Set BEANBOT_EOS_CLIENT_ID, BEANBOT_EOS_CLIENT_SECRET, and BEANBOT_EOS_DEPLOYMENT_ID.");
        }

        Environment.SetEnvironmentVariable("BEANBOT_EOS_CLIENT_ID", id);
        Environment.SetEnvironmentVariable("BEANBOT_EOS_CLIENT_SECRET", secret);
        Environment.SetEnvironmentVariable("BEANBOT_EOS_DEPLOYMENT_ID", dep);
    }

    private static HttpControlServer? TryStartHttpControl(CommandSurface surface, object target, ref int httpPort)
    {
        if (httpPort <= 0)
        {
            using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            httpPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        var server = target switch
        {
            IFleetControlTarget fleet => new HttpControlServer(surface, fleet, httpPort),
            BotClient bot => new HttpControlServer(surface, bot, httpPort),
            _ => throw new InvalidOperationException("Unsupported control target."),
        };

        try
        {
            server.Start();
            Console.WriteLine($"Control HTTP listening on {server.BaseUri}.");
            return server;
        }
        catch (HttpListenerException ex)
        {
            Console.Error.WriteLine($"Warning: HTTP control server on port {httpPort} disabled ({ex.Message}).");
            server.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return null;
        }
    }

    private static int RunBan(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: beanbot ban <accountId>");
            return 1;
        }

        var vault = OpenVaultOrPrompt(Vault.DefaultVaultPath, null);
        vault.Ban(args[0]);
        Console.WriteLine($"Banned account '{args[0]}' in sealed vault.");
        return 0;
    }

    private static async Task<int> RunSessionAsync(string[] args)
    {
        var profileMode = "live";
        var region = "na";
        var matchmakerUrl = "";
        var accountId = "bot-00";
        var displayName = "BeanBot";
        var botCount = 1;
        int? spacingMs = null;
        var httpPort = 8777;
        var makePublic = false;
        var vaultPath = Vault.DefaultVaultPath;
        string? vaultKey = null;
        string? roomCode = null;
        var initialCommands = new List<string>();

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--profile" when i + 1 < args.Length:
                    profileMode = args[++i].ToLowerInvariant();
                    break;
                case "--region" when i + 1 < args.Length:
                    region = args[++i].ToLowerInvariant();
                    break;
                case "--matchmaker" when i + 1 < args.Length:
                    matchmakerUrl = args[++i];
                    break;
                case "--account" when i + 1 < args.Length:
                    accountId = args[++i];
                    break;
                case "--name" when i + 1 < args.Length:
                    displayName = args[++i];
                    break;
                case "--bots" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out botCount) || botCount < 1 || botCount > Roster.MaxTotalSeats)
                    {
                        Console.Error.WriteLine($"Invalid --bots '{args[i]}'. Expected 1..{Roster.MaxTotalSeats}.");
                        return 1;
                    }
                    break;
                case "--spacing-ms" when i + 1 < args.Length && int.TryParse(args[++i], out var s):
                    spacingMs = Math.Max(0, s);
                    break;
                case "--public":
                    makePublic = true;
                    break;
                case "--http-port" when i + 1 < args.Length && int.TryParse(args[++i], out var p):
                    httpPort = p;
                    break;
                case "--vault-path" when i + 1 < args.Length:
                    vaultPath = args[++i];
                    break;
                case "--vault-key" when i + 1 < args.Length:
                    vaultKey = args[++i];
                    break;
                default:
                    if (!args[i].StartsWith("--", StringComparison.Ordinal) && roomCode is null && args[0].Equals("join", StringComparison.OrdinalIgnoreCase))
                    {
                        roomCode = args[i];
                    }
                    break;
            }
        }

        if (botCount > 1 && string.Equals(roomCode, "host", StringComparison.OrdinalIgnoreCase))
        {
            var hostArgs = new List<string>
            {
                "host",
                "--bots",
                (botCount - 1).ToString(),
                "--region",
                region,
                "--profile",
                profileMode,
                "--http-port",
                httpPort.ToString(),
                "--vault-path",
                vaultPath,
            };
            if (!string.IsNullOrWhiteSpace(matchmakerUrl))
            {
                hostArgs.Add("--matchmaker");
                hostArgs.Add(matchmakerUrl);
            }

            if (makePublic)
            {
                hostArgs.Add("--public");
            }

            if (vaultKey is not null)
            {
                hostArgs.Add("--vault-key");
                hostArgs.Add(vaultKey);
            }

            if (spacingMs.HasValue)
            {
                hostArgs.Add("--spacing-ms");
                hostArgs.Add(spacingMs.Value.ToString());
            }

            return await RunHostAsync(hostArgs.ToArray()).ConfigureAwait(false);
        }

        var profile = ResolveProfile(profileMode, region, matchmakerUrl);
        if (!string.IsNullOrWhiteSpace(roomCode) &&
            !roomCode.Equals("auto", StringComparison.OrdinalIgnoreCase) &&
            !roomCode.Equals("host", StringComparison.OrdinalIgnoreCase) &&
            GameCode.From(roomCode).IsInvalid)
        {
            throw new RoomLookupException(
                JoinLookupStatus.Error,
                $"Invalid room code '{roomCode}'. Expected a 4- or 6-letter Among Us lobby code, 'auto', or 'host'.");
        }

        EnsureEosConfigIfRequired(profile);

        ILoggerFactory loggerFactory = CreateConsoleLoggerFactory();
        var vault = OpenVaultOrPrompt(vaultPath, vaultKey);
        using var http = new HttpClient();
        var auth = new DeviceAuthSource(vault, http, logger: loggerFactory.CreateLogger<DeviceAuthSource>());
        var matchmaker = new MatchmakerClient(http, logger: loggerFactory.CreateLogger<MatchmakerClient>());

        if (botCount > 1)
        {
            var targetRoom = string.IsNullOrWhiteSpace(roomCode) ? "auto" : roomCode;
            var timing = ResolveTiming(profile, spacingMs);
            var roster = Roster.CreateJoiners(botCount);
            await using var fleet = new Fleet(
                profile,
                vault,
                auth,
                matchmaker,
                roster: roster,
                timing: timing,
                loggerFactory: loggerFactory);

            var fleetSurface = new CommandSurface(fleet, vault);
            await using var fleetControlServer = TryStartHttpControl(fleetSurface, fleet, ref httpPort);
            Console.WriteLine($"Fleet join starting (bots={botCount}, target={targetRoom}, profile={profile.Mode}, region={profile.Region}).");

            var joinedCode = await fleet.StartJoinLobbyAsync(targetRoom).ConfigureAwait(false);
            var snap = fleet.GetStatusSnapshot();
            Console.WriteLine($"Fleet joined lobby: code={joinedCode}, bots={snap.ConnectedCount}/{snap.TargetSeats}, seats={snap.SeatsHeld}/{snap.TargetSeats}.");

            await RunReplAsync(fleetSurface).ConfigureAwait(false);
            await fleet.StopAllAsync().ConfigureAwait(false);
            return 0;
        }

        await using var bot = new BotClient(profile, vault, auth, matchmaker, loggerFactory: loggerFactory)
        {
            MakePublicWhenHosting = makePublic,
        };
        var surface = new CommandSurface(bot, vault);
        await using var controlServer = TryStartHttpControl(surface, bot, ref httpPort);
        Console.WriteLine($"Session ready (profile={profile.Mode}, region={profile.Region}).");

        if (!string.IsNullOrWhiteSpace(roomCode))
        {
            var res = await surface.ExecuteAsync($"join \"{roomCode}\" \"{accountId}\" \"{displayName}\"");
            if (res.Ok && roomCode.Equals("host", StringComparison.OrdinalIgnoreCase) && profile.Mode == ServerProfileMode.Live)
            {
                await bot.WaitForLobbyIndexedAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            }

            Console.WriteLine(res.Message);
            if (!res.Ok)
            {
                return 1;
            }
        }

        foreach (var cmd in initialCommands)
        {
            var r = await surface.ExecuteAsync(cmd);
            Console.WriteLine(r.Message);
        }

        await RunReplAsync(surface).ConfigureAwait(false);
        await bot.StopAsync();
        return 0;
    }

    private static OrchestratorTimingConfig ResolveTiming(ServerProfile profile, int? spacingMs)
    {
        var baseTiming = profile.Mode == ServerProfileMode.Live
            ? OrchestratorTimingConfig.LiveDefault
            : OrchestratorTimingConfig.FastLocal;
        return spacingMs.HasValue
            ? baseTiming with
            {
                AuthSpacing = TimeSpan.FromMilliseconds(spacingMs.Value),
                JoinSpacing = TimeSpan.FromMilliseconds(spacingMs.Value),
            }
            : baseTiming;
    }

    private static async Task<int> RunHostAsync(string[] args)
    {
        var joiners = Roster.MaxJoiners;
        var mapName = "skeld";
        var profileMode = "live";
        var region = "na";
        var matchmakerUrl = "";
        var makePublic = false;
        var kickStrangers = false;
        bool? autoStartFlag = null;
        var httpPort = 8777;
        var vaultPath = Vault.DefaultVaultPath;
        string? vaultKey = null;
        int? spacingMs = null;

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--bots" when i + 1 < args.Length && int.TryParse(args[++i], out var b):
                    joiners = b;
                    break;
                case "--map" when i + 1 < args.Length:
                    mapName = args[++i].ToLowerInvariant();
                    break;
                case "--region" when i + 1 < args.Length:
                    region = args[++i].ToLowerInvariant();
                    break;
                case "--profile" when i + 1 < args.Length:
                    profileMode = args[++i].ToLowerInvariant();
                    break;
                case "--matchmaker" when i + 1 < args.Length:
                    matchmakerUrl = args[++i];
                    break;
                case "--public":
                    makePublic = true;
                    break;
                case "--kick-strangers":
                    kickStrangers = true;
                    break;
                case "--autostart":
                    autoStartFlag = true;
                    break;
                case "--no-autostart":
                    autoStartFlag = false;
                    break;
                case "--http-port" when i + 1 < args.Length && int.TryParse(args[++i], out var p):
                    httpPort = p;
                    break;
                case "--vault-path" when i + 1 < args.Length:
                    vaultPath = args[++i];
                    break;
                case "--vault-key" when i + 1 < args.Length:
                    vaultKey = args[++i];
                    break;
                case "--spacing-ms" when i + 1 < args.Length && int.TryParse(args[++i], out var s):
                    spacingMs = Math.Max(0, s);
                    break;
            }
        }

        if (mapName != "skeld")
        {
            Console.Error.WriteLine($"Map '{mapName}' is not supported in Phase 2 (only 'skeld').");
            return 1;
        }

        var profile = ResolveProfile(profileMode, region, matchmakerUrl);
        EnsureEosConfigIfRequired(profile);

        var timing = ResolveTiming(profile, spacingMs);

        ILoggerFactory loggerFactory = CreateConsoleLoggerFactory();
        var vault = OpenVaultOrPrompt(vaultPath, vaultKey);
        using var http = new HttpClient();
        var auth = new DeviceAuthSource(vault, http, logger: loggerFactory.CreateLogger<DeviceAuthSource>());
        var matchmaker = new MatchmakerClient(http, logger: loggerFactory.CreateLogger<MatchmakerClient>());

        var roster = Roster.CreateDefault(joiners);
        var autoStart = autoStartFlag ?? (joiners > 0);
        var hostOptions = new HostLobbyOptions
        {
            Map = MapTypes.Skeld,
            ExpectedJoiners = joiners,
            PublicWhenSeated = makePublic,
            KickUnexpectedClients = kickStrangers,
            AutoStartWhenReady = autoStart,
            StartGateDwell = timing.StartGateDwell,
        };

        await using var fleet = new Fleet(
            profile,
            vault,
            auth,
            matchmaker,
            roster: roster,
            hostOptions: hostOptions,
            timing: timing,
            loggerFactory: loggerFactory);

        var surface = new CommandSurface(fleet, vault);
        await using var controlServer = TryStartHttpControl(surface, fleet, ref httpPort);
        Console.WriteLine($"Host mode starting (profile={profile.Mode}, region={profile.Region}).");

        var code = await fleet.StartHostLobbyAsync().ConfigureAwait(false);
        var snap = fleet.GetStatusSnapshot();
        Console.WriteLine($"Self-hosted lobby ready: code={code}, seats={snap.SeatsHeld}/{snap.TargetSeats}, public={makePublic}.");

        await RunReplAsync(surface).ConfigureAwait(false);
        await fleet.StopAllAsync().ConfigureAwait(false);
        return 0;
    }

    private static ServerProfile ResolveProfile(string profileMode, string region, string matchmakerUrl) =>
        profileMode switch
        {
            "local" => ServerProfile.Local(),
            "private" => ServerProfile.Private(string.IsNullOrWhiteSpace(matchmakerUrl) ? "http://127.0.0.1:22023" : matchmakerUrl),
            "live" => ServerProfile.Live(region),
            _ => throw new ArgumentException($"Unknown profile '{profileMode}'. Expected local|private|live."),
        };

    private static async Task RunReplAsync(CommandSurface surface)
    {
        Console.WriteLine("Interactive prompt ready (commands: status, join, stop [--all|<botId>], say, follow [botId|all] <player|off>, kill, tasks, vent, vote, ban, exit):");
        while (true)
        {
            Console.Write("beanbot> ");
            var line = Console.In.ReadLine();
            if (line is null)
            {
                break;
            }

            var cleaned = line.Trim().TrimStart('\uFEFF');
            if (cleaned.Equals("exit", StringComparison.OrdinalIgnoreCase) || cleaned.Equals("quit", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(cleaned))
            {
                continue;
            }

            var result = await surface.ExecuteAsync(cleaned).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(result.Message))
            {
                Console.WriteLine(result.Message);
            }
        }
    }

    private static int RunReplay(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: beanbot replay <file.jsonl> [--all]");
            return 1;
        }

        var path = args[0];
        var dumpAll = args.Any(a => a == "--all");
        var viewer = ReplayViewer.Load(path);

        if (dumpAll)
        {
            for (var i = 0; i < viewer.Count; i++)
            {
                Console.WriteLine(ReplayViewer.FormatEntry(viewer.Entries[i], i, viewer.Count));
            }

            return 0;
        }

        viewer.RunInteractive(Console.In, Console.Out);
        return 0;
    }

    private static async Task<int> RunPinAsync(string[] args)
    {
        var acceptNew = args.Contains("--accept-new");
        string? region = null;
        string? endpointText = null;

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--endpoint" && i + 1 < args.Length)
            {
                endpointText = args[++i];
            }
            else if (!args[i].StartsWith("--", StringComparison.Ordinal) && region is null)
            {
                region = args[i];
            }
        }

        if (string.IsNullOrWhiteSpace(region) || string.IsNullOrWhiteSpace(endpointText))
        {
            Console.Error.WriteLine("Usage: beanbot pin [--accept-new] <region> --endpoint <ip:port>");
            return 1;
        }

        if (!IPEndPoint.TryParse(endpointText, out var ep))
        {
            Console.Error.WriteLine($"Invalid endpoint '{endpointText}'.");
            return 1;
        }

        var profile = ServerProfile.Live(region, acceptNewPin: acceptNew);
        await using var transport = profile.CreateTransport();
        await transport.ConnectAsync(ep);
        var (_, shaPath) = new TofuCertificatePinStore(profile.CertsDirectory).GetPinPaths(region);
        Console.WriteLine($"Pinned {region} -> {File.ReadAllText(shaPath).Trim()}");
        return 0;
    }

    private static bool TryLaunchGui()
    {
        var guiPath = Path.Combine(AppContext.BaseDirectory, "beanbot-gui.exe");
        if (!File.Exists(guiPath))
        {
            return false;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(guiPath)
        {
            UseShellExecute = false,
            WorkingDirectory = Environment.CurrentDirectory,
        });
        return true;
    }

    private static ILoggerFactory CreateConsoleLoggerFactory() => new StderrLoggerFactory(LogLevel.Warning);

    private static void PrintUsage()
    {
        Console.WriteLine("""
            BeanBot (Headless Client & Self-Hosted 15-Bot Fleet)
            Commands:
              beanbot [--gui | --cli]
              beanbot protocol-check
              beanbot host --bots <0..14> --map skeld --region <na|eu|as> [--public] [--kick-strangers] [--autostart|--no-autostart] [--profile local|private|live] [--http-port 8777]
              beanbot join <room_code|auto|host> [--bots <1..15>] [--public] [--profile local|private|live] [--region na|eu|as] [--account id] [--name nick] [--http-port 8777]
              beanbot serve [--profile local|private|live] [--region na|eu|as] [--http-port 8777]
              beanbot ban <accountId>
              beanbot replay <file.jsonl> [--all]
              beanbot pin [--accept-new] <region> --endpoint <ip:port>
            """);
    }

    private sealed class StderrLoggerFactory(LogLevel minLevel) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider) { }
        public ILogger CreateLogger(string categoryName) => new StderrLogger(categoryName, minLevel);
        public void Dispose() { }

        private sealed class StderrLogger(string categoryName, LogLevel minLevel) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= minLevel && logLevel != LogLevel.None;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel))
                {
                    return;
                }

                var msg = formatter(state, exception);
                if (exception is not null)
                {
                    msg = $"{msg} | {exception.Message}";
                }

                var shortCat = categoryName.Split('.').LastOrDefault() ?? categoryName;
                Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss} {logLevel} {shortCat}] {msg}");
            }
        }
    }
}
