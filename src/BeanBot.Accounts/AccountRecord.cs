namespace BeanBot.Accounts;

public enum AccountState
{
    New,
    LoggedIn,
    InLobby,
    Cooling,
    Banned,
}

public sealed class AccountRecord
{
    public required string AccountId { get; init; }
    public required string DisplayName { get; set; }
    public required string DeviceId { get; init; }
    public string? Puid { get; set; }
    public string? FriendCode { get; set; }
    public AccountState State { get; set; } = AccountState.New;
    public string? CachedToken { get; set; }
    public string? CachedIdToken { get; set; }
    public string? RefreshToken { get; set; }
    public string? DeviceAccessToken { get; set; }
    public DateTimeOffset? TokenExpiresAt { get; set; }
    public string? CachedMatchmakerToken { get; set; }
    public string? CachedMatchmakerBaseUrl { get; set; }
    public DateTimeOffset? CachedMatchmakerTokenExpiresAt { get; set; }
    public DateTimeOffset? CooldownUntil { get; set; }
    public int ConsecutiveJoinFailures { get; set; }

    public bool IsCooling(DateTimeOffset now) =>
        State == AccountState.Cooling && CooldownUntil.HasValue && CooldownUntil.Value > now;
}
