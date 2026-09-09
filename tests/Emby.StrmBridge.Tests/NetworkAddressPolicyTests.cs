using System.Net;
using System.Net.Sockets;
using System.Text;
using Emby.StrmBridge.Playback;
using Emby.StrmBridge.Policy;

namespace Emby.StrmBridge.Tests;

[TestClass]
public sealed class NetworkAddressPolicyTests
{
    [TestMethod]
    public async Task TrustExpansion_RetiresOldPoolWithoutInterruptingItsActiveResponseBody()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var target = new Uri($"http://localhost:{((IPEndPoint)listener.LocalEndpoint).Port}/media");
        var rules = new[] { "127.0.0.0/8", "::1" };
        using var client = new HttpClient(PinnedHttpHandler.Create(
            new RedirectPolicy(() => Volatile.Read(ref rules)), new WebProxy()));
        var server = Task.Run(async () =>
        {
            using var first = await listener.AcceptTcpClientAsync(timeout.Token);
            using var firstStream = first.GetStream();
            await ReadHeadersAsync(firstStream, timeout.Token);
            await firstStream.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Length: 9\r\n\r\nleft"), timeout.Token);
            using var second = await listener.AcceptTcpClientAsync(timeout.Token);
            using var secondStream = second.GetStream();
            await ReadHeadersAsync(secondStream, timeout.Token);
            await secondStream.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n"), timeout.Token);
            await firstStream.WriteAsync(Encoding.ASCII.GetBytes("right"), timeout.Token);
            return await firstStream.ReadAsync(new byte[1], timeout.Token);
        });

        using var firstResponse = await client.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Volatile.Write(ref rules, new[] { "127.0.0.0/8", "::1", "new.invalid" });
        using var secondResponse = await client.GetAsync(target, timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.AreEqual("leftright", await firstResponse.Content.ReadAsStringAsync(timeout.Token));
        Assert.AreEqual(0, await server, "The retired connection should close after its active body finishes.");
    }

    [TestMethod]
    public async Task TrustRevocation_ClosesOldKeepAlivePoolBeforeTheNextRequest()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var target = new Uri($"http://localhost:{((IPEndPoint)listener.LocalEndpoint).Port}/media");
        var rules = new[] { "127.0.0.0/8", "::1" };
        var policy = new RedirectPolicy(() => Volatile.Read(ref rules));
        using var client = new HttpClient(PinnedHttpHandler.Create(policy, new WebProxy()));
        var server = Task.Run(async () =>
        {
            using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = connection.GetStream();
            for (var index = 0; index < 2; index++)
            {
                await ReadHeadersAsync(stream, timeout.Token);
                await stream.WriteAsync(Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n"), timeout.Token);
            }
            return await stream.ReadAsync(new byte[1], timeout.Token);
        });

        async Task SendAsync()
        {
            using var response = await client.GetAsync(target, timeout.Token);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        await SendAsync();
        // Equivalent rule order must retain the connection rather than opening another socket.
        Volatile.Write(ref rules, new[] { "::1", "127.0.0.0/8" });
        await SendAsync();
        Volatile.Write(ref rules, Array.Empty<string>());
        var error = await Assert.ThrowsAsync<HttpRequestException>(SendAsync);
        Assert.AreEqual(RedirectRejectionReason.UntrustedTargetHost, RedirectPolicy.FindRejection(error)?.Reason);
        Assert.AreEqual(0, await server, "The old connection received a request after its address trust was revoked.");
        Assert.IsFalse(listener.Pending(), "Revoked trust must be rejected before opening another connection.");
    }

    [TestMethod]
    public async Task ConnectionCallback_RejectsUnapprovedPrivateDnsBeforeConnecting()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = new HttpClient(PinnedHttpHandler.Create(new RedirectPolicy(), new WebProxy())) { Timeout = TimeSpan.FromSeconds(5) };
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync($"http://localhost:{port}/media"));
        Assert.IsNotNull(RedirectPolicy.FindRejection(exception));
        Assert.IsFalse(listener.Pending());
    }

    [TestMethod]
    public async Task ConnectionCallback_UsesApprovedDnsAddressesAndPreservesHost()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var server = Task.Run(async () =>
        {
            using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = connection.GetStream();
            var data = new byte[4096];
            var request = new StringBuilder();
            while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var read = await stream.ReadAsync(data, timeout.Token);
                if (read == 0) throw new IOException();
                request.Append(Encoding.ASCII.GetString(data, 0, read));
            }
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), timeout.Token);
            return request.ToString();
        });
        using var handler = (SocketsHttpHandler)PinnedHttpHandler.CreateDirect(new RedirectPolicy(() => new[] { "127.0.0.0/8", "::1" }));
        Assert.IsNull(handler.SslOptions.RemoteCertificateValidationCallback);
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using var response = await client.GetAsync($"http://localhost:{port}/media", timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains($"Host: localhost:{port}", await server);
    }

    [TestMethod]
    [DataRow("127.0.0.1")]
    [DataRow("10.1.2.3")]
    [DataRow("169.254.169.254")]
    [DataRow("192.168.1.2")]
    [DataRow("::1")]
    [DataRow("::ffff:127.0.0.1")]
    [DataRow("fd00::1")]
    [DataRow("fe80::1")]
    public void WildcardHostTrustDoesNotAuthorizePrivateDnsTargets(string value)
    {
        var detected = new List<string>();
        var policy = new RedirectPolicy(() => new[] { "*.example.invalid" }, detected.Add);
        var target = new Uri("https://cdn.example.invalid/media");
        var address = IPAddress.Parse(value);
        Assert.ThrowsExactly<RedirectRejectedException>(() => policy.ValidateAddress(target, address));
        Assert.HasCount(1, detected);
        Assert.IsFalse(detected[0].Contains('/'));
    }

    [TestMethod]
    public void PrivateDnsRequiresExplicitAddressApprovalWhileLiteralSourcesRemainUsable()
    {
        var policy = new RedirectPolicy(() => new[] { "10.0.0.0/8", "::1" });
        policy.ValidateAddress(new Uri("http://library.invalid/media"), IPAddress.Parse("10.2.3.4"));
        policy.ValidateAddress(new Uri("http://library.invalid/media"), IPAddress.IPv6Loopback);
        new RedirectPolicy().ValidateAddress(new Uri("http://127.0.0.1/media"), IPAddress.Loopback);
        Assert.ThrowsExactly<RedirectRejectedException>(() =>
            policy.ValidateAddress(new Uri("http://library.invalid/media"), IPAddress.Parse("192.168.1.2")));
    }

    private static async Task ReadHeadersAsync(Stream stream, CancellationToken cancellationToken)
    {
        var data = new byte[1];
        var header = new StringBuilder();
        while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (header.Length >= 16384 || await stream.ReadAsync(data, cancellationToken) == 0)
                throw new IOException("Synthetic HTTP request ended before its headers.");
            header.Append((char)data[0]);
        }
    }
}
