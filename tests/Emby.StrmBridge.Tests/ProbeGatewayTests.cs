using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Emby.StrmBridge.Api;
using Emby.StrmBridge.Configuration;
using Emby.StrmBridge.Domain;
using Emby.StrmBridge.Playback;
using Emby.StrmBridge.Policy;
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
public sealed class ProbeGatewayTests
{
    [TestMethod]
    [DataRow(PlaybackRoutingMode.Native)]
    [DataRow(PlaybackRoutingMode.RedirectOnly)]
    [DataRow(PlaybackRoutingMode.Adaptive)]
    public async Task Probe_AlwaysRelaysActualBytesThroughEveryRedirect(PlaybackRoutingMode mode)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = ServeAsync(listener, new[] { Redirect("/second"), Redirect("/final"),
            "HTTP/1.1 200 OK\r\nContent-Length: 4\r\nConnection: close\r\n\r\ndata" });
        using var fixture = new Fixture(port, mode);
        var result = await fixture.Service.Get(new GetStrmBridgePlayback { Ticket = fixture.Ticket, FileName = "stream" });
        using var stream = (Stream)result;
        using var reader = new StreamReader(stream);
        Assert.AreEqual("data", await reader.ReadToEndAsync());
        Assert.IsFalse(fixture.Headers.ContainsKey("Location"));
        Assert.AreEqual(3, await server.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task Probe_RejectsAndReportsAnUntrustedSecondHop()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = ServeAsync(listener, new[] { Redirect("/second"), Redirect("https://denied.invalid/private?secret=synthetic") });
        using var fixture = new Fixture(port, PlaybackRoutingMode.RedirectOnly);
        Assert.IsTrue(fixture.Runtime.Tickets.TryInspect(fixture.Ticket, out var payload));
        await Assert.ThrowsExactlyAsync<ResourceNotFoundException>(() =>
            fixture.Service.Get(new GetStrmBridgePlayback { Ticket = fixture.Ticket, FileName = "stream" }));
        Assert.AreEqual((int)RedirectRejectionReason.UntrustedTargetHost, payload!.ProbeRejectionReason);
        CollectionAssert.AreEqual(new[] { "denied.invalid" }, fixture.Runtime.GetDetectedRedirectHosts());
        Assert.AreEqual(2, await server.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task ProbeTicket_IsRejectedOutsideLoopbackBeforeAnyUpstreamRequest()
    {
        using var fixture = new Fixture(1, PlaybackRoutingMode.Native, IPAddress.Parse("192.0.2.1"));
        await Assert.ThrowsExactlyAsync<ResourceNotFoundException>(() =>
            fixture.Service.Get(new GetStrmBridgePlayback { Ticket = fixture.Ticket, FileName = "stream" }));
        Assert.AreEqual(0, fixture.Runtime.Tickets.Count);
        Assert.AreEqual(0, fixture.Runtime.Gateway!.ActiveRequests);
    }

    [TestMethod]
    public async Task UpstreamConnectionFailure_IsReportedAsBadGatewayInsteadOfMissingMedia()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var fixture = new Fixture(port, PlaybackRoutingMode.Adaptive);
        var result = await fixture.Service.Get(new GetStrmBridgePlayback { Ticket = fixture.Ticket, FileName = "stream" });
        Assert.AreEqual(string.Empty, result);
        Assert.AreEqual(502, fixture.StatusCode);
        Assert.AreEqual(0, fixture.Runtime.Gateway!.ActiveRequests);
    }

    [TestMethod]
    public async Task DnsTrustRejection_IsReportedWithoutExposingNetworkDetails()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var fixture = new Fixture(((IPEndPoint)listener.LocalEndpoint).Port, PlaybackRoutingMode.Adaptive,
            sourceHost: "localhost");
        Assert.IsTrue(fixture.Runtime.Tickets.TryInspect(fixture.Ticket, out var payload));
        var result = await fixture.Service.Get(new GetStrmBridgePlayback { Ticket = fixture.Ticket, FileName = "stream" });
        Assert.AreEqual(string.Empty, result);
        Assert.AreEqual(403, fixture.StatusCode);
        Assert.AreEqual((int)RedirectRejectionReason.UntrustedTargetHost, payload!.ProbeRejectionReason);
        Assert.IsFalse(listener.Pending());
    }

    private static string Redirect(string target) =>
        "HTTP/1.1 303 See Other\r\nLocation: " + target + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";

    private static async Task<int> ServeAsync(TcpListener listener, string[] responses)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
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
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response), timeout.Token);
        }
        return responses.Length;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TestWorkspace workspace = new();
        public PluginRuntime Runtime { get; } = new();
        public GatewayService Service { get; }
        public string Ticket { get; }
        public Dictionary<string, string> Headers { get; } = new();
        public int StatusCode { get; private set; }

        public Fixture(int port, PlaybackRoutingMode mode, IPAddress? remote = null, string sourceHost = "127.0.0.1")
        {
            var folder = new Folder { Id = Guid.NewGuid() };
            var item = new Movie { Id = Guid.NewGuid(), Path = workspace.Write("probe.strm", $"http://{sourceHost}:{port}/first") };
            Runtime.Initialize(workspace.Path, new ManualClock());
            Runtime.UpdateOptions(new PluginConfiguration
            {
                Enabled = true,
                PlaybackMode = mode,
                IncludedLibraryIds = new[] { folder.Id.ToString("N") },
            }, false);
            Ticket = Runtime.Tickets.IssuePlayback(item.Id, "probe", null, Runtime.SourcePolicy!.Read(item.Path),
                PlaybackTicketPurpose.ExtractionProbe, Runtime.Generation, TimeSpan.FromSeconds(30));
            var library = TestProxy.Create<ILibraryManager>((method, _) => method.Name switch
            {
                nameof(ILibraryManager.GetItemById) => item,
                nameof(ILibraryManager.GetCollectionFolders) => new[] { folder },
                _ => TestDispatchProxy.DefaultValue(method.ReturnType),
            });
            var authorization = TestProxy.Create<IAuthorizationContext>((method, _) => TestDispatchProxy.DefaultValue(method.ReturnType));
            var results = TestProxy.Create<IHttpResultFactory>((method, args) => method.Name == "GetResult"
                ? args![1] : TestDispatchProxy.DefaultValue(method.ReturnType));
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
                "get_RemoteIp" => remote ?? IPAddress.Loopback,
                "get_HttpMethod" => "GET",
                _ => TestDispatchProxy.DefaultValue(method.ReturnType),
            });
            Service = new GatewayService(library, authorization, results, logs, Runtime) { Request = request };
        }

        public void Dispose()
        {
            Runtime.Dispose();
            workspace.Dispose();
        }
    }
}
