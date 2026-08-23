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
            Assert.AreEqual("/media/new.mkv", second.EffectiveUri.AbsolutePath);
        }
        await server;

        CollectionAssert.AreEqual(
            new[] { "/start", "/media/old.mkv", "/media/old.mkv", "/start", "/media/new.mkv" },
            observed.Select(RequestPath).ToArray());
    }

    [TestMethod]
    public async Task Transport_ReusesAValidatedSourceCandidateWithTheActualUserAgent()
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
            Assert.IsTrue(playback.UsedCachedRedirect);
        await server;

        CollectionAssert.AreEqual(
            new[] { "/start", "/media/file.mkv", "/media/file.mkv" },
            observed.Select(RequestPath).ToArray());
        Assert.IsTrue(observed[0].Contains("User-Agent: ProbeAgent/1.0", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(observed[1].Contains("User-Agent: ProbeAgent/1.0", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(observed[2].Contains("User-Agent: MediaTransport/2.0", StringComparison.OrdinalIgnoreCase));
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
                   source, "ticket-scope", "source-scope", "GET", "MediaTransport/2.0",
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
        CollectionAssert.AreEqual(
            new[] { "/start", "/media/first.mkv", "/start", "/media/second.mkv" },
            observed.Select(RequestPath).ToArray());
        Assert.AreEqual(0, transport.RedirectLeaseCount);
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
        using var lease = await opening;
        await server;

        Assert.AreEqual(1, lease.RedirectCount);
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
    public void Planner_UsesRequestPurposeForAdaptiveFileDelivery()
    {
        Assert.AreEqual(
            GatewayTransportPlan.Redirect,
            TransportPlanner.Create(
                PlaybackRoutingMode.Adaptive,
                SourceTransportBehavior.FileBody,
                PlaybackTicketPurpose.DirectClient));
        Assert.AreEqual(
            GatewayTransportPlan.RelayFile,
            TransportPlanner.Create(
                PlaybackRoutingMode.Adaptive,
                SourceTransportBehavior.FileBody,
                PlaybackTicketPurpose.ServerFfmpeg));
        Assert.AreEqual(
            GatewayTransportPlan.RelayHls,
            TransportPlanner.Create(
                PlaybackRoutingMode.Adaptive,
                SourceTransportBehavior.HlsManifest,
                PlaybackTicketPurpose.DirectClient));
        Assert.AreEqual(
            GatewayTransportPlan.RelayFile,
            TransportPlanner.Create(
                PlaybackRoutingMode.RelayOnly,
                SourceTransportBehavior.FileBody,
                PlaybackTicketPurpose.DirectClient));
        Assert.AreEqual(
            GatewayTransportPlan.Redirect,
            TransportPlanner.Create(
                PlaybackRoutingMode.RedirectOnly,
                SourceTransportBehavior.HlsManifest,
                PlaybackTicketPurpose.ServerFfmpeg));
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
