using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.DevOps.Agent.Operations.Audit;

internal sealed class JsonlAuditSink : IAuditSink, IProbeableAuditSink
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly TextWriter _writer;
    private readonly bool _ownsWriter;
    private readonly SemaphoreSlim _writeMutex = new(1, 1);

    private JsonlAuditSink(TextWriter writer, bool ownsWriter, string target)
    {
        _writer = writer;
        _ownsWriter = ownsWriter;
        Target = target;
    }

    public string Target { get; }

    public bool TryProbe(out string reason)
    {
        reason = string.Empty;
        try
        {
            _writeMutex.Wait();
            try
            {
                _writer.Flush();
                if (_writer is StreamWriter streamWriter && streamWriter.BaseStream is FileStream fileStream)
                {
                    fileStream.Flush(flushToDisk: true);
                }

                return true;
            }
            finally
            {
                _writeMutex.Release();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            reason = $"audit sink probe failed ({exception.GetType().Name})";
            return false;
        }
    }

    internal static JsonlAuditSink ForStdout()
    {
        return new JsonlAuditSink(Console.Out, ownsWriter: false, target: "stdout-evidence");
    }

    internal static JsonlAuditSink ForStderr()
    {
        return new JsonlAuditSink(Console.Error, ownsWriter: false, target: "stderr-evidence");
    }

    internal static JsonlAuditSink ForFile(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        FileStream stream = new(
            fullPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read);
        StreamWriter writer = new(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };
        return new JsonlAuditSink(writer, ownsWriter: true, target: $"file:{fullPath}");
    }

    public async Task WriteAsync(AuditRecord record, CancellationToken cancellationToken = default)
    {
        string line = JsonSerializer.Serialize(record, SerializerOptions);

        await _writeMutex.WaitAsync(cancellationToken);
        try
        {
            await _writer.WriteLineAsync(line.AsMemory(), cancellationToken);
            await _writer.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeMutex.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsWriter)
        {
            await _writer.FlushAsync();
            await _writer.DisposeAsync();
        }

        _writeMutex.Dispose();
    }
}
