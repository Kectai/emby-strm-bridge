using System.Net;
using System.Net.Sockets;
using System.Text;
using Emby.StrmBridge.Configuration;
using Emby.StrmBridge.Domain;
using Emby.StrmBridge.Playback;
using Emby.StrmBridge.Policy;

namespace Emby.StrmBridge.Tests;

[TestClass]
public sealed class GatewayTransportTests
{
    [TestMethod]
    [DataRow("mp4", true)]
    [DataRow("MKV", true)]
    [DataRow("mpegts", true)]
    [DataRow("hls", false)]
    [DataRow("m3u8", false)]
    [DataRow("opaque", false)]
    [DataRow("", false)]
    public void KnownFileContainerHint_IsConservativeAndCaseInsensitive(string container, bool expected) =>
        Assert.AreEqual(expected, SourceBehaviorClassifier.IsKnownFileContainer(container));

    [TestMethod]
    public async Task Transport_ConnectionFailureBacksOffTheSourceWithoutConsumingCapacity()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var source = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/offline");
        listener.Stop();
        using var transport = new GatewayTransport(new RedirectPolicy(), new ManualClock());
        var headers = new Dictionary<string, string>();
        await Assert.ThrowsAsync<HttpRequestException>(() => transport.OpenAsync(
            source, "GET", null, headers, CreateOptions(), CancellationToken.None));
        var blocked = await Assert.ThrowsExactlyAsync<GatewaySourceBackoffException>(() => transport.OpenAsync(
            source, "GET", null, headers, CreateOptions(), CancellationToken.None));
        Assert.AreEqual(502, blocked.StatusCode);
        Assert.AreEqual(2, blocked.RetryAfterSeconds);
        Assert.AreEqual(1, transport.SourceBackoffCount);
        Assert.AreEqual(0, transport.ActiveRequests);
    }

    [TestMethod]
    public async Task Transport_RequestTimeoutResponseBacksOffAndHonorsRetryAfter()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var source = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/media");
        var observed = new List<string>();
        var server = ServeAsync(listener, observed, new[]
        {
            "HTTP/1.1 408 Request Timeout\r\nRetry-After: 4\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
        });
        using var transport = new GatewayTransport(new RedirectPolicy(), new ManualClock());
        using (var first = await transport.OpenAsync(source, "GET", null,
                   new Dictionary<string, string>(), CreateOptions(), CancellationToken.None))
            Assert.AreEqual(408, (int)first.Response.StatusCode);
        var blocked = await Assert.ThrowsExactlyAsync<GatewaySourceBackoffException>(() => transport.OpenAsync(
            source, "GET", null, new Dictionary<string, string>(), CreateOptions(), CancellationToken.None));
        await server;
        Assert.AreEqual(408, blocked.StatusCode);
        Assert.AreEqual(4, blocked.RetryAfterSeconds);
        Assert.HasCount(1, observed);
        Assert.AreEqual(0, transport.ActiveRequests);
    }

    [TestMethod]
    [DataRow("request")]
    [DataRow("generation")]
    [DataRow("budget")]
    public async Task Transport_CancellationAndConfigurationClearDoNotBackoff(string cancellationKind)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var cancellation = new CancellationTokenSource();
        using var headerBudget = new CancellationTokenSource();
        using var transport = new GatewayTransport(new RedirectPolicy(), new ManualClock());
        var source = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/waiting");
        var request = transport.OpenAsync(source, null, null, "GET", null,
            new Dictionary<string, string>(), CreateOptions(), cancellation.Token, headerBudget.Token);
        using var client = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
        if (cancellationKind == "generation") transport.Clear();
        else if (cancellationKind == "budget") headerBudget.Cancel();
        else cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await request);
        Assert.AreEqual(0, transport.SourceBackoffCount);
        Assert.AreEqual(0, transport.ActiveRequests);
    }

    [TestMethod]
    public async Task Transport_HeaderBudgetDoesNotCancelHandedOffBody()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var observed = new List<string>();
        var server = ServeAsync(listener, observed, new[]
        {
            "HTTP/1.1 200 OK\r\nContent-Length: 1\r\nConnection: close\r\n\r\nx",
        });
        using var headerBudget = new CancellationTokenSource();
        using var transport = new GatewayTransport(new RedirectPolicy(), new ManualClock());
        using var lease = await transport.OpenAsync(
            new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/media"),
            null, null, "GET", null, new Dictionary<string, string>(), CreateOptions(),
            CancellationToken.None, headerBudget.Token);
        using var stream = await lease.OpenOwnedStreamAsync();
        headerBudget.Cancel();
        using var reader = new StreamReader(stream);
        Assert.AreEqual("x", await reader.ReadToEndAsync());
        await server;
    }

    [TestMethod]
    public async Task Transport_SourceCandidateSeparatesHlsResources()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var observed = new List<string>();
        var server = ServeAsync(listener, observed, new[]
        {
            "HTTP/1.1 302 Found\r\nLocation: /edge-a\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
            PartialContent("bytes 0-0/10", "A"),
            "HTTP/1.1 302 Found\r\nLocation: /edge-b\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
            PartialContent("bytes 0-0/10", "B"),
        });
        using var transport = new GatewayTransport(new RedirectPolicy());
        var headers = new Dictionary<string, string> { ["Range"] = "bytes=0-0" };
        using (var first = await transport.OpenAsync(new Uri($"http://127.0.0.1:{port}/segment-a"),
                   "ticket-a", "same-root", "GET", "Agent", headers, CreateOptions(), CancellationToken.None))
            Assert.AreEqual("A", await first.Response.Content.ReadAsStringAsync());
        using (var second = await transport.OpenAsync(new Uri($"http://127.0.0.1:{port}/segment-b"),
                   "ticket-b", "same-root", "GET", "Agent", headers, CreateOptions(), CancellationToken.None))
        {
            Assert.IsFalse(second.UsedCachedRedirect);
            Assert.AreEqual("B", await second.Response.Content.ReadAsStringAsync());
        }
        await server.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(observed[2].StartsWith("GET /segment-b ", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Transport_PreservesSignedRedirectQueryOctets()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var observed = new List<string>();
        const string target = "/media/file.mkv?sig=a+b%2fC%2F&dup=1&dup=2&empty=&bare&&tail=";
        var server = ServeAsync(listener, observed, new[]
        {
            "HTTP/1.1 302 Found\r\nLocation: " + target +
            "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
            "HTTP/1.1 200 OK\r\nContent-Length: 1\r\nConnection: close\r\n\r\nx",
        });
        using var transport = new GatewayTransport(new RedirectPolicy());

        using var lease = await transport.OpenAsync(
            new Uri($"http://127.0.0.1:{port}/source"),
            "GET",
            "GenericClient/1.0",
            new Dictionary<string, string>(),
            CreateOptions(),
            CancellationToken.None);
        await server;

        Assert.AreEqual(target, RequestPath(observed[1]));
        Assert.AreEqual(target, lease.EffectiveUri.PathAndQuery);
    }

    [TestMethod]
    [DataRow("bytes 1-1/20", "one")]
    [DataRow("bytes 1-1/10", "two")]
    public async Task Transport_RejectsChangedRepresentation(string range, string etag)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var observed = new List<string>();
        var server = ServeAsync(listener, observed, new[]
        {
            "HTTP/1.1 302 Found\r\nLocation: /edge\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
            PartialContent("bytes 0-0/10", "A").Replace("Connection:", "ETag: \"one\"\r\nConnection:"),
            PartialContent(range, "B").Replace("Connection:", "ETag: \"" + etag + "\"\r\nConnection:"),
        });
        using var transport = new GatewayTransport(new RedirectPolicy());
        var source = new Uri($"http://127.0.0.1:{port}/start");
        Uri? invalidated = null;
        transport.RepresentationChanged = value => invalidated = value;
        var headers = new Dictionary<string, string> { ["Range"] = "bytes=0-0" };
        using (await transport.OpenAsync(source, "ticket", "GET", null, headers, CreateOptions(), CancellationToken.None)) { }
        headers["Range"] = "bytes=1-1";
        await Assert.ThrowsExactlyAsync<GatewayRepresentationChangedException>(() =>
            transport.OpenAsync(source, "ticket", "GET", null, headers, CreateOptions(), CancellationToken.None));
        await server.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(source, invalidated);
        Assert.AreEqual(0, transport.RedirectLeaseCount);
        Assert.AreEqual(0, transport.ActiveRequests);
    }

    [TestMethod]
    public async Task Transport_ClearClosesHandedOffBodyAndReleasesCapacity()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = ServeHeadersThenWaitAsync(listener, release);
        using var transport = new GatewayTransport(new RedirectPolicy());
        using var lease = await transport.OpenAsync(new Uri($"http://127.0.0.1:{port}/body"),
            "GET", null, new Dictionary<string, string>(), CreateOptions(), CancellationToken.None);
        using var stream = await lease.OpenOwnedStreamAsync();
        transport.Clear();
        Assert.AreEqual(0, transport.ActiveRequests);
        release.TrySetResult(true);
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task Transport_FollowsRelativeRedirectAndPreservesRangeAndUserAgent()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var observed = new List<string>();
        var server = ServeAsync(listener, observed, new[]
        {
            "HTTP/1.1 302 Found\r\nLocation: /media/file.mkv\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
            "HTTP/1.1 206 Partial Content\r\nContent-Type: video/x-matroska\r\n" +
            "Content-Range: bytes 0-0/10\r\nContent-Length: 1\r\nConnection: close\r\n\r\nx",
        });
        using var transport = new GatewayTransport(new RedirectPolicy());
        var options = new PluginConfiguration
        {
            GatewayTimeoutSeconds = 10,
            RedirectHopLimit = 5,
            RelayConcurrency = 2,
        };

        using var lease = await transport.OpenAsync(
            new Uri($"http://127.0.0.1:{port}/start"),
            "GET",
            "GenericClient/1.0",
            new Dictionary<string, string> { ["Range"] = "bytes=0-0" },
            options,
            CancellationToken.None);
        await server;

        Assert.AreEqual(1, lease.RedirectCount);
        Assert.AreEqual("/media/file.mkv", lease.EffectiveUri.AbsolutePath);
        Assert.AreEqual(206, (int)lease.Response.StatusCode);
        Assert.AreEqual(SourceTransportBehavior.FileBody,
            SourceBehaviorClassifier.Classify(lease.Response, lease.EffectiveUri));
        Assert.AreEqual(2, observed.Count);
        Assert.IsTrue(observed.All(request => request.Contains(
            "Range: bytes=0-0", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(observed.All(request => request.Contains(
            "User-Agent: GenericClient/1.0", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task Transport_EnforcesConfiguredConcurrencyUntilLeaseDisposal()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var observed = new List<string>();
        var server = ServeAsync(listener, observed, new[]
        {
            "HTTP/1.1 200 OK\r\nContent-Length: 1\r\nConnection: close\r\n\r\nx",
        });
        using var transport = new GatewayTransport(new RedirectPolicy());
        var options = new PluginConfiguration
        {
            GatewayTimeoutSeconds = 10,
            RedirectHopLimit = 5,
            RelayConcurrency = 1,
        };
        using var first = await transport.OpenAsync(
            new Uri($"http://127.0.0.1:{port}/media"),
            "GET",
            null,
            new Dictionary<string, string>(),
            options,
            CancellationToken.None);
        await server;
        StringAssert.Contains(observed.Single(), "User-Agent: Emby.StrmBridge/", StringComparison.OrdinalIgnoreCase);

        await Assert.ThrowsExactlyAsync<GatewayCapacityException>(() => transport.OpenAsync(
            new Uri($"http://127.0.0.1:{port}/second"),
            "GET",
            null,
            new Dictionary<string, string>(),
            options,
            CancellationToken.None));
        Assert.AreEqual(1, transport.ActiveRequests);
    }

    [TestMethod]
    public async Task Transport_ReservesOneConfiguredSlotForPlaybackWhileAProbeIsActive()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var observed = new List<string>();
        var server = ServeAsync(listener, observed, new[]
        {
            "HTTP/1.1 200 OK\r\nContent-Length: 1\r\nConnection: close\r\n\r\nx",
            "HTTP/1.1 200 OK\r\nContent-Length: 1\r\nConnection: close\r\n\r\ny",
        });
        using var transport = new GatewayTransport(new RedirectPolicy());
        var options = new PluginConfiguration
        {
            GatewayTimeoutSeconds = 10,
            RedirectHopLimit = 5,
            RelayConcurrency = 2,
        };

        using var probe = await transport.OpenProbeAsync(
            new Uri($"http://127.0.0.1:{port}/probe"),
            null,
            null,
            "GET",
            null,
            new Dictionary<string, string>(),
            options,
            CancellationToken.None);
        await Assert.ThrowsExactlyAsync<GatewayCapacityException>(() => transport.OpenProbeAsync(
            new Uri($"http://127.0.0.1:{port}/second-probe"),
            null,
            null,
            "GET",
            null,
            new Dictionary<string, string>(),
            options,
            CancellationToken.None));
        using var playback = await transport.OpenAsync(
            new Uri($"http://127.0.0.1:{port}/playback"),
            "GET",
            null,
            new Dictionary<string, string>(),
            options,
            CancellationToken.None);

        await server;
        Assert.HasCount(2, observed);
        Assert.AreEqual(2, transport.ActiveRequests);
    }

    [TestMethod]
    public async Task Transport_ReusesRedirectLeaseAcrossRangeRequests()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var observed = new List<string>();
        var server = ServeAsync(listener, observed, new[]
        {
            "HTTP/1.1 302 Found\r\nLocation: /media/file.mkv\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
            PartialContent("bytes 0-0/10", "x"),
            PartialContent("bytes 1-1/10", "y"),
        });
        var clock = new ManualClock();
        using var transport = new GatewayTransport(new RedirectPolicy(), clock);
        var options = CreateOptions();
        var source = new Uri($"http://127.0.0.1:{port}/start");

        using (var first = await transport.OpenAsync(
                   source,
                   "ticket-one",
                   "GET",
                   "GenericClient/1.0",
                   new Dictionary<string, string> { ["Range"] = "bytes=0-0" },
                   options,
                   CancellationToken.None))
        {
            Assert.IsFalse(first.UsedCachedRedirect);
            Assert.AreEqual(206, (int)first.Response.StatusCode);
        }

        using (var second = await transport.OpenAsync(
                   source,
                   "ticket-one",
                   "GET",
                   "GenericClient/1.0",
                   new Dictionary<string, string> { ["Range"] = "bytes=1-1" },
                   options,
                   CancellationToken.None))
        {
            Assert.IsTrue(second.UsedCachedRedirect);
            Assert.AreEqual(206, (int)second.Response.StatusCode);
        }
        await server;

        CollectionAssert.AreEqual(
            new[] { "/start", "/media/file.mkv", "/media/file.mkv" },
            observed.Select(RequestPath).ToArray());
        Assert.AreEqual(1, transport.RedirectLeaseCount);
    }

    [TestMethod]
    public async Task Transport_RemembersRedirectedFileBehaviorWithoutCachingTheSignedTarget()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var observed = new List<string>();
        var server = ServeAsync(listener, observed, new[]
        {
            "HTTP/1.1 302 Found\r\nLocation: /media/file.m2ts\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
            PartialContent("bytes 0-0/10", "x"),
            PartialContent("bytes 1-1/10", "y"),
        });
        var clock = new ManualClock();
        using var transport = new GatewayTransport(new RedirectPolicy(), clock);
        var source = new Uri($"http://127.0.0.1:{port}/start");
        const string scope = "direct-context";
        const string userAgent = "GenericClient/1.0";
        var headers = new Dictionary<string, string> { ["Range"] = "bytes=0-0" };

        using (var first = await transport.OpenAsync(
                   source, scope, "GET", userAgent, headers, CreateOptions(), CancellationToken.None))
            Assert.IsFalse(first.UsedCachedRedirect);

        headers["Range"] = "bytes=1-1";
        using (var confirmed = await transport.OpenAsync(
                   source, scope, "GET", userAgent, headers, CreateOptions(), CancellationToken.None))
        {
            Assert.IsTrue(confirmed.UsedCachedRedirect);
            transport.RememberDirectRedirect(
                source, scope, "GET", userAgent, headers, confirmed, SourceTransportBehavior.FileBody);
        }
        await server;

        headers["Range"] = "bytes=8-9";
        Assert.IsTrue(transport.TryGetDirectRoute(
            source, scope, "GET", userAgent, headers, out var decision));
        Assert.IsTrue(decision.ResolveSourceRedirect);
        Assert.IsNull(decision.EffectiveUri);
        Assert.AreEqual(SourceTransportBehavior.FileBody, decision.Behavior);
        Assert.AreEqual(1, transport.DirectRouteCount);
        Assert.AreEqual(0, transport.RedirectLeaseCount);
        CollectionAssert.AreEqual(
            new[] { "/start", "/media/file.m2ts", "/media/file.m2ts" },
            observed.Select(RequestPath).ToArray());
    }

    [TestMethod]
    public async Task Transport_SourceRedirectHandoffStopsBeforeTheTarget()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var observed = new List<string>();
        var server = ServeAsync(listener, observed, new[]
        {
            "HTTP/1.1 307 Temporary Redirect\r\nLocation: /signed/file.m2ts\r\n" +
            "Content-Length: 0\r\nConnection: close\r\n\r\n",
        });
        using var transport = new GatewayTransport(new RedirectPolicy(), new ManualClock());
        var headers = new Dictionary<string, string> { ["Range"] = "bytes=100-199" };

        using (var handoff = await transport.OpenSourceRedirectHandoffAsync(
                   new Uri($"http://127.0.0.1:{port}/start"), "GET", "GenericClient/1.0",
                   headers, CreateOptions(), CancellationToken.None))
        {
            Assert.IsTrue(handoff.IsRedirectHandoff);
            Assert.AreEqual(307, (int)handoff.Response.StatusCode);
            Assert.AreEqual("/signed/file.m2ts", handoff.EffectiveUri.AbsolutePath);
        }
        await server;

        CollectionAssert.AreEqual(new[] { "/start" }, observed.Select(RequestPath).ToArray());
        Assert.AreEqual(0, transport.ActiveRequests);
        Assert.AreEqual(0, transport.RedirectLeaseCount);
    }

    [TestMethod]
    public async Task Transport_ConfiguredSourceRedirectCacheStoresOnlyTheValidatedTarget()
    {
        var clock = new ManualClock();
        using var transport = new GatewayTransport(new RedirectPolicy(), clock);
        var source = new Uri("https://source.example/media");
        var target = new Uri("https://source.example/signed/media.m2ts");
        var headers = new Dictionary<string, string> { ["Range"] = "bytes=0-9" };
        using var gate = await transport.AcquireDirectRouteGateAsync(
            "direct-context", "GET", "GenericClient/1.0", headers, CancellationToken.None);

        transport.RememberDirectSourceRedirect(
            source,
            "direct-context",
            "GET",
            "GenericClient/1.0",
            headers,
            target,
            SourceTransportBehavior.FileBody,
            TimeSpan.FromSeconds(20),
            gate!.Generation);

        headers["Range"] = "bytes=1048576-1048585";
        Assert.IsTrue(transport.TryGetDirectRoute(
            source, "direct-context", "GET", "GenericClient/1.0", headers, out var decision));
        Assert.IsFalse(decision.ResolveSourceRedirect);
        Assert.AreEqual(target, decision.EffectiveUri);
        Assert.AreEqual(clock.UtcNow.AddSeconds(20), decision.ExpiresAtUtc);
        Assert.AreEqual(0, transport.RedirectLeaseCount);

        clock.Advance(TimeSpan.FromSeconds(21));
        Assert.IsFalse(transport.TryGetDirectRoute(
            source, "direct-context", "GET", "GenericClient/1.0", headers, out _));
    }

    [TestMethod]
    public async Task Transport_DisposingSourceRedirectHandoffAbortsAnUnreadResponseBody()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var connectionClosed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            await ReadRequestAsync(stream);
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 302 Found\r\nLocation: /signed/file.m2ts\r\n" +
                "Content-Length: 1048576\r\n\r\n"));
            var buffer = new byte[1];
            try
            {
                connectionClosed.TrySetResult(await stream.ReadAsync(buffer) == 0);
            }
            catch (IOException)
            {
                connectionClosed.TrySetResult(true);
            }
        });
        using var transport = new GatewayTransport(new RedirectPolicy(), new ManualClock());

        using (await transport.OpenSourceRedirectHandoffAsync(
                   new Uri($"http://127.0.0.1:{port}/start"), "GET", "GenericClient/1.0",
                   new Dictionary<string, string>(), CreateOptions(), CancellationToken.None))
        {
        }

        Assert.IsTrue(await connectionClosed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await server.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(0, transport.ActiveRequests);
    }

    [TestMethod]
    public async Task Transport_DirectRouteGateSerializesTheSameContextWithoutConsumingTransportCapacity()
    {
        using var transport = new GatewayTransport(new RedirectPolicy(), new ManualClock());
        var headers = new Dictionary<string, string> { ["Range"] = "bytes=0-" };
        using var first = await transport.AcquireDirectRouteGateAsync(
            "direct-context", "GET", "GenericClient/1.0", headers, CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            transport.AcquireDirectRouteGateAsync(
                "direct-context", "GET", "GenericClient/1.0", headers, timeout.Token));

        Assert.AreEqual(0, transport.ActiveRequests);
        Assert.AreEqual(1, transport.DirectRouteGateCount);
        first!.Dispose();
        Assert.AreEqual(0, transport.DirectRouteGateCount);
    }

    [TestMethod]
    public async Task Transport_ClearInvalidatesAQueuedDirectRouteGateWaiter()
    {
        using var transport = new GatewayTransport(new RedirectPolicy(), new ManualClock());
        var headers = new Dictionary<string, string>();
        var first = await transport.AcquireDirectRouteGateAsync(
            "direct-context", "GET", "GenericClient/1.0", headers, CancellationToken.None);
        var queued = transport.AcquireDirectRouteGateAsync(
            "direct-context", "GET", "GenericClient/1.0", headers, CancellationToken.None);
        Assert.IsTrue(SpinWait.SpinUntil(
            () => transport.DirectRouteGateReferenceCount == 2,
            TimeSpan.FromSeconds(2)));

        transport.Clear();
        first!.Dispose();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => queued);
        Assert.AreEqual(0, transport.DirectRouteGateCount);
    }

    [TestMethod]
    public async Task Transport_DirectOpenRejectsAGateLeaseFromBeforeClear()
    {
        using var transport = new GatewayTransport(new RedirectPolicy(), new ManualClock());
        var headers = new Dictionary<string, string>();
        using var gate = await transport.AcquireDirectRouteGateAsync(
            "direct-context", "GET", "GenericClient/1.0", headers, CancellationToken.None);

        transport.Clear();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => transport.OpenDirectAsync(
            new Uri("https://source.invalid/media"),
            "direct-context",
            gate!.Generation,
            "GET",
            "GenericClient/1.0",
            headers,
            CreateOptions(),
            CancellationToken.None));
        Assert.AreEqual(0, transport.ActiveRequests);
        Assert.AreEqual(0, transport.RedirectLeaseCount);
        Assert.AreEqual(0, transport.DirectRouteCount);
    }

    [TestMethod]
    public async Task Transport_DisposeInvalidatesAQueuedDirectRouteGateWaiter()
    {
        var transport = new GatewayTransport(new RedirectPolicy(), new ManualClock());
        var headers = new Dictionary<string, string>();
        var first = await transport.AcquireDirectRouteGateAsync(
            "direct-context", "GET", "GenericClient/1.0", headers, CancellationToken.None);
        var queued = transport.AcquireDirectRouteGateAsync(
            "direct-context", "GET", "GenericClient/1.0", headers, CancellationToken.None);
        Assert.IsTrue(SpinWait.SpinUntil(
            () => transport.DirectRouteGateReferenceCount == 2,
            TimeSpan.FromSeconds(2)));

        transport.Dispose();
        first!.Dispose();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => queued);
        Assert.AreEqual(0, transport.DirectRouteGateCount);
    }

    [TestMethod]
    public async Task Transport_BoundsRememberedDirectRoutes()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = ServeAsync(listener, new List<string>(), new[]
        {
            "HTTP/1.1 200 OK\r\nContent-Length: 1\r\nConnection: close\r\n\r\nx",
        });
        using var transport = new GatewayTransport(new RedirectPolicy(), new ManualClock());
        var source = new Uri($"http://127.0.0.1:{port}/media");
        var headers = new Dictionary<string, string>();
        using var lease = await transport.OpenAsync(
            source, "GET", "GenericClient/1.0", headers, CreateOptions(), CancellationToken.None);
        await server;

        for (var index = 0; index <= GatewayTransport.MaximumRedirectLeases; index++)
            transport.RememberDirectRedirect(
                source,
                "direct-context-" + index,
                "GET",
                "GenericClient/1.0",
                headers,
                lease,
                SourceTransportBehavior.FileBody);

        Assert.AreEqual(GatewayTransport.MaximumRedirectLeases, transport.DirectRouteCount);
        Assert.IsFalse(transport.TryGetDirectRoute(
            source, "direct-context-0", "GET", "GenericClient/1.0", headers, out _));
        Assert.IsTrue(transport.TryGetDirectRoute(
            source,
            "direct-context-" + GatewayTransport.MaximumRedirectLeases,
            "GET",
            "GenericClient/1.0",
            headers,
            out _));
    }

    [TestMethod]
    public async Task Transport_ReResolvesWhenATicketLeaseReturnsTheWrongRange()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var observed = new List<string>();
        var server = ServeAsync(listener, observed, new[]
        {
            "HTTP/1.1 302 Found\r\nLocation: /media/old.mkv\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
            PartialContent("bytes 0-0/10", "x"),
            PartialContent("bytes 0-0/10", "x"),
            "HTTP/1.1 302 Found\r\nLocation: /media/new.mkv\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
            PartialContent("bytes 1-1/10", "y"),
        });
        using var transport = new GatewayTransport(new RedirectPolicy(), new ManualClock());
        var options = CreateOptions();
        var source = new Uri($"http://127.0.0.1:{port}/start");

        using (await transport.OpenAsync(
                   source, "ticket-one", "GET", "MediaTransport/1.0",
                   new Dictionary<string, string> { ["Range"] = "bytes=0-0" },
                   options, CancellationToken.None))
        {
        }
        using (var second = await transport.OpenAsync(
                   source, "ticket-one", "GET", "MediaTransport/1.0",
                   new Dictionary<string, string> { ["Range"] = "bytes=1-1" },
                   options, CancellationToken.None))
        {
            Assert.IsFalse(second.UsedCachedRedirect);
            Assert.IsTrue(second.RetriedRejectedRedirect);
            Assert.AreEqual("/media/new.mkv", second.EffectiveUri.AbsolutePath);
            Assert.IsFalse(TransportPlanner.RequiresAdaptiveRelay(
                (int)second.Response.StatusCode));
        }
        await server;

        CollectionAssert.AreEqual(
            new[] { "/start", "/media/old.mkv", "/media/old.mkv", "/start", "/media/new.mkv" },
            observed.Select(RequestPath).ToArray());
    }

    [TestMethod]
    public async Task Transport_DirectRouteStateCannotCrossPlaybackScopes()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = ServeAsync(listener, new List<string>(), new[]
        {
            "HTTP/1.1 200 OK\r\nContent-Length: 1\r\nConnection: close\r\n\r\nx",
        });
        using var transport = new GatewayTransport(new RedirectPolicy(), new ManualClock());
        var source = new Uri($"http://127.0.0.1:{port}/media");
        var headers = new Dictionary<string, string>();

        using (var lease = await transport.OpenAsync(
                   source, "playback-one", "GET", "GenericClient/1.0", headers,
                   CreateOptions(), CancellationToken.None))
            transport.RememberDirectRedirect(
                source,
                "playback-one",
                "GET",
                "GenericClient/1.0",
                headers,
                lease,
                SourceTransportBehavior.FileBody);
        await server;

        Assert.IsTrue(transport.TryGetDirectRoute(
            source, "playback-one", "GET", "GenericClient/1.0", headers, out _));
        Assert.IsFalse(transport.TryGetDirectRoute(
            source, "playback-two", "GET", "GenericClient/1.0", headers, out _));
    }

    [TestMethod]
    public async Task Transport_DoesNotReuseASourceCandidateAcrossUserAgents()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var observed = new List<string>();
        var server = ServeAsync(listener, observed, new[]
        {
            "HTTP/1.1 302 Found\r\nLocation: /media/probe.mkv\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
            PartialContent("bytes 0-0/10", "x"),
            "HTTP/1.1 302 Found\r\nLocation: /media/playback.mkv\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
            PartialContent("bytes 1-1/10", "y"),
        });
        using var transport = new GatewayTransport(new RedirectPolicy(), new ManualClock());
        var source = new Uri($"http://127.0.0.1:{port}/start");
        var options = CreateOptions();

        using (var probe = await transport.OpenAsync(
                   source,
                   "probe-scope",
                   "source-scope",
                   "GET",
                   "ProbeAgent/1.0",
                   new Dictionary<string, string>
                   {
                       ["Range"] = "bytes=0-0",
                       ["Accept"] = "*/*",
                   },
                   options,
                   CancellationToken.None))
            Assert.IsFalse(probe.UsedCachedRedirect);

        using (var playback = await transport.OpenAsync(
                   source,
                   "ticket-scope",
                   "source-scope",
                   "GET",
                   "MediaTransport/2.0",
                   new Dictionary<string, string>
                   {
                       ["Range"] = "bytes=1-1",
                       ["Accept"] = "*/*",
                   },
                   options,
                   CancellationToken.None))
            Assert.IsFalse(playback.UsedCachedRedirect);
        await server;

        CollectionAssert.AreEqual(
            new[] { "/start", "/media/probe.mkv", "/start", "/media/playback.mkv" },
            observed.Select(RequestPath).ToArray());
        Assert.IsTrue(observed[0].Contains("User-Agent: ProbeAgent/1.0", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(observed[1].Contains("User-Agent: ProbeAgent/1.0", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(observed[2].Contains("User-Agent: MediaTransport/2.0", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(observed[3].Contains("User-Agent: MediaTransport/2.0", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task Transport_ReResolvesWhenASourceCandidateDoesNotHonorTheRequestedRange()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var observed = new List<string>();
        var server = ServeAsync(listener, observed, new[]
        {
            "HTTP/1.1 302 Found\r\nLocation: /media/old.mkv\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
            PartialContent("bytes 0-0/10", "x"),
            PartialContent("bytes 1-9/10", "123456789"),
            "HTTP/1.1 302 Found\r\nLocation: /media/new.mkv\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
            PartialContent("bytes 1-1/10", "y"),
        });
        using var transport = new GatewayTransport(new RedirectPolicy(), new ManualClock());
        var source = new Uri($"http://127.0.0.1:{port}/start");
        var options = CreateOptions();
        var commonHeaders = new Dictionary<string, string>
        {
            ["Range"] = "bytes=0-0",
            ["Accept"] = "*/*",
        };

        using (await transport.OpenAsync(
                   source, "probe-scope", "source-scope", "GET", "ProbeAgent/1.0",
                   commonHeaders, options, CancellationToken.None))
        {
        }
        commonHeaders["Range"] = "bytes=1-1";
        using (var playback = await transport.OpenAsync(
                   source, "ticket-scope", "source-scope", "GET", "ProbeAgent/1.0",
                   commonHeaders, options, CancellationToken.None))
        {
            Assert.IsFalse(playback.UsedCachedRedirect);
            Assert.AreEqual("/media/new.mkv", playback.EffectiveUri.AbsolutePath);
        }
        await server;

        CollectionAssert.AreEqual(
            new[] { "/start", "/media/old.mkv", "/media/old.mkv", "/start", "/media/new.mkv" },
            observed.Select(RequestPath).ToArray());
        Assert.AreEqual(0, transport.ActiveRequests);
    }

    [TestMethod]
    public async Task Transport_MergesConcurrentFirstRedirectResolution()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var observed = new List<string>();
        var server = ServeAsync(listener, observed, new[]
        {
            "HTTP/1.1 302 Found\r\nLocation: /media/file.mkv\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
            PartialContent("bytes 0-0/10", "x"),
            PartialContent("bytes 1-1/10", "y"),
        });
        using var transport = new GatewayTransport(new RedirectPolicy(), new ManualClock());
        var source = new Uri($"http://127.0.0.1:{port}/start");
        var options = CreateOptions();

        var requests = new[]
        {
            transport.OpenAsync(
                source, "ticket-one", "GET", null,
                new Dictionary<string, string> { ["Range"] = "bytes=0-0" },
                options, CancellationToken.None),
            transport.OpenAsync(
                source, "ticket-one", "GET", null,
                new Dictionary<string, string> { ["Range"] = "bytes=1-1" },
                options, CancellationToken.None),
        };
        var leases = await Task.WhenAll(requests);
        foreach (var lease in leases) lease.Dispose();
        await server;

        Assert.AreEqual(1, observed.Count(request => RequestPath(request) == "/start"));
        Assert.AreEqual(2, observed.Count(request => RequestPath(request) == "/media/file.mkv"));
    }

    [TestMethod]
    public async Task Transport_MergesConcurrentSourceCandidateResolutionAcrossTickets()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var observed = new List<string>();
        var firstRequestSeen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRedirect = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = ServeControlledCandidateAsync(listener, observed, firstRequestSeen, releaseRedirect);
        using var transport = new GatewayTransport(new RedirectPolicy(), new ManualClock());
        var source = new Uri($"http://127.0.0.1:{port}/start");
        var options = CreateOptions();

        var first = transport.OpenAsync(
            source,
            "ticket-one",
            "source-scope",
            "GET",
            null,
            new Dictionary<string, string> { ["Range"] = "bytes=0-0" },
            options,
            CancellationToken.None);
        await firstRequestSeen.Task;
        var second = transport.OpenAsync(
            source,
            "ticket-two",
            "source-scope",
            "GET",
            null,
            new Dictionary<string, string> { ["Range"] = "bytes=1-1" },
            options,
            CancellationToken.None);
        releaseRedirect.SetResult(true);

        var leases = await Task.WhenAll(first, second);
        foreach (var lease in leases) lease.Dispose();
        await server;

        Assert.AreEqual(1, observed.Count(request => RequestPath(request) == "/start"));
        Assert.AreEqual(2, observed.Count(request => RequestPath(request) == "/media/file.mkv"));
        Assert.AreEqual(0, transport.ActiveRequests);
    }

    [TestMethod]
    public async Task Transport_SeparatesHeadAndGetRedirectLeases()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var observed = new List<string>();
        var server = ServeAsync(listener, observed, new[]
        {
            "HTTP/1.1 302 Found\r\nLocation: /media/file.mkv\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
            "HTTP/1.1 200 OK\r\nContent-Length: 10\r\nConnection: close\r\n\r\n",
            "HTTP/1.1 302 Found\r\nLocation: /media/file.mkv\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
            PartialContent("bytes 0-0/10", "x"),
        });
        using var transport = new GatewayTransport(new RedirectPolicy(), new ManualClock());
        var source = new Uri($"http://127.0.0.1:{port}/start");

        using (await transport.OpenAsync(
                   source, "ticket-one", "HEAD", null,
                   new Dictionary<string, string>(), CreateOptions(), CancellationToken.None))
        {
        }
        using (await transport.OpenAsync(
                   source, "ticket-one", "GET", null,
                   new Dictionary<string, string> { ["Range"] = "bytes=0-0" },
                   CreateOptions(), CancellationToken.None))
        {
        }
        await server;

        CollectionAssert.AreEqual(
            new[] { "HEAD /start", "HEAD /media/file.mkv", "GET /start", "GET /media/file.mkv" },
            observed.Select(RequestMethodAndPath).ToArray());
    }

    [TestMethod]
    public async Task Transport_ResolvesRedirectAgainAfterLeaseExpiry()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var observed = new List<string>();
        var server = ServeAsync(listener, observed, new[]
        {
            "HTTP/1.1 302 Found\r\nLocation: /media/file.mkv\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
            PartialContent("bytes 0-0/10", "x"),
            "HTTP/1.1 302 Found\r\nLocation: /media/file.mkv\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
            PartialContent("bytes 1-1/10", "y"),
        });
        var clock = new ManualClock();
        using var transport = new GatewayTransport(new RedirectPolicy(), clock);
        var options = CreateOptions();
        var source = new Uri($"http://127.0.0.1:{port}/start");

        using (var first = await transport.OpenAsync(
                   source, "ticket-one", "GET", null,
                   new Dictionary<string, string> { ["Range"] = "bytes=0-0" },
                   options, CancellationToken.None))
            Assert.IsFalse(first.UsedCachedRedirect);

        clock.Advance(GatewayTransport.RedirectLeaseLifetime + TimeSpan.FromSeconds(1));

        using (var second = await transport.OpenAsync(
                   source, "ticket-one", "GET", null,
                   new Dictionary<string, string> { ["Range"] = "bytes=1-1" },
                   options, CancellationToken.None))
            Assert.IsFalse(second.UsedCachedRedirect);
        await server;

        CollectionAssert.AreEqual(
            new[] { "/start", "/media/file.mkv", "/start", "/media/file.mkv" },
            observed.Select(RequestPath).ToArray());
    }

    [TestMethod]
    public async Task Transport_InvalidCachedTargetFallsBackToOriginalSource()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var observed = new List<string>();
        var server = ServeAsync(listener, observed, new[]
        {
            "HTTP/1.1 302 Found\r\nLocation: /media/old.mkv\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
            PartialContent("bytes 0-0/10", "x"),
            "HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
            "HTTP/1.1 302 Found\r\nLocation: /media/new.mkv\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
            PartialContent("bytes 1-1/10", "y"),
        });
        using var transport = new GatewayTransport(new RedirectPolicy(), new ManualClock());
        var options = CreateOptions();
        var source = new Uri($"http://127.0.0.1:{port}/start");

        using (var first = await transport.OpenAsync(
                   source, "ticket-one", "GET", null,
                   new Dictionary<string, string> { ["Range"] = "bytes=0-0" },
                   options, CancellationToken.None))
            Assert.AreEqual("/media/old.mkv", first.EffectiveUri.AbsolutePath);

        using (var second = await transport.OpenAsync(
                   source, "ticket-one", "GET", null,
                   new Dictionary<string, string> { ["Range"] = "bytes=1-1" },
                   options, CancellationToken.None))
        {
            Assert.IsFalse(second.UsedCachedRedirect);
            Assert.AreEqual("/media/new.mkv", second.EffectiveUri.AbsolutePath);
            Assert.AreEqual(206, (int)second.Response.StatusCode);
        }
        await server;

        CollectionAssert.AreEqual(
            new[] { "/start", "/media/old.mkv", "/media/old.mkv", "/start", "/media/new.mkv" },
            observed.Select(RequestPath).ToArray());
        Assert.AreEqual(1, transport.RedirectLeaseCount);
    }

    [TestMethod]
    public async Task Transport_ReResolvesOnceWhenFreshRedirectTargetIsRejected()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var observed = new List<string>();
        var server = ServeAsync(listener, observed, new[]
        {
            "HTTP/1.1 302 Found\r\nLocation: /media/rejected.mkv\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
            "HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
            "HTTP/1.1 302 Found\r\nLocation: /media/accepted.mkv\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
            PartialContent("bytes 0-0/10", "x"),
        });
        using var transport = new GatewayTransport(new RedirectPolicy(), new ManualClock());
        var source = new Uri($"http://127.0.0.1:{port}/start");

        using var lease = await transport.OpenAsync(
            source,
            "ticket-one",
            "GET",
            null,
            new Dictionary<string, string> { ["Range"] = "bytes=0-0" },
            CreateOptions(),
            CancellationToken.None);
        await server;

        Assert.IsTrue(lease.RetriedRejectedRedirect);
        Assert.IsFalse(lease.UsedCachedRedirect);
        Assert.AreEqual(206, (int)lease.Response.StatusCode);
        Assert.IsFalse(TransportPlanner.RequiresAdaptiveRelay(
            (int)lease.Response.StatusCode));
        Assert.AreEqual("/media/accepted.mkv", lease.EffectiveUri.AbsolutePath);
        CollectionAssert.AreEqual(
            new[] { "/start", "/media/rejected.mkv", "/start", "/media/accepted.mkv" },
            observed.Select(RequestPath).ToArray());
        Assert.AreEqual(1, transport.RedirectLeaseCount);
    }

    [TestMethod]
    public async Task Transport_DoesNotRetryRejectedDirectSource()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var observed = new List<string>();
        var server = ServeAsync(listener, observed, new[]
        {
            "HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
        });
        using var transport = new GatewayTransport(new RedirectPolicy(), new ManualClock());

        using var lease = await transport.OpenAsync(
            new Uri($"http://127.0.0.1:{port}/direct"),
            "ticket-one",
            "GET",
            null,
            new Dictionary<string, string>(),
            CreateOptions(),
            CancellationToken.None);
        await server;

        Assert.IsFalse(lease.RetriedRejectedRedirect);
        Assert.AreEqual(403, (int)lease.Response.StatusCode);
        CollectionAssert.AreEqual(new[] { "/direct" }, observed.Select(RequestPath).ToArray());
        Assert.AreEqual(0, transport.RedirectLeaseCount);
    }

    [TestMethod]
    public async Task Transport_DoesNotCacheARedirectWhoseFinalResponseIsAServerError()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var observed = new List<string>();
        var server = ServeAsync(listener, observed, new[]
        {
            "HTTP/1.1 302 Found\r\nLocation: /media/unavailable.mkv\r\n" +
            "Content-Length: 0\r\nConnection: close\r\n\r\n",
            "HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
            "HTTP/1.1 302 Found\r\nLocation: /media/available.mkv\r\n" +
            "Content-Length: 0\r\nConnection: close\r\n\r\n",
            PartialContent("bytes 0-0/10", "x"),
        });
        var clock = new ManualClock();
        using var transport = new GatewayTransport(new RedirectPolicy(), clock);
        var source = new Uri($"http://127.0.0.1:{port}/start");
        var headers = new Dictionary<string, string> { ["Range"] = "bytes=0-0" };

        using (var first = await transport.OpenAsync(
                   source, "ticket-one", "GET", null, headers, CreateOptions(), CancellationToken.None))
        {
            Assert.AreEqual(503, (int)first.Response.StatusCode);
            Assert.IsFalse(first.UsedCachedRedirect);
            Assert.IsFalse(first.RetriedRejectedRedirect);
        }
        Assert.AreEqual(0, transport.RedirectLeaseCount);
        Assert.AreEqual(1, transport.SourceBackoffCount);

        await Assert.ThrowsExactlyAsync<GatewaySourceBackoffException>(() => transport.OpenAsync(
            source, "ticket-one", "GET", null, headers, CreateOptions(), CancellationToken.None));
        clock.Advance(TimeSpan.FromSeconds(2));

        using (var second = await transport.OpenAsync(
                   source, "ticket-one", "GET", null, headers, CreateOptions(), CancellationToken.None))
        {
            Assert.AreEqual(206, (int)second.Response.StatusCode);
            Assert.IsFalse(second.UsedCachedRedirect);
            Assert.AreEqual("/media/available.mkv", second.EffectiveUri.AbsolutePath);
        }
        await server;

        CollectionAssert.AreEqual(
            new[] { "/start", "/media/unavailable.mkv", "/start", "/media/available.mkv" },
            observed.Select(RequestPath).ToArray());
        Assert.AreEqual(1, transport.RedirectLeaseCount);
        Assert.AreEqual(0, transport.SourceBackoffCount);
    }

    [TestMethod]
    public async Task Transport_BackoffSuppressesConcurrentAuthoritativeRefreshes()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var observed = new List<string>();
        var firstRequestSeen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstResponse = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = ServeControlledRejectedRefreshAsync(
            listener,
            observed,
            firstRequestSeen,
            releaseFirstResponse);
        using var transport = new GatewayTransport(new RedirectPolicy(), new ManualClock());
        var source = new Uri($"http://127.0.0.1:{port}/start");
        var headers = new Dictionary<string, string>
        {
            ["Range"] = "bytes=0-0",
            ["Accept"] = "*/*",
        };

        var firstOpening = transport.OpenAsync(
            source, "ticket-one", "GET", null, headers, CreateOptions(), CancellationToken.None);
        await firstRequestSeen.Task;
        var concurrentOpening = transport.OpenAsync(
            source, "ticket-two", "GET", null,
            new Dictionary<string, string>
            {
                ["Range"] = "bytes=8-9",
                ["Accept"] = "video/*",
            },
            CreateOptions(), CancellationToken.None);
        releaseFirstResponse.SetResult(true);

        using var first = await firstOpening;
        Assert.AreEqual(403, (int)first.Response.StatusCode);
        Assert.IsTrue(first.RetriedRejectedRedirect);
        var backedOff = await Assert.ThrowsExactlyAsync<GatewaySourceBackoffException>(
            () => concurrentOpening);
        await server;

        Assert.AreEqual(403, backedOff.StatusCode);
        Assert.AreEqual(2, backedOff.RetryAfterSeconds);
        Assert.AreEqual(4, observed.Count);
        CollectionAssert.AreEqual(
            new[] { "/start", "/media/first.mkv", "/start", "/media/second.mkv" },
            observed.Select(RequestPath).ToArray());
        Assert.AreEqual(1, transport.ActiveRequests);
        Assert.AreEqual(1, transport.SourceBackoffCount);
    }

    [TestMethod]
    public async Task Transport_CachedRejectionPerformsOnlyOneAuthoritativeRefresh()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var observed = new List<string>();
        var server = ServeAsync(listener, observed, new[]
        {
            "HTTP/1.1 302 Found\r\nLocation: /media/cached.mkv\r\n" +
            "Content-Length: 0\r\nConnection: close\r\n\r\n",
            PartialContent("bytes 0-0/10", "x"),
            "HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
            "HTTP/1.1 302 Found\r\nLocation: /media/refreshed.mkv\r\n" +
            "Content-Length: 0\r\nConnection: close\r\n\r\n",
            "HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
        });
        using var transport = new GatewayTransport(new RedirectPolicy(), new ManualClock());
        var source = new Uri($"http://127.0.0.1:{port}/start");
        var headers = new Dictionary<string, string> { ["Range"] = "bytes=0-0" };

        using (var first = await transport.OpenAsync(
                   source, "ticket-one", "GET", null, headers, CreateOptions(), CancellationToken.None))
            Assert.AreEqual(206, (int)first.Response.StatusCode);

        headers["Range"] = "bytes=1-1";
        using (var rejected = await transport.OpenAsync(
                   source, "ticket-one", "GET", null, headers, CreateOptions(), CancellationToken.None))
        {
            Assert.AreEqual(403, (int)rejected.Response.StatusCode);
            Assert.IsTrue(rejected.RetriedRejectedRedirect);
        }
        await server;

        CollectionAssert.AreEqual(
            new[] { "/start", "/media/cached.mkv", "/media/cached.mkv", "/start", "/media/refreshed.mkv" },
            observed.Select(RequestPath).ToArray());
        Assert.AreEqual(0, transport.RedirectLeaseCount);
        Assert.AreEqual(1, transport.SourceBackoffCount);
    }

    [TestMethod]
    public async Task Transport_RetryAfterBackoffDoesNotConsumeAnotherCapacitySlot()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var observed = new List<string>();
        var server = ServeAsync(listener, observed, new[]
        {
            "HTTP/1.1 429 Too Many Requests\r\nRetry-After: 7\r\n" +
            "Content-Length: 0\r\nConnection: close\r\n\r\n",
        });
        using var transport = new GatewayTransport(new RedirectPolicy(), new ManualClock());
        var options = CreateOptions();
        options.RelayConcurrency = 1;
        var source = new Uri($"http://127.0.0.1:{port}/media");

        using var first = await transport.OpenAsync(
            source, "ticket-one", "GET", null, new Dictionary<string, string>(),
            options, CancellationToken.None);
        await server;
        Assert.AreEqual(429, (int)first.Response.StatusCode);
        Assert.AreEqual(1, transport.ActiveRequests);

        var backedOff = await Assert.ThrowsExactlyAsync<GatewaySourceBackoffException>(() =>
            transport.OpenAsync(
                source, "ticket-two", "GET", null, new Dictionary<string, string>(),
                options, CancellationToken.None));

        Assert.AreEqual(429, backedOff.StatusCode);
        Assert.AreEqual(7, backedOff.RetryAfterSeconds);
        Assert.AreEqual(1, transport.ActiveRequests);
        Assert.HasCount(1, observed);
    }

    [TestMethod]
    public async Task Transport_KnownSourceBackoffStopsRememberedDirectHandoff()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var observed = new List<string>();
        var server = ServeAsync(listener, observed, new[]
        {
            PartialContent("bytes 0-0/10", "x"),
            PartialContent("bytes 1-1/10", "y"),
            "HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
        });
        using var transport = new GatewayTransport(new RedirectPolicy(), new ManualClock());
        var source = new Uri($"http://127.0.0.1:{port}/start");
        const string userAgent = "GenericClient/1.0";
        var headers = new Dictionary<string, string> { ["Range"] = "bytes=0-0" };

        using (await transport.OpenAsync(
                   source, "direct-context", "GET", userAgent, headers,
                   CreateOptions(), CancellationToken.None))
        {
        }
        headers["Range"] = "bytes=1-1";
        using var confirmed = await transport.OpenAsync(
            source, "direct-context", "GET", userAgent, headers,
            CreateOptions(), CancellationToken.None);
        transport.RememberDirectRedirect(
            source, "direct-context", "GET", userAgent, headers,
            confirmed, SourceTransportBehavior.FileBody);
        Assert.AreEqual(1, transport.DirectRouteCount);

        using (var rejected = await transport.OpenAsync(
                   source, "another-context", "GET", userAgent, headers,
                   CreateOptions(), CancellationToken.None))
            Assert.AreEqual(503, (int)rejected.Response.StatusCode);
        await server;

        var backedOff = Assert.ThrowsExactly<GatewaySourceBackoffException>(() =>
            transport.TryGetDirectRoute(
                source, "direct-context", "GET", userAgent, headers, out _));
        Assert.AreEqual(503, backedOff.StatusCode);
        Assert.AreEqual(0, transport.DirectRouteCount);
        transport.RememberDirectRedirect(
            source, "direct-context", "GET", userAgent, headers,
            confirmed, SourceTransportBehavior.FileBody);
        Assert.AreEqual(0, transport.DirectRouteCount);
    }

    [TestMethod]
    public async Task Transport_DefaultSourceBackoffIncreasesAndSuccessClearsIt()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var observed = new List<string>();
        var server = ServeAsync(listener, observed, new[]
        {
            "HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
            "HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
            "HTTP/1.1 200 OK\r\nContent-Length: 1\r\nConnection: close\r\n\r\nx",
        });
        var clock = new ManualClock();
        using var transport = new GatewayTransport(new RedirectPolicy(), clock);
        var source = new Uri($"http://127.0.0.1:{port}/media");

        using (var first = await transport.OpenAsync(
                   source, "ticket-one", "GET", "GenericClient/1.0",
                   new Dictionary<string, string>(), CreateOptions(), CancellationToken.None))
            Assert.AreEqual(503, (int)first.Response.StatusCode);
        clock.Advance(TimeSpan.FromSeconds(2));

        using (var second = await transport.OpenAsync(
                   source, "ticket-two", "GET", "GenericClient/1.0",
                   new Dictionary<string, string>(), CreateOptions(), CancellationToken.None))
            Assert.AreEqual(503, (int)second.Response.StatusCode);
        var backedOff = await Assert.ThrowsExactlyAsync<GatewaySourceBackoffException>(() =>
            transport.OpenAsync(
                source, "ticket-three", "GET", "GenericClient/1.0",
                new Dictionary<string, string>(), CreateOptions(), CancellationToken.None));
        Assert.AreEqual(4, backedOff.RetryAfterSeconds);
        clock.Advance(TimeSpan.FromSeconds(4));

        using (var recovered = await transport.OpenAsync(
                   source, "ticket-four", "GET", "GenericClient/1.0",
                   new Dictionary<string, string>(), CreateOptions(), CancellationToken.None))
            Assert.AreEqual(200, (int)recovered.Response.StatusCode);
        await server;

        Assert.AreEqual(0, transport.SourceBackoffCount);
        Assert.HasCount(3, observed);
    }

    [TestMethod]
    public async Task Transport_SourceBackoffIsIsolatedByActualRequestProfile()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var observed = new List<string>();
        var server = ServeAsync(listener, observed, new[]
        {
            "HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
            "HTTP/1.1 200 OK\r\nContent-Length: 1\r\nConnection: close\r\n\r\nx",
        });
        using var transport = new GatewayTransport(new RedirectPolicy(), new ManualClock());
        var source = new Uri($"http://127.0.0.1:{port}/media");

        using (var denied = await transport.OpenAsync(
                   source, "ticket-one", "GET", "ProfileOne/1.0",
                   new Dictionary<string, string>(), CreateOptions(), CancellationToken.None))
            Assert.AreEqual(403, (int)denied.Response.StatusCode);
        using (var allowed = await transport.OpenAsync(
                   source, "ticket-two", "GET", "ProfileTwo/1.0",
                   new Dictionary<string, string>(), CreateOptions(), CancellationToken.None))
            Assert.AreEqual(200, (int)allowed.Response.StatusCode);
        await server;

        Assert.HasCount(2, observed);
        Assert.IsTrue(observed[0].Contains("User-Agent: ProfileOne/1.0", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(observed[1].Contains("User-Agent: ProfileTwo/1.0", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task Transport_ReturnsRejectedSecondRedirectWithoutAnotherRetry()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var observed = new List<string>();
        var server = ServeAsync(listener, observed, new[]
        {
            "HTTP/1.1 302 Found\r\nLocation: /media/first.mkv\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
            "HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
            "HTTP/1.1 302 Found\r\nLocation: /media/second.mkv\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
            "HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
        });
        using var transport = new GatewayTransport(new RedirectPolicy(), new ManualClock());

        using var lease = await transport.OpenAsync(
            new Uri($"http://127.0.0.1:{port}/start"),
            "ticket-one",
            "GET",
            null,
            new Dictionary<string, string>(),
            CreateOptions(),
            CancellationToken.None);
        await server;

        Assert.IsTrue(lease.RetriedRejectedRedirect);
        Assert.AreEqual(403, (int)lease.Response.StatusCode);
        Assert.IsTrue(TransportPlanner.RequiresAdaptiveRelay(
            (int)lease.Response.StatusCode));
        CollectionAssert.AreEqual(
            new[] { "/start", "/media/first.mkv", "/start", "/media/second.mkv" },
            observed.Select(RequestPath).ToArray());
        Assert.AreEqual(0, transport.RedirectLeaseCount);
        Assert.AreEqual(0, transport.DirectRouteCount);
    }

    [TestMethod]
    public async Task Transport_ClearRemovesRedirectLeases()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var observed = new List<string>();
        var server = ServeAsync(listener, observed, new[]
        {
            "HTTP/1.1 302 Found\r\nLocation: /media/file.mkv\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
            PartialContent("bytes 0-0/10", "x"),
        });
        using var transport = new GatewayTransport(new RedirectPolicy(), new ManualClock());
        using (var lease = await transport.OpenAsync(
                   new Uri($"http://127.0.0.1:{port}/start"),
                   "ticket-one",
                   "GET",
                   null,
                   new Dictionary<string, string> { ["Range"] = "bytes=0-0" },
                   CreateOptions(),
                   CancellationToken.None))
            Assert.AreEqual(206, (int)lease.Response.StatusCode);
        await server;
        Assert.AreEqual(1, transport.RedirectLeaseCount);

        transport.Clear();

        Assert.AreEqual(0, transport.RedirectLeaseCount);
    }

    [TestMethod]
    public async Task Transport_ClearRejectsLateRedirectLeaseWrite()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var observed = new List<string>();
        var firstRequestSeen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRedirect = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = ServeControlledRedirectAsync(
            listener,
            observed,
            firstRequestSeen,
            releaseRedirect);
        using var transport = new GatewayTransport(new RedirectPolicy(), new ManualClock());
        var opening = transport.OpenAsync(
            new Uri($"http://127.0.0.1:{port}/start"),
            "ticket-one",
            "GET",
            null,
            new Dictionary<string, string> { ["Range"] = "bytes=0-0" },
            CreateOptions(),
            CancellationToken.None);

        await firstRequestSeen.Task;
        transport.Clear();
        releaseRedirect.SetResult(true);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await opening);
        listener.Stop();
        try { await server; }
        catch (Exception exception) when (exception is SocketException || exception is IOException ||
                                          exception is InvalidOperationException)
        { }
        Assert.AreEqual(0, transport.ActiveRequests);
        Assert.AreEqual(0, transport.RedirectLeaseCount);
    }

    [TestMethod]
    public async Task Lease_PeekedPrefixIsReplayedToRelayConsumer()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var observed = new List<string>();
        const string body = "#EXTM3U\nsegment.ts\n";
        var server = ServeAsync(listener, observed, new[]
        {
            "HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: " +
            body.Length + "\r\nConnection: close\r\n\r\n" + body,
        });
        using var transport = new GatewayTransport(new RedirectPolicy());
        using var lease = await transport.OpenAsync(
            new Uri($"http://127.0.0.1:{port}/manifest"),
            "GET",
            null,
            new Dictionary<string, string>(),
            CreateOptions(),
            CancellationToken.None);

        var prefix = await lease.PeekPrefixAsync(10, CancellationToken.None);
        Assert.AreEqual(SourceTransportBehavior.HlsManifest,
            SourceBehaviorClassifier.Classify(lease.Response, lease.EffectiveUri, prefix));
        await using (var stream = await lease.OpenOwnedStreamAsync())
        using (var reader = new StreamReader(stream, Encoding.UTF8))
            Assert.AreEqual(body, await reader.ReadToEndAsync());
        await server;
        Assert.AreEqual(0, transport.ActiveRequests);
    }

    [TestMethod]
    public async Task Lease_ReleasesCapacityWhenPrefixPeekIsCancelled()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var releaseServer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = ServeHeadersThenWaitAsync(listener, releaseServer);
        using var transport = new GatewayTransport(new RedirectPolicy());
        using var lease = await transport.OpenAsync(
            new Uri($"http://127.0.0.1:{port}/media"),
            "GET",
            null,
            new Dictionary<string, string>(),
            CreateOptions(),
            CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        OperationCanceledException? cancelled = null;
        try
        {
            await lease.PeekPrefixAsync(10, cancellation.Token);
        }
        catch (OperationCanceledException exception)
        {
            cancelled = exception;
        }
        finally
        {
            releaseServer.TrySetResult(true);
            await server;
        }
        Assert.IsNotNull(cancelled);
        Assert.AreEqual(0, transport.ActiveRequests);
    }

    [TestMethod]
    public void Classifier_DetectsRedirectedHlsFromUtf8Signature()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("\uFEFF#EXTM3U\n")),
        };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            "application/octet-stream");
        var prefix = Encoding.UTF8.GetBytes("\uFEFF#EXTM3U");

        Assert.AreEqual(SourceTransportBehavior.HlsManifest,
            SourceBehaviorClassifier.Classify(
                response,
                new Uri("https://media.invalid/content"),
                prefix));
    }

    [TestMethod]
    public void Planner_UsesModeBehaviorAndAuthoritativeResponse()
    {
        Assert.AreEqual(
            GatewayTransportPlan.Redirect,
            TransportPlanner.Create(
                PlaybackRoutingMode.Adaptive,
                SourceTransportBehavior.FileBody));
        Assert.AreEqual(
            GatewayTransportPlan.RelayHls,
            TransportPlanner.Create(
                PlaybackRoutingMode.Adaptive,
                SourceTransportBehavior.HlsManifest));
        Assert.AreEqual(
            GatewayTransportPlan.RelayFile,
            TransportPlanner.Create(
                PlaybackRoutingMode.RelayOnly,
                SourceTransportBehavior.FileBody));
        Assert.AreEqual(
            GatewayTransportPlan.Redirect,
            TransportPlanner.Create(
                PlaybackRoutingMode.RedirectOnly,
                SourceTransportBehavior.HlsManifest));
        Assert.AreEqual(
            GatewayTransportPlan.RelayFile,
            TransportPlanner.Create(
                PlaybackRoutingMode.Adaptive,
                SourceTransportBehavior.FileBody,
                relayCurrentResponse: true));
        Assert.AreEqual(
            GatewayTransportPlan.Redirect,
            TransportPlanner.Create(
                PlaybackRoutingMode.RedirectOnly,
                SourceTransportBehavior.FileBody,
                relayCurrentResponse: true));

        Assert.IsTrue(TransportPlanner.CanHandoffRedirect(200));
        Assert.IsTrue(TransportPlanner.CanHandoffRedirect(206));
        Assert.IsFalse(TransportPlanner.CanHandoffRedirect(204));
        Assert.IsFalse(TransportPlanner.CanHandoffRedirect(503));
        Assert.IsTrue(TransportPlanner.RequiresAdaptiveRelay(503));
        Assert.IsTrue(TransportPlanner.RequiresAdaptiveRelay(403));
        Assert.IsFalse(TransportPlanner.RequiresAdaptiveRelay(206));
        Assert.IsFalse(TransportPlanner.RequiresAdaptiveRelay(416));
    }

    private static PluginConfiguration CreateOptions() => new()
    {
        GatewayTimeoutSeconds = 10,
        RedirectHopLimit = 5,
        RelayConcurrency = 2,
    };

    private static string PartialContent(string range, string body) =>
        "HTTP/1.1 206 Partial Content\r\nContent-Type: video/x-matroska\r\n" +
        "Content-Range: " + range + "\r\nContent-Length: " + body.Length +
        "\r\nConnection: close\r\n\r\n" + body;

    private static string RequestPath(string request)
    {
        var firstLine = request.Split(new[] { "\r\n" }, StringSplitOptions.None)[0];
        return firstLine.Split(' ')[1];
    }

    private static string RequestMethodAndPath(string request)
    {
        var values = request.Split(new[] { "\r\n" }, StringSplitOptions.None)[0].Split(' ');
        return values[0] + " " + values[1];
    }

    private static async Task ServeAsync(
        TcpListener listener,
        ICollection<string> observed,
        IReadOnlyList<string> responses)
    {
        foreach (var responseText in responses)
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            observed.Add(await ReadRequestAsync(stream));
            var response = Encoding.ASCII.GetBytes(responseText);
            await stream.WriteAsync(response);
        }
    }

    private static async Task ServeControlledRedirectAsync(
        TcpListener listener,
        ICollection<string> observed,
        TaskCompletionSource<bool> firstRequestSeen,
        TaskCompletionSource<bool> releaseRedirect)
    {
        using (var client = await listener.AcceptTcpClientAsync())
        using (var stream = client.GetStream())
        {
            observed.Add(await ReadRequestAsync(stream));
            firstRequestSeen.SetResult(true);
            await releaseRedirect.Task;
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 302 Found\r\nLocation: /media/file.mkv\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
        }
        using (var client = await listener.AcceptTcpClientAsync())
        using (var stream = client.GetStream())
        {
            observed.Add(await ReadRequestAsync(stream));
            await stream.WriteAsync(Encoding.ASCII.GetBytes(PartialContent("bytes 0-0/10", "x")));
        }
    }

    private static async Task ServeControlledRejectedRefreshAsync(
        TcpListener listener,
        ICollection<string> observed,
        TaskCompletionSource<bool> firstRequestSeen,
        TaskCompletionSource<bool> releaseFirstResponse)
    {
        using (var client = await listener.AcceptTcpClientAsync())
        using (var stream = client.GetStream())
        {
            observed.Add(await ReadRequestAsync(stream));
            firstRequestSeen.SetResult(true);
            await releaseFirstResponse.Task;
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 302 Found\r\nLocation: /media/first.mkv\r\n" +
                "Content-Length: 0\r\nConnection: close\r\n\r\n"));
        }
        using (var client = await listener.AcceptTcpClientAsync())
        using (var stream = client.GetStream())
        {
            observed.Add(await ReadRequestAsync(stream));
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
        }
        using (var client = await listener.AcceptTcpClientAsync())
        using (var stream = client.GetStream())
        {
            observed.Add(await ReadRequestAsync(stream));
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 302 Found\r\nLocation: /media/second.mkv\r\n" +
                "Content-Length: 0\r\nConnection: close\r\n\r\n"));
        }
        using (var client = await listener.AcceptTcpClientAsync())
        using (var stream = client.GetStream())
        {
            observed.Add(await ReadRequestAsync(stream));
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
        }
    }

    private static async Task ServeControlledCandidateAsync(
        TcpListener listener,
        ICollection<string> observed,
        TaskCompletionSource<bool> firstRequestSeen,
        TaskCompletionSource<bool> releaseRedirect)
    {
        await ServeControlledRedirectAsync(listener, observed, firstRequestSeen, releaseRedirect);
        using var client = await listener.AcceptTcpClientAsync();
        using var stream = client.GetStream();
        observed.Add(await ReadRequestAsync(stream));
        await stream.WriteAsync(Encoding.ASCII.GetBytes(PartialContent("bytes 1-1/10", "y")));
    }

    private static async Task ServeHeadersThenWaitAsync(
        TcpListener listener,
        TaskCompletionSource<bool> release)
    {
        using var client = await listener.AcceptTcpClientAsync();
        using var stream = client.GetStream();
        await ReadRequestAsync(stream);
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\n" +
            "Content-Length: 100\r\nConnection: close\r\n\r\n"));
        await release.Task;
    }

    private static async Task<string> ReadRequestAsync(NetworkStream stream)
    {
        var buffer = new byte[4096];
        var request = new StringBuilder();
        while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0) break;
            request.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }
        return request.ToString();
    }
}
