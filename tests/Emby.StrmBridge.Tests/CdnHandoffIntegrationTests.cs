using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Emby.StrmBridge.Api;
using Emby.StrmBridge.Configuration;
using Emby.StrmBridge.Domain;
using Emby.StrmBridge.Playback;
using Emby.StrmBridge.Runtime;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;

namespace Emby.StrmBridge.Tests;

[TestClass]
public sealed class CdnHandoffIntegrationTests
{
    [TestMethod]
    public async Task DirectClient_RepeatedRangesResolveFreshOneUseLocationsWithoutServerCdnReads()
    {
        await using var cdn = new SyntheticCdn();
        using var fixture = new GatewayFixture(cdn.Origin);
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SyntheticPlayer/1.0");

        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (attempt == 4)
            {
                fixture.Clock.Advance(TimeSpan.FromSeconds(31));
                cdn.Version = 2;
            }
            var start = attempt * 47;
            var end = start + 31;
            var beforeGateway = cdn.RequestCount;
            var redirect = await fixture.OpenAsync(start, end);
            var afterGateway = cdn.RequestCount;
            Assert.AreEqual(302, redirect.Status);
            Assert.AreEqual("private, no-store", redirect.Headers["Cache-Control"]);
            Assert.AreEqual(string.Empty, redirect.Body, "The gateway must not return the media body.");
            Assert.AreEqual(0, fixture.Runtime.Gateway!.ActiveRequests);
            Assert.AreEqual(
                1,
                afterGateway - beforeGateway,
                "A direct-file handoff must stop at the redirect origin and leave the one-use CDN URL to the player.");
            if (attempt == 4)
                StringAssert.Contains(redirect.Headers["Location"], "version=2");

            var directBytes = await ReadRangeAsync(client, new Uri(redirect.Headers["Location"]), start, end);
            var referenceBytes = await ReadRangeAsync(client, new Uri(cdn.Origin, "/reference"), start, end);
            CollectionAssert.AreEqual(referenceBytes, directBytes,
                "The plugin handoff must deliver the same range as a plain HTTP redirect.");
            CollectionAssert.AreEqual(cdn.Body.Skip(start).Take(end - start + 1).ToArray(), directBytes);
        }
    }

    [TestMethod]
    public async Task DirectClient_UnknownFileRefreshesItsFirstOneUseLocationBeforeClientHandoff()
    {
        await using var cdn = new SyntheticCdn();
        using var fixture = new GatewayFixture(cdn.Origin, knownFile: false);
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SyntheticPlayer/1.0");

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var start = attempt * 47;
            var end = start + 31;
            var beforeGateway = cdn.RequestCount;
            var redirect = await fixture.OpenAsync(start, end);
            var gatewayRequests = cdn.RequestCount - beforeGateway;

            Assert.AreEqual(302, redirect.Status);
            Assert.AreEqual("private, no-store", redirect.Headers["Cache-Control"]);
            Assert.AreEqual(attempt == 0 ? 3 : 1, gatewayRequests,
                "The first unknown range may classify one CDN response but must refresh the source before handoff; later ranges use only the source first hop.");
            var directBytes = await ReadRangeAsync(client, new Uri(redirect.Headers["Location"]), start, end);
            CollectionAssert.AreEqual(cdn.Body.Skip(start).Take(end - start + 1).ToArray(), directBytes);
            Assert.AreEqual(1, fixture.Runtime.Gateway!.DirectRouteCount);
            Assert.AreEqual(0, fixture.Runtime.Gateway.RedirectLeaseCount);
        }
    }

    [TestMethod]
    public async Task DirectClient_ConfiguredWindowReusesOnlyTheUnopenedValidatedLocation()
    {
        await using var cdn = new SyntheticCdn(singleUseLocations: false);
        using var fixture = new GatewayFixture(cdn.Origin, directRedirectCacheSeconds: 20);
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SyntheticPlayer/1.0");

        var first = await fixture.OpenAsync(0, 31);
        var second = await fixture.OpenAsync(64, 95);

        Assert.AreEqual(302, first.Status);
        Assert.AreEqual("max-age=20, private, must-revalidate", first.Headers["Cache-Control"]);
        Assert.IsTrue(first.Headers.ContainsKey("Expires"));
        Assert.AreEqual("User-Agent, Accept, Accept-Language", first.Headers["Vary"]);
        Assert.AreEqual(first.Headers["Location"], second.Headers["Location"]);
        Assert.AreEqual("max-age=20, private, must-revalidate", second.Headers["Cache-Control"]);
        Assert.AreEqual("User-Agent, Accept, Accept-Language", second.Headers["Vary"]);
        Assert.AreEqual(1, cdn.RequestCount,
            "The gateway must resolve the source once and must not open the cached CDN target.");

        var firstBytes = await ReadRangeAsync(client, new Uri(first.Headers["Location"]), 0, 31);
        var secondBytes = await ReadRangeAsync(client, new Uri(second.Headers["Location"]), 64, 95);
        CollectionAssert.AreEqual(cdn.Body.Take(32).ToArray(), firstBytes);
        CollectionAssert.AreEqual(cdn.Body.Skip(64).Take(32).ToArray(), secondBytes);

        fixture.Clock.Advance(TimeSpan.FromSeconds(21));
        var refreshed = await fixture.OpenAsync(128, 159);
        Assert.AreNotEqual(first.Headers["Location"], refreshed.Headers["Location"]);
        Assert.AreEqual(4, cdn.RequestCount,
            "Expiry must resolve one fresh origin Location without reading the new CDN target.");
    }

    private static async Task<byte[]> ReadRangeAsync(HttpClient client, Uri uri, int start, int end)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Range = new RangeHeaderValue(start, end);
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.AreEqual((long)start, response.Content.Headers.ContentRange!.From);
        Assert.AreEqual((long)end, response.Content.Headers.ContentRange.To);
        return await response.Content.ReadAsByteArrayAsync();
    }

    private sealed class GatewayFixture : IDisposable
    {
        private readonly TestWorkspace workspace = new();
        private readonly ILibraryManager library;
        private readonly IAuthorizationContext authorization;
        private readonly ILogManager logs;
        private readonly IHttpResultFactory results;
        private readonly string ticket;

        public ManualClock Clock { get; } = new();
        public PluginRuntime Runtime { get; } = new();

        public GatewayFixture(Uri origin, bool knownFile = true, int directRedirectCacheSeconds = 0)
        {
            var folder = new Folder { Id = Guid.NewGuid() };
            var item = new Movie { Id = Guid.NewGuid(), Path = workspace.Write("direct.strm", origin.AbsoluteUri) };
            Runtime.Initialize(workspace.Path, Clock);
            Runtime.UpdateOptions(new PluginConfiguration
            {
                Enabled = true,
                PlaybackMode = PlaybackRoutingMode.Adaptive,
                DirectRedirectCacheSeconds = directRedirectCacheSeconds,
                IncludedLibraryIds = new[] { folder.Id.ToString("N") },
            }, false);
            ticket = Runtime.Tickets.IssuePlayback(item.Id, "file", null, Runtime.SourcePolicy!.Read(item.Path),
                PlaybackTicketPurpose.DirectClient, Runtime.Generation,
                sourceRedirectHandoffAllowed: knownFile);
            library = TestProxy.Create<ILibraryManager>((method, _) => method.Name switch
            {
                nameof(ILibraryManager.GetItemById) => item,
                nameof(ILibraryManager.GetCollectionFolders) => new[] { folder },
                _ => TestDispatchProxy.DefaultValue(method.ReturnType),
            });
            authorization = TestProxy.Create<IAuthorizationContext>((method, _) => TestDispatchProxy.DefaultValue(method.ReturnType));
            results = TestProxy.Create<IHttpResultFactory>((method, args) => method.Name == "GetResult"
                ? args![1] : TestDispatchProxy.DefaultValue(method.ReturnType));
            var logger = TestProxy.Create<ILogger>((method, _) => TestDispatchProxy.DefaultValue(method.ReturnType));
            logs = TestProxy.Create<ILogManager>((method, _) => method.Name == "GetLogger"
                ? logger : TestDispatchProxy.DefaultValue(method.ReturnType));
        }

        public async Task<(int Status, Dictionary<string, string> Headers, object Body)> OpenAsync(int start, int end)
        {
            var status = 0;
            var responseHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var response = DispatchProxy.Create(typeof(IRequest).GetProperty("Response")!.PropertyType, typeof(TestDispatchProxy));
            ((TestDispatchProxy)response).Handler = (method, args) =>
            {
                if (method.Name == "set_StatusCode") status = (int)args![0]!;
                if (method.Name == "AddHeader") responseHeaders[(string)args![0]!] = (string)args[1]!;
                return TestDispatchProxy.DefaultValue(method.ReturnType);
            };
            var headersType = typeof(IRequest).GetProperty("Headers")!.PropertyType;
            var headers = Activator.CreateInstance(headersType);
            var indexer = headersType.GetProperty("Item", new[] { typeof(string) })!;
            indexer.SetValue(headers, $"bytes={start}-{end}", new object[] { "Range" });
            indexer.SetValue(headers, "SyntheticPlayer/1.0", new object[] { "User-Agent" });
            var request = TestProxy.Create<IRequest>((method, _) => method.Name switch
            {
                "get_Response" => response,
                "get_RemoteIp" => IPAddress.Loopback,
                "get_HttpMethod" => "GET",
                "get_Headers" => headers,
                _ => TestDispatchProxy.DefaultValue(method.ReturnType),
            });
            var service = new GatewayService(library, authorization, results, logs, Runtime) { Request = request };
            var body = await service.Get(new GetStrmBridgePlayback { Ticket = ticket, FileName = "stream.ts" });
            return (status, responseHeaders, body);
        }

        public void Dispose()
        {
            Runtime.Dispose();
            workspace.Dispose();
        }
    }

    private sealed class SyntheticCdn : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource shutdown = new(TimeSpan.FromSeconds(15));
        private readonly Task server;
        private int version = 1;
        private int requests;
        private int issuedLocations;
        private readonly bool singleUseLocations;
        private readonly HashSet<string> consumedLocations = new(StringComparer.Ordinal);

        public byte[] Body { get; } = Enumerable.Range(0, 1024).Select(value => (byte)(value % 251)).ToArray();
        public Uri Origin { get; }
        public int RequestCount => Volatile.Read(ref requests);
        public int Version { set => Volatile.Write(ref version, value); }

        public SyntheticCdn(bool singleUseLocations = true)
        {
            this.singleUseLocations = singleUseLocations;
            listener.Start();
            Origin = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/source");
            server = ServeAsync();
        }

        private async Task ServeAsync()
        {
            try
            {
                while (true)
                {
                    using var connection = await listener.AcceptTcpClientAsync(shutdown.Token);
                    using var stream = connection.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                    var firstLine = await reader.ReadLineAsync(shutdown.Token) ?? throw new IOException();
                    var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    string? line;
                    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(shutdown.Token)))
                    {
                        var colon = line.IndexOf(':');
                        if (colon > 0) fields[line[..colon]] = line[(colon + 1)..].Trim();
                    }
                    Interlocked.Increment(ref requests);
                    var path = firstLine.Split(' ')[1];
                    var currentVersion = Volatile.Read(ref version);
                    if (path is "/source" or "/reference")
                    {
                        var nonce = Interlocked.Increment(ref issuedLocations).ToString();
                        await SendAsync(stream,
                            $"302 Found\r\nLocation: /cdn?version={currentVersion}&nonce={nonce}\r\nContent-Length: 0",
                            Array.Empty<byte>());
                        continue;
                    }
                    if (!path.StartsWith($"/cdn?version={currentVersion}&nonce=", StringComparison.Ordinal) ||
                        singleUseLocations && !consumedLocations.Add(path))
                    {
                        await SendAsync(stream, "403 Forbidden\r\nContent-Length: 0", Array.Empty<byte>());
                        continue;
                    }
                    var range = RangeHeaderValue.Parse(fields["Range"]).Ranges.Single();
                    var start = checked((int)range.From!.Value);
                    var end = checked((int)range.To!.Value);
                    var bytes = Body.Skip(start).Take(end - start + 1).ToArray();
                    await SendAsync(stream,
                        $"206 Partial Content\r\nContent-Type: video/mp2t\r\nETag: \"stable\"\r\nContent-Range: bytes {start}-{end}/{Body.Length}\r\nContent-Length: {bytes.Length}", bytes);
                }
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        }

        private async Task SendAsync(Stream stream, string headers, byte[] body)
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 " + headers + "\r\nConnection: close\r\n\r\n"), shutdown.Token);
            await stream.WriteAsync(body, shutdown.Token);
        }

        public async ValueTask DisposeAsync()
        {
            shutdown.Cancel();
            await server;
            listener.Stop();
            shutdown.Dispose();
        }
    }
}
