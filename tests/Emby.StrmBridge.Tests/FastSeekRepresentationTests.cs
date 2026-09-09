using System.Net;
using System.Net.Http.Headers;
using Emby.StrmBridge.Configuration;
using Emby.StrmBridge.Domain;
using Emby.StrmBridge.Playback;

namespace Emby.StrmBridge.Tests;

[TestClass]
public sealed class FastSeekRepresentationTests
{
    [TestMethod]
    [DataRow("W/\"weak\"")]
    [DataRow("*")]
    [DataRow("\"tag\"\r\nInjected: value")]
    public void Representation_RejectsNonStrongOrInjectedValidators(string value) =>
        Assert.ThrowsExactly<ArgumentException>(() => new FastSeekRepresentation(value, 100));

    [TestMethod]
    public void Representation_RequiresExactStrongEtagLengthAndIdentityEncoding()
    {
        var expected = new FastSeekRepresentation("\"A\"", 100);
        using var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent(new byte[10]),
        };
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(20, 29, 100);
        response.Headers.ETag = new EntityTagHeaderValue("\"A\"");
        Assert.IsTrue(expected.Matches(response));
        response.Headers.ETag = new EntityTagHeaderValue("\"B\"");
        Assert.IsFalse(expected.Matches(response));
        response.Headers.ETag = new EntityTagHeaderValue("\"A\"", true);
        Assert.IsFalse(expected.Matches(response));
        response.Headers.ETag = new EntityTagHeaderValue("\"A\"");
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(20, 29, 101);
        Assert.IsFalse(expected.Matches(response));
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(20, 29, 100);
        response.Content.Headers.ContentEncoding.Add("gzip");
        Assert.IsFalse(expected.Matches(response));
        Assert.ThrowsExactly<ArgumentException>(() => new FastSeekRepresentation("\"" + new string('a', 1024) + "\"", 100));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Coordinator_RejectsUnvalidatedOrChangingRepresentationWithoutDisablingOtherSources(bool changes)
    {
        var coordinator = new FastSeekCoordinator(new RepresentationProbe(changes), new ManualClock(),
            FastSeekCoordinatorTests.CreateLogger());
        var target = TimeSpan.FromSeconds(50).Ticks;
        var duration = TimeSpan.FromSeconds(100).Ticks;
        Assert.IsFalse(await coordinator.PrepareAsync(TestSources.Create(), "unvalidated", target, duration,
            0, new PluginConfiguration(), CancellationToken.None));
        Assert.IsTrue(await coordinator.PrepareAsync(TestSources.Create("two"), "validated", target, duration,
            0, new PluginConfiguration(), CancellationToken.None));
    }

    private sealed class RepresentationProbe(bool changes) : IFastSeekProbeClient
    {
        private readonly SyntheticFastSeekProbeClient inner = new();

        public async Task<FastSeekProbeResult> ReadAsync(SourceIdentity source, long offset, int maximumBytes,
            PluginConfiguration options, CancellationToken cancellationToken)
        {
            var sample = await inner.ReadAsync(source, offset, maximumBytes, options, cancellationToken);
            if (source.SourceFingerprint.EndsWith("2", StringComparison.Ordinal)) return sample;
            var representation = changes
                ? new FastSeekRepresentation(offset == 0 ? "\"A\"" : "\"B\"", sample.TotalLength)
                : null;
            return new FastSeekProbeResult(sample.RangeStart, sample.TotalLength, sample.Bytes, representation);
        }
    }
}
