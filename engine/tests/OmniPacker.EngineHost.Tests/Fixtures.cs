namespace OmniPacker.EngineHost.Tests;

/// <summary>
/// Locates and reads the shared IPC fixtures in contracts/fixtures by walking up
/// from the test binary. The same files are read by the Rust host's tests.
/// </summary>
public static class Fixtures
{
    public static string Dir { get; } = Locate();

    public static string ReadLine(string name) =>
        File.ReadAllText(Path.Combine(Dir, name)).Trim();

    private static string Locate()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "contracts", "fixtures");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            $"contracts/fixtures not found above {AppContext.BaseDirectory}");
    }
}
