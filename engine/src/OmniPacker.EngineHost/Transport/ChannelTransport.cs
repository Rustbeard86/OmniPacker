using System.Threading.Channels;

namespace OmniPacker.EngineHost.Transport;

/// <summary>
/// In-memory transport for tests: the test enqueues inbound lines and drains the
/// outbound lines the host wrote. Proves the host loop end to end without any
/// real process, pipe, or socket. Also the reference for a future socket
/// transport - same interface, different backing stream.
/// </summary>
public sealed class ChannelTransport : IDuplexTransport
{
    private readonly Channel<string> _inbound = Channel.CreateUnbounded<string>();
    private readonly Channel<string> _outbound = Channel.CreateUnbounded<string>();

    /// <summary>Queue a line as if received from the peer.</summary>
    public void EnqueueInbound(string line) => _inbound.Writer.TryWrite(line);

    /// <summary>Signal end of the inbound stream so the host loop exits.</summary>
    public void CompleteInbound() => _inbound.Writer.TryComplete();

    /// <summary>All lines the host has written, in order, as they arrive.</summary>
    public IAsyncEnumerable<string> ReadOutboundAsync(CancellationToken ct = default) =>
        _outbound.Reader.ReadAllAsync(ct);

    /// <summary>Try to read one already-written outbound line without waiting.</summary>
    public bool TryReadOutbound(out string? line)
    {
        if (_outbound.Reader.TryRead(out var l))
        {
            line = l;
            return true;
        }
        line = null;
        return false;
    }

    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _inbound.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public Task WriteLineAsync(string line, CancellationToken cancellationToken = default)
    {
        _outbound.Writer.TryWrite(line);
        return Task.CompletedTask;
    }

    /// <summary>Mark the outbound side complete (host loop finished).</summary>
    public void CompleteOutbound() => _outbound.Writer.TryComplete();
}
