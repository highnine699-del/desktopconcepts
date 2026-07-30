using DesktopConcepts.Infrastructure.AI;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;

namespace DesktopConcepts.Tests.Infrastructure;

/// <summary>
/// ModelDownloadService: progress events, completion, failure surface,
/// resume (range header sent), cancellation preserves partial file,
/// and IsModelPresent helper.
/// Uses a fake HttpMessageHandler — no real network.
/// </summary>
public sealed class ModelDownloadServiceTests : IDisposable
{
    private readonly string _dir  = Path.Combine(Path.GetTempPath(), $"dc_dl_{Guid.NewGuid():N}");
    private string DestPath => Path.Combine(_dir, "model.gguf");

    public ModelDownloadServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ModelDownloadService MakeService(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler);
        return new ModelDownloadService(client, NullLogger<ModelDownloadService>.Instance);
    }

    /// <summary>Returns a handler that serves <paramref name="body"/> bytes.</summary>
    private static HttpMessageHandler OkHandler(byte[] body, bool supportsRange = false)
        => new FakeHandler(body, supportsRange);

    private static HttpMessageHandler ErrorHandler(HttpStatusCode status)
        => new FakeHandler(status);

    // ── Tests: happy path ────────────────────────────────────────────────────

    [Fact]
    public async Task DownloadAsync_CompletesSuccessfully_FileExistsAfterwards()
    {
        var body    = new byte[1024];
        var service = MakeService(OkHandler(body));

        await service.DownloadAsync("http://fake/model", DestPath, CancellationToken.None);

        Assert.True(File.Exists(DestPath));
        Assert.Equal(body.Length, new FileInfo(DestPath).Length);
    }

    [Fact]
    public async Task DownloadAsync_RaisesDownloadCompleted()
    {
        var body      = new byte[512];
        var service   = MakeService(OkHandler(body));
        var completed = false;
        service.DownloadCompleted += () => completed = true;

        await service.DownloadAsync("http://fake/model", DestPath, CancellationToken.None);

        Assert.True(completed);
    }

    [Fact]
    public async Task DownloadAsync_RaisesProgressChanged_WithIncreasingValues()
    {
        // Use a larger body so multiple read chunks fire progress events
        var body     = new byte[256 * 1024]; // 256 KB
        var service  = MakeService(OkHandler(body));
        var progress = new List<double>();
        service.ProgressChanged += p => progress.Add(p);

        await service.DownloadAsync("http://fake/model", DestPath, CancellationToken.None);

        Assert.NotEmpty(progress);
        Assert.Equal(100.0, progress.Last());
        // Progress must be monotonically non-decreasing
        for (var i = 1; i < progress.Count; i++)
            Assert.True(progress[i] >= progress[i - 1],
                $"Progress went backwards at index {i}: {progress[i - 1]} → {progress[i]}");
    }

    // ── Tests: failure ───────────────────────────────────────────────────────

    [Fact]
    public async Task DownloadAsync_RaisesDownloadFailed_OnHttpError()
    {
        var service = MakeService(ErrorHandler(HttpStatusCode.ServiceUnavailable));
        string? failMsg = null;
        service.DownloadFailed += m => failMsg = m;

        // Must not throw — failure is surfaced via event
        var ex = await Record.ExceptionAsync(() =>
            service.DownloadAsync("http://fake/model", DestPath, CancellationToken.None));

        Assert.Null(ex);
        Assert.NotNull(failMsg);
        Assert.False(File.Exists(DestPath)); // partial file not renamed to final on failure
    }

    [Fact]
    public async Task DownloadAsync_RaisesDownloadFailed_OnNetworkException()
    {
        var service = MakeService(new ThrowingHandler());
        string? failMsg = null;
        service.DownloadFailed += m => failMsg = m;

        var ex = await Record.ExceptionAsync(() =>
            service.DownloadAsync("http://fake/model", DestPath, CancellationToken.None));

        Assert.Null(ex);
        Assert.NotNull(failMsg);
    }

    [Fact]
    public async Task DownloadAsync_DoesNotRaiseCompleted_OnFailure()
    {
        var service   = MakeService(ErrorHandler(HttpStatusCode.Forbidden));
        var completed = false;
        service.DownloadCompleted += () => completed = true;

        await service.DownloadAsync("http://fake/model", DestPath, CancellationToken.None);

        Assert.False(completed);
    }

    // ── Tests: cancellation ───────────────────────────────────────────────────

    [Fact]
    public async Task DownloadAsync_Cancellation_PreservesPartialFile()
    {
        var body    = new byte[512 * 1024]; // 512 KB — large enough to cancel mid-stream
        var cts     = new CancellationTokenSource();
        var service = MakeService(new SlowHandler(body, cts));

        await service.DownloadAsync("http://fake/model", DestPath, cts.Token);

        // Partial file (.partial) should be preserved for resume
        var partialPath = DestPath + ".partial";
        Assert.True(File.Exists(partialPath),
            "Partial file must be preserved on cancellation so a retry can resume.");
        Assert.False(File.Exists(DestPath),
            "Final file must NOT exist after cancellation.");
    }

    // ── Tests: resume ─────────────────────────────────────────────────────────

    [Fact]
    public async Task DownloadAsync_SendsRangeHeader_WhenPartialFileExists()
    {
        var partialPath = DestPath + ".partial";
        var existing    = new byte[100];
        Directory.CreateDirectory(_dir);
        await File.WriteAllBytesAsync(partialPath, existing);

        var handler = new CapturingHandler(new byte[200]);
        var service = MakeService(handler);

        await service.DownloadAsync("http://fake/model", DestPath, CancellationToken.None);

        Assert.NotNull(handler.CapturedRequest?.Headers.Range);
        Assert.Equal(100, handler.CapturedRequest!.Headers.Range!.Ranges.First().From);
    }

    // ── Tests: IsModelPresent ─────────────────────────────────────────────────

    [Fact]
    public void IsModelPresent_ReturnsFalse_WhenFileMissing()
    {
        Assert.False(ModelDownloadService.IsModelPresent(
            Path.Combine(_dir, "nonexistent.gguf")));
    }

    [Fact]
    public async Task IsModelPresent_ReturnsFalse_WhenFileEmpty()
    {
        await File.WriteAllBytesAsync(DestPath, Array.Empty<byte>());
        Assert.False(ModelDownloadService.IsModelPresent(DestPath));
    }

    [Fact]
    public async Task IsModelPresent_ReturnsTrue_WhenFileNonEmpty()
    {
        await File.WriteAllBytesAsync(DestPath, new byte[] { 1, 2, 3 });
        Assert.True(ModelDownloadService.IsModelPresent(DestPath));
    }

    // ── Fake HTTP handlers ────────────────────────────────────────────────────

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly byte[]?       _body;
        private readonly HttpStatusCode _status;
        private readonly bool           _supportsRange;

        public FakeHandler(byte[] body, bool supportsRange = false)
        { _body = body; _status = HttpStatusCode.OK; _supportsRange = supportsRange; }

        public FakeHandler(HttpStatusCode status)
        { _body = null; _status = status; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            if (_body is null)
                return Task.FromResult(new HttpResponseMessage(_status));

            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_body)
            };
            resp.Content.Headers.ContentLength = _body.Length;
            return Task.FromResult(resp);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("Simulated network failure.");
    }

    /// <summary>Cancels after the first chunk so we get a partial file.</summary>
    private sealed class SlowHandler : HttpMessageHandler
    {
        private readonly byte[] _body;
        private readonly CancellationTokenSource _cts;

        public SlowHandler(byte[] body, CancellationTokenSource cts)
        { _body = body; _cts = cts; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new CancellingContent(_body, _cts)
            };
            resp.Content.Headers.ContentLength = _body.Length;
            return Task.FromResult(resp);
        }
    }

    private sealed class CancellingContent : HttpContent
    {
        private readonly byte[] _data;
        private readonly CancellationTokenSource _cts;

        public CancellingContent(byte[] data, CancellationTokenSource cts)
        { _data = data; _cts = cts; }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            // Write first 4 KB then cancel
            var chunk = Math.Min(4096, _data.Length);
            await stream.WriteAsync(_data.AsMemory(0, chunk));
            await stream.FlushAsync();
            _cts.Cancel();
            // Let cancellation propagate naturally — don't throw here
            await Task.Delay(Timeout.Infinite, _cts.Token).ContinueWith(_ => { });
        }

        protected override bool TryComputeLength(out long length)
        { length = _data.Length; return true; }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly byte[] _body;
        public HttpRequestMessage? CapturedRequest { get; private set; }

        public CapturingHandler(byte[] body) => _body = body;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            CapturedRequest = request;
            var resp = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(_body)
            };
            resp.Content.Headers.ContentLength = _body.Length;
            return Task.FromResult(resp);
        }
    }
}
