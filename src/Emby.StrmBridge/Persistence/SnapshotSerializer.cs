using System;
using System.IO;
using System.Runtime.Serialization.Json;
using Emby.StrmBridge.Domain;

namespace Emby.StrmBridge.Persistence;

public sealed class SnapshotSerializer
{
    public const int MaximumSerializedBytes = 512 * 1024;

    public byte[] Serialize(MediaInfoSnapshot snapshot)
    {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));
        using var stream = new MemoryStream();
        CreateSerializer().WriteObject(stream, snapshot);
        if (stream.Length > MaximumSerializedBytes)
            throw new InvalidDataException("The snapshot exceeds the configured size limit.");
        return stream.ToArray();
    }

    public MediaInfoSnapshot Deserialize(byte[] data)
    {
        if (data is null || data.Length == 0) throw new InvalidDataException("The snapshot is empty.");
        if (data.Length > MaximumSerializedBytes)
            throw new InvalidDataException("The snapshot exceeds the configured size limit.");
        using var stream = new MemoryStream(data, writable: false);
        var result = CreateSerializer().ReadObject(stream) as MediaInfoSnapshot
            ?? throw new InvalidDataException("The snapshot has an invalid shape.");
        if (result.SchemaVersion != MediaInfoSnapshot.CurrentSchemaVersion ||
            string.IsNullOrWhiteSpace(result.SourceFingerprint) ||
            result.MediaStreams is null ||
            result.MediaStreams.Count > MediaInfoSnapshot.MaximumMediaStreams)
        {
            throw new InvalidDataException("The snapshot schema is unsupported or incomplete.");
        }
        return result;
    }

    private static DataContractJsonSerializer CreateSerializer() => new(typeof(MediaInfoSnapshot));
}
