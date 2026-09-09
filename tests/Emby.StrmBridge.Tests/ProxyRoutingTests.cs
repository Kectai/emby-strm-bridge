using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using Emby.StrmBridge.Configuration;
using Emby.StrmBridge.Playback;
using Emby.StrmBridge.Policy;

namespace Emby.StrmBridge.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ProxyRoutingTests
{
    [TestMethod]
    public async Task AddressTrustChanges_DoNotDiscardTheSelectedProxyConnection()
    {
        using var proxy = new LoopbackServer();
        var rules = new[] { "127.0.0.0/8" };
        var server = Task.Run(async () =>
        {
            using var connection = await proxy.AcceptAsync();
            using var stream = connection.GetStream();
            for (var index = 0; index < 2; index++)
            {
                await ReadHeadersAsync(stream, proxy.Token);
                await stream.WriteAsync(Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Length: 4\r\n\r\ndata"), proxy.Token);
            }
        });
        using var client = new HttpClient(PinnedHttpHandler.Create(
            new RedirectPolicy(() => Volatile.Read(ref rules)), new FixedTestProxy(proxy.Address)));
        Assert.AreEqual("data", await client.GetStringAsync("http://media.invalid/media", proxy.Token));
        Volatile.Write(ref rules, Array.Empty<string>());
        Assert.AreEqual("data", await client.GetStringAsync("http://media.invalid/media", proxy.Token));
        await server;
        Assert.IsFalse(proxy.Pending);
    }

    [TestMethod]
    [DataRow("http://media.invalid/media")]
    [DataRow("http://localhost/media")]
    public async Task ForwardProxy_OwnsTargetResolutionAndPreservesRangeAndHost(string target)
    {
        using var proxy = new LoopbackServer();
        var server = proxy.ServeAsync(PartialResponse);
        using var client = CreateClient(new FixedTestProxy(proxy.Address));
        using var request = new HttpRequestMessage(HttpMethod.Get, target);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(5, 8);
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.AreEqual("data", await response.Content.ReadAsStringAsync());
        var observed = await server;
        StringAssert.StartsWith(observed, "GET " + target + " HTTP/1.1\r\n");
        StringAssert.Contains(observed, "Host: " + new Uri(target).Host + "\r\n");
        StringAssert.Contains(observed, "Range: bytes=5-8\r\n");
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task ProxyBypass_NullOrDestinationResultKeepsDirectDnsProtection(int mode)
    {
        using var origin = new LoopbackServer();
        var target = new Uri($"http://localhost:{origin.Address.Port}/media");
        var proxy = new SelectingProxy(uri => mode == 1 ? null : uri, _ => mode == 0);
        using var client = CreateClient(proxy);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(target));
        Assert.IsNotNull(RedirectPolicy.FindRejection(error));
        Assert.IsFalse(origin.Pending);
    }

    [TestMethod]
    public async Task ProxyFailure_DoesNotFallBackToDirectOrigin()
    {
        using var origin = new LoopbackServer();
        using var closedProxy = new LoopbackServer();
        var address = closedProxy.Address;
        closedProxy.Dispose();
        using var client = CreateClient(new FixedTestProxy(address));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(origin.Address));
        Assert.IsFalse(origin.Pending);
    }

    [TestMethod]
    public async Task ConcurrentProxyRequests_KeepTheirOwnRanges()
    {
        using var proxy = new LoopbackServer();
        const int count = 48;
        var server = Task.Run(async () =>
        {
            var work = new List<Task>();
            for (var i = 0; i < count; i++)
            {
                var connection = await proxy.AcceptAsync();
                work.Add(RespondAsync(connection));
            }
            await Task.WhenAll(work);
        });
        async Task RespondAsync(TcpClient connection)
        {
            using (connection)
            {
                using var stream = connection.GetStream();
                var request = await ReadHeadersAsync(stream, proxy.Token);
                var value = request.Split("\r\n").Single(line => line.StartsWith("Range: bytes=", StringComparison.Ordinal))[13..];
                var body = Encoding.ASCII.GetBytes(value);
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), proxy.Token);
                await stream.WriteAsync(body, proxy.Token);
            }
        }
        using var client = CreateClient(new FixedTestProxy(proxy.Address));
        await Task.WhenAll(Enumerable.Range(0, count).Select(async i =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "http://media.invalid/media");
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(i, i);
            using var response = await client.SendAsync(request, proxy.Token);
            Assert.AreEqual($"{i}-{i}", await response.Content.ReadAsStringAsync());
        }));
        await server;
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ProxyLookupFailure_DoesNotBecomeAnUnvalidatedDirectRequest(bool failBypass)
    {
        using var origin = new LoopbackServer();
        var proxy = new SelectingProxy(_ => throw new InvalidOperationException("synthetic lookup failure"),
            _ => failBypass ? throw new InvalidOperationException("synthetic bypass failure") : false);
        using var client = CreateClient(proxy);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(origin.Address));
        Assert.IsFalse(origin.Pending);
    }

    [TestMethod]
    public async Task SelectedProxy_IsPinnedForTheRequest_AndPooledForFollowingRequests()
    {
        using var proxy = new LoopbackServer();
        var lookups = 0;
        var selection = new SelectingProxy(_ =>
        {
            Interlocked.Increment(ref lookups);
            return proxy.Address;
        }, _ => false);
        var server = Task.Run(async () =>
        {
            using var connection = await proxy.AcceptAsync();
            using var stream = connection.GetStream();
            for (var i = 0; i < 2; i++)
            {
                await ReadHeadersAsync(stream, proxy.Token);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 4\r\n\r\ndata"), proxy.Token);
            }
        });
        using var client = CreateClient(selection);
        for (var i = 0; i < 2; i++) Assert.AreEqual("data", await client.GetStringAsync("http://media.invalid/media"));
        await server;
        Assert.AreEqual(2, lookups);
        Assert.IsFalse(proxy.Pending);
    }

    [TestMethod]
    public async Task ProxyCredentials_AreUsedForProxyAuthenticationOnly()
    {
        using var proxy = new LoopbackServer();
        using var origin = new LoopbackServer();
        var selection = new SelectingProxy(_ => proxy.Address, uri => uri.Host == origin.Address.Host)
        {
            Credentials = new NetworkCredential("synthetic-user", "synthetic-password"),
        };
        var server = Task.Run(async () =>
        {
            await proxy.ServeAsync("HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"synthetic\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            return await proxy.ServeAsync(PartialResponse);
        });
        using var client = CreateClient(selection);
        Assert.AreEqual("data", await client.GetStringAsync("http://media.invalid/media"));
        StringAssert.Contains(await server, "Proxy-Authorization: Basic " +
            Convert.ToBase64String(Encoding.ASCII.GetBytes("synthetic-user:synthetic-password")));
        var direct = origin.ServeAsync(PartialResponse);
        Assert.AreEqual("data", await client.GetStringAsync(origin.Address));
        Assert.IsFalse((await direct).Contains("Authorization:", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task HttpsProxy_UsesConnectAndStartsTls()
    {
        using var proxy = new LoopbackServer();
        var observed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            using var connection = await proxy.AcceptAsync();
            using var stream = connection.GetStream();
            observed.TrySetResult(await ReadHeadersAsync(stream, proxy.Token));
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"), proxy.Token);
            var record = await ReadBytesAsync(stream, 5, proxy.Token);
            Assert.AreEqual(22, (int)record[0]);
            var hello = await ReadBytesAsync(stream, (record[3] << 8) | record[4], proxy.Token);
            Assert.AreEqual(1, (int)hello[0]);
            await stream.WriteAsync(new byte[] { 21, 3, 3, 0, 2, 2, 40 }, proxy.Token);
        });
        using var client = CreateClient(new FixedTestProxy(proxy.Address));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://media.invalid/media"));
        Assert.IsTrue(ExceptionChain(error).Any(e => e is AuthenticationException));
        StringAssert.StartsWith(await observed.Task.WaitAsync(proxy.Token), "CONNECT media.invalid:443 HTTP/1.1\r\n");
        await server;
    }

    [TestMethod]
    public void NativeProxyTransport_KeepsTlsValidationRedirectAndCookieDefaults()
    {
        using var transport = (SocketsHttpHandler)PinnedHttpHandler.CreateTransport(new FixedTestProxy(new Uri("http://localhost:1")));
        Assert.IsNull(transport.ConnectCallback);
        Assert.IsNull(transport.SslOptions.RemoteCertificateValidationCallback);
        Assert.IsTrue(transport.UseProxy);
        Assert.IsFalse(transport.AllowAutoRedirect);
        Assert.IsFalse(transport.UseCookies);
        Assert.AreEqual(DecompressionMethods.None, transport.AutomaticDecompression);
    }

    [TestMethod]
    public async Task SocksProxy_ReceivesDomainNameAndReturnsMediaBytes()
    {
        using var proxy = new LoopbackServer();
        var server = Task.Run(async () =>
        {
            using var connection = await proxy.AcceptAsync();
            using var stream = connection.GetStream();
            var greeting = await ReadBytesAsync(stream, 2, proxy.Token);
            Assert.AreEqual(5, (int)greeting[0]);
            await ReadBytesAsync(stream, greeting[1], proxy.Token);
            await stream.WriteAsync(new byte[] { 5, 0 }, proxy.Token);
            var header = await ReadBytesAsync(stream, 5, proxy.Token);
            CollectionAssert.AreEqual(new byte[] { 5, 1, 0, 3 }, header.Take(4).ToArray());
            var host = Encoding.ASCII.GetString(await ReadBytesAsync(stream, header[4], proxy.Token));
            await ReadBytesAsync(stream, 2, proxy.Token);
            await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 80 }, proxy.Token);
            var request = await ReadHeadersAsync(stream, proxy.Token);
            await stream.WriteAsync(Encoding.ASCII.GetBytes(PartialResponse), proxy.Token);
            return (host, request);
        });
        using var client = CreateClient(new FixedTestProxy(new Uri($"socks5://127.0.0.1:{proxy.Address.Port}")));
        using var response = await client.GetAsync("http://media.invalid/media");
        Assert.AreEqual("data", await response.Content.ReadAsStringAsync());
        var observed = await server;
        Assert.AreEqual("media.invalid", observed.host);
        StringAssert.StartsWith(observed.request, "GET /media HTTP/1.1\r\n");
    }

    [TestMethod]
    public async Task ProxyRequest_CancellationClosesPendingConnection()
    {
        using var proxy = new LoopbackServer();
        var accepted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            using var connection = await proxy.AcceptAsync();
            using var stream = connection.GetStream();
            await ReadHeadersAsync(stream, proxy.Token);
            accepted.TrySetResult(true);
            return await stream.ReadAsync(new byte[1], proxy.Token);
        });
        using var client = CreateClient(new FixedTestProxy(proxy.Address));
        using var cancel = new CancellationTokenSource();
        var response = client.GetAsync("http://media.invalid/media", cancel.Token);
        await accepted.Task.WaitAsync(proxy.Token);
        cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await response);
        Assert.AreEqual(0, await server);
    }

    [TestMethod]
    public async Task DefaultSystemProxy_IsUsedByGateway_WithRedirectTrustIntact()
    {
        using var proxy = new LoopbackServer();
        var previous = HttpClient.DefaultProxy;
        HttpClient.DefaultProxy = new FixedTestProxy(proxy.Address);
        try
        {
            var redirect = proxy.ServeAsync("HTTP/1.1 302 Found\r\nLocation: http://denied.invalid/media\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            var detected = new List<string>();
            using var gateway = new GatewayTransport(new RedirectPolicy(untrustedHostObserver: detected.Add));
            await Assert.ThrowsExactlyAsync<RedirectRejectedException>(() => gateway.OpenAsync(new Uri("http://source.invalid/media"),
                "GET", "Synthetic/1", new Dictionary<string, string>(), new PluginConfiguration(), proxy.Token));
            CollectionAssert.AreEqual(new[] { "denied.invalid" }, detected);
            await redirect;
            Assert.AreEqual(0, gateway.ActiveRequests);
            Assert.IsFalse(proxy.Pending);
        }
        finally
        {
            HttpClient.DefaultProxy = previous;
        }
    }

    private static HttpClient CreateClient(IWebProxy proxy) =>
        new(PinnedHttpHandler.Create(new RedirectPolicy(), proxy)) { Timeout = TimeSpan.FromSeconds(8) };

    private const string PartialResponse = "HTTP/1.1 206 Partial Content\r\nContent-Range: bytes 5-8/10\r\nContent-Length: 4\r\nConnection: close\r\n\r\ndata";

    private static IEnumerable<Exception> ExceptionChain(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException) yield return current;
    }

    private static async Task<byte[]> ReadBytesAsync(Stream stream, int count, CancellationToken token)
    {
        var data = new byte[count];
        var offset = 0;
        while (offset < data.Length)
        {
            var read = await stream.ReadAsync(data.AsMemory(offset), token);
            if (read == 0) throw new IOException("Synthetic connection ended early.");
            offset += read;
        }
        return data;
    }

    private static async Task<string> ReadHeadersAsync(Stream stream, CancellationToken token)
    {
        var data = new StringBuilder();
        while (data.Length < 16384)
        {
            data.Append((char)(await ReadBytesAsync(stream, 1, token))[0]);
            if (data.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal)) return data.ToString();
        }
        throw new IOException("Synthetic headers exceeded their budget.");
    }

    private sealed class LoopbackServer : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource timeout = new(TimeSpan.FromSeconds(12));

        public LoopbackServer()
        {
            listener.Start();
            Address = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
        }

        public Uri Address { get; }
        public CancellationToken Token => timeout.Token;
        public bool Pending => listener.Pending();
        public ValueTask<TcpClient> AcceptAsync() => listener.AcceptTcpClientAsync(Token);

        public async Task<string> ServeAsync(string response)
        {
            using var client = await AcceptAsync();
            using var stream = client.GetStream();
            var request = await ReadHeadersAsync(stream, Token);
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response), Token);
            return request;
        }

        public void Dispose()
        {
            listener.Stop();
            timeout.Dispose();
        }
    }

    private sealed class FixedTestProxy(Uri address) : IWebProxy
    {
        public ICredentials? Credentials { get; set; }
        public Uri GetProxy(Uri destination) => address;
        public bool IsBypassed(Uri host) => false;
    }

    private sealed class SelectingProxy(Func<Uri, Uri?> select, Func<Uri, bool> bypass) : IWebProxy
    {
        public ICredentials? Credentials { get; set; }
        public Uri? GetProxy(Uri destination) => select(destination);
        public bool IsBypassed(Uri host) => bypass(host);
    }
}
