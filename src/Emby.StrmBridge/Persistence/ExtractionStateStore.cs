using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace Emby.StrmBridge.Persistence;

public sealed class ExtractionStateStore
{
    private const int MaximumConsecutiveFailures = 7;
    private const int MaximumRetryDelaySeconds = 24 * 60 * 60;
    internal const int DefaultMaximumEntries = 32768;
    private const int MaximumDocumentBytes = 32 * 1024 * 1024;
    private const int MaximumObjectGraphItemsPerEntry = 8;
    private readonly object sync = new();
    private readonly string path;
    private readonly int maximumEntries;
    private readonly DataContractJsonSerializer serializer;
    private readonly Dictionary<string, ExtractionStateEntry> entriesByKey = new(StringComparer.Ordinal);
    private ExtractionStateDocument document;
    private bool dirty;

    public ExtractionStateStore(string path) : this(path, DefaultMaximumEntries) { }

    internal ExtractionStateStore(string path, int maximumEntries)
    {
        this.path = path ?? throw new ArgumentNullException(nameof(path));
        if (maximumEntries < 1 || maximumEntries > DefaultMaximumEntries)
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        this.maximumEntries = maximumEntries;
        serializer = new DataContractJsonSerializer(
            typeof(ExtractionStateDocument),
            new DataContractJsonSerializerSettings
            {
                MaxItemsInObjectGraph = checked(
                    maximumEntries * MaximumObjectGraphItemsPerEntry + 32),
            });
        var directory = Path.GetDirectoryName(path)
            ?? throw new ArgumentException("The extraction-state path has no directory.", nameof(path));
        Directory.CreateDirectory(directory);
        RemoveStaleTemporaryFiles(directory, Path.GetFileName(path));
        document = Load();
        RebuildIndex();
    }

    public bool ShouldAttempt(string storageKey, string sourceFingerprint, DateTimeOffset now)
    {
        ValidateKey(storageKey, nameof(storageKey));
        ValidateKey(sourceFingerprint, nameof(sourceFingerprint));
        lock (sync)
        {
            entriesByKey.TryGetValue(storageKey, out var entry);
            return entry is null ||
                   !string.Equals(entry.LastFailureSourceFingerprint, sourceFingerprint, StringComparison.Ordinal) ||
                   now.UtcDateTime.Ticks >= entry.RetryAtUtcTicks;
        }
    }

    public string? GetLastSuccessfulFingerprint(string storageKey)
    {
        lock (sync)
        {
            return entriesByKey.TryGetValue(storageKey, out var entry)
                ? entry.LastSuccessfulSourceFingerprint
                : null;
        }
    }

    public void RecordSuccess(string storageKey, string sourceFingerprint)
    {
        ValidateKey(storageKey, nameof(storageKey));
        ValidateKey(sourceFingerprint, nameof(sourceFingerprint));
        lock (sync)
        {
            var entry = GetOrCreateSuccessEntry(storageKey);
            if (entry is null) return;
            entry.ConsecutiveFailures = 0;
            entry.RetryAtUtcTicks = 0;
            entry.LastSuccessfulSourceFingerprint = sourceFingerprint;
            entry.LastFailureSourceFingerprint = null;
            dirty = true;
        }
    }

    public bool TryRecordBaseline(string storageKey, string sourceFingerprint)
    {
        ValidateKey(storageKey, nameof(storageKey));
        ValidateKey(sourceFingerprint, nameof(sourceFingerprint));
        lock (sync)
        {
            if (entriesByKey.TryGetValue(storageKey, out var existing))
            {
                existing.ConsecutiveFailures = 0;
                existing.RetryAtUtcTicks = 0;
                existing.LastSuccessfulSourceFingerprint = sourceFingerprint;
                existing.LastFailureSourceFingerprint = null;
                dirty = true;
                return true;
            }
            if (document.Entries.Count >= maximumEntries)
            {
                document.CapacityExceeded = true;
                dirty = true;
                return false;
            }
            if (document.CapacityExceeded)
            {
                document.CapacityExceeded = false;
                dirty = true;
            }
            var entry = new ExtractionStateEntry
            {
                StorageKey = storageKey,
                LastSuccessfulSourceFingerprint = sourceFingerprint,
            };
            document.Entries.Add(entry);
            entriesByKey.Add(storageKey, entry);
            dirty = true;
            return true;
        }
    }

    public void Flush()
    {
        lock (sync)
        {
            if (dirty) Save();
        }
    }

    public void RecordFailure(string storageKey, string sourceFingerprint, DateTimeOffset now)
    {
        ValidateKey(storageKey, nameof(storageKey));
        ValidateKey(sourceFingerprint, nameof(sourceFingerprint));
        lock (sync)
        {
            var entry = GetOrCreateFailureEntry(storageKey, now);
            if (entry is null) return;
            var previousFailures = string.Equals(
                                       entry.LastFailureSourceFingerprint,
                                       sourceFingerprint,
                                       StringComparison.Ordinal) &&
                                   entry.ConsecutiveFailures is >= 1 and <= MaximumConsecutiveFailures
                ? entry.ConsecutiveFailures
                : 0;
            entry.ConsecutiveFailures = Math.Min(previousFailures + 1, MaximumConsecutiveFailures);
            var exponent = Math.Min(2 * (entry.ConsecutiveFailures - 1), 12);
            var seconds = Math.Min(MaximumRetryDelaySeconds, 30 * (1 << exponent));
            entry.RetryAtUtcTicks = now.AddSeconds(seconds).UtcDateTime.Ticks;
            entry.LastFailureSourceFingerprint = sourceFingerprint;
            dirty = true;
        }
    }

    public void ClearFailures()
    {
        lock (sync)
        {
            var changed = false;
            foreach (var entry in document.Entries)
            {
                if (entry.ConsecutiveFailures == 0 && entry.RetryAtUtcTicks == 0 &&
                    string.IsNullOrEmpty(entry.LastFailureSourceFingerprint))
                    continue;
                entry.ConsecutiveFailures = 0;
                entry.RetryAtUtcTicks = 0;
                entry.LastFailureSourceFingerprint = null;
                changed = true;
            }
            if (changed) dirty = true;
        }
    }

    public void Clear()
    {
        lock (sync)
        {
            document = new ExtractionStateDocument();
            RebuildIndex();
            dirty = true;
            Save();
            var backup = path + ".bak";
            if (File.Exists(backup)) File.Delete(backup);
        }
    }

    public int Remove(ISet<string> storageKeys)
    {
        if (storageKeys is null) throw new ArgumentNullException(nameof(storageKeys));
        lock (sync)
        {
            var removed = document.Entries.RemoveAll(item => storageKeys.Contains(item.StorageKey));
            if (removed == 0) return 0;
            RebuildIndex();
            if (document.CapacityExceeded && document.Entries.Count < maximumEntries)
                document.CapacityExceeded = false;
            dirty = true;
            Save();
            return removed;
        }
    }

    public void RemoveMissing(ISet<string> activeStorageKeys)
    {
        lock (sync)
        {
            var changed = document.Entries.RemoveAll(item => !activeStorageKeys.Contains(item.StorageKey)) > 0;
            if (changed) RebuildIndex();
            if (document.CapacityExceeded && document.Entries.Count < maximumEntries)
            {
                document.CapacityExceeded = false;
                changed = true;
            }
            if (changed)
            {
                dirty = true;
                Save();
            }
        }
    }

    private ExtractionStateDocument Load()
    {
        if (TryLoad(path, out var primary)) return primary!;
        if (TryLoad(path + ".bak", out var backup)) return backup!;
        return new ExtractionStateDocument();
    }

    private void Save()
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            byte[] data;
            using (var buffer = new MemoryStream())
            {
                serializer.WriteObject(buffer, document);
                if (buffer.Length > MaximumDocumentBytes)
                    throw new InvalidDataException("The extraction-state document exceeds its size limit.");
                data = buffer.ToArray();
            }
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(data, 0, data.Length);
                stream.Flush(true);
            }
            if (File.Exists(path))
            {
                if (TryLoad(path, out _)) File.Replace(temporary, path, path + ".bak");
                else File.Replace(temporary, path, null);
            }
            else File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        dirty = false;
    }

    private static void RemoveStaleTemporaryFiles(string directory, string stateFileName)
    {
        var prefix = stateFileName + ".";
        foreach (var temporary in Directory.EnumerateFiles(directory, prefix + "*.tmp"))
        {
            var name = Path.GetFileName(temporary);
            var suffix = name.Substring(prefix.Length, name.Length - prefix.Length - ".tmp".Length);
            if (suffix.Length == 32 && suffix.All(character =>
                    character >= '0' && character <= '9' || character >= 'a' && character <= 'f') &&
                File.GetLastWriteTimeUtc(temporary) < DateTime.UtcNow.AddMinutes(-5))
                File.Delete(temporary);
        }
    }

    private bool TryLoad(string candidatePath, out ExtractionStateDocument? loaded)
    {
        loaded = null;
        try
        {
            if ((File.GetAttributes(candidatePath) & FileAttributes.ReparsePoint) != 0) return false;
            byte[] data;
            using (var stream = new FileStream(
                       candidatePath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read,
                       4096,
                       FileOptions.SequentialScan))
            {
                var length = stream.Length;
                if (length < 1 || length > MaximumDocumentBytes) return false;
                data = new byte[checked((int)length)];
                var offset = 0;
                while (offset < data.Length)
                {
                    var read = stream.Read(data, offset, data.Length - offset);
                    if (read == 0) return false;
                    offset += read;
                }
                if (stream.ReadByte() != -1) return false;
            }
            using var buffer = new MemoryStream(data, writable: false);
            loaded = serializer.ReadObject(buffer) as ExtractionStateDocument;
            if (loaded?.SchemaVersion != 3 || loaded.Entries is null ||
                loaded.Entries.Count > maximumEntries)
                return false;
            var storageKeys = new HashSet<string>(StringComparer.Ordinal);
            if (loaded.Entries.Any(entry =>
                    !IsValidEntry(entry) || !storageKeys.Add(entry.StorageKey)))
                return false;
            loaded.CapacityExceeded = loaded.Entries.Count >= maximumEntries;
            return true;
        }
        catch (Exception exception) when (
            exception is IOException || exception is SerializationException || exception is UnauthorizedAccessException)
        {
            loaded = null;
            return false;
        }
    }

    private ExtractionStateEntry? GetOrCreateSuccessEntry(string storageKey)
    {
        if (entriesByKey.TryGetValue(storageKey, out var entry)) return entry;
        if (document.Entries.Count >= maximumEntries)
        {
            document.CapacityExceeded = true;
            dirty = true;
            return null;
        }
        return AddEntry(storageKey);
    }

    private ExtractionStateEntry? GetOrCreateFailureEntry(string storageKey, DateTimeOffset now)
    {
        if (entriesByKey.TryGetValue(storageKey, out var entry)) return entry;
        if (document.Entries.Count >= maximumEntries)
        {
            // A new failure must never rotate out a source whose retry delay is still
            // active. Prefer state that no longer controls probing: successful baselines,
            // then an expired failure. If every retained failure is active, preserve them
            // and leave the new source untracked instead of cascading through the whole
            // state set in stable library order.
            var nowTicks = now.UtcDateTime.Ticks;
            var removable = document.Entries.FirstOrDefault(candidate =>
                candidate.ConsecutiveFailures == 0);
            removable ??= document.Entries.FirstOrDefault(candidate =>
                candidate.RetryAtUtcTicks <= nowTicks);
            document.CapacityExceeded = true;
            dirty = true;
            if (removable is null) return null;
            entriesByKey.Remove(removable.StorageKey);
            document.Entries.Remove(removable);
        }
        return AddEntry(storageKey);
    }

    private ExtractionStateEntry AddEntry(string storageKey)
    {
        var entry = new ExtractionStateEntry { StorageKey = storageKey };
        document.Entries.Add(entry);
        entriesByKey.Add(storageKey, entry);
        return entry;
    }

    private void RebuildIndex()
    {
        entriesByKey.Clear();
        foreach (var entry in document.Entries) entriesByKey.Add(entry.StorageKey, entry);
    }

    private static void ValidateKey(string value, string parameterName)
    {
        if (!IsKey(value)) throw new ArgumentException("The HMAC identity is invalid.", parameterName);
    }

    private static bool IsKey(string? value) =>
        value?.Length == 64 && value.All(character =>
            character >= '0' && character <= '9' || character >= 'a' && character <= 'f');

    private static bool IsValidEntry(ExtractionStateEntry? entry)
    {
        if (entry is null || !IsKey(entry.StorageKey) ||
            (!string.IsNullOrEmpty(entry.LastSuccessfulSourceFingerprint) &&
             !IsKey(entry.LastSuccessfulSourceFingerprint)) ||
            entry.ConsecutiveFailures is < 0 or > MaximumConsecutiveFailures ||
            entry.RetryAtUtcTicks < 0 || entry.RetryAtUtcTicks > DateTime.MaxValue.Ticks)
            return false;

        if (entry.ConsecutiveFailures == 0)
            return entry.RetryAtUtcTicks == 0 &&
                   string.IsNullOrEmpty(entry.LastFailureSourceFingerprint);
        return entry.RetryAtUtcTicks > 0 && IsKey(entry.LastFailureSourceFingerprint);
    }

    [DataContract]
    private sealed class ExtractionStateDocument
    {
        [DataMember(Order = 1)] public int SchemaVersion { get; set; } = 3;
        [DataMember(Order = 2)] public List<ExtractionStateEntry> Entries { get; set; } = new();
        [DataMember(Order = 3, EmitDefaultValue = false)] public bool CapacityExceeded { get; set; }
    }

    [DataContract]
    private sealed class ExtractionStateEntry
    {
        [DataMember(Order = 1)] public string StorageKey { get; set; } = string.Empty;
        [DataMember(Order = 2)] public int ConsecutiveFailures { get; set; }
        [DataMember(Order = 3)] public long RetryAtUtcTicks { get; set; }
        [DataMember(Order = 4, EmitDefaultValue = false)] public string? LastSuccessfulSourceFingerprint { get; set; }
        [DataMember(Order = 5, EmitDefaultValue = false)] public string? LastFailureSourceFingerprint { get; set; }
    }
}
