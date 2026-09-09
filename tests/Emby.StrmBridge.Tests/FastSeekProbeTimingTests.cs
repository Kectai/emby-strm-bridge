using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Emby.StrmBridge.Configuration;
using Emby.StrmBridge.Domain;
using Emby.StrmBridge.Playback;
using Emby.StrmBridge.Policy;

namespace Emby.StrmBridge.Tests;

[TestClass]
public sealed class FastSeekProbeTimingTests
{
    [TestMethod]
    public async Task ProbeTiming_IncludesDelayedResponseHeadersInSetupDuration()
    {
        await using var source = new TimedPartialSource(
            TimeSpan.FromMilliseconds(300), TimeSpan.Zero);
        using var transport = new GatewayTransport(new RedirectPolicy());
        var probe = new GatewayFastSeekProbeClient(transport);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var elapsed = Stopwatch.StartNew();

        var result = await probe.ReadAsync(source.Identity, TimedPartialSource.RangeStart,
            source.Body.Length, new PluginConfiguration(), deadline.Token);

        elapsed.Stop();
        await source.Completion;
        AssertResponse(source, result);
        Assert.IsTrue(result.SetupDuration >= TimeSpan.FromMilliseconds(280),
            "Setup timing must start before opening the response, not after its headers arrive.");
        AssertMeasuredDurations(result, elapsed.Elapsed);
        Assert.AreEqual(0, transport.ActiveRequests);
    }

    [TestMethod]
    public async Task ProbeTiming_IncludesPacedReadsInBodyDuration()
    {
        await using var source = new TimedPartialSource(
            TimeSpan.Zero, TimeSpan.FromMilliseconds(100));
        using var transport = new GatewayTransport(new RedirectPolicy());
        var probe = new GatewayFastSeekProbeClient(transport);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var elapsed = Stopwatch.StartNew();

        var result = await probe.ReadAsync(source.Identity, TimedPartialSource.RangeStart,
            source.Body.Length, new PluginConfiguration(), deadline.Token);

        elapsed.Stop();
        await source.Completion;
        AssertResponse(source, result);
        Assert.IsTrue(result.BodyDuration >= TimeSpan.FromMilliseconds(280),
            "Body timing must include waiting for successive response-body chunks.");
        AssertMeasuredDurations(result, elapsed.Elapsed);
        Assert.AreEqual(0, transport.ActiveRequests);
    }

    private static void AssertResponse(TimedPartialSource source, FastSeekProbeResult result)
    {
        Assert.AreEqual(TimedPartialSource.RangeStart, result.RangeStart);
        Assert.AreEqual(TimedPartialSource.TotalLength, result.TotalLength);
        CollectionAssert.AreEqual(source.Body, result.Bytes);
        Assert.IsNotNull(result.Representation);
        Assert.AreEqual("\"timing-fixture\"", result.Representation!.StrongETag);
        StringAssert.Contains(source.RequestHeaders,
            $"Range: bytes={TimedPartialSource.RangeStart}-{TimedPartialSource.RangeStart + source.Body.Length - 1}");
    }

    private static void AssertMeasuredDurations(FastSeekProbeResult result, TimeSpan elapsed)
    {
        Assert.IsTrue(result.SetupDuration.HasValue);
        Assert.IsTrue(result.BodyDuration.HasValue);
        Assert.IsTrue(result.SetupDuration.GetValueOrDefault() >= TimeSpan.Zero);
        Assert.IsTrue(result.BodyDuration.GetValueOrDefault() > TimeSpan.Zero);
        Assert.IsTrue(result.SetupDuration.GetValueOrDefault() + result.BodyDuration.GetValueOrDefault() <= elapsed,
            "Setup and body observations must be disjoint parts of this request's elapsed time.");
    }

    private sealed class TimedPartialSource : IAsyncDisposable
    {
        internal const long RangeStart = 188;
        internal const long TotalLength = 16 * 1024 * 1024;
        private const int ChunkCount = 4;
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource shutdown = new(TimeSpan.FromSeconds(10));

        internal TimedPartialSource(TimeSpan setupDelay, TimeSpan bodyChunkDelay)
        {
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Identity = new SourceIdentity(new string('a', 64), new string('b', 64),
                new Uri($"http://127.0.0.1:{port}/timing"), "/library/timing.strm", 1,
                DateTimeOffset.UtcNow);
            Completion = ServeAsync(setupDelay, bodyChunkDelay);
        }

        internal byte[] Body { get; } = Enumerable.Range(0, 512 * 1024)
            .Select(index => (byte)(index % 251)).ToArray();
        internal SourceIdentity Identity { get; }
        internal Task Completion { get; }
        internal string RequestHeaders { get; private set; } = string.Empty;

        private async Task ServeAsync(TimeSpan setupDelay, TimeSpan bodyChunkDelay)
        {
            using var connection = await listener.AcceptTcpClientAsync(shutdown.Token);
            connection.NoDelay = true;
            await using var stream = connection.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
            var headers = new StringBuilder();
            string? line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(shutdown.Token)))
                headers.AppendLine(line);
            RequestHeaders = headers.ToString();
            await Task.Delay(setupDelay, shutdown.Token);
            var response = Encoding.ASCII.GetBytes(
                "HTTP/1.1 206 Partial Content\r\n" +
                "Content-Type: application/octet-stream\r\n" +
                "ETag: \"timing-fixture\"\r\n" +
                $"Content-Range: bytes {RangeStart}-{RangeStart + Body.Length - 1}/{TotalLength}\r\n" +
                $"Content-Length: {Body.Length}\r\n" +
                "Connection: close\r\n\r\n");
            await stream.WriteAsync(response, shutdown.Token);
            var chunkLength = Body.Length / ChunkCount;
            for (var chunk = 0; chunk < ChunkCount; chunk++)
            {
                await Task.Delay(bodyChunkDelay, shutdown.Token);
                await stream.WriteAsync(Body.AsMemory(chunk * chunkLength, chunkLength), shutdown.Token);
            }
        }

        public async ValueTask DisposeAsync()
        {
            shutdown.Cancel();
            listener.Stop();
            try { await Completion; }
            catch (OperationCanceledException) { }
            catch (SocketException) when (shutdown.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (shutdown.IsCancellationRequested) { }
            shutdown.Dispose();
        }
    }
}
