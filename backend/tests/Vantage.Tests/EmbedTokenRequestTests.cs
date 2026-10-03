using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Embedding;

namespace Vantage.Tests;

/// <summary>The GenerateToken V2 body must only contain values Power BI accepts, or it answers 400 "Invalid value".</summary>
public class EmbedTokenRequestTests
{
    private sealed class Tokens : IPowerBiTokenProvider
    {
        public Task<string> GetAccessTokenAsync(BiTenant tenant, CancellationToken ct) => Task.FromResult("t");
    }

    private sealed class Capture : HttpMessageHandler
    {
        public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"token":"abc","expiration":"2026-10-03T10:00:00Z"}""", Encoding.UTF8, "application/json") };
        }
    }

    private static async Task<JsonElement> SendAsync(IReadOnlyList<RlsIdentity> identities)
    {
        var capture = new Capture();
        var client = new PowerBiClient(new HttpClient(capture), new Tokens(), NullLogger<PowerBiClient>.Instance);
        await client.GenerateTokenAsync(new BiTenant { Name = "T" }, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), identities, default);
        return JsonDocument.Parse(capture.Body!).RootElement;
    }

    [Fact]
    public async Task Without_rls_the_body_has_no_identities_and_no_xmla_value()
    {
        var body = await SendAsync([]);
        Assert.False(body.TryGetProperty("identities", out _));
        var dataset = body.GetProperty("datasets")[0];
        Assert.False(dataset.TryGetProperty("xmlaPermissions", out _));
        Assert.True(body.TryGetProperty("reports", out _));
        Assert.True(body.TryGetProperty("targetWorkspaces", out _));
    }

    [Fact]
    public async Task With_rls_the_identity_has_username_roles_and_dataset()
    {
        var ds = Guid.NewGuid().ToString();
        var body = await SendAsync([new RlsIdentity("a@rrd.com", ["Region_North"], [ds])]);
        var id = body.GetProperty("identities")[0];
        Assert.Equal("a@rrd.com", id.GetProperty("username").GetString());
        Assert.Equal("Region_North", id.GetProperty("roles")[0].GetString());
        Assert.Equal(ds, id.GetProperty("datasets")[0].GetString());
    }
}
