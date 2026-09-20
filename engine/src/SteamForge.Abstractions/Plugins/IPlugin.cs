namespace SteamForge.Abstractions.Plugins;

/// <summary>
/// Base contract for every SteamForge plugin. Plugins ride the SteamKit2 engine
/// chassis and are composed into a job pipeline (source -> archive -> upload).
/// </summary>
public interface IPlugin
{
    /// <summary>Stable machine id, e.g. "workshop", "gofile", "sevenzip".</summary>
    string Id { get; }

    /// <summary>Human-readable name for the admin UI.</summary>
    string Name { get; }

    /// <summary>Semantic version of the plugin.</summary>
    string Version { get; }
}

/// <summary>The role a plugin plays in the pipeline.</summary>
public enum PluginKind
{
    ContentSource,
    Archiver,
    Uploader,
}
