namespace BeanBot.Accounts;

public sealed class AccountAlreadyActiveException : Exception
{
    public AccountAlreadyActiveException(string accountId)
        : base($"Account '{accountId}' is already active (account already active).")
    {
        AccountId = accountId;
    }

    public string AccountId { get; }
}

/// <summary>
/// Per-account exclusive file lock held for the lifetime of a connection (G5, F12).
/// Prevents silent eviction from duplicate logins on the same PUID/device id.
/// </summary>
public sealed class SessionLock : IDisposable
{
    private readonly FileStream _stream;
    private bool _disposed;

    private SessionLock(string accountId, string lockPath, FileStream stream)
    {
        AccountId = accountId;
        LockPath = lockPath;
        _stream = stream;
    }

    public string AccountId { get; }
    public string LockPath { get; }

    public static SessionLock Acquire(string accountId, string locksDirectory = "data/locks")
    {
        if (string.IsNullOrWhiteSpace(accountId))
        {
            throw new ArgumentException("Account id cannot be empty.", nameof(accountId));
        }

        locksDirectory = Vault.ResolveSafePath(locksDirectory);
        Directory.CreateDirectory(locksDirectory);
        var safeId = string.Concat(accountId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        var lockPath = Path.Combine(locksDirectory, $"{safeId}.lock");

        try
        {
            var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var marker = System.Text.Encoding.UTF8.GetBytes($"pid={Environment.ProcessId};utc={DateTimeOffset.UtcNow:O}\n");
            stream.SetLength(0);
            stream.Write(marker, 0, marker.Length);
            stream.Flush();
            return new SessionLock(accountId, lockPath, stream);
        }
        catch (IOException)
        {
            throw new AccountAlreadyActiveException(accountId);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stream.Dispose();
    }
}
