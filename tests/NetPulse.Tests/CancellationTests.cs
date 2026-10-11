using System.Net;
using NetPulse.Core.Services;

namespace NetPulse.Tests;

public class CancellationTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpStatusCode> _status;
        public StubHandler(Func<HttpRequestMessage, HttpStatusCode> status) => _status = status;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var code = _status(request);
            return Task.FromResult(new HttpResponseMessage(code) { Content = new ByteArrayContent(new byte[1024]) });
        }
    }

    private static HttpClient Client(HttpStatusCode code) => new(new StubHandler(_ => code));

    [Fact]
    public async Task Bufferbloat_PreCancelledToken_ThrowsOperationCanceled()
    {
        var service = new BufferbloatService(new PingService(), null, Client(HttpStatusCode.OK));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunTestAsync("127.0.0.1", null, cts.Token));
    }

    [Fact]
    public async Task SpeedTest_PreCancelledToken_ThrowsOperationCanceled()
    {
        var service = new SpeedTestService(null, Client(HttpStatusCode.OK));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunSpeedTestAsync(null, cts.Token));
    }

    [Fact]
    public async Task Bufferbloat_UnreachableHost_ReturnsInvalidResultInsteadOfGrade()
    {
        var service = new BufferbloatService(new PingService(), null, Client(HttpStatusCode.OK));

        // 192.0.2.0/24 is TEST-NET-1 (RFC 5737): never answers, so no baseline can be established.
        var result = await service.RunTestAsync("192.0.2.1");

        Assert.False(result.IsValid);
        Assert.Equal("N/A", result.Grade);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task SpeedTest_ProviderRejectsRequests_ReportsFailureNotZeroMbpsSuccess()
    {
        var service = new SpeedTestService(null, Client(HttpStatusCode.Forbidden));

        var result = await service.RunSpeedTestAsync();

        Assert.False(result.CompletedSuccessfully);
        Assert.Contains("403", result.ErrorMessage);
        Assert.Equal(0, result.DownloadSpeedMbps);
    }

    [Fact]
    public async Task Bufferbloat_ProviderRejectsRequests_ReturnsInvalidResultWithoutPingingForAGrade()
    {
        var service = new BufferbloatService(new PingService(), null, Client(HttpStatusCode.Forbidden));
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var result = await service.RunTestAsync("127.0.0.1");

        Assert.False(result.IsValid);
        Assert.Contains("403", result.ErrorMessage);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), "provider check must fail fast, before any measurement");
    }

    [Fact]
    public async Task CloudflareProvider_ReportsNetworkErrorsAsMessages()
    {
        var provider = new CloudflareTransferProvider();
        using var http = new HttpClient(new ThrowingHandler());

        var error = await provider.ValidateAsync(http, CancellationToken.None);

        Assert.NotNull(error);
        Assert.Contains("Could not reach", error);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("simulated network failure");
    }

    [Fact]
    public void ProviderResolution_FallsBackToDefaultAndCarriesDisclosure()
    {
        var provider = TransferProviders.Resolve("does-not-exist");

        Assert.Equal("cloudflare", provider.Id);
        Assert.Contains("Cloudflare", provider.Disclosure);
    }
}
