using System;
using System.IO;
using System.Linq;
using Emby.StrmBridge.Domain;

namespace Emby.StrmBridge.Persistence;

public sealed class MediaInfoStore
{
    private const int LockStripeCount = 64;
    private readonly string directoryPath;
    private readonly SnapshotSerializer serializer;
    private readonly object[] locks = Enumerable.Range(0, LockStripeCount).Select(_ => new object()).ToArray();

    public MediaInfoStore(string directoryPath, SnapshotSerializer serializer)
    {
        this.directoryPath = directoryPath ?? throw new ArgumentNullException(nameof(directoryPath));
        this.serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        Directory.CreateDirectory(directoryPath);
    }

    public void Save(SourceIdentity source, MediaInfoSnapshot snapshot)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (snapshot is null || !snapshot.Matches(source))
        {
            throw new ArgumentException("The snapshot does not match the current source.", nameof(snapshot));
        }
        var path = GetPath(source.StorageKey);
        lock (GetLock(source.StorageKey))
        {
            var primaryIsValid = TryRead(path, out _);
            var backupIsValid = TryRead(path + ".bak", out _);
            // Legacy schema 2 is readable evidence for safe replacement, but never restoration.
            var replaceableLegacy = !primaryIsValid &&
                                    TryRead(path, out var legacy, allowLegacyReplacement: true) &&
                                    legacy!.SchemaVersion == 2;
            if (File.Exists(path) && !primaryIsValid && !backupIsValid && !replaceableLegacy)
                throw new InvalidDataException("The existing snapshot and its backup are unreadable.");
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                WriteFully(temporary, serializer.Serialize(snapshot));
                if (File.Exists(path))
                {
                    File.Replace(temporary, path, primaryIsValid ? path + ".bak" : null);
                }
                else
                {
                    File.Move(temporary, path);
                }
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }

    public bool TryLoad(SourceIdentity source, out MediaInfoSnapshot? snapshot)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        var path = GetPath(source.StorageKey);
        lock (GetLock(source.StorageKey))
        {
            if (TryRead(path, out snapshot) && SafelyMatches(snapshot!, source)) return true;
            if (TryRead(path + ".bak", out snapshot) && SafelyMatches(snapshot!, source)) return true;
            snapshot = null;
            return false;
        }
    }

    public int RemoveOrphans(Func<string, bool> shouldKeep)
    {
        if (shouldKeep is null) throw new ArgumentNullException(nameof(shouldKeep));
        var removed = 0;
        foreach (var path in Directory.EnumerateFiles(directoryPath, "*.json"))
        {
            var key = Path.GetFileNameWithoutExtension(path);
            if (key.Length == 64 && !shouldKeep(key))
            {
                File.Delete(path);
                var backup = path + ".bak";
                if (File.Exists(backup)) File.Delete(backup);
                removed++;
            }
        }
        foreach (var path in Directory.EnumerateFiles(directoryPath, "*.json.bak"))
        {
            var name = Path.GetFileName(path);
            var key = name.Substring(0, name.Length - ".json.bak".Length);
            if (IsStorageKey(key) && !shouldKeep(key))
            {
                File.Delete(path);
                removed++;
            }
        }
        return removed;
    }

    public int RemoveTemporaryFiles()
    {
        var removed = 0;
        foreach (var path in Directory.EnumerateFiles(directoryPath, "*.tmp"))
        {
            var name = Path.GetFileName(path);
            if (IsSnapshotTemporaryName(name) &&
                File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddMinutes(-5))
            {
                File.Delete(path);
                removed++;
            }
        }
        return removed;
    }

    public bool Remove(string storageKey)
    {
        var path = GetPath(storageKey);
        lock (GetLock(storageKey))
        {
            var removed = false;
            var backup = path + ".bak";
            if (File.Exists(backup))
            {
                File.Delete(backup);
                removed = true;
            }
            if (File.Exists(path))
            {
                File.Delete(path);
                removed = true;
            }
            return removed;
        }
    }

    public int Clear() => RemoveOrphans(_ => false);

    private string GetPath(string storageKey)
    {
        if (!IsStorageKey(storageKey))
        {
            throw new ArgumentException("The storage key is invalid.", nameof(storageKey));
        }
        return Path.Combine(directoryPath, storageKey + ".json");
    }

    private bool TryRead(string path, out MediaInfoSnapshot? snapshot, bool allowLegacyReplacement = false)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < 1 || info.Length > SnapshotSerializer.MaximumSerializedBytes)
            {
                snapshot = null;
                return false;
            }
            snapshot = serializer.Deserialize(File.ReadAllBytes(path), allowLegacyReplacement);
            return snapshot is not null;
        }
        catch (Exception exception) when (
            exception is IOException || exception is UnauthorizedAccessException ||
            exception is InvalidDataException || exception is System.Runtime.Serialization.SerializationException)
        {
            snapshot = null;
            return false;
        }
    }

    private object GetLock(string storageKey) =>
        locks[(int)((uint)StringComparer.Ordinal.GetHashCode(storageKey) % LockStripeCount)];

    private static bool SafelyMatches(MediaInfoSnapshot snapshot, SourceIdentity source)
    {
        try { return snapshot.Matches(source); }
        catch (Exception exception) when (
            exception is ArgumentException || exception is InvalidOperationException ||
            exception is NullReferenceException)
        {
            return false;
        }
    }

    private static bool IsStorageKey(string? storageKey) =>
        storageKey?.Length == 64 && storageKey.All(character =>
            character >= '0' && character <= '9' || character >= 'a' && character <= 'f');

    private static bool IsSnapshotTemporaryName(string name)
    {
        const string marker = ".json.";
        const string suffix = ".tmp";
        if (!name.EndsWith(suffix, StringComparison.Ordinal) ||
            name.Length != 64 + marker.Length + 32 + suffix.Length ||
            !string.Equals(name.Substring(64, marker.Length), marker, StringComparison.Ordinal) ||
            !IsStorageKey(name.Substring(0, 64)))
            return false;
        return name.Substring(64 + marker.Length, 32).All(character =>
            character >= '0' && character <= '9' || character >= 'a' && character <= 'f');
    }

    private static void WriteFully(string path, byte[] data)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(data, 0, data.Length);
        stream.Flush(true);
    }
}
