namespace SteamForge.Engine.Steam;

/// <summary>One Steam account the service may log in as.</summary>
public sealed class SteamAccountOptions
{
    /// <summary>Safe label written to logs; never the password.</summary>
    public string Name { get; set; } = "";

    /// <summary>SteamCMD/SteamKit login name.</summary>
    public string Username { get; set; } = "";

    /// <summary>
    /// Optional password. Can be left empty to force interactive QR login,
    /// which never puts the password on disk.
    /// </summary>
    public string Password { get; set; } = "";

    /// <summary>64-bit SteamID, used for the owned-games catalog scan.</summary>
    public string SteamId { get; set; } = "";
}

/// <summary>Steam session configuration, bound from the "Steam" config section.</summary>
public sealed class SteamOptions
{
    public const string SectionName = "Steam";

    /// <summary>Accounts available to the service. First is the default.</summary>
    public List<SteamAccountOptions> Accounts { get; set; } = [];

    /// <summary>Steam Web API key for the startup owned-games catalog scan.</summary>
    public string WebApiKey { get; set; } = "";

    /// <summary>Directory for tokens, database, cache. Relative paths resolve to content root.</summary>
    public string DataDirectory { get; set; } = "data";

    /// <summary>Device name Steam shows in the authorized-devices list.</summary>
    public string DeviceFriendlyName { get; set; } = "SteamForge";
}
