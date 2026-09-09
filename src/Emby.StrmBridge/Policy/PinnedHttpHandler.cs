using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.StrmBridge.Policy;

internal static class PinnedHttpHandler
{
    public static HttpMessageHandler Create(RedirectPolicy policy) =>
        Create(policy, typeof(HttpClient).GetProperty("DefaultProxy")?.GetValue(null) as IWebProxy ??
            throw new PlatformNotSupportedException("System proxy selection is unavailable."));

    internal static HttpMessageHandler Create(RedirectPolicy policy, IWebProxy proxy) =>
        new ProxyRoutingHandler(policy, proxy);

    internal static HttpMessageHandler CreateDirect(RedirectPolicy policy)
    {
        var handler = CreateTransport(proxy: null);
        var handlerType = handler.GetType();
        try
        {
            var callback = handlerType.GetProperty("ConnectCallback") ??
                throw new PlatformNotSupportedException("Validated HTTP connections require ConnectCallback.");
            var contextType = callback.PropertyType.GenericTypeArguments[0];
            var endpointProperty = contextType.GetProperty("DnsEndPoint")!;
            var requestProperty = contextType.GetProperty("InitialRequestMessage")!;
            Func<object, CancellationToken, ValueTask<Stream>> connect = (context, cancellation) =>
                ConnectAsync((DnsEndPoint)endpointProperty.GetValue(context)!,
                    ((HttpRequestMessage)requestProperty.GetValue(context)!).RequestUri!, policy, cancellation);
            callback.SetValue(handler, connect);
            return handler;
        }
        catch
        {
            handler.Dispose();
            throw;
        }
    }

    internal static HttpMessageHandler CreateTransport(IWebProxy? proxy)
    {
        var handlerType = typeof(HttpClient).Assembly.GetType("System.Net.Http.SocketsHttpHandler", throwOnError: true)!;
        var handler = (HttpMessageHandler)Activator.CreateInstance(handlerType)!;
        try
        {
            handlerType.GetProperty("AllowAutoRedirect")!.SetValue(handler, false);
            handlerType.GetProperty("UseCookies")!.SetValue(handler, false);
            handlerType.GetProperty("UseProxy")!.SetValue(handler, proxy is not null);
            if (proxy is not null) handlerType.GetProperty("Proxy")!.SetValue(handler, proxy);
            handlerType.GetProperty("AutomaticDecompression")!.SetValue(handler, DecompressionMethods.None);
            handlerType.GetProperty("PooledConnectionLifetime")!.SetValue(handler, TimeSpan.FromMinutes(2));
            return handler;
        }
        catch
        {
            handler.Dispose();
            throw;
        }
    }

    private sealed class ProxyRoutingHandler : HttpMessageHandler
    {
        private const int MaximumProxyRoutes = 32;
        private readonly object sync = new();
        private readonly IWebProxy systemProxy;
        private readonly RedirectPolicy policy;
        private HttpMessageInvoker direct;
        private string directTrustKey;
        private readonly Dictionary<Uri, HttpMessageInvoker> proxyRoutes = new();
        private bool disposed;

        public ProxyRoutingHandler(RedirectPolicy policy, IWebProxy systemProxy)
        {
            this.systemProxy = systemProxy ?? throw new ArgumentNullException(nameof(systemProxy));
            this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
            directTrustKey = policy.GetTransportTrustKey();
            direct = new HttpMessageInvoker(CreateDirect(policy));
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = request.RequestUri ?? throw new HttpRequestException("The source address is unavailable.");
            Uri? proxy;
            ICredentials? credentials;
            try
            {
                proxy = systemProxy.IsBypassed(target) ? null : systemProxy.GetProxy(target);
                credentials = proxy is null || proxy == target ? null : systemProxy.Credentials;
            }
            catch (Exception exception)
            {
                // Resolve outside SocketsHttpHandler: it otherwise treats proxy lookup errors as a direct route.
                throw new HttpRequestException("System proxy selection failed.", exception);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (proxy is not null && !proxy.IsAbsoluteUri)
                throw new HttpRequestException("The system proxy address is invalid.");
            try
            {
                Task<HttpResponseMessage> response;
                lock (sync)
                {
                    if (disposed) throw new ObjectDisposedException(nameof(ProxyRoutingHandler));
                    HttpMessageInvoker route;
                    if (proxy is null || proxy == target)
                    {
                        var trustKey = policy.GetTransportTrustKey();
                        if (!string.Equals(directTrustKey, trustKey, StringComparison.Ordinal))
                        {
                            // ConnectCallback only runs for new connections. A pool authorized by
                            // older rules must never serve a request under the updated policy.
                            var replacement = new HttpMessageInvoker(CreateDirect(policy));
                            var previous = direct;
                            direct = replacement;
                            directTrustKey = trustKey;
                            previous.Dispose();
                        }
                        route = direct;
                    }
                    else if (!proxyRoutes.TryGetValue(proxy, out route!))
                    {
                        if (proxyRoutes.Count >= MaximumProxyRoutes)
                            throw new HttpRequestException("The system proxy route capacity was reached.");
                        route = new HttpMessageInvoker(CreateTransport(new FixedProxy(proxy, credentials)));
                        proxyRoutes.Add(proxy, route);
                    }
                    // Start while holding the route lock so rotation cannot dispose a selected
                    // invoker before its request is submitted.
                    response = route.SendAsync(request, cancellationToken);
                }
                return await response.ConfigureAwait(false);
            }
            catch (NotSupportedException exception)
            {
                throw new HttpRequestException("The selected connection protocol is unsupported.", exception);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                HttpMessageInvoker[] routes;
                lock (sync)
                {
                    if (disposed) return;
                    disposed = true;
                    routes = proxyRoutes.Values.ToArray();
                    proxyRoutes.Clear();
                }
                direct.Dispose();
                foreach (var route in routes) route.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class FixedProxy : IWebProxy
    {
        private readonly Uri address;

        public FixedProxy(Uri address, ICredentials? credentials)
        {
            this.address = address;
            Credentials = credentials;
        }

        public ICredentials? Credentials { get; set; }

        public Uri GetProxy(Uri destination) => address;

        public bool IsBypassed(Uri host) => false;
    }

    private static async ValueTask<Stream> ConnectAsync(
        DnsEndPoint endpoint, Uri target, RedirectPolicy policy, CancellationToken cancellation)
    {
        var addresses = IPAddress.TryParse(endpoint.Host.Trim('[', ']'), out var literal)
            ? new[] { literal }
            : await ResolveAsync(endpoint.Host, cancellation).ConfigureAwait(false);
        if (addresses.Length == 0) throw new HttpRequestException("The source has no network address.");
        foreach (var address in addresses) policy.ValidateAddress(target, address);
        foreach (var address in addresses.Distinct())
        {
            cancellation.ThrowIfCancellationRequested();
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            using var registration = cancellation.Register(socket.Dispose);
            try
            {
                await socket.ConnectAsync(address, endpoint.Port).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception exception) when (exception is SocketException || exception is ObjectDisposedException)
            {
                socket.Dispose();
                cancellation.ThrowIfCancellationRequested();
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        throw new HttpRequestException("The validated source addresses are unavailable.");
    }

    private static async Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellation)
    {
        var query = Dns.GetHostAddressesAsync(host);
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellation.Register(() => cancelled.TrySetResult(true));
        if (await Task.WhenAny(query, cancelled.Task).ConfigureAwait(false) != query)
        {
            _ = query.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            cancellation.ThrowIfCancellationRequested();
        }
        return await query.ConfigureAwait(false);
    }
}
