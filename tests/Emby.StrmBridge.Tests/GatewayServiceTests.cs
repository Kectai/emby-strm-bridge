using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Emby.StrmBridge.Api;
using Emby.StrmBridge.Configuration;
using Emby.StrmBridge.Domain;
using Emby.StrmBridge.Playback;
using Emby.StrmBridge.Runtime;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;

namespace Emby.StrmBridge.Tests;

[TestClass]
public sealed class GatewayServiceTests
{

    [TestMethod]
    public async Task AudioExtraction_UsesGatewayButAudioPlaybackRemainsExcluded()
    {
        using var listener = Listen();
        var observed = new List<string>();
        var server = Serve(listener, observed, Response(200, "audio").Replace("application/vnd.apple.mpegurl", "audio/flac"));
        using var probe = new Fixture(Port(listener), PlaybackRoutingMode.Native,
            PlaybackTicketPurpose.ExtractionProbe, audio: true);
        using var body = (Stream)await probe.Get();
        using var reader = new StreamReader(body);
        Assert.AreEqual("audio", await reader.ReadToEndAsync());
        await server;
        Assert.HasCount(1, observed);
        using var playback = new Fixture(Port(listener), audio: true);
        await Assert.ThrowsExactlyAsync<MediaBrowser.Common.Extensions.ResourceNotFoundException>(() => playback.Get());
    }

    [TestMethod]
    public async Task ServerFfmpeg_RedirectedSourceHasBoundedPositiveLease()
    {
        using var listener = Listen();
        var observed = new List<string>();
        var server = Serve(listener, observed,
            "HTTP/1.1 302 Found\r\nLocation: /file\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
            Response(200, "0123456789").Replace("application/vnd.apple.mpegurl", "video/mp2t"));
        using var fixture = new Fixture(Port(listener), PlaybackRoutingMode.Adaptive, PlaybackTicketPurpose.ServerFfmpeg);
        await fixture.Get();
        await server;
        Assert.AreEqual(307, fixture.StatusCode);
        var expiry = DateTimeOffset.Parse(fixture.Headers["Expires"], System.Globalization.CultureInfo.InvariantCulture);
        var remaining = expiry - fixture.Runtime.Clock.UtcNow;
        Assert.IsTrue(remaining > TimeSpan.Zero && remaining <= TimeSpan.FromSeconds(20));
        Assert.DoesNotContain("no-store", fixture.Headers["Cache-Control"]);
    }

    [TestMethod]
    [DataRow("bytes=100-109", "bytes 100-109/1000", true)]
    [DataRow("bytes=990-", "bytes 990-999/1000", true)]
    [DataRow("bytes=-10", "bytes 990-999/1000", true)]
    [DataRow("bytes=-2000", "bytes 0-9/10", true)]
    [DataRow("bytes=100-109", "bytes 0-9/1000", false)]
    [DataRow("bytes=-10", "bytes 0-9/1000", false)]
    public async Task InitialRange_ValidatesAuthoritativeResponseIncludingSuffix(string requested, string actual, bool valid)
    {
        using var listener = Listen();
        var observed = new List<string>();
        var response = Response(206, "0123456789", "Content-Range: " + actual + "\r\n")
            .Replace("application/vnd.apple.mpegurl", "video/mp2t");
        var server = Serve(listener, observed, valid ? new[] { response } : new[] { response, response });
        using var fixture = new Fixture(Port(listener));
        fixture.RequestHeaders["Range"] = requested;
        var result = await fixture.Get();
        if (result is Stream body)
        {
            using (body)
            using (var reader = new StreamReader(body)) Assert.AreEqual("0123456789", await reader.ReadToEndAsync());
        }
        await server;
        Assert.AreEqual(valid ? 206 : 502, fixture.StatusCode);
        Assert.HasCount(valid ? 1 : 2, observed);
        Assert.AreEqual(0, fixture.Runtime.Gateway!.ActiveRequests);
    }

    [TestMethod]
    public async Task InitialRange_RecoversOnceFromBadAuthoritativeResponse()
    {
        using var listener = Listen();
        var observed = new List<string>();
        var bad = Response(206, "0123456789", "Content-Range: bytes 0-9/1000\r\n")
            .Replace("application/vnd.apple.mpegurl", "video/mp2t");
        var good = bad.Replace("bytes 0-9/1000", "bytes 100-109/1000");
        var server = Serve(listener, observed, bad, good);
        using var fixture = new Fixture(Port(listener));
        fixture.RequestHeaders["Range"] = "bytes=100-109";
        using var body = (Stream)await fixture.Get();
        using var reader = new StreamReader(body);
        Assert.AreEqual("0123456789", await reader.ReadToEndAsync());
        await server;
        Assert.AreEqual(206, fixture.StatusCode);
        Assert.HasCount(2, observed);
    }

    [TestMethod]
    public async Task HlsFailedRefresh_PreservesPreviousWindowAndRollsBackOnlyNewTickets()
    {
        using var listener = Listen();
        var observed = new List<string>();
        var server = Serve(listener, observed, Response(200, Manifest),
            Response(200, "#EXTM3U\n#EXTINF:6,\nnew.ts\n#EXTINF:6,\ninvalid.ts#fragment"));
        using var fixture = new Fixture(Port(listener));
        await fixture.Get();
        var count = fixture.Runtime.Tickets.Count;
        await Assert.ThrowsExactlyAsync<MediaBrowser.Common.Extensions.ResourceNotFoundException>(() => fixture.Get());
        await server;
        Assert.AreEqual(count, fixture.Runtime.Tickets.Count);
        Assert.IsTrue(fixture.Runtime.Tickets.TryInspect(fixture.Ticket, out _));
    }

    [TestMethod]
    public async Task LiveHls_RollingWindowsKeepTicketUsageBounded()
    {
        using var listener = Listen();
        var observed = new List<string>();
        var responses = Enumerable.Range(0, 80).Select(i => Response(200,
            "#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXT-X-MEDIA-SEQUENCE:" + i + "\n" +
            string.Join("\n", Enumerable.Range(i, 3).Select(n => "#EXTINF:6,\nsegment" + n + ".ts?signature=" + n)))).ToArray();
        var server = Serve(listener, observed, responses);
        using var fixture = new Fixture(Port(listener));
        for (var i = 0; i < responses.Length; i++)
        {
            var result = (ReadOnlyMemory<byte>)await fixture.Get();
            Assert.AreEqual(200, fixture.StatusCode);
            Assert.Contains("/hls/", Encoding.UTF8.GetString(result.Span));
            Assert.IsTrue(fixture.Runtime.Tickets.Count <= 25, "Retired windows must leave the capacity pool after the grace period.");
            ((ManualClock)fixture.Runtime.Clock).Advance(TimeSpan.FromSeconds(6));
        }
        await server;
    }

    private const string Manifest = "#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXTINF:6,\na.ts\n#EXT-X-ENDLIST\n";

    [TestMethod]
    [DataRow(PlaybackTicketPurpose.ServerFfmpeg)]
    [DataRow(PlaybackTicketPurpose.ExtractionProbe)]
    public async Task ServerOnlyTicket_NonLoopbackRequestIsRejectedAndRevoked(
        PlaybackTicketPurpose purpose)
    {
        using var listener = Listen();
        using var fixture = new Fixture(
            Port(listener),
            purpose: purpose,
            remoteIp: IPAddress.Parse("192.0.2.1"));

        await Assert.ThrowsExactlyAsync<ResourceNotFoundException>(() => fixture.Get());

        Assert.IsFalse(fixture.Runtime.Tickets.TryInspect(fixture.Ticket, out _));
        Assert.AreEqual(0, fixture.Payload.ProbeRequestObserved);
    }

    [TestMethod]
    [DataRow("synthetic", 104857600L, 206, 307)]
    [DataRow("different", 104857600L, 206, 412)]
    [DataRow("synthetic", 104857601L, 206, 412)]
    [DataRow("synthetic", 104857600L, 412, 412)]
    public async Task ActivatedFastSeek_EnforcesItsProbeRepresentationBeforeHandoff(
        string etag, long length, int upstreamStatus, int expectedStatus)
    {
        using var listener = Listen();
        var observed = new List<string>();
        var server = Serve(listener, observed,
            "HTTP/1.1 " + upstreamStatus + " Response\r\nContent-Type: video/mp2t\r\n" +
            "Content-Range: bytes 0-9/" + length + "\r\nContent-Length: 10\r\nETag: \"" + etag +
            "\"\r\nConnection: close\r\n\r\n0123456789");
        using var fixture = new Fixture(Port(listener), PlaybackRoutingMode.Adaptive, PlaybackTicketPurpose.ServerFfmpeg);
        await fixture.ActivateFastSeek();
        fixture.RequestHeaders["User-Agent"] = GatewayTransport.ProbeUserAgent;
        fixture.RequestHeaders["Range"] = "bytes=0-9";
        fixture.RequestHeaders["If-Match"] = "\"untrusted-client-value\"";

        Assert.AreEqual(string.Empty, await fixture.Get());
        await server;

        Assert.AreEqual(expectedStatus, fixture.StatusCode);
        Assert.AreEqual(expectedStatus == 307, fixture.Headers.ContainsKey("Location"));
        StringAssert.Contains(observed.Single(), "If-Match: \"synthetic\"");
        Assert.IsFalse(observed.Single().Contains("untrusted-client-value", StringComparison.Ordinal));
        Assert.AreEqual(0, fixture.Runtime.Gateway!.ActiveRequests);
    }

    [TestMethod]
    public async Task HlsCompleteRange_IsRewrittenAsAFullResponseWithoutOriginValidators()
    {
        using var listener = Listen();
        var observed = new List<string>();
        var server = Serve(listener, observed, Response(206, Manifest,
            "Content-Range: bytes 0-" + (Manifest.Length - 1) + "/" + Manifest.Length + "\r\n"));
        using var fixture = new Fixture(Port(listener));
        fixture.RequestHeaders["Range"] = "bytes=0-";
        var result = (ReadOnlyMemory<byte>)await fixture.Get();
        await server;

        Assert.AreEqual(200, fixture.StatusCode);
        Assert.IsTrue(result.Length > Manifest.Length);
        StringAssert.Contains(Encoding.UTF8.GetString(result.Span), "/hls/");
        foreach (var name in new[] { "Content-Range", "Accept-Ranges", "ETag", "Last-Modified" })
            Assert.IsFalse(fixture.Headers.ContainsKey(name), name);
        Assert.HasCount(1, observed);
    }

    [TestMethod]
    [DataRow(206, "GET")]
    [DataRow(304, "GET")]
    [DataRow(200, "HEAD")]
    public async Task HlsPartialConditionalOrHead_FetchesAnUnconditionalCompleteManifest(int status, string method)
    {
        using var listener = Listen();
        var observed = new List<string>();
        var firstBody = status == 206 ? "a.ts\n" : string.Empty;
        var firstHeaders = status == 206 ? "Content-Range: bytes 42-46/63\r\n" : string.Empty;
        var server = Serve(listener, observed, Response(status, firstBody, firstHeaders), Response(200, Manifest));
        using var fixture = new Fixture(Port(listener));
        fixture.Method = method;
        fixture.RequestHeaders["Range"] = "bytes=42-46";
        fixture.RequestHeaders["If-None-Match"] = "\"origin\"";
        fixture.RequestHeaders["If-Modified-Since"] = "Sun, 30 Aug 2026 00:00:00 GMT";
        fixture.RequestHeaders["If-Range"] = "\"origin\"";
        var result = (ReadOnlyMemory<byte>)await fixture.Get();
        await server;

        Assert.AreEqual(200, fixture.StatusCode);
        StringAssert.StartsWith(Encoding.UTF8.GetString(result.Span), "#EXTM3U");
        Assert.HasCount(2, observed);
        StringAssert.StartsWith(observed[1], "GET ");
        foreach (var name in new[] { "Range:", "If-Range:", "If-None-Match:", "If-Modified-Since:" })
            Assert.IsFalse(observed[1].Contains(name, StringComparison.OrdinalIgnoreCase), name);
    }

    [TestMethod]
    public async Task HlsLiveRefresh_AllowsChangedValidatorsAfterSignatureClassification()
    {
        using var listener = Listen();
        var observed = new List<string>();
        var next = Manifest.Replace("a.ts", "next-segment.ts", StringComparison.Ordinal);
        var server = Serve(listener, observed,
            "HTTP/1.1 302 Found\r\nLocation: /opaque-resource\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
            Response(200, Manifest).Replace("application/vnd.apple.mpegurl", "application/octet-stream", StringComparison.Ordinal),
            Response(200, next).Replace("application/vnd.apple.mpegurl", "application/octet-stream", StringComparison.Ordinal)
                .Replace("\"origin\"", "\"updated\"", StringComparison.Ordinal));
        using var fixture = new Fixture(Port(listener));
        var first = (ReadOnlyMemory<byte>)await fixture.Get();
        var second = (ReadOnlyMemory<byte>)await fixture.Get();
        await server;

        Assert.AreEqual(200, fixture.StatusCode);
        Assert.IsFalse(first.Span.SequenceEqual(second.Span));
        Assert.HasCount(3, observed);
        StringAssert.StartsWith(observed[2], "GET /opaque-resource ");
    }

    [TestMethod]
    public async Task HlsClassification_DoesNotChangeAnotherUserAgentLeaseForTheSameUri()
    {
        using var listener = Listen();
        var observed = new List<string>();
        const string redirect = "HTTP/1.1 302 Found\r\nLocation: /shared-uri\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
        var binary = Response(200, "0123456789")
            .Replace("application/vnd.apple.mpegurl", "video/mp2t", StringComparison.Ordinal);
        var hiddenHls = Response(200, Manifest)
            .Replace("application/vnd.apple.mpegurl", "application/octet-stream", StringComparison.Ordinal)
            .Replace("\"origin\"", "\"playlist\"", StringComparison.Ordinal);
        var server = Serve(listener, observed, redirect, binary, redirect, hiddenHls, binary);
        using var fixture = new Fixture(Port(listener));

        fixture.RequestHeaders["User-Agent"] = "BinaryProfile";
        using (var stream = (Stream)await fixture.Get())
        using (var reader = new StreamReader(stream))
            Assert.AreEqual("0123456789", await reader.ReadToEndAsync());
        fixture.RequestHeaders["User-Agent"] = "PlaylistProfile";
        var manifest = (ReadOnlyMemory<byte>)await fixture.Get();
        StringAssert.Contains(Encoding.UTF8.GetString(manifest.Span), "/hls/");
        fixture.RequestHeaders["User-Agent"] = "BinaryProfile";
        using (var stream = (Stream)await fixture.Get())
        using (var reader = new StreamReader(stream))
            Assert.AreEqual("0123456789", await reader.ReadToEndAsync());
        await server;

        Assert.AreEqual(200, fixture.StatusCode);
        Assert.HasCount(5, observed);
        StringAssert.StartsWith(observed[4], "GET /shared-uri ");
        StringAssert.Contains(observed[4], "User-Agent: BinaryProfile");
    }

    [TestMethod]
    public async Task ProbeCancellation_AfterStreamHandoffClosesOwnedUpstream()
    {
        using var listener = Listen();
        var observed = new List<string>();
        var server = Serve(listener, observed, Response(200, "0123456789")
            .Replace("application/vnd.apple.mpegurl", "application/octet-stream", StringComparison.Ordinal));
        using var fixture = new Fixture(Port(listener), purpose: PlaybackTicketPurpose.ExtractionProbe);
        using var cancellation = new CancellationTokenSource();
        var observedCallbacks = 0;
        fixture.Payload.ProbeCancellation = cancellation.Token;
        fixture.Payload.ProbeInputObservedCallback = () => Interlocked.Increment(ref observedCallbacks);
        using var stream = (Stream)await fixture.Get();
        Assert.AreEqual(1, fixture.Payload.ProbeRequestObserved);
        Assert.AreEqual(1, observedCallbacks);
        Assert.IsNull(fixture.Payload.ProbeInputObservedCallback);
        await server;
        Assert.AreEqual(1, fixture.Runtime.Gateway!.ActiveRequests);

        cancellation.Cancel();

        Assert.AreEqual(0, fixture.Runtime.Gateway.ActiveRequests);
    }

    [TestMethod]
    public async Task Probe_GatewayCapacityMarksLocalFailureAfterObservingLoopbackRequest()
    {
        using var listener = Listen();
        var observed = new List<string>();
        var server = Serve(listener, observed, Response(200, "held-response")
            .Replace("application/vnd.apple.mpegurl", "application/octet-stream", StringComparison.Ordinal));
        using var fixture = new Fixture(
            Port(listener),
            purpose: PlaybackTicketPurpose.ExtractionProbe,
            relayConcurrency: 1);
        var options = fixture.Runtime.GetOptionsSnapshot();
        using var held = await fixture.Runtime.Gateway!.OpenAsync(
            fixture.Payload.UpstreamUri,
            "GET",
            null,
            new Dictionary<string, string>(),
            options,
            CancellationToken.None);
        await server;

        Assert.AreEqual(string.Empty, await fixture.Get());

        Assert.AreEqual(503, fixture.StatusCode);
        Assert.AreEqual(1, fixture.Payload.ProbeRequestObserved);
        Assert.AreEqual(1, fixture.Payload.ProbeLocalFailureObserved);
        Assert.HasCount(1, observed);
    }

    [TestMethod]
    public async Task Probe_SourceFileChangeMarksLocalFailureInsteadOfRemoteSourceFailure()
    {
        using var listener = Listen();
        using var fixture = new Fixture(
            Port(listener),
            purpose: PlaybackTicketPurpose.ExtractionProbe);
        File.WriteAllText(
            fixture.Payload.Source.LocalPath,
            "https://source.invalid/changed",
            new UTF8Encoding(false));

        await Assert.ThrowsExactlyAsync<ResourceNotFoundException>(() => fixture.Get());

        Assert.AreEqual(1, fixture.Payload.ProbeRequestObserved);
        Assert.AreEqual(1, fixture.Payload.ProbeLocalFailureObserved);
    }

    [TestMethod]
    public async Task RelayHead_PreservesTheUpstreamContentEncoding()
    {
        using var listener = Listen();
        var observed = new List<string>();
        var server = Serve(listener, observed,
            Response(200, "encoded-body", "Content-Encoding: gzip\r\n")
                .Replace("application/vnd.apple.mpegurl", "application/octet-stream", StringComparison.Ordinal));
        using var fixture = new Fixture(Port(listener));
        fixture.Method = "HEAD";

        Assert.IsInstanceOfType<ReadOnlyMemory<byte>>(await fixture.Get());
        await server;

        Assert.AreEqual("gzip", fixture.Headers["Content-Encoding"]);
        StringAssert.Contains(observed.Single(), "Accept-Encoding: identity");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RelayBody_FailsClosedWhenTheOriginIgnoresIdentityEncoding(bool opaqueContentType)
    {
        using var listener = Listen();
        var observed = new List<string>();
        var response = Response(200, Manifest, "Content-Encoding: gzip\r\n");
        if (opaqueContentType)
            response = response.Replace(
                "application/vnd.apple.mpegurl",
                "application/octet-stream",
                StringComparison.Ordinal);
        var server = Serve(listener, observed, response);
        using var fixture = new Fixture(Port(listener));

        await Assert.ThrowsExactlyAsync<ResourceNotFoundException>(() => fixture.Get());
        await server;

        Assert.HasCount(1, observed);
        Assert.AreEqual(0, fixture.Runtime.Gateway!.ActiveRequests);
    }

    [TestMethod]
    public async Task RelayHls_CompleteGetRefetchAlsoRejectsAnEncodedBody()
    {
        using var listener = Listen();
        var observed = new List<string>();
        var server = Serve(listener, observed,
            Response(200, string.Empty),
            Response(200, Manifest, "Content-Encoding: gzip\r\n"));
        using var fixture = new Fixture(Port(listener));
        fixture.Method = "HEAD";

        await Assert.ThrowsExactlyAsync<ResourceNotFoundException>(() => fixture.Get());
        await server;

        Assert.HasCount(2, observed);
        StringAssert.StartsWith(observed[0], "HEAD ");
        StringAssert.StartsWith(observed[1], "GET ");
        Assert.AreEqual(0, fixture.Runtime.Gateway!.ActiveRequests);
    }

    [TestMethod]
    public async Task ServerFfmpeg_RedirectedCdnUsesValidatedLeaseRatherThanClientFirstHopPolicy()
    {
        using var listener = Listen();
        var observed = new List<string>();
        var server = Serve(listener, observed,
            "HTTP/1.1 302 Found\r\nLocation: /signed-media\r\n" +
            "Content-Length: 0\r\nConnection: close\r\n\r\n",
            Response(200, "media-body")
                .Replace("application/vnd.apple.mpegurl", "application/octet-stream", StringComparison.Ordinal));
        using var fixture = new Fixture(
            Port(listener),
            PlaybackRoutingMode.Adaptive,
            PlaybackTicketPurpose.ServerFfmpeg);

        Assert.AreEqual(string.Empty, await fixture.Get());
        await server;

        Assert.AreEqual(307, fixture.StatusCode);
        Assert.Contains("max-age=20", fixture.Headers["Cache-Control"]);
        Assert.IsTrue(fixture.Headers.ContainsKey("Expires"));
        StringAssert.EndsWith(fixture.Headers["Location"], "/signed-media");
        Assert.HasCount(2, observed);
    }

    [TestMethod]
    [DataRow(301)]
    [DataRow(302)]
    [DataRow(303)]
    [DataRow(307)]
    [DataRow(308)]
    public async Task DirectClient_KnownFileHandsOffTheValidatedFirstRedirectWithoutOpeningItsTarget(int status)
    {
        using var listener = Listen();
        var observed = new List<string>();
        var server = Serve(listener, observed,
            $"HTTP/1.1 {status} Redirect\r\nLocation: /one-use-cdn\r\n" +
            "Content-Length: 0\r\nConnection: close\r\n\r\n");
        using var fixture = new Fixture(
            Port(listener), PlaybackRoutingMode.Adaptive,
            sourceRedirectHandoffAllowed: true);
        fixture.RequestHeaders["Range"] = "bytes=100-199";
        fixture.RequestHeaders["User-Agent"] = "GenericClient/1.0";

        Assert.AreEqual(string.Empty, await fixture.Get());
        await server;

        Assert.AreEqual(302, fixture.StatusCode);
        Assert.AreEqual("private, no-store", fixture.Headers["Cache-Control"]);
        StringAssert.EndsWith(fixture.Headers["Location"], "/one-use-cdn");
        Assert.HasCount(1, observed);
        StringAssert.Contains(observed[0], "Range: bytes=100-199");
        StringAssert.Contains(observed[0], "User-Agent: GenericClient/1.0");
        Assert.AreEqual(0, fixture.Runtime.Gateway!.ActiveRequests);
        Assert.AreEqual(0, fixture.Runtime.Gateway.RedirectLeaseCount);
    }

    [TestMethod]
    public async Task DirectClient_KnownFileCachesOnlyTheUnopenedLocationWhenConfigured()
    {
        using var listener = Listen();
        var observed = new List<string>();
        var server = Serve(listener, observed,
            "HTTP/1.1 302 Found\r\nLocation: /reusable-cdn\r\n" +
            "Cache-Control: no-store\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        using var fixture = new Fixture(
            Port(listener), PlaybackRoutingMode.Adaptive,
            sourceRedirectHandoffAllowed: true,
            directRedirectCacheSeconds: 20);
        fixture.RequestHeaders["Range"] = "bytes=0-9";

        Assert.AreEqual(string.Empty, await fixture.Get());
        var firstLocation = fixture.Headers["Location"];
        Assert.AreEqual("max-age=20, private, must-revalidate", fixture.Headers["Cache-Control"]);
        Assert.IsTrue(fixture.Headers.ContainsKey("Expires"));
        Assert.AreEqual("User-Agent, Accept, Accept-Language", fixture.Headers["Vary"]);
        Assert.IsFalse(fixture.Headers.ContainsKey("Pragma"));
        Assert.AreEqual(1, fixture.Runtime.Gateway!.DirectRouteCount);

        fixture.RequestHeaders["Range"] = "bytes=1048576-1048585";
        Assert.AreEqual(string.Empty, await fixture.Get());
        await server;

        Assert.AreEqual(302, fixture.StatusCode);
        Assert.AreEqual(firstLocation, fixture.Headers["Location"]);
        Assert.AreEqual("max-age=20, private, must-revalidate", fixture.Headers["Cache-Control"]);
        Assert.HasCount(1, observed, "The cached Location must not be opened by the plugin.");
        StringAssert.StartsWith(observed[0], "GET /first ");
    }

    [TestMethod]
    public async Task DirectClient_NoCacheRequestRefreshesAConfiguredReusableLocation()
    {
        using var listener = Listen();
        var observed = new List<string>();
        var server = Serve(listener, observed,
            "HTTP/1.1 302 Found\r\nLocation: /first-cdn\r\n" +
            "Content-Length: 0\r\nConnection: close\r\n\r\n",
            "HTTP/1.1 302 Found\r\nLocation: /second-cdn\r\n" +
            "Content-Length: 0\r\nConnection: close\r\n\r\n");
        using var fixture = new Fixture(
            Port(listener), PlaybackRoutingMode.Adaptive,
            sourceRedirectHandoffAllowed: true,
            directRedirectCacheSeconds: 20);

        Assert.AreEqual(string.Empty, await fixture.Get());
        StringAssert.EndsWith(fixture.Headers["Location"], "/first-cdn");

        fixture.RequestHeaders["Cache-Control"] = "no-cache";
        fixture.RequestHeaders["Range"] = "bytes=100-199";
        Assert.AreEqual(string.Empty, await fixture.Get());
        await server;

        StringAssert.EndsWith(fixture.Headers["Location"], "/second-cdn");
        Assert.HasCount(2, observed);
        StringAssert.Contains(observed[1], "Cache-Control: no-cache");
    }

    [TestMethod]
    public async Task Adaptive_KnownDirectFileLearnsANoPreReadRouteAfterTheFirstRequest()
    {
        using var listener = Listen();
        var observed = new List<string>();
        var server = Serve(listener, observed,
            Response(200, "media-body")
                .Replace("application/vnd.apple.mpegurl", "application/octet-stream", StringComparison.Ordinal));
        using var fixture = new Fixture(
            Port(listener), PlaybackRoutingMode.Adaptive,
            sourceRedirectHandoffAllowed: true);

        Assert.AreEqual(string.Empty, await fixture.Get());
        await server;
        Assert.AreEqual(302, fixture.StatusCode);
        Assert.AreEqual(1, fixture.Runtime.Gateway!.DirectRouteCount);

        Assert.AreEqual(string.Empty, await fixture.Get());

        Assert.AreEqual(302, fixture.StatusCode);
        StringAssert.EndsWith(fixture.Headers["Location"], "/first");
        Assert.HasCount(1, observed);
        Assert.AreEqual(0, fixture.Runtime.Gateway.RedirectLeaseCount);
    }

    [TestMethod]
    public async Task Adaptive_KnownDirectFileAdvertisesConfiguredCacheOnTheFirstResponse()
    {
        using var listener = Listen();
        var observed = new List<string>();
        var server = Serve(listener, observed,
            Response(200, "media-body")
                .Replace("application/vnd.apple.mpegurl", "application/octet-stream", StringComparison.Ordinal));
        using var fixture = new Fixture(
            Port(listener), PlaybackRoutingMode.Adaptive,
            sourceRedirectHandoffAllowed: true,
            directRedirectCacheSeconds: 20);

        Assert.AreEqual(string.Empty, await fixture.Get());
        await server;

        Assert.AreEqual(302, fixture.StatusCode);
        Assert.AreEqual("max-age=20, private, must-revalidate", fixture.Headers["Cache-Control"]);
        Assert.IsTrue(fixture.Headers.ContainsKey("Expires"));
        Assert.AreEqual("User-Agent, Accept, Accept-Language", fixture.Headers["Vary"]);
        Assert.HasCount(1, observed);
        Assert.AreEqual(1, fixture.Runtime.Gateway!.DirectRouteCount);
    }

    [TestMethod]
    public async Task RelayOnly_KnownFileStillFollowsTheRedirectAndRelaysTheBody()
    {
        using var listener = Listen();
        var observed = new List<string>();
        var server = Serve(listener, observed,
            "HTTP/1.1 302 Found\r\nLocation: /media-file\r\n" +
            "Content-Length: 0\r\nConnection: close\r\n\r\n",
            Response(200, "media-body")
                .Replace("application/vnd.apple.mpegurl", "application/octet-stream", StringComparison.Ordinal));
        using var fixture = new Fixture(
            Port(listener), PlaybackRoutingMode.RelayOnly,
            sourceRedirectHandoffAllowed: true);

        using var result = (Stream)await fixture.Get();
        using var reader = new StreamReader(result, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        await server;

        Assert.AreEqual(200, fixture.StatusCode);
        Assert.AreEqual("media-body", body);
        Assert.IsFalse(fixture.Headers.ContainsKey("Location"));
        Assert.HasCount(2, observed);
    }

    [TestMethod]
    public async Task RedirectOnly_UnknownResourceAlsoStopsAtTheValidatedFirstRedirect()
    {
        using var listener = Listen();
        var observed = new List<string>();
        var server = Serve(listener, observed,
            "HTTP/1.1 302 Found\r\nLocation: /opaque-resource\r\n" +
            "Content-Length: 0\r\nConnection: close\r\n\r\n");
        using var fixture = new Fixture(Port(listener), PlaybackRoutingMode.RedirectOnly);

        Assert.AreEqual(string.Empty, await fixture.Get());
        await server;

        Assert.AreEqual(302, fixture.StatusCode);
        StringAssert.EndsWith(fixture.Headers["Location"], "/opaque-resource");
        Assert.HasCount(1, observed);
    }

    [TestMethod]
    public async Task Adaptive_UnknownFileRefreshesTheFirstHandoffAndLearnsWithoutRetainingSignedTargets()
    {
        using var listener = Listen();
        var observed = new List<string>();
        var server = Serve(listener, observed,
            "HTTP/1.1 302 Found\r\nLocation: /one-use-cdn\r\n" +
            "Content-Length: 0\r\nConnection: close\r\n\r\n",
            Response(200, "media-body")
                .Replace("application/vnd.apple.mpegurl", "application/octet-stream", StringComparison.Ordinal),
            "HTTP/1.1 307 Temporary Redirect\r\nLocation: /fresh-cdn\r\n" +
            "Content-Length: 0\r\nConnection: close\r\n\r\n",
            "HTTP/1.1 302 Found\r\nLocation: /next-cdn\r\n" +
            "Content-Length: 0\r\nConnection: close\r\n\r\n");
        using var fixture = new Fixture(Port(listener), PlaybackRoutingMode.Adaptive);
        fixture.RequestHeaders["Range"] = "bytes=0-9";

        Assert.AreEqual(string.Empty, await fixture.Get());
        Assert.AreEqual(302, fixture.StatusCode);
        StringAssert.EndsWith(fixture.Headers["Location"], "/fresh-cdn");
        Assert.AreEqual(1, fixture.Runtime.Gateway!.DirectRouteCount);
        Assert.AreEqual(0, fixture.Runtime.Gateway.RedirectLeaseCount);

        fixture.RequestHeaders["Range"] = "bytes=100-199";
        Assert.AreEqual(string.Empty, await fixture.Get());
        await server;

        Assert.AreEqual(302, fixture.StatusCode);
        StringAssert.EndsWith(fixture.Headers["Location"], "/next-cdn");
        Assert.AreEqual(0, fixture.Runtime.Gateway.RedirectLeaseCount);
        Assert.HasCount(4, observed);
        StringAssert.StartsWith(observed[0], "GET /first ");
        StringAssert.StartsWith(observed[1], "GET /one-use-cdn ");
        StringAssert.StartsWith(observed[2], "GET /first ");
        StringAssert.Contains(observed[2], "Range: bytes=0-9");
        StringAssert.StartsWith(observed[3], "GET /first ");
        StringAssert.Contains(observed[3], "Range: bytes=100-199");
    }

    [TestMethod]
    public async Task Adaptive_KnownFileHintIsOverriddenByAnExplicitHlsFirstHop()
    {
        using var listener = Listen();
        var observed = new List<string>();
        var server = Serve(listener, observed,
            "HTTP/1.1 302 Found\r\nLocation: /stale-hint.m3u8\r\n" +
            "Content-Length: 0\r\nConnection: close\r\n\r\n",
            "HTTP/1.1 302 Found\r\nLocation: /current.m3u8\r\n" +
            "Content-Length: 0\r\nConnection: close\r\n\r\n",
            Response(200, Manifest));
        using var fixture = new Fixture(
            Port(listener), PlaybackRoutingMode.Adaptive,
            sourceRedirectHandoffAllowed: true);

        var result = (ReadOnlyMemory<byte>)await fixture.Get();
        await server;

        Assert.AreEqual(200, fixture.StatusCode);
        StringAssert.Contains(Encoding.UTF8.GetString(result.Span), "/hls/");
        Assert.IsFalse(fixture.Headers.ContainsKey("Location"));
        Assert.AreEqual(0, fixture.Runtime.Gateway!.DirectRouteCount);
        Assert.AreEqual(0, fixture.Runtime.Gateway.RedirectLeaseCount);
        CollectionAssert.AreEqual(
            new[] { "/first", "/first", "/current.m3u8" },
            observed.Select(RequestPath).ToArray());
    }

    [TestMethod]
    public async Task Adaptive_ExplicitHlsFirstHopClearsAnEarlierLearnedFileDecision()
    {
        using var listener = Listen();
        var observed = new List<string>();
        var server = Serve(listener, observed,
            "HTTP/1.1 302 Found\r\nLocation: /current.m3u8\r\n" +
            "Content-Length: 0\r\nConnection: close\r\n\r\n",
            "HTTP/1.1 302 Found\r\nLocation: /current.m3u8\r\n" +
            "Content-Length: 0\r\nConnection: close\r\n\r\n",
            Response(200, Manifest));
        using var fixture = new Fixture(Port(listener), PlaybackRoutingMode.Adaptive);
        var scope = GatewayTransport.CreateDirectRouteScope(fixture.Payload, fixture.Ticket);
        using (var gate = await fixture.Runtime.Gateway!.AcquireDirectRouteGateAsync(
                   scope, "GET", null, new Dictionary<string, string>(), CancellationToken.None))
        {
            Assert.IsNotNull(gate);
            fixture.Runtime.Gateway.RememberDirectSourceRedirect(
                fixture.Payload.UpstreamUri,
                scope,
                "GET",
                null,
                new Dictionary<string, string>(),
                SourceTransportBehavior.FileBody,
                gate!.Generation);
        }
        Assert.AreEqual(1, fixture.Runtime.Gateway.DirectRouteCount);

        var result = (ReadOnlyMemory<byte>)await fixture.Get();
        await server;

        Assert.AreEqual(200, fixture.StatusCode);
        StringAssert.Contains(Encoding.UTF8.GetString(result.Span), "/hls/");
        Assert.AreEqual(0, fixture.Runtime.Gateway.DirectRouteCount);
        CollectionAssert.AreEqual(
            new[] { "/first", "/first", "/current.m3u8" },
            observed.Select(RequestPath).ToArray());
    }

    [TestMethod]
    public async Task Adaptive_AuthoritativeRefreshHlsSignalOverridesTheEarlierFileClassification()
    {
        using var listener = Listen();
        var observed = new List<string>();
        var server = Serve(listener, observed,
            "HTTP/1.1 302 Found\r\nLocation: /initial-file\r\n" +
            "Content-Length: 0\r\nConnection: close\r\n\r\n",
            Response(200, "media-body")
                .Replace("application/vnd.apple.mpegurl", "application/octet-stream", StringComparison.Ordinal),
            "HTTP/1.1 302 Found\r\nLocation: /changed.m3u8\r\n" +
            "Content-Length: 0\r\nConnection: close\r\n\r\n",
            "HTTP/1.1 302 Found\r\nLocation: /current.m3u8\r\n" +
            "Content-Length: 0\r\nConnection: close\r\n\r\n",
            Response(200, Manifest));
        using var fixture = new Fixture(Port(listener), PlaybackRoutingMode.Adaptive);

        var result = (ReadOnlyMemory<byte>)await fixture.Get();
        await server;

        Assert.AreEqual(200, fixture.StatusCode);
        StringAssert.Contains(Encoding.UTF8.GetString(result.Span), "/hls/");
        Assert.IsFalse(fixture.Headers.ContainsKey("Location"));
        Assert.AreEqual(0, fixture.Runtime.Gateway!.DirectRouteCount);
        Assert.AreEqual(0, fixture.Runtime.Gateway.RedirectLeaseCount);
        Assert.HasCount(5, observed);
        CollectionAssert.AreEqual(
            new[] { "/first", "/initial-file", "/first", "/first", "/current.m3u8" },
            observed.Select(RequestPath).ToArray());
    }

    [TestMethod]
    public async Task Adaptive_UnknownRedirectStillFollowsAndRewritesAnOpaqueHlsManifest()
    {
        using var listener = Listen();
        var observed = new List<string>();
        var server = Serve(listener, observed,
            "HTTP/1.1 302 Found\r\nLocation: /opaque-resource\r\n" +
            "Content-Length: 0\r\nConnection: close\r\n\r\n",
            Response(200, Manifest));
        using var fixture = new Fixture(Port(listener), PlaybackRoutingMode.Adaptive);

        var result = (ReadOnlyMemory<byte>)await fixture.Get();
        await server;

        Assert.AreEqual(200, fixture.StatusCode);
        StringAssert.Contains(Encoding.UTF8.GetString(result.Span), "/hls/");
        Assert.HasCount(2, observed);
    }

    [TestMethod]
    public async Task DirectGateWaitAndUpstreamHeaders_ShareOneTimeoutBudget()
    {
        using var listener = Listen();
        using var fixture = new Fixture(Port(listener), PlaybackRoutingMode.Adaptive);
        var scope = GatewayTransport.CreateDirectRouteScope(fixture.Payload, fixture.Ticket);
        var gate = await fixture.Runtime.Gateway!.AcquireDirectRouteGateAsync(
            scope, "GET", null, new Dictionary<string, string>(), CancellationToken.None);
        var releaseServer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await releaseServer.Task;
        });
        var watch = Stopwatch.StartNew();
        var request = fixture.Get();
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(6));
            gate!.Dispose();
            await request.WaitAsync(TimeSpan.FromSeconds(6));
            Assert.AreEqual(504, fixture.StatusCode);
            Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(12), watch.Elapsed.ToString());
            Assert.AreEqual(0, fixture.Runtime.Gateway.ActiveRequests);
            Assert.AreEqual(0, fixture.Runtime.Gateway.SourceBackoffCount);
        }
        finally
        {
            gate?.Dispose();
            releaseServer.TrySetResult(true);
            await server.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [TestMethod]
    public async Task SlowPrefixAndManifest_StopAtTheControlDeadlineDespiteContinuousProgress()
    {
        // Both readers receive a byte every two seconds, well inside the ten-second idle timeout.
        // Neither may keep extending the deadline before the gateway can deliver response headers.
        await Task.WhenAll(CheckSlowControl("application/octet-stream"),
            CheckSlowControl("application/vnd.apple.mpegurl"));

        static async Task CheckSlowControl(string contentType)
        {
            using var listener = Listen();
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var fixture = new Fixture(Port(listener), PlaybackRoutingMode.Adaptive);
            var writes = 0;
            var server = Task.Run(async () =>
            {
                try
                {
                    using var client = await listener.AcceptTcpClientAsync(shutdown.Token);
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync(shutdown.Token))) { }
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\nContent-Type: " + contentType +
                        "\r\nContent-Length: 1024\r\nConnection: close\r\n\r\n"), shutdown.Token);
                    while (true)
                    {
                        await stream.WriteAsync(new byte[] { (byte)'x' }, shutdown.Token);
                        Interlocked.Increment(ref writes);
                        await Task.Delay(TimeSpan.FromSeconds(2), shutdown.Token);
                    }
                }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
                catch (IOException) { } // The gateway closes the deliberately incomplete response on timeout.
            });
            var watch = Stopwatch.StartNew();
            try
            {
                Assert.AreEqual(string.Empty, await fixture.Get().WaitAsync(TimeSpan.FromSeconds(12)));
                Assert.AreEqual(504, fixture.StatusCode, contentType);
                Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(12), contentType);
                Assert.IsTrue(Volatile.Read(ref writes) >= 3, "The upstream must make progress between idle deadlines.");
                Assert.AreEqual(0, fixture.Runtime.Gateway!.ActiveRequests);
                Assert.AreEqual(0, fixture.Runtime.Gateway.SourceBackoffCount);
                Assert.IsFalse(fixture.Headers.ContainsKey("Location"));
            }
            finally
            {
                shutdown.Cancel();
                await server.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    private static TcpListener Listen()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return listener;
    }

    private static int Port(TcpListener listener) => ((IPEndPoint)listener.LocalEndpoint).Port;

    private static string Response(int status, string body, string extra = "") =>
        "HTTP/1.1 " + status + " Response\r\nContent-Type: application/vnd.apple.mpegurl\r\n" +
        "Content-Length: " + Encoding.UTF8.GetByteCount(body) + "\r\nETag: \"origin\"\r\n" +
        "Last-Modified: Sun, 30 Aug 2026 00:00:00 GMT\r\nAccept-Ranges: bytes\r\n" + extra +
        "Connection: close\r\n\r\n" + body;

    private static string RequestPath(string request) =>
        request.Split(new[] { "\r\n" }, StringSplitOptions.None)[0].Split(' ')[1];

    private static async Task Serve(TcpListener listener, List<string> observed, params string[] responses)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        foreach (var response in responses)
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = client.GetStream();
            var bytes = new byte[4096];
            var request = new StringBuilder();
            while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var count = await stream.ReadAsync(bytes, timeout.Token);
                if (count == 0) throw new IOException("The synthetic request ended early.");
                request.Append(Encoding.ASCII.GetString(bytes, 0, count));
            }
            observed.Add(request.ToString());
            await stream.WriteAsync(Encoding.UTF8.GetBytes(response), timeout.Token);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TestWorkspace workspace = new();
        public PluginRuntime Runtime { get; } = new();
        public GatewayService Service { get; }
        public string Ticket { get; }
        public TicketPayload Payload { get; }
        public QueryParamCollection RequestHeaders { get; } = new();
        public Dictionary<string, string> Headers { get; } = new();
        public string Method { get; set; } = "GET";
        public int StatusCode { get; private set; }

        public Fixture(int port, PlaybackRoutingMode mode = PlaybackRoutingMode.RelayOnly,
            PlaybackTicketPurpose purpose = PlaybackTicketPurpose.DirectClient,
            bool sourceRedirectHandoffAllowed = false,
            int directRedirectCacheSeconds = 0,
            int relayConcurrency = 4,
            IPAddress? remoteIp = null, bool audio = false)
        {
            var folder = new Folder { Id = Guid.NewGuid() };
            BaseItem item = audio ? new MediaBrowser.Controller.Entities.Audio.Audio() : new Movie();
            item.Id = Guid.NewGuid();
            item.Path = workspace.Write("source.strm", $"http://127.0.0.1:{port}/first");
            Runtime.Initialize(workspace.Path, new ManualClock());
            Runtime.UpdateOptions(new PluginConfiguration
            {
                Enabled = true,
                PlaybackMode = mode,
                GatewayTimeoutSeconds = 10,
                RelayConcurrency = relayConcurrency,
                DirectRedirectCacheSeconds = directRedirectCacheSeconds,
                IncludedLibraryIds = new[] { folder.Id.ToString("N") },
            }, false);
            Ticket = Runtime.Tickets.IssuePlayback(item.Id, "source", null, Runtime.SourcePolicy!.Read(item.Path),
                purpose, Runtime.Generation,
                purpose == PlaybackTicketPurpose.ExtractionProbe ? TimeSpan.FromSeconds(30) : null,
                sourceRedirectHandoffAllowed: sourceRedirectHandoffAllowed);
            Runtime.Tickets.TryInspect(Ticket, out var payload);
            Payload = payload!;
            var library = TestProxy.Create<ILibraryManager>((method, _) => method.Name switch
            {
                nameof(ILibraryManager.GetItemById) => item,
                nameof(ILibraryManager.GetCollectionFolders) => new[] { folder },
                _ => TestDispatchProxy.DefaultValue(method.ReturnType),
            });
            var authorization = TestProxy.Create<IAuthorizationContext>((method, _) => TestDispatchProxy.DefaultValue(method.ReturnType));
            var results = TestProxy.Create<IHttpResultFactory>((method, args) =>
            {
                if (method.Name != "GetResult") return TestDispatchProxy.DefaultValue(method.ReturnType);
                foreach (var pair in (IEnumerable<KeyValuePair<string, string>>)args![3]!) Headers[pair.Key] = pair.Value;
                return args[1];
            });
            var logger = TestProxy.Create<ILogger>((method, _) => TestDispatchProxy.DefaultValue(method.ReturnType));
            var logs = TestProxy.Create<ILogManager>((method, _) => method.Name == "GetLogger" ? logger : TestDispatchProxy.DefaultValue(method.ReturnType));
            var response = DispatchProxy.Create(typeof(IRequest).GetProperty("Response")!.PropertyType, typeof(TestDispatchProxy));
            ((TestDispatchProxy)response).Handler = (method, args) =>
            {
                if (method.Name == "AddHeader") Headers[(string)args![0]!] = (string)args[1]!;
                if (method.Name == "set_StatusCode") StatusCode = (int)args![0]!;
                return TestDispatchProxy.DefaultValue(method.ReturnType);
            };
            var request = TestProxy.Create<IRequest>((method, _) => method.Name switch
            {
                "get_Response" => response,
                "get_RemoteIp" => remoteIp ?? IPAddress.Loopback,
                "get_HttpMethod" or "get_Verb" => Method,
                "get_Headers" => RequestHeaders,
                _ => TestDispatchProxy.DefaultValue(method.ReturnType),
            });
            Service = new GatewayService(library, authorization, results, logs, Runtime) { Request = request };
        }

        public Task<object> Get() => Service.Get(new GetStrmBridgePlayback { Ticket = Ticket, FileName = "stream" });

        public async Task ActivateFastSeek()
        {
            var logger = TestProxy.Create<ILogger>((method, _) => TestDispatchProxy.DefaultValue(method.ReturnType));
            Runtime.InitializeFastSeek(logger, new SyntheticFastSeekProbeClient());
            var coordinator = Runtime.FastSeek!;
            var options = Runtime.GetOptionsSnapshot();
            var target = TimeSpan.FromSeconds(50).Ticks;
            var duration = TimeSpan.FromSeconds(100).Ticks;
            const string input = "http://127.0.0.1/synthetic-job";
            Assert.IsTrue(await coordinator.PrepareAsync(Payload.Source, Payload.MediaSourceId,
                target, duration, Runtime.Generation, options, CancellationToken.None));
            Assert.IsTrue(coordinator.TryBindInput(Payload.Source, Payload.MediaSourceId,
                target, duration, Runtime.Generation, input, options, ticket: Ticket));
            Assert.IsTrue(coordinator.TryGetBoundPlan(input, Runtime.Generation, target,
                CancellationToken.None, out var plan));
            Assert.IsTrue(coordinator.TryActivateInput(input, plan!, Runtime.Tickets, out _));
        }

        public void Dispose() { Runtime.Dispose(); workspace.Dispose(); }
    }
}
