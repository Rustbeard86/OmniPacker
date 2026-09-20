namespace OmniPacker.EngineHost.Tests;

/// <summary>A throwaway data directory for an engine instance; deleted on dispose.</summary>
public sealed class TempData : IDisposable
{
    public string Dir { get; } =
        Path.Combine(Path.GetTempPath(), "omnipacker-test-" + Guid.NewGuid().ToString("N"));

    public TempData() => Directory.CreateDirectory(Dir);

    public void Dispose()
    {
        try { Directory.Delete(Dir, recursive: true); }
        catch { /* best effort - a locked sqlite handle should not fail a passing test */ }
    }
}
