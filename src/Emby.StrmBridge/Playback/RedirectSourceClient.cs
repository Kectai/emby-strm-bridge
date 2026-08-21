using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.StrmBridge.Playback;

public interface IRedirectSourceClient
{
    Task<RedirectSourceResponse> SendAsync(Uri source, string userAgent, CancellationToken cancellationToken);
}

public sealed class RedirectSourceResponse
{
    public RedirectSourceResponse(int statusCode, string? location, int? retryAfterSeconds)
    {
        StatusCode = statusCode;
        Location = location;
        RetryAfterSeconds = retryAfterSeconds;
    }

    public int StatusCode { get; }

    internal string? Location { get; }

    public int? RetryAfterSeconds { get; }
}

public sealed class HttpRedirectSourceClient : IRedirectSourceClient, IDisposable
{
    private readonly HttpClient client;
    private readonly Func<TimeSpan> timeoutProvider;

    public HttpRedirectSourceClient(TimeSpan timeout)
        : this(() => timeout)
    {
        ValidateTimeout(timeout);
    }

    internal HttpRedirectSourceClient(Func<TimeSpan> timeoutProvider)
    {
        this.timeoutProvider = timeoutProvider ?? throw new ArgumentNullException(nameof(timeoutProvider));
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
        };
        client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<RedirectSourceResponse> SendAsync(
        Uri source,
        string userAgent,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        request.Headers.Range = new RangeHeaderValue(0, 0);
        if (!string.IsNullOrEmpty(userAgent))
        {
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        }
        var requestTimeout = timeoutProvider();
        ValidateTimeout(requestTimeout);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(requestTimeout);
        using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token)
            .ConfigureAwait(false);
        return new RedirectSourceResponse(
            (int)response.StatusCode,
            response.Headers.Location?.OriginalString,
            GetRetryAfterSeconds(response.Headers.RetryAfter));
    }

    public void Dispose() => client.Dispose();

    private static void ValidateTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(180))
            throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    private static int? GetRetryAfterSeconds(RetryConditionHeaderValue? retryAfter)
    {
        if (retryAfter?.Delta is TimeSpan delta)
        {
            return Clamp((int)Math.Ceiling(delta.TotalSeconds));
        }
        if (retryAfter?.Date is DateTimeOffset date)
        {
            return Clamp((int)Math.Ceiling((date - DateTimeOffset.UtcNow).TotalSeconds));
        }
        return null;
    }

    private static int Clamp(int seconds) => Math.Max(1, Math.Min(60, seconds));
}
