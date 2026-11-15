using System.Text.Json;

namespace BeanBot.Commands;

public sealed record CommandResult(bool Ok, string Message, object? Data = null);

public sealed record PlayerListEntry(
    int ClientId,
    byte PlayerId,
    string Name,
    string ColorName,
    World.PlayerRole Role,
    bool IsAlive,
    bool IsHost,
    bool IsBot);

/// <summary>
/// Shared operator command surface used by both the CLI REPL/subcommands and loopback HTTP control server (P1.10 / P1.11).
/// </summary>
public sealed class CommandSurface
{
    private readonly BotClient? _bot;
    private readonly IFleetControlTarget? _fleet;
    private readonly Accounts.Vault _vault;

    public CommandSurface(BotClient bot, Accounts.Vault vault)
    {
        _bot = bot;
        _vault = vault;
    }

    public CommandSurface(IFleetControlTarget fleet, Accounts.Vault vault)
    {
        _fleet = fleet;
        _vault = vault;
    }

    public IReadOnlyList<BotStatusSnapshot> GetBotsSnapshot()
    {
        if (_fleet is not null)
        {
            return _fleet.GetBots().Select(b => b.GetStatus()).ToArray();
        }

        return _bot is not null ? [_bot.GetStatus()] : [];
    }

    public IReadOnlyList<PlayerListEntry> GetPlayersSnapshot()
    {
        var bots = _fleet is not null ? _fleet.GetBots() : (_bot is not null ? [_bot] : Array.Empty<BotClient>());
        var botClientIds = bots
            .Where(b => b.World.LocalClientId > 0)
            .Select(b => b.World.LocalClientId)
            .ToHashSet();

        var playersById = new Dictionary<int, PlayerListEntry>();
        foreach (var bot in bots)
        {
            foreach (var p in bot.World.PlayersByClientId.Values)
            {
                if (p.ClientId <= 0)
                {
                    continue;
                }

                var isBot = botClientIds.Contains(p.ClientId);
                var entry = new PlayerListEntry(
                    ClientId: p.ClientId,
                    PlayerId: p.PlayerId,
                    Name: string.IsNullOrWhiteSpace(p.Name) ? $"Player_{p.ClientId}" : p.Name,
                    ColorName: p.ColorName,
                    Role: p.Role,
                    IsAlive: p.IsAlive,
                    IsHost: p.IsHost,
                    IsBot: isBot);

                if (!playersById.TryGetValue(p.ClientId, out var existing) ||
                    (string.IsNullOrWhiteSpace(existing.Name) && !string.IsNullOrWhiteSpace(p.Name)))
                {
                    playersById[p.ClientId] = entry;
                }
            }
        }

        return playersById.Values
            .OrderBy(p => p.IsBot ? 1 : 0)
            .ThenBy(p => p.ClientId)
            .ToArray();
    }

    private (IReadOnlyList<BotClient> Bots, List<string> Args) ResolveTargets(
        List<string> parts,
        bool allowBroadcastWithoutBotId = false)
    {
        if (_fleet is null)
        {
            return ([_bot ?? throw new InvalidOperationException("No BotClient bound.")], parts.Skip(1).ToList());
        }

        if (parts.Count < 2)
        {
            throw new ArgumentException("Fleet mode requires <botId|all> (e.g. bot-00, Bean00, or all).");
        }

        var selector = parts[1].Trim();
        if (selector.Equals("all", StringComparison.OrdinalIgnoreCase) || selector == "*")
        {
            var all = _fleet.GetBots();
            if (all.Count == 0)
            {
                throw new InvalidOperationException("No bots are initialized in the fleet.");
            }

            return (all, parts.Skip(2).ToList());
        }

        var resolved = _fleet.ResolveBot(selector);
        if (resolved is not null)
        {
            return ([resolved], parts.Skip(2).ToList());
        }

        if (allowBroadcastWithoutBotId && !LooksLikeBotSpecifier(selector))
        {
            var all = _fleet.GetBots();
            if (all.Count > 0)
            {
                return (all, parts.Skip(1).ToList());
            }
        }

        throw new KeyNotFoundException($"Bot '{selector}' not found in fleet.");
    }

    private static bool LooksLikeBotSpecifier(string value) =>
        value.StartsWith("bot-", StringComparison.OrdinalIgnoreCase) || int.TryParse(value, out _);

    private (BotClient Bot, List<string> Args) ResolveSingleTarget(List<string> parts)
    {
        var (bots, args) = ResolveTargets(parts, allowBroadcastWithoutBotId: false);
        if (bots.Count != 1)
        {
            throw new ArgumentException("This command requires a single <botId>.");
        }

        return (bots[0], args);
    }

    public async Task<CommandResult> ExecuteAsync(string commandLine, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return new(true, string.Empty);
        }

        var parts = SplitArgs(commandLine.Trim());
        if (parts.Count == 0)
        {
            return new(true, string.Empty);
        }

        var verb = parts[0].ToLowerInvariant();
        try
        {
            switch (verb)
            {
                case "join":
                {
                    if (_bot is null)
                    {
                        return new(false, "Cannot run single-bot 'join' while operating in fleet host mode.");
                    }

                    if (parts.Count < 2)
                    {
                        return new(false, "Usage: join <room_code|auto> [accountId] [displayName]");
                    }

                    var code = parts[1];
                    var accountId = parts.Count >= 3 ? parts[2] : "bot-00";
                    var name = parts.Count >= 4 ? parts[3] : "BeanBot";
                    await _bot.JoinAsync(code, accountId, name, ct: ct).ConfigureAwait(false);
                    var status = _bot.GetStatus();
                    return new(true, $"Joined lobby {status.LobbyCode} as {status.DisplayName} (clientId={status.LocalClientId}).", status);
                }

                case "status":
                {
                    object status = _fleet is not null ? _fleet.GetFleetStatus() : _bot!.GetStatus();
                    return new(true, JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true }), status);
                }

                case "stop":
                {
                    if (_fleet is not null)
                    {
                        var target = parts.Count >= 2 ? parts[1] : "--all";
                        var msg = await _fleet.StopBotOrAllAsync(target, ct).ConfigureAwait(false);
                        return new(true, msg);
                    }

                    await _bot!.StopAsync().ConfigureAwait(false);
                    return new(true, "Bot disconnected and stopped.");
                }

                case "say":
                {
                    var (bots, args) = ResolveTargets(parts, allowBroadcastWithoutBotId: true);
                    if (args.Count < 1)
                    {
                        return new(false, _fleet is not null ? "Usage: say [botId|all] <message>" : "Usage: say <message>");
                    }

                    var message = string.Join(" ", args);
                    var activeBots = bots.Where(b => b.IsConnected).ToList();
                    if (activeBots.Count == 0)
                    {
                        activeBots = bots.ToList();
                    }

                    var anyOk = false;
                    string lastMsg = string.Empty;
                    foreach (var bot in activeBots)
                    {
                        var (ok, feedback) = await bot.SayWithStatusAsync(message, ct).ConfigureAwait(false);
                        anyOk |= ok;
                        lastMsg = feedback;
                    }

                    return activeBots.Count == 1
                        ? new(anyOk, lastMsg)
                        : new(anyOk, anyOk ? $"{lastMsg} ({activeBots.Count} bots)" : lastMsg);
                }

                case "follow":
                {
                    var (bots, args) = ResolveTargets(parts, allowBroadcastWithoutBotId: true);
                    if (args.Count < 1)
                    {
                        return new(false, _fleet is not null ? "Usage: follow [botId|all] <player|off> [kill]" : "Usage: follow <player|off> [kill]");
                    }

                    var killHold = args.Count > 1 && string.Equals(args[^1], "kill", StringComparison.OrdinalIgnoreCase);
                    var targetTokens = killHold ? args.Take(args.Count - 1) : args;
                    var targetQuery = string.Join(" ", targetTokens).Trim();
                    if (string.IsNullOrWhiteSpace(targetQuery))
                    {
                        return new(false, _fleet is not null ? "Usage: follow [botId|all] <player|off> [kill]" : "Usage: follow <player|off> [kill]");
                    }

                    var activeBots = bots.Where(b => b.IsConnected).ToList();
                    if (activeBots.Count == 0)
                    {
                        activeBots = bots.ToList();
                    }

                    var matchedCount = 0;
                    string lastMsg = string.Empty;
                    foreach (var bot in activeBots)
                    {
                        var (matched, msg) = bot.CommandFollowWithStatus(targetQuery, killHold);
                        if (matched)
                        {
                            matchedCount++;
                        }
                        lastMsg = msg;
                    }

                    var isClear = targetQuery.Equals("off", StringComparison.OrdinalIgnoreCase) ||
                                  targetQuery.Equals("stop", StringComparison.OrdinalIgnoreCase) ||
                                  targetQuery.Equals("clear", StringComparison.OrdinalIgnoreCase) ||
                                  targetQuery.Equals("none", StringComparison.OrdinalIgnoreCase);

                    var summary = isClear
                        ? (activeBots.Count == 1 ? "Follow cleared." : $"Follow cleared on {activeBots.Count} bots.")
                        : activeBots.Count == 1
                            ? lastMsg
                            : matchedCount > 0
                                ? $"Follow target '{targetQuery}' active on {matchedCount}/{activeBots.Count} bots (killHold={killHold})."
                                : $"Follow queued for '{targetQuery}' on {activeBots.Count} bots (not currently visible in room; will attach when seen).";
                    return new(true, summary);
                }

                case "kill":
                {
                    var (bot, args) = ResolveSingleTarget(parts);
                    if (args.Count < 1)
                    {
                        return new(false, _fleet is not null ? "Usage: kill <botId> <player> [--blind]" : "Usage: kill <player> [--blind]");
                    }

                    var blind = args.Any(p => string.Equals(p, "--blind", StringComparison.OrdinalIgnoreCase));
                    var killTarget = string.Join(" ", args.Where(p => !string.Equals(p, "--blind", StringComparison.OrdinalIgnoreCase))).Trim();
                    if (string.IsNullOrWhiteSpace(killTarget))
                    {
                        return new(false, _fleet is not null ? "Usage: kill <botId> <player> [--blind]" : "Usage: kill <player> [--blind]");
                    }

                    var (accepted, msg) = bot.CommandKill(killTarget, blind);
                    return new(accepted, msg);
                }

                case "tasks":
                {
                    var (bots, args) = ResolveTargets(parts, allowBroadcastWithoutBotId: true);
                    if (args.Count < 1 || (!args[0].Equals("on", StringComparison.OrdinalIgnoreCase) && !args[0].Equals("off", StringComparison.OrdinalIgnoreCase)))
                    {
                        return new(false, _fleet is not null ? "Usage: tasks [botId|all] on|off" : "Usage: tasks on|off");
                    }

                    var enabled = args[0].Equals("on", StringComparison.OrdinalIgnoreCase);
                    foreach (var bot in bots)
                    {
                        bot.CommandTasks(enabled);
                    }
                    return new(true, bots.Count == 1 ? $"Tasks {(enabled ? "enabled" : "disabled")}." : $"Tasks {(enabled ? "enabled" : "disabled")} for {bots.Count} bots.");
                }

                case "vent":
                {
                    var (bot, args) = ResolveSingleTarget(parts);
                    if (args.Count < 1 || !int.TryParse(args[0], out var ventId))
                    {
                        return new(false, _fleet is not null ? "Usage: vent <botId> <ventId> [exitVentId]" : "Usage: vent <ventId> [exitVentId]");
                    }

                    int? exitId = args.Count >= 2 && int.TryParse(args[1], out var parsedExit) ? parsedExit : null;
                    var (ok, msg) = bot.CommandVent(ventId, exitId);
                    return new(ok, msg);
                }

                case "vote":
                {
                    var (bots, args) = ResolveTargets(parts, allowBroadcastWithoutBotId: true);
                    if (args.Count < 1)
                    {
                        return new(false, _fleet is not null ? "Usage: vote [botId|all] <player|skip|clear>" : "Usage: vote <player|skip|clear>");
                    }

                    var voteTarget = string.Join(" ", args).Trim();
                    var anyOk = false;
                    var lastMsg = string.Empty;
                    foreach (var bot in bots)
                    {
                        var (ok, msg) = bot.CommandVote(voteTarget);
                        anyOk |= ok;
                        lastMsg = msg;
                    }
                    return new(anyOk, bots.Count == 1 ? lastMsg : $"{lastMsg} ({bots.Count} bots)");
                }

                case "ban":
                {
                    if (parts.Count < 2)
                    {
                        return new(false, "Usage: ban <accountId>");
                    }

                    _vault.Ban(parts[1]);
                    return new(true, $"Marked account '{parts[1]}' banned in sealed vault.");
                }

                default:
                    return new(false, $"Unknown command '{verb}'. Commands: join, status, stop, say, follow, kill, tasks, vent, vote, ban");
            }
        }
        catch (Exception ex)
        {
            return new(false, $"Command '{verb}' failed: {ex.Message}");
        }
    }

    private static List<string> SplitArgs(string input)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        foreach (var ch in input)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result;
    }
}
