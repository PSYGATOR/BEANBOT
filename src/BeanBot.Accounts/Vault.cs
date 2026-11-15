using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BeanBot.Observability;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace BeanBot.Accounts;

/// <summary>
/// Sealed account vault persisted at data/accounts.json.age (Section 6, G6).
/// Uses ChaCha20-Poly1305 authenticated encryption with a 256-bit key derived from BEANBOT_VAULT_KEY
/// (or an explicit key passed by the host). Never stores plaintext on disk.
/// </summary>
public sealed class Vault
{
    public const string DefaultVaultPath = "data/accounts.json.age";
    public const string VaultKeyEnvVar = "BEANBOT_VAULT_KEY";

    private static readonly byte[] MagicHeader = Encoding.ASCII.GetBytes("BBVAULT1");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _vaultPath;
    private readonly byte[] _key;
    private readonly object _gate = new();
    private Dictionary<string, AccountRecord> _records = new(StringComparer.Ordinal);

    public Vault(string vaultPath = DefaultVaultPath, string? passphrase = null)
    {
        _vaultPath = ResolveSafePath(vaultPath);
        var resolvedKey = passphrase ?? Environment.GetEnvironmentVariable(VaultKeyEnvVar);
        if (string.IsNullOrWhiteSpace(resolvedKey))
        {
            throw new InvalidOperationException(
                $"Vault key missing. Set environment variable '{VaultKeyEnvVar}' or supply a passphrase.");
        }

        _key = SHA256.HashData(Encoding.UTF8.GetBytes(resolvedKey));
        if (File.Exists(_vaultPath))
        {
            LoadLocked();
        }
    }

    public string VaultPath => _vaultPath;

    public static string ResolveSafePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            return path;
        }

        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var cwd = Environment.CurrentDirectory;
        if (!string.IsNullOrWhiteSpace(winDir) && cwd.StartsWith(winDir, StringComparison.OrdinalIgnoreCase))
        {
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath);
            if (!string.IsNullOrWhiteSpace(exeDir) && Directory.Exists(exeDir))
            {
                return Path.Combine(exeDir, path);
            }

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                return Path.Combine(localAppData, "BeanBot", path);
            }
        }

        return path;
    }

    public IReadOnlyCollection<AccountRecord> List()
    {
        lock (_gate)
        {
            return _records.Values.ToArray();
        }
    }

    public AccountRecord? Get(string accountId)
    {
        lock (_gate)
        {
            return _records.TryGetValue(accountId, out var rec) ? rec : null;
        }
    }

    /// <summary>
    /// Ensures an account exists with a persisted UUIDv4 DeviceId BEFORE first login (G6).
    /// </summary>
    public AccountRecord GetOrCreate(string accountId, string displayName)
    {
        lock (_gate)
        {
            if (_records.TryGetValue(accountId, out var existing))
            {
                return existing;
            }

            var created = new AccountRecord
            {
                AccountId = accountId,
                DisplayName = displayName,
                DeviceId = Guid.NewGuid().ToString("D"),
                State = AccountState.New,
            };

            _records[accountId] = created;
            SaveLocked();
            return created;
        }
    }

    public void SaveRecord(AccountRecord record)
    {
        lock (_gate)
        {
            _records[record.AccountId] = record;
            SaveLocked();
        }
    }

    public void Ban(string accountId)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(accountId, out var rec))
            {
                throw new KeyNotFoundException($"Account '{accountId}' not found in vault.");
            }

            rec.State = AccountState.Banned;
            SaveLocked();
        }
    }

    public static void VerifyDistinctPuids(IEnumerable<AccountRecord> accounts, Counters? counters = null)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var account in accounts)
        {
            if (string.IsNullOrWhiteSpace(account.Puid))
            {
                throw new InvalidOperationException($"Account '{account.AccountId}' has no PUID assigned.");
            }

            if (!seen.Add(account.Puid))
            {
                counters?.IncrementPuidCollision();
                throw new InvalidOperationException(
                    $"PUID collision detected for '{account.Puid}' on account '{account.AccountId}'. Aborting.");
            }
        }
    }

    private void LoadLocked()
    {
        var envelope = File.ReadAllBytes(_vaultPath);
        if (envelope.Length < MagicHeader.Length + 12 + 16 ||
            !envelope.AsSpan(0, MagicHeader.Length).SequenceEqual(MagicHeader))
        {
            throw new InvalidDataException($"Corrupt or invalid sealed vault header in '{_vaultPath}'.");
        }

        var nonce = envelope.AsSpan(MagicHeader.Length, 12).ToArray();
        var ciphertext = envelope.AsSpan(MagicHeader.Length + 12).ToArray();

        var cipher = new Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305();
        cipher.Init(false, new AeadParameters(new KeyParameter(_key), 128, nonce, MagicHeader));
        var output = new byte[cipher.GetOutputSize(ciphertext.Length)];
        var len = cipher.ProcessBytes(ciphertext, 0, ciphertext.Length, output, 0);
        len += cipher.DoFinal(output, len);

        var json = Encoding.UTF8.GetString(output, 0, len);
        var list = JsonSerializer.Deserialize<List<AccountRecord>>(json, JsonOptions) ?? new();
        _records = list.ToDictionary(r => r.AccountId, StringComparer.Ordinal);
    }

    private void SaveLocked()
    {
        var dir = Path.GetDirectoryName(_vaultPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(_records.Values.OrderBy(r => r.AccountId).ToList(), JsonOptions);
        var nonce = RandomNumberGenerator.GetBytes(12);

        var cipher = new Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305();
        cipher.Init(true, new AeadParameters(new KeyParameter(_key), 128, nonce, MagicHeader));
        var ciphertext = new byte[cipher.GetOutputSize(jsonBytes.Length)];
        var len = cipher.ProcessBytes(jsonBytes, 0, jsonBytes.Length, ciphertext, 0);
        len += cipher.DoFinal(ciphertext, len);

        var tmpPath = _vaultPath + ".tmp";
        using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(MagicHeader);
            fs.Write(nonce);
            fs.Write(ciphertext, 0, len);
        }

        File.Move(tmpPath, _vaultPath, overwrite: true);
    }
}
