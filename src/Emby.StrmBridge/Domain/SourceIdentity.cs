using System;

namespace Emby.StrmBridge.Domain;

public sealed class SourceIdentity
{
    public SourceIdentity(
        string storageKey,
        string sourceFingerprint,
        Uri sourceUri,
        string localPath,
        long localFileLength,
        DateTimeOffset localLastWriteUtc)
    {
        StorageKey = storageKey ?? throw new ArgumentNullException(nameof(storageKey));
        SourceFingerprint = sourceFingerprint ?? throw new ArgumentNullException(nameof(sourceFingerprint));
        SourceUri = sourceUri ?? throw new ArgumentNullException(nameof(sourceUri));
        LocalPath = localPath ?? throw new ArgumentNullException(nameof(localPath));
        LocalFileLength = localFileLength;
        LocalLastWriteUtc = localLastWriteUtc;
    }

    public string StorageKey { get; }

    public string SourceFingerprint { get; }

    internal Uri SourceUri { get; }

    internal string LocalPath { get; }

    public long LocalFileLength { get; }

    public DateTimeOffset LocalLastWriteUtc { get; }

    public bool HasSameFileVersion(SourceIdentity other) =>
        other is not null &&
        string.Equals(StorageKey, other.StorageKey, StringComparison.Ordinal) &&
        string.Equals(SourceFingerprint, other.SourceFingerprint, StringComparison.Ordinal) &&
        LocalFileLength == other.LocalFileLength &&
        LocalLastWriteUtc.UtcTicks == other.LocalLastWriteUtc.UtcTicks;
}
