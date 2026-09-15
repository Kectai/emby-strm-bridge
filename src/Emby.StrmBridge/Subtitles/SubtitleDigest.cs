using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Entities;
using System.Security.Cryptography;
using System.Text;

namespace Emby.StrmBridge.Subtitles;

internal static class SubtitleDigest
{
    internal static string Streams(IEnumerable<MediaStream> streams) => Compute(string.Join(";", streams.Select(stream =>
        stream.Index + ":" + stream.Type + ":" + stream.Codec + ":" + stream.Language + ":" + stream.IsExternal)));

    internal static string Compute(string value)
    {
        using var hash = SHA256.Create();
        return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant();
    }
}
