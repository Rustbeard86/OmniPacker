using System.Text.Json;

namespace SteamForge.Engine.Steam;

/// <summary>
/// Persisted per-account Steam auth material so we do not re-run the QR / Steam
/// Guard dance on every start. Holds the long-lived refresh token and the
/// machine "guard data" blob Steam issues after a successful 2FA.
/// </summary>
/// <remarks>
/// A refresh token is a bearer credential. The file lives under the data
/// directory (git-ignored) and should be on a volume only the service can read.
/// </remarks>
public sealed record SteamToken(
    string AccountName,
    string RefreshToken,
    string? GuardData,
    DateTimeOffset ObtainedAt);

public sealed class SteamTokenStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, SteamToken> _tokens;

    public SteamTokenStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _path = Path.Combine(dataDirectory, "steam-tokens.json");
        _tokens = Load(_path);
    }

    public SteamToken? Get(string accountName)
    {
        lock (_gate)
        {
            return _tokens.TryGetValue(Key(accountName), out var token) ? token : null;
        }
    }

    /// <summary>All stored account tokens, newest first - the accounts we can swap to.</summary>
    public IReadOnlyList<SteamToken> ListTokens()
    {
        lock (_gate)
        {
            return _tokens.Values.OrderByDescending(t => t.ObtainedAt).ToList();
        }
    }

    /// <summary>
    /// The most recently obtained token across all accounts, or null if none.
    /// QR login is the primary path and configures no username, so resume falls
    /// back to this: the token is keyed by the Steam-returned account name, which
    /// app config does not know ahead of time.
    /// </summary>
    public SteamToken? GetMostRecent()
    {
        lock (_gate)
        {
            return _tokens.Values
                .OrderByDescending(t => t.ObtainedAt)
                .FirstOrDefault();
        }
    }

    public void Save(SteamToken token)
    {
        lock (_gate)
        {
            _tokens[Key(token.AccountName)] = token;
            Persist();
        }
    }

    public void Remove(string accountName)
    {
        lock (_gate)
        {
            if (_tokens.Remove(Key(accountName)))
            {
                Persist();
            }
        }
    }

    private void Persist()
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_tokens, JsonOptions));
        File.Move(tmp, _path, overwrite: true);
    }

    private static Dictionary<string, SteamToken> Load(string path)
    {
        if (!File.Exists(path))
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, SteamToken>>(File.ReadAllText(path));
            return loaded is null
                ? new(StringComparer.OrdinalIgnoreCase)
                : new(loaded, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string Key(string accountName) => accountName.Trim().ToLowerInvariant();
}
