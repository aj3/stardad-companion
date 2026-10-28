using System.Net;
using StarDexs.Sync.Core;
using Xunit;

namespace StarDexs.Sync.Tests;

public class NetworkBoundsTests
{
    [Fact]
    public async Task Sync_metadata_and_files_are_bounded_even_without_content_length()
    {
        foreach (var file in new[] { false, true })
        {
            using var http = new HttpClient(new Oversized(file ? 32 * 1024 * 1024 + 1 : 65537)) { BaseAddress = new Uri("https://synthetic.test/") };
            var api = new SyncApi(http);
            await Assert.ThrowsAsync<HttpRequestException>(async () => {
                if (file) await api.FileAsync("synthetic-token", CancellationToken.None);
                else await api.StatusAsync("synthetic-token", CancellationToken.None);
            });
        }
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"patch\":42,\"settings\":\"x\"}")]
    public async Task Malformed_sync_metadata_is_a_controlled_network_error(string json)
    {
        using var http = new HttpClient(new JsonResponse(json)) { BaseAddress=new Uri("https://synthetic.test/") };
        await Assert.ThrowsAsync<HttpRequestException>(()=>new SyncApi(http).StatusAsync("synthetic", CancellationToken.None));
    }
    private sealed class JsonResponse(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content=new StringContent(json) });
    }

    private sealed class Oversized(int length) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnknownLength(length) });
    }
    private sealed class UnknownLength(int length) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(new byte[length]).AsTask();
        protected override bool TryComputeLength(out long value) { value = 0; return false; }
    }
}
