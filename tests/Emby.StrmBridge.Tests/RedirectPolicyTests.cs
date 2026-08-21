using Emby.StrmBridge.Policy;

namespace Emby.StrmBridge.Tests;

[TestClass]
public sealed class RedirectPolicyTests
{
    private readonly RedirectPolicy policy = new(() => new[] { "media.invalid" });

    [TestMethod]
    public void Validate_AcceptsOpaqueHttpsTarget()
    {
        var target = policy.Validate(
            new Uri("https://source.invalid/start?credential=source-secret-value"),
            "https://media.invalid/video?signature=different-secret");
        Assert.AreEqual("media.invalid", target.Host);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("/relative")]
    [DataRow("file:///etc/passwd")]
    [DataRow("https://user:pass@media.invalid/video")]
    [DataRow("https://media.invalid/video#fragment")]
    [DataRow("https://media.invalid/video\r\nInjected: yes")]
    public void Validate_RejectsUnsafeLocations(string location)
    {
        Assert.ThrowsExactly<RedirectRejectedException>(() =>
            policy.Validate(new Uri("https://source.invalid/start"), location));
    }

    [TestMethod]
    public void Validate_RejectsCredentialValueCopiedToAnotherPartOfTarget()
    {
        var exception = Assert.ThrowsExactly<RedirectRejectedException>(() => policy.Validate(
            new Uri("https://source.invalid/start?credential=long-source-secret"),
            "https://media.invalid/path/long-source-secret/video"));
        Assert.AreEqual(RedirectRejectionReason.CredentialPropagation, exception.Reason);
    }

    [TestMethod]
    public void Validate_RejectsCrossHostTargetUnlessExplicitlyTrusted()
    {
        string? detectedHost = null;
        var exception = Assert.ThrowsExactly<RedirectRejectedException>(() =>
            new RedirectPolicy(untrustedHostObserver: host => detectedHost = host).Validate(
                new Uri("https://source.invalid/start"),
                "http://127.0.0.1/internal"));
        Assert.AreEqual(RedirectRejectionReason.UntrustedTargetHost, exception.Reason);
        Assert.AreEqual("127.0.0.1", detectedHost);
    }

    [TestMethod]
    public void Validate_DoesNotReportAnUntrustedHostThatContainsASourceCredential()
    {
        string? detectedHost = null;
        var exception = Assert.ThrowsExactly<RedirectRejectedException>(() =>
            new RedirectPolicy(untrustedHostObserver: host => detectedHost = host).Validate(
                new Uri("https://source.invalid/start?token=long-source-secret"),
                "https://long-source-secret.attacker.invalid/video"));

        Assert.AreEqual(RedirectRejectionReason.CredentialPropagation, exception.Reason);
        Assert.IsNull(detectedHost);
    }

    [TestMethod]
    public void Validate_AcceptsOnlyLabelBoundedSubdomainsForExplicitWildcardRule()
    {
        var wildcardPolicy = new RedirectPolicy(() => new[] { "*.cdn.example.invalid" });

        var direct = wildcardPolicy.Validate(
            new Uri("https://source.invalid/start"),
            "https://edge.cdn.example.invalid/video");
        var nested = wildcardPolicy.Validate(
            new Uri("https://source.invalid/start"),
            "https://region.edge.cdn.example.invalid/video");

        Assert.AreEqual("edge.cdn.example.invalid", direct.Host);
        Assert.AreEqual("region.edge.cdn.example.invalid", nested.Host);
        foreach (var location in new[]
                 {
                     "https://cdn.example.invalid/video",
                     "https://evilcdn.example.invalid/video",
                     "https://cdn.example.invalid.attacker.invalid/video",
                 })
        {
            var exception = Assert.ThrowsExactly<RedirectRejectedException>(() =>
                wildcardPolicy.Validate(new Uri("https://source.invalid/start"), location));
            Assert.AreEqual(RedirectRejectionReason.UntrustedTargetHost, exception.Reason);
        }
    }
}
