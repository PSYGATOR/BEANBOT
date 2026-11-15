using System.Text.Json;

namespace BeanBot.Observability;

/// <summary>
/// Loads and steps through newline-JSON replay logs produced by ReplayLogger (P0.3, Section 13).
/// </summary>
public sealed class ReplayViewer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IReadOnlyList<ReplayEntry> _entries;
    private int _cursor;

    public ReplayViewer(IReadOnlyList<ReplayEntry> entries)
    {
        _entries = entries ?? throw new ArgumentNullException(nameof(entries));
        _cursor = entries.Count > 0 ? 0 : -1;
    }

    public int Count => _entries.Count;
    public int Cursor => _cursor;
    public ReplayEntry? Current => _cursor >= 0 && _cursor < _entries.Count ? _entries[_cursor] : null;
    public IReadOnlyList<ReplayEntry> Entries => _entries;

    public static ReplayViewer Load(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Replay log not found: {filePath}", filePath);
        }

        var entries = new List<ReplayEntry>();
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        while (sr.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var entry = JsonSerializer.Deserialize<ReplayEntry>(line, JsonOptions)
                        ?? throw new InvalidDataException($"Invalid replay JSON line: {line}");
            entries.Add(entry);
        }

        return new ReplayViewer(entries);
    }

    public ReplayEntry? Next()
    {
        if (_entries.Count == 0) return null;
        if (_cursor < _entries.Count - 1) _cursor++;
        return Current;
    }

    public ReplayEntry? Prev()
    {
        if (_entries.Count == 0) return null;
        if (_cursor > 0) _cursor--;
        return Current;
    }

    public ReplayEntry? Goto(int index)
    {
        if (_entries.Count == 0) return null;
        _cursor = Math.Clamp(index, 0, _entries.Count - 1);
        return Current;
    }

    public IReadOnlyList<ReplayEntry> Filter(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return _entries;
        }

        return _entries
            .Where(e =>
                e.BotId.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                e.Action.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                e.CausePacketName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (e.Detail?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToArray();
    }

    public static string FormatEntry(ReplayEntry entry, int index, int total)
    {
        var causeId = entry.CausePacketId.HasValue ? $"0x{entry.CausePacketId.Value:x2}" : "none";
        var detail = string.IsNullOrWhiteSpace(entry.Detail) ? string.Empty : $" | {entry.Detail}";
        return $"[{index + 1}/{total}] t=+{entry.MonotonicMs}ms bot={entry.BotId} action={entry.Action} cause={entry.CausePacketName}({causeId}) state={entry.StateHash}{detail}";
    }

    public void RunInteractive(TextReader input, TextWriter output)
    {
        if (Count == 0)
        {
            output.WriteLine("Replay file is empty.");
            return;
        }

        output.WriteLine($"Loaded {Count} replay entries. Commands: [n]ext, [p]rev, [g <idx>], [f <text>], [a]ll, [q]uit");
        output.WriteLine(FormatEntry(Current!, _cursor, Count));

        while (true)
        {
            output.Write("replay> ");
            var line = input.ReadLine();
            if (line is null)
            {
                break;
            }

            var parts = line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                Next();
                output.WriteLine(FormatEntry(Current!, _cursor, Count));
                continue;
            }

            switch (parts[0].ToLowerInvariant())
            {
                case "q":
                case "quit":
                case "exit":
                    return;

                case "n":
                case "next":
                    Next();
                    output.WriteLine(FormatEntry(Current!, _cursor, Count));
                    break;

                case "p":
                case "prev":
                    Prev();
                    output.WriteLine(FormatEntry(Current!, _cursor, Count));
                    break;

                case "g":
                case "goto":
                    if (parts.Length > 1 && int.TryParse(parts[1], out var idx))
                    {
                        Goto(idx - 1);
                        output.WriteLine(FormatEntry(Current!, _cursor, Count));
                    }
                    else
                    {
                        output.WriteLine("Usage: goto <1-based-index>");
                    }
                    break;

                case "f":
                case "filter":
                    var q = parts.Length > 1 ? parts[1] : string.Empty;
                    var matches = Filter(q);
                    output.WriteLine($"Matches ({matches.Count}):");
                    for (var i = 0; i < matches.Count; i++)
                    {
                        output.WriteLine(FormatEntry(matches[i], i, matches.Count));
                    }
                    break;

                case "a":
                case "all":
                    for (var i = 0; i < Count; i++)
                    {
                        output.WriteLine(FormatEntry(_entries[i], i, Count));
                    }
                    break;

                default:
                    output.WriteLine("Unknown command. Use: n, p, g <n>, f <text>, a, q");
                    break;
            }
        }
    }
}
