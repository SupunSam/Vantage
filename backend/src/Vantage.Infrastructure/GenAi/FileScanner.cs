using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Vantage.Domain;
using Vantage.Infrastructure.Services;

namespace Vantage.Infrastructure.GenAi;

/// <summary>The result of a malware scan: Passed or Failed, or NotRequired when no scanner is configured. <see cref="Report"/> is kept with the file version.</summary>
public sealed record ScanOutcome(ScanStatus Status, string? Report);

/// <summary>Scans an uploaded GenAI file for malware before it is stored.</summary>
public interface IFileScanner
{
    /// <summary>Returns the outcome. Throws a RuleException if a configured scanner can't be reached, so nothing is accepted unscanned.</summary>
    Task<ScanOutcome> ScanAsync(byte[] content, CancellationToken ct = default);
}

/// <summary>Used when GenAi:ScanHost isn't set (a local build without ClamAV).</summary>
public sealed class NoFileScanner : IFileScanner
{
    public Task<ScanOutcome> ScanAsync(byte[] content, CancellationToken ct = default) => Task.FromResult(new ScanOutcome(ScanStatus.NotRequired, null));
}

/// <summary>
/// ClamAV over its clamd TCP protocol (INSTREAM): the bytes are streamed to the scanner and its verdict is read back.
/// If the scanner is configured but can't answer, the upload is refused (fail closed).
/// </summary>
public sealed class ClamAvScanner(string host, int port, TimeSpan? timeout = null) : IFileScanner
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(30);

    public async Task<ScanOutcome> ScanAsync(byte[] content, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        string reply;
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, cts.Token);
            await using var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes("zINSTREAM\0"), cts.Token);
            var header = new byte[4];
            for (var offset = 0; offset < content.Length; offset += 65536)
            {
                var length = Math.Min(65536, content.Length - offset);
                BinaryPrimitives.WriteInt32BigEndian(header, length);
                await stream.WriteAsync(header, cts.Token);
                await stream.WriteAsync(content.AsMemory(offset, length), cts.Token);
            }
            await stream.WriteAsync(new byte[4], cts.Token); // zero length ends the stream
            using var ms = new MemoryStream();
            var buffer = new byte[512];
            int read;
            while ((read = await stream.ReadAsync(buffer, cts.Token)) > 0)
            {
                ms.Write(buffer, 0, read);
                if (buffer[read - 1] == 0) break;
            }
            reply = Encoding.UTF8.GetString(ms.ToArray()).TrimEnd('\0', '\n').Trim();
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            throw new RuleException("The malware scanner couldn't be reached, so the file was not accepted. Try again in a few minutes.");
        }

        if (reply.EndsWith("OK", StringComparison.Ordinal)) return new ScanOutcome(ScanStatus.Passed, "Malware scan (ClamAV): clean.");
        if (reply.EndsWith("FOUND", StringComparison.Ordinal))
        {
            var signature = reply.Replace("stream:", "", StringComparison.Ordinal).Replace("FOUND", "", StringComparison.Ordinal).Trim();
            return new ScanOutcome(ScanStatus.Failed, $"Malware scan (ClamAV) flagged the file: {signature}.");
        }
        throw new RuleException("The malware scanner returned an unexpected answer, so the file was not accepted.");
    }
}
