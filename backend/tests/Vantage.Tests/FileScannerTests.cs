using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Vantage.Domain;
using Vantage.Infrastructure.GenAi;
using Vantage.Infrastructure.Services;

namespace Vantage.Tests;

/// <summary>The ClamAV client, against a small fake clamd on a local port. No database needed.</summary>
public class FileScannerTests
{
    /// <summary>Accepts one connection, reads the INSTREAM command and chunks, and answers with <paramref name="reply"/>.</summary>
    private static (int Port, Task<(string Command, byte[] Received)> Served) FakeClamd(string reply)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var served = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var command = new byte[10];
            await stream.ReadExactlyAsync(command);
            var received = new MemoryStream();
            var header = new byte[4];
            while (true)
            {
                await stream.ReadExactlyAsync(header);
                var length = BinaryPrimitives.ReadInt32BigEndian(header);
                if (length == 0) break;
                var chunk = new byte[length];
                await stream.ReadExactlyAsync(chunk);
                received.Write(chunk);
            }
            await stream.WriteAsync(Encoding.ASCII.GetBytes(reply + "\0"));
            listener.Stop();
            return (Encoding.ASCII.GetString(command), received.ToArray());
        });
        return (port, served);
    }

    [Fact]
    public async Task A_clean_file_passes_and_the_whole_content_is_streamed()
    {
        var (port, served) = FakeClamd("stream: OK");
        var content = new byte[200_000];
        Random.Shared.NextBytes(content);

        var outcome = await new ClamAvScanner("127.0.0.1", port).ScanAsync(content);

        Assert.Equal(ScanStatus.Passed, outcome.Status);
        var (command, received) = await served;
        Assert.Equal("zINSTREAM\0", command);
        Assert.Equal(content, received);
    }

    [Fact]
    public async Task A_flagged_file_fails_with_the_signature_name()
    {
        var (port, _) = FakeClamd("stream: Eicar-Test-Signature FOUND");

        var outcome = await new ClamAvScanner("127.0.0.1", port).ScanAsync(Encoding.ASCII.GetBytes("x"));

        Assert.Equal(ScanStatus.Failed, outcome.Status);
        Assert.Contains("Eicar-Test-Signature", outcome.Report);
    }

    [Fact]
    public async Task An_unexpected_answer_or_an_unreachable_scanner_refuses_the_file()
    {
        var (port, _) = FakeClamd("stream: size limit exceeded ERROR");
        await Assert.ThrowsAsync<RuleException>(() => new ClamAvScanner("127.0.0.1", port).ScanAsync([1]));

        var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var freePort = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        await Assert.ThrowsAsync<RuleException>(() => new ClamAvScanner("127.0.0.1", freePort, TimeSpan.FromSeconds(2)).ScanAsync([1]));
    }

    [Fact]
    public async Task Without_a_scanner_the_upload_is_marked_not_required()
    {
        var outcome = await new NoFileScanner().ScanAsync([1]);

        Assert.Equal(ScanStatus.NotRequired, outcome.Status);
        Assert.Null(outcome.Report);
    }
}
