namespace OmniPacker.EngineHost.Transport;

/// <summary>
/// A transport-agnostic duplex line stream ("virtual file"): the protocol is
/// identical whether the bytes flow over stdio pipes (v1) or a socket (future).
/// Only this interface is swapped to change transports.
/// </summary>
public interface IDuplexTransport
{
    /// <summary>Read the next line (without the trailing newline), or null at end of stream.</summary>
    Task<string?> ReadLineAsync(CancellationToken cancellationToken = default);

    /// <summary>Write one line (a newline is appended) and flush.</summary>
    Task WriteLineAsync(string line, CancellationToken cancellationToken = default);
}
