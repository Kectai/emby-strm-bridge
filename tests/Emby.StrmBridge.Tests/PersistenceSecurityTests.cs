using System.Text;
using Emby.StrmBridge.Persistence;

namespace Emby.StrmBridge.Tests;

[TestClass]
public sealed class PersistenceSecurityTests
{
    [TestMethod]
    public void IdentityKey_IsStablePrivateAndRepositoryLocalToConfiguredDirectory()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Path, "identity.key");
        var stale = path + ".tmp-" + new string('a', 32);
        File.WriteAllText(stale, "stale");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddMinutes(-10));

        var first = HmacIdentityProvider.LoadOrCreate(path).Compute("value");
        var second = HmacIdentityProvider.LoadOrCreate(path).Compute("value");

        Assert.AreEqual(first, second);
        Assert.AreEqual(HmacIdentityProvider.KeyLength, new FileInfo(path).Length);
        Assert.AreEqual(0, Directory.GetFiles(workspace.Path, "identity.key.tmp-*").Length);
        if (!OperatingSystem.IsWindows())
        {
            var permissions = File.GetUnixFileMode(path);
            var forbidden = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            Assert.AreEqual((UnixFileMode)0, permissions & forbidden);
        }
    }

    [TestMethod]
    public void ExtractionState_AppliesBackoffAndStoresNoSourceMaterial()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Path, "state", "extraction-state.json");
        var store = new ExtractionStateStore(path);
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var key = new string('a', 64);

        var fingerprint = new string('b', 64);
        store.RecordFailure(key, fingerprint, now);
        Assert.IsFalse(store.ShouldAttempt(key, fingerprint, now.AddSeconds(29)));
        Assert.IsTrue(store.ShouldAttempt(key, fingerprint, now.AddSeconds(30)));
        Assert.IsTrue(store.ShouldAttempt(key, new string('c', 64), now));
        store.Flush();
        var content = File.ReadAllText(path, Encoding.UTF8);
        Assert.IsFalse(content.Contains("http", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(content.Contains("token", StringComparison.OrdinalIgnoreCase));

        store.RecordSuccess(key, fingerprint);
        Assert.IsTrue(store.ShouldAttempt(key, fingerprint, now));
        Assert.AreEqual(fingerprint, store.GetLastSuccessfulFingerprint(key));
    }

    [TestMethod]
    public void ExtractionState_ClearFailuresRemovesBackoffAndPreservesSuccess()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Path, "state", "extraction-state.json");
        var key = new string('a', 64);
        var success = new string('b', 64);
        var failure = new string('c', 64);
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var store = new ExtractionStateStore(path);
        store.RecordSuccess(key, success);
        store.RecordFailure(key, failure, now);
        Assert.IsFalse(store.ShouldAttempt(key, failure, now));

        store.ClearFailures();
        store.Flush();

        var reloaded = new ExtractionStateStore(path);
        Assert.IsTrue(reloaded.ShouldAttempt(key, failure, now));
        Assert.AreEqual(success, reloaded.GetLastSuccessfulFingerprint(key));
    }

    [TestMethod]
    public void ExtractionState_RemoveDeletesOnlySelectedEntriesAndPersistsTheChange()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Path, "state", "extraction-state.json");
        var removedKey = new string('a', 64);
        var retainedKey = new string('b', 64);
        var store = new ExtractionStateStore(path);
        store.RecordSuccess(removedKey, new string('c', 64));
        store.RecordSuccess(retainedKey, new string('d', 64));
        store.Flush();

        Assert.AreEqual(1, store.Remove(new HashSet<string>(StringComparer.Ordinal) { removedKey }));

        var reloaded = new ExtractionStateStore(path);
        Assert.IsNull(reloaded.GetLastSuccessfulFingerprint(removedKey));
        Assert.AreEqual(new string('d', 64), reloaded.GetLastSuccessfulFingerprint(retainedKey));
    }

    [TestMethod]
    public void SnapshotStore_CleansOnlyStrictlyNamedTemporaryFiles()
    {
        using var workspace = new TestWorkspace();
        var store = new MediaInfoStore(workspace.Path, new SnapshotSerializer());
        var randomized = Path.Combine(
            workspace.Path,
            new string('c', 64) + ".json." + new string('d', 32) + ".tmp");
        var unrelated = Path.Combine(workspace.Path, "unrelated.json.tmp");
        File.WriteAllText(randomized, "temporary");
        File.WriteAllText(unrelated, "keep");
        File.SetLastWriteTimeUtc(randomized, DateTime.UtcNow.AddMinutes(-10));

        Assert.AreEqual(1, store.RemoveTemporaryFiles());
        Assert.IsFalse(File.Exists(randomized));
        Assert.IsTrue(File.Exists(unrelated));
    }

    [TestMethod]
    public void ExtractionState_RecoversFromBackupWithoutOverwritingItWithCorruptPrimary()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Path, "state", "extraction-state.json");
        var key = new string('a', 64);
        var firstFingerprint = new string('b', 64);
        var secondFingerprint = new string('c', 64);
        var store = new ExtractionStateStore(path);
        store.RecordSuccess(key, firstFingerprint);
        store.Flush();
        store.RecordFailure(
            key,
            firstFingerprint,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        store.Flush();
        File.WriteAllText(path, "corrupt-primary", Encoding.UTF8);

        var recovered = new ExtractionStateStore(path);
        Assert.AreEqual(firstFingerprint, recovered.GetLastSuccessfulFingerprint(key));
        recovered.RecordSuccess(key, secondFingerprint);
        recovered.Flush();

        Assert.AreEqual(
            secondFingerprint,
            new ExtractionStateStore(path).GetLastSuccessfulFingerprint(key));
    }

    [TestMethod]
    public void SnapshotStore_RemovesOrphanBackupWithoutPrimary()
    {
        using var workspace = new TestWorkspace();
        var key = new string('d', 64);
        var backup = Path.Combine(workspace.Path, key + ".json.bak");
        File.WriteAllText(backup, "orphan", Encoding.UTF8);
        var store = new MediaInfoStore(workspace.Path, new SnapshotSerializer());

        Assert.AreEqual(1, store.RemoveOrphans(_ => false));
        Assert.IsFalse(File.Exists(backup));
    }

    [TestMethod]
    public void ExtractionState_WhenCapacityWasExceededRefusesUnknownBaseline()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Path, "state", "extraction-state.json");
        var store = new ExtractionStateStore(path, maximumEntries: 1);
        var firstKey = new string('a', 64);
        var secondKey = new string('b', 64);

        Assert.IsTrue(store.TryRecordBaseline(firstKey, new string('c', 64)));
        Assert.IsFalse(store.TryRecordBaseline(secondKey, new string('d', 64)));
        store.RecordSuccess(secondKey, new string('d', 64));

        Assert.IsNull(store.GetLastSuccessfulFingerprint(firstKey));
        Assert.IsFalse(store.TryRecordBaseline(firstKey, new string('e', 64)));
    }

    [TestMethod]
    public void ExtractionState_FullCapacityCanBeSavedAndReloaded()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Path, "state", "extraction-state.json");
        var store = new ExtractionStateStore(path);
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (var index = 0; index < ExtractionStateStore.DefaultMaximumEntries; index++)
        {
            var key = index.ToString("x64");
            var fingerprint = (index + ExtractionStateStore.DefaultMaximumEntries).ToString("x64");
            store.RecordSuccess(key, fingerprint);
            store.RecordFailure(key, fingerprint, now);
        }

        store.Flush();
        var reloaded = new ExtractionStateStore(path);
        var lastKey = (ExtractionStateStore.DefaultMaximumEntries - 1).ToString("x64");
        var lastFingerprint = (2 * ExtractionStateStore.DefaultMaximumEntries - 1).ToString("x64");

        Assert.AreEqual(lastFingerprint, reloaded.GetLastSuccessfulFingerprint(lastKey));
    }

    [TestMethod]
    public void ExtractionState_RejectsExcessiveObjectGraphBeforeFilteringEntries()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Path, "state", "extraction-state.json");
        var key = new string('a', 64);
        var fingerprint = new string('b', 64);
        var store = new ExtractionStateStore(path, maximumEntries: 1);
        store.RecordSuccess(key, fingerprint);
        store.Flush();
        File.Copy(path, path + ".bak");

        var emptyEntries = string.Join(",", Enumerable.Repeat("{}", 256));
        File.WriteAllText(
            path,
            "{\"SchemaVersion\":1,\"Entries\":[" + emptyEntries + "]}",
            Encoding.UTF8);

        var recovered = new ExtractionStateStore(path, maximumEntries: 1);

        Assert.AreEqual(fingerprint, recovered.GetLastSuccessfulFingerprint(key));
    }
}
