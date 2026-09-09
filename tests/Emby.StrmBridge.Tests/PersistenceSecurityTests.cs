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
    public void ExtractionState_IncreasesRepeatedFailureBackoffToOneDay()
    {
        using var workspace = new TestWorkspace();
        var store = new ExtractionStateStore(Path.Combine(workspace.Path, "state", "extraction-state.json"));
        var key = new string('a', 64);
        var fingerprint = new string('b', 64);
        var attempt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var expectedDelays = new[] { 30, 120, 480, 1920, 7680, 30720, 86400, 86400 };

        foreach (var delaySeconds in expectedDelays)
        {
            store.RecordFailure(key, fingerprint, attempt);
            Assert.IsFalse(store.ShouldAttempt(key, fingerprint, attempt.AddSeconds(delaySeconds - 1)));
            Assert.IsTrue(store.ShouldAttempt(key, fingerprint, attempt.AddSeconds(delaySeconds)));
            attempt = attempt.AddSeconds(delaySeconds);
        }
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
    public void ExtractionState_OldVideoOnlySchemaDoesNotKeepAudioFailuresBackedOff()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Path, "state", "extraction-state.json");
        var key = new string('a', 64);
        var fingerprint = new string('b', 64);
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var store = new ExtractionStateStore(path);
        store.RecordFailure(key, fingerprint, now);
        store.Flush();
        var oldSchema = File.ReadAllText(path, Encoding.UTF8)
            .Replace("\"SchemaVersion\":3", "\"SchemaVersion\":2", StringComparison.Ordinal);
        File.WriteAllText(path, oldSchema, Encoding.UTF8);

        var reloaded = new ExtractionStateStore(path);

        Assert.IsTrue(reloaded.ShouldAttempt(key, fingerprint, now));
        Assert.IsNull(reloaded.GetLastSuccessfulFingerprint(key));
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
    public void ExtractionState_RejectsExtremeFailureStateAndRecoversValidBackup()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Path, "state", "extraction-state.json");
        var key = new string('a', 64);
        var fingerprint = new string('b', 64);
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var store = new ExtractionStateStore(path);
        store.RecordSuccess(key, fingerprint);
        store.Flush();
        store.RecordFailure(key, fingerprint, now);
        store.Flush();
        var invalid = File.ReadAllText(path, Encoding.UTF8);
        invalid = ReplaceJsonNumber(invalid, "\"ConsecutiveFailures\":", int.MaxValue.ToString());
        invalid = ReplaceJsonNumber(invalid, "\"RetryAtUtcTicks\":", long.MaxValue.ToString());
        File.WriteAllText(path, invalid, Encoding.UTF8);

        var recovered = new ExtractionStateStore(path);

        Assert.AreEqual(fingerprint, recovered.GetLastSuccessfulFingerprint(key));
        Assert.IsTrue(recovered.ShouldAttempt(key, fingerprint, now),
            "The invalid primary must not install a practically permanent retry delay.");
        recovered.RecordFailure(key, fingerprint, now);
        Assert.IsFalse(recovered.ShouldAttempt(key, fingerprint, now));
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

        Assert.AreEqual(new string('c', 64), store.GetLastSuccessfulFingerprint(firstKey));
        Assert.IsNull(store.GetLastSuccessfulFingerprint(secondKey));
        Assert.IsFalse(store.TryRecordBaseline(secondKey, new string('e', 64)));
    }

    [TestMethod]
    public void ExtractionState_FullCapacityDoesNotEvictActiveFailureForNewSource()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Path, "state", "extraction-state.json");
        var store = new ExtractionStateStore(path, maximumEntries: 1);
        var firstKey = new string('a', 64);
        var secondKey = new string('b', 64);
        var firstFingerprint = new string('c', 64);
        var secondFingerprint = new string('d', 64);
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        store.RecordFailure(firstKey, firstFingerprint, now);

        store.RecordFailure(secondKey, secondFingerprint, now);

        Assert.IsFalse(store.ShouldAttempt(firstKey, firstFingerprint, now),
            "A new failure must not rotate out an existing active retry delay.");
        Assert.IsTrue(store.ShouldAttempt(secondKey, secondFingerprint, now),
            "An untracked overflow source remains eligible rather than being reported as persisted.");
    }

    [TestMethod]
    public void ExtractionState_RemoveMissingClearsCapacityFlagAndAdmitsActiveOverflowBaseline()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Path, "state", "extraction-state.json");
        var store = new ExtractionStateStore(path, maximumEntries: 2);
        var firstKey = new string('a', 64);
        var removedKey = new string('b', 64);
        var overflowKey = new string('c', 64);
        var firstFingerprint = new string('d', 64);
        var removedFingerprint = new string('e', 64);
        var overflowFingerprint = new string('f', 64);
        Assert.IsTrue(store.TryRecordBaseline(firstKey, firstFingerprint));
        Assert.IsTrue(store.TryRecordBaseline(removedKey, removedFingerprint));
        Assert.IsFalse(store.TryRecordBaseline(overflowKey, overflowFingerprint));

        store.RemoveMissing(new HashSet<string>(StringComparer.Ordinal) { firstKey, overflowKey });

        Assert.IsNull(store.GetLastSuccessfulFingerprint(removedKey));
        Assert.IsTrue(store.TryRecordBaseline(overflowKey, overflowFingerprint),
            "Removing an orphan must reopen the physical slot even when an active key was previously untracked.");
        Assert.AreEqual(overflowFingerprint, store.GetLastSuccessfulFingerprint(overflowKey));
    }

    [TestMethod]
    public void ExtractionState_FailureCapacityEvictsSuccessBeforeEarlierExpiredFailure()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Path, "state", "extraction-state.json");
        var store = new ExtractionStateStore(path, maximumEntries: 2);
        var expiredKey = new string('a', 64);
        var successKey = new string('b', 64);
        var newFailureKey = new string('c', 64);
        var expiredFingerprint = new string('d', 64);
        var successFingerprint = new string('e', 64);
        var newFailureFingerprint = new string('f', 64);
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        store.RecordSuccess(expiredKey, expiredFingerprint);
        store.RecordFailure(expiredKey, expiredFingerprint, now);
        Assert.IsTrue(store.TryRecordBaseline(successKey, successFingerprint));

        store.RecordFailure(newFailureKey, newFailureFingerprint, now.AddSeconds(31));

        Assert.AreEqual(expiredFingerprint, store.GetLastSuccessfulFingerprint(expiredKey),
            "An expired failure still carries more retry history than a success-only baseline.");
        Assert.IsNull(store.GetLastSuccessfulFingerprint(successKey));
        Assert.IsFalse(store.ShouldAttempt(
            newFailureKey,
            newFailureFingerprint,
            now.AddSeconds(31)));
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

    private static string ReplaceJsonNumber(string json, string marker, string replacement)
    {
        var start = json.IndexOf(marker, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0);
        start += marker.Length;
        var end = start;
        while (end < json.Length && (json[end] == '-' || char.IsDigit(json[end]))) end++;
        Assert.IsTrue(end > start);
        return json.Substring(0, start) + replacement + json.Substring(end);
    }
}
