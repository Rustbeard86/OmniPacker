using System.Text;

namespace OmniPacker.EngineHost.Transport;

/// <summary>
/// v1 transport: read requests from stdin, write responses/events to stdout.
/// Writes are serialized behind a lock and flushed per line so the host sees a
/// complete message immediately. stderr is left for human diagnostics only.
/// </summary>
public sealed class StdioTransport : IDuplexTransport
{
    private readonly TextReader _in;
    private readonly TextWriter _out;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public StdioTransport()
    {
        // UTF-8 without BOM, autoflush off (we flush explicitly per line).
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        _in = Console.In;
        _out = Console.Out;
    }

    // Test-friendly overload.
    public StdioTransport(TextReader input, TextWriter output)
    {
        _in = input;
        _out = output;
    }

    public Task<string?> ReadLineAsync(CancellationToken cancellationToken = default) =>
        _in.ReadLineAsync(cancellationToken).AsTask();

    public async Task WriteLineAsync(string line, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _out.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _out.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
