using System.Net;
using System.Net.Sockets;
using System.Text;
using Emby.StrmBridge.Domain;
using Emby.StrmBridge.Playback;
using Emby.StrmBridge.Policy;

namespace Emby.StrmBridge.Tests;

[TestClass]
public sealed class RedirectHttpIntegrationTests
{
    [TestMethod]
    public async Task Client_UsesRangeDisablesAutoRedirectAndPreservesUserAgent()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var observed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = ServeOnceAsync(
            listener,
            observed,
            "HTTP/1.1 302 Found\r\n" +
            "Location: https://media.invalid/video?signature=temporary\r\n" +
            "Content-Length: 0\r\nConnection: close\r\n\r\n");
        using var resolver = CreateResolver();

        var lease = await resolver.ResolveAsync(CreateSource(port), "TestPlayer/1.0", CancellationToken.None);
        var headers = await observed.Task;
        await server;

        Assert.AreEqual("https://media.invalid/video?signature=temporary", lease.GetLocation());
        StringAssert.Contains(headers, "Range: bytes=0-0", StringComparison.OrdinalIgnoreCase);
        StringAssert.Contains(headers, "User-Agent: TestPlayer/1.0", StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task Client_ParsesRetryAfterFromTransientHttpResponse()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var observed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = ServeOnceAsync(
            listener,
            observed,
            "HTTP/1.1 429 Too Many Requests\r\nRetry-After: 7\r\n" +
            "Content-Length: 0\r\nConnection: close\r\n\r\n");
        using var resolver = CreateResolver();

        var failure = await Assert.ThrowsExactlyAsync<RedirectSourceUnavailableException>(() =>
            resolver.ResolveAsync(CreateSource(port), "TestPlayer/1.0", CancellationToken.None));
        await server;

        Assert.AreEqual(7, failure.RetryAfterSeconds);
    }

    [TestMethod]
    public async Task Client_ClassifiesPermanentHttpResponse()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var observed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = ServeOnceAsync(
            listener,
            observed,
            "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        using var resolver = CreateResolver();

        var failure = await Assert.ThrowsExactlyAsync<RedirectRejectedException>(() =>
            resolver.ResolveAsync(CreateSource(port), "TestPlayer/1.0", CancellationToken.None));
        await server;

        Assert.AreEqual(RedirectRejectionReason.PermanentFailure, failure.Reason);
    }

    [TestMethod]
    public async Task Client_RejectsUnsafeLocationFromRealHttpResponse()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var observed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = ServeOnceAsync(
            listener,
            observed,
            "HTTP/1.1 302 Found\r\nLocation: file:///synthetic/media\r\n" +
            "Content-Length: 0\r\nConnection: close\r\n\r\n");
        using var resolver = CreateResolver();

        var failure = await Assert.ThrowsExactlyAsync<RedirectRejectedException>(() =>
            resolver.ResolveAsync(CreateSource(port), "TestPlayer/1.0", CancellationToken.None));
        await server;

        Assert.AreEqual(RedirectRejectionReason.UnsafeLocation, failure.Reason);
    }

    [TestMethod]
    public async Task Client_ClassifiesDelayedResponseAsTransientFailure()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var observed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = ServeOnceAsync(
            listener,
            observed,
            "HTTP/1.1 302 Found\r\nLocation: https://media.invalid/video\r\n" +
            "Content-Length: 0\r\nConnection: close\r\n\r\n",
            TimeSpan.FromMilliseconds(200));
        using var resolver = CreateResolver(TimeSpan.FromMilliseconds(50));

        var failure = await Assert.ThrowsExactlyAsync<RedirectSourceUnavailableException>(() =>
            resolver.ResolveAsync(CreateSource(port), "TestPlayer/1.0", CancellationToken.None));
        await server;

        Assert.IsTrue(failure.RetryAfterSeconds >= 1);
    }

    [TestMethod]
    public async Task Client_UsesCurrentTimeoutValueForEveryRequest()
    {
        var requestTimeout = TimeSpan.FromMilliseconds(50);
        using var client = new HttpRedirectSourceClient(() => requestTimeout);

        using (var firstListener = new TcpListener(IPAddress.Loopback, 0))
        {
            firstListener.Start();
            var firstPort = ((IPEndPoint)firstListener.LocalEndpoint).Port;
            var firstObserved = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstServer = ServeOnceAsync(
                firstListener,
                firstObserved,
                "HTTP/1.1 302 Found\r\nLocation: https://media.invalid/video\r\n" +
                "Content-Length: 0\r\nConnection: close\r\n\r\n",
                TimeSpan.FromMilliseconds(200));

            await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => client.SendAsync(
                new Uri($"http://127.0.0.1:{firstPort}/redirect"),
                "TestPlayer/1.0",
                CancellationToken.None));
            await firstServer;
        }

        requestTimeout = TimeSpan.FromSeconds(1);
        using var secondListener = new TcpListener(IPAddress.Loopback, 0);
        secondListener.Start();
        var secondPort = ((IPEndPoint)secondListener.LocalEndpoint).Port;
        var secondObserved = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondServer = ServeOnceAsync(
            secondListener,
            secondObserved,
            "HTTP/1.1 302 Found\r\nLocation: https://media.invalid/video\r\n" +
            "Content-Length: 0\r\nConnection: close\r\n\r\n",
            TimeSpan.FromMilliseconds(200));

        var response = await client.SendAsync(
            new Uri($"http://127.0.0.1:{secondPort}/redirect"),
            "TestPlayer/1.0",
            CancellationToken.None);
        await secondServer;

        Assert.AreEqual(302, response.StatusCode);
    }

    private static RedirectResolver CreateResolver(TimeSpan? timeout = null) => new(
        new HttpRedirectSourceClient(timeout ?? TimeSpan.FromSeconds(5)),
        new RedirectPolicy(() => new[] { "media.invalid" }),
        new SystemClock());

    private static SourceIdentity CreateSource(int port) => new(
        new string('a', 64),
        new string('b', 64),
        new Uri($"http://127.0.0.1:{port}/redirect"),
        "/synthetic/item.strm",
        32,
        DateTimeOffset.UtcNow);

    private static async Task ServeOnceAsync(
        TcpListener listener,
        TaskCompletionSource<string> observed,
        string responseText,
        TimeSpan? responseDelay = null)
    {
        using var client = await listener.AcceptTcpClientAsync();
        using var stream = client.GetStream();
        var buffer = new byte[4096];
        var builder = new StringBuilder();
        while (!builder.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0) break;
            builder.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }
        observed.TrySetResult(builder.ToString());
        if (responseDelay.HasValue) await Task.Delay(responseDelay.Value);
        var response = Encoding.ASCII.GetBytes(responseText);
        try { await stream.WriteAsync(response); }
        catch (IOException) when (responseDelay.HasValue)
        {
            // The timed-out client is expected to close before the synthetic response is written.
        }
    }
}
