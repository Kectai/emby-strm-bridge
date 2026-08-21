using System.Text;
using Emby.StrmBridge.Persistence;
using Emby.StrmBridge.Policy;

namespace Emby.StrmBridge.Tests;

[TestClass]
public sealed class StrmSourcePolicyTests
{
    [TestMethod]
    public void Read_AcceptsBomSingleHttpsRecordAndNeverUsesPlaintextAsIdentity()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Path, "item.strm");
        File.WriteAllBytes(path, new byte[] { 0xef, 0xbb, 0xbf }
            .Concat(Encoding.UTF8.GetBytes("  https://source.invalid/start?token=opaque-value  \n"))
            .ToArray());
        var policy = CreatePolicy();

        var result = policy.Read(path);

        Assert.AreEqual(64, result.StorageKey.Length);
        Assert.AreEqual(64, result.SourceFingerprint.Length);
        Assert.IsFalse(result.StorageKey.Contains("source", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(new FileInfo(path).Length, result.LocalFileLength);
    }

    [TestMethod]
    [DataRow("https://source.invalid/a\nhttps://source.invalid/b", SourceRejectionReason.InvalidRecordCount)]
    [DataRow("file:///tmp/video.mkv", SourceRejectionReason.UnsafeUrl)]
    [DataRow("ftp://source.invalid/video", SourceRejectionReason.UnsafeUrl)]
    [DataRow("https://user:password@source.invalid/video", SourceRejectionReason.UnsafeUrl)]
    [DataRow("https://source.invalid/video#fragment", SourceRejectionReason.UnsafeUrl)]
    [DataRow("relative/video", SourceRejectionReason.InvalidUrl)]
    public void Read_RejectsUnsupportedContent(string content, SourceRejectionReason reason)
    {
        using var workspace = new TestWorkspace();
        var path = workspace.Write("item.strm", content);

        var exception = Assert.ThrowsExactly<SourcePolicyException>(() => CreatePolicy().Read(path));

        Assert.AreEqual(reason, exception.Reason);
    }

    [TestMethod]
    public void Read_RejectsInvalidUtf8AndOversizedFiles()
    {
        using var workspace = new TestWorkspace();
        var invalid = Path.Combine(workspace.Path, "invalid.strm");
        File.WriteAllBytes(invalid, new byte[] { 0xc3, 0x28 });
        Assert.AreEqual(
            SourceRejectionReason.InvalidEncoding,
            Assert.ThrowsExactly<SourcePolicyException>(() => CreatePolicy().Read(invalid)).Reason);

        var oversized = Path.Combine(workspace.Path, "oversized.strm");
        File.WriteAllBytes(oversized, Enumerable.Repeat((byte)'a', StrmSourcePolicy.DefaultMaximumFileSize + 1).ToArray());
        Assert.AreEqual(
            SourceRejectionReason.FileTooLarge,
            Assert.ThrowsExactly<SourcePolicyException>(() => CreatePolicy().Read(oversized)).Reason);
    }

    [TestMethod]
    public void Read_RejectsNonStrmPathBeforeOpeningIt()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.Write("item.txt", "https://source.invalid/video");
        Assert.AreEqual(
            SourceRejectionReason.NotLocalStrm,
            Assert.ThrowsExactly<SourcePolicyException>(() => CreatePolicy().Read(path)).Reason);
    }

    [TestMethod]
    public void Read_RejectsSymbolicLinks()
    {
        using var workspace = new TestWorkspace();
        var target = workspace.Write("target.strm", "https://source.invalid/video");
        var link = Path.Combine(workspace.Path, "link.strm");
        try { File.CreateSymbolicLink(link, target); }
        catch (Exception exception) when (exception is UnauthorizedAccessException || exception is PlatformNotSupportedException)
        {
            Assert.Inconclusive("Symbolic-link creation is unavailable on this test host.");
            return;
        }

        Assert.AreEqual(
            SourceRejectionReason.SymbolicLink,
            Assert.ThrowsExactly<SourcePolicyException>(() => CreatePolicy().Read(link)).Reason);
    }

    [TestMethod]
    public void Read_RejectsSymbolicDirectoryAncestor()
    {
        using var workspace = new TestWorkspace();
        var targetDirectory = Path.Combine(workspace.Path, "target");
        Directory.CreateDirectory(targetDirectory);
        File.WriteAllText(
            Path.Combine(targetDirectory, "item.strm"),
            "https://source.invalid/video",
            new UTF8Encoding(false));
        var linkDirectory = Path.Combine(workspace.Path, "linked");
        try { Directory.CreateSymbolicLink(linkDirectory, targetDirectory); }
        catch (Exception exception) when (exception is UnauthorizedAccessException || exception is PlatformNotSupportedException)
        {
            Assert.Inconclusive("Symbolic-link creation is unavailable on this test host.");
            return;
        }

        Assert.AreEqual(
            SourceRejectionReason.SymbolicLink,
            Assert.ThrowsExactly<SourcePolicyException>(() =>
                CreatePolicy().Read(Path.Combine(linkDirectory, "item.strm"))).Reason);
    }

    private static StrmSourcePolicy CreatePolicy() =>
        new(new HmacIdentityProvider(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray()));
}
