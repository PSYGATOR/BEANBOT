using BeanBot.Accounts;
using BeanBot.Host;
using BeanBot.Observability;
using BeanBot.Protocol;
using BeanBot.Protocol.Maps;
using BeanBot.Transport;
using Microsoft.Extensions.Logging;

namespace BeanBot.Orchestrator;

public sealed record RosterSeat(
    int SeatIndex,
    bool IsHost,
    string AccountId,
    string DisplayName,
    ColorType Color);

/// <summary>
/// P2.0 & P2.3 Roster:
/// - Hard cap 14 joiners + 1 host = 15 seats total
/// - Unique pre-assigned Appendix A colours (host = Red) and unique display names
/// - Ensures 15 distinct persisted UUIDv4 DeviceIds before login and verifies 15 distinct PUIDs before lobby open
/// </summary>
public sealed class Roster
{
    public const int MaxJoiners = 14;
    public const int MaxTotalSeats = 1 + MaxJoiners;

    public IReadOnlyList<RosterSeat> Seats { get; }
    public RosterSeat HostSeat => Seats[0];
    public IReadOnlyList<RosterSeat> JoinerSeats { get; }

    public Roster(IReadOnlyList<RosterSeat> seats, bool requireHostSeat = true)
    {
        if (seats.Count is < 1 or > MaxTotalSeats)
        {
            throw new ArgumentOutOfRangeException(
                nameof(seats),
                seats.Count,
                $"Roster must contain between 1 and {MaxTotalSeats} seats.");
        }

        if (requireHostSeat && !seats[0].IsHost)
        {
            throw new ArgumentException("Seat 0 must be marked IsHost=true.", nameof(seats));
        }

        var distinctIds = new HashSet<string>(StringComparer.Ordinal);
        var distinctNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var distinctColors = new HashSet<ColorType>();
        foreach (var seat in seats)
        {
            if (!distinctIds.Add(seat.AccountId))
            {
                throw new InvalidOperationException($"Duplicate roster AccountId '{seat.AccountId}'.");
            }

            if (!distinctNames.Add(seat.DisplayName))
            {
                throw new InvalidOperationException($"Duplicate roster DisplayName '{seat.DisplayName}'.");
            }

            if (!distinctColors.Add(seat.Color))
            {
                throw new InvalidOperationException($"Duplicate roster Color '{seat.Color}'.");
            }
        }

        Seats = seats;
        JoinerSeats = seats.Skip(1).ToArray();
    }

    public static Roster CreateDefault(int joinerCount = MaxJoiners, string accountPrefix = "bot")
    {
        if (joinerCount < 0 || joinerCount > MaxJoiners)
        {
            throw new ArgumentOutOfRangeException(
                nameof(joinerCount),
                joinerCount,
                $"Joiner count must be between 0 and {MaxJoiners} (cap {MaxTotalSeats} total seats).");
        }

        return new Roster(BuildSeats(1 + joinerCount, firstIsHost: true, accountPrefix), requireHostSeat: true);
    }

    /// <summary>
    /// Creates an all-joiner roster (<c>1..15</c> bots) for joining an existing room by code or auto-join.
    /// </summary>
    public static Roster CreateJoiners(int botCount, string accountPrefix = "bot")
    {
        if (botCount < 1 || botCount > MaxTotalSeats)
        {
            throw new ArgumentOutOfRangeException(
                nameof(botCount),
                botCount,
                $"Joiner bot count must be between 1 and {MaxTotalSeats}.");
        }

        return new Roster(BuildSeats(botCount, firstIsHost: false, accountPrefix), requireHostSeat: false);
    }

    private static List<RosterSeat> BuildSeats(int total, bool firstIsHost, string accountPrefix)
    {
        var seats = new List<RosterSeat>(total);
        for (var i = 0; i < total; i++)
        {
            seats.Add(new RosterSeat(
                SeatIndex: i,
                IsHost: firstIsHost && i == 0,
                AccountId: $"{accountPrefix}-{i:D2}",
                DisplayName: ColorPalette.GetSeatDefaultName(i),
                Color: ColorPalette.GetSeatColor(i)));
        }

        return seats;
    }

    /// <summary>
    /// Ensures persisted UUIDv4 DeviceIds exist in the sealed vault BEFORE any login occurs (G6, P2.0).
    /// </summary>
    public IReadOnlyList<AccountRecord> EnsurePersistedAccounts(Vault vault)
    {
        var records = new List<AccountRecord>(Seats.Count);
        var deviceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var seat in Seats)
        {
            var rec = vault.GetOrCreate(seat.AccountId, seat.DisplayName);
            if (!string.Equals(rec.DisplayName, seat.DisplayName, StringComparison.Ordinal))
            {
                rec.DisplayName = seat.DisplayName;
                vault.SaveRecord(rec);
            }

            if (string.IsNullOrWhiteSpace(rec.DeviceId) || !deviceIds.Add(rec.DeviceId))
            {
                throw new InvalidOperationException(
                    $"Account '{seat.AccountId}' has missing or duplicate DeviceId '{rec.DeviceId}'.");
            }

            records.Add(rec);
        }

        return records;
    }

    /// <summary>
    /// Authenticates all roster accounts with paced spacing, saves PUIDs, and aborts on any PUID collision (P2.0, F8).
    /// </summary>
    public async Task<IReadOnlyList<AuthSessionResult>> AuthenticateAndVerifyDistinctPuidsAsync(
        Vault vault,
        IDeviceAuthSource auth,
        ServerProfile profile,
        TimeSpan authSpacing,
        Counters? counters = null,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        var accounts = EnsurePersistedAccounts(vault);
        var results = new List<AuthSessionResult>(accounts.Count);
        var remoteAuthCalls = 0;

        for (var i = 0; i < accounts.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var needsRemoteAuth = profile.UseDeviceAuth && !DeviceAuthSource.HasValidCachedToken(accounts[i], DateTimeOffset.UtcNow);
            if (needsRemoteAuth && remoteAuthCalls > 0 && authSpacing > TimeSpan.Zero)
            {
                var jitterMs = Random.Shared.Next(0, Math.Max(1, (int)(authSpacing.TotalMilliseconds * 0.25)));
                await Task.Delay(authSpacing + TimeSpan.FromMilliseconds(jitterMs), ct).ConfigureAwait(false);
            }

            var res = await auth.AuthenticateAsync(accounts[i], profile, ct).ConfigureAwait(false);
            if (needsRemoteAuth)
            {
                remoteAuthCalls++;
            }

            results.Add(res);
            logger?.LogInformation("Authenticated seat {Seat} ({AccountId}) -> PUID={Puid}.", i, accounts[i].AccountId, res.Puid);
        }

        var refreshed = Seats.Select(s => vault.Get(s.AccountId)!).ToArray();
        Vault.VerifyDistinctPuids(refreshed, counters);
        return results;
    }

    public IReadOnlyList<ExpectedRosterSeat> BuildExpectedSeats(Vault vault)
    {
        var result = new List<ExpectedRosterSeat>(Seats.Count);
        foreach (var seat in Seats)
        {
            var rec = vault.Get(seat.AccountId);
            if (rec is not null && !string.IsNullOrWhiteSpace(rec.Puid))
            {
                result.Add(new ExpectedRosterSeat(rec.Puid, seat.DisplayName, seat.Color, seat.IsHost));
            }
        }

        return result;
    }
}
