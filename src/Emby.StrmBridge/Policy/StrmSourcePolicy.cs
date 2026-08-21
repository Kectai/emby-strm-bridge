using System;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using Emby.StrmBridge.Domain;
using Emby.StrmBridge.Persistence;

namespace Emby.StrmBridge.Policy;

public sealed class StrmSourcePolicy
{
    public const int DefaultMaximumFileSize = 16 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly HmacIdentityProvider identityProvider;
    private readonly int maximumFileSize;

    public StrmSourcePolicy(HmacIdentityProvider identityProvider, int maximumFileSize = DefaultMaximumFileSize)
    {
        this.identityProvider = identityProvider ?? throw new ArgumentNullException(nameof(identityProvider));
        if (maximumFileSize < 1 || maximumFileSize > DefaultMaximumFileSize)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumFileSize));
        }
        this.maximumFileSize = maximumFileSize;
    }

    public SourceIdentity Read(string path)
    {
        try
        {
            return ReadCore(path);
        }
        catch (SourcePolicyException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException || exception is UnauthorizedAccessException ||
            exception is SecurityException || exception is ArgumentException ||
            exception is NotSupportedException)
        {
            throw new SourcePolicyException(SourceRejectionReason.FileAccessFailure);
        }
    }

    public string GetStorageKey(string path)
    {
        try
        {
            return identityProvider.Compute(NormalizePath(ValidateAndGetFullPath(path)));
        }
        catch (SourcePolicyException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException || exception is UnauthorizedAccessException ||
            exception is SecurityException || exception is ArgumentException ||
            exception is NotSupportedException)
        {
            throw new SourcePolicyException(SourceRejectionReason.FileAccessFailure);
        }
    }

    private SourceIdentity ReadCore(string path)
    {
        var fullPath = ValidateAndGetFullPath(path);
        RejectReparsePointsInPath(fullPath);
        var before = GetFileInfo(fullPath);
        if ((before.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
        {
            throw new SourcePolicyException(SourceRejectionReason.SymbolicLink);
        }
        if (before.Length < 1 || before.Length > maximumFileSize)
        {
            throw new SourcePolicyException(SourceRejectionReason.FileTooLarge);
        }

        byte[] bytes;
        using (var stream = new FileStream(
                   fullPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   4096,
                   FileOptions.SequentialScan))
        {
            if (stream.Length != before.Length)
                throw new SourcePolicyException(SourceRejectionReason.FileChanged);
            bytes = new byte[before.Length];
            var offset = 0;
            while (offset < bytes.Length)
            {
                var read = stream.Read(bytes, offset, bytes.Length - offset);
                if (read == 0) break;
                offset += read;
            }
            if (offset != bytes.Length || stream.ReadByte() != -1)
            {
                throw new SourcePolicyException(SourceRejectionReason.FileChanged);
            }
        }

        RejectReparsePointsInPath(fullPath);
        var after = GetFileInfo(fullPath);
        if ((after.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 ||
            before.Length != after.Length || before.LastWriteTimeUtc != after.LastWriteTimeUtc)
        {
            throw new SourcePolicyException(SourceRejectionReason.FileChanged);
        }

        string content;
        try
        {
            var offset = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
            content = StrictUtf8.GetString(bytes, offset, bytes.Length - offset).Trim();
        }
        catch (DecoderFallbackException)
        {
            throw new SourcePolicyException(SourceRejectionReason.InvalidEncoding);
        }

        if (content.Length == 0 || content.IndexOfAny(new[] { '\r', '\n' }) >= 0)
        {
            throw new SourcePolicyException(SourceRejectionReason.InvalidRecordCount);
        }
        if (content.Any(character => char.IsControl(character)))
        {
            throw new SourcePolicyException(SourceRejectionReason.UnsafeUrl);
        }
        if (!Uri.TryCreate(content, UriKind.Absolute, out var uri))
        {
            throw new SourcePolicyException(SourceRejectionReason.InvalidUrl);
        }
        if (!IsHttp(uri) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new SourcePolicyException(SourceRejectionReason.UnsafeUrl);
        }

        var canonicalPath = NormalizePath(fullPath);
        return new SourceIdentity(
            identityProvider.Compute(canonicalPath),
            identityProvider.Compute(bytes),
            uri,
            fullPath,
            after.Length,
            new DateTimeOffset(after.LastWriteTimeUtc, TimeSpan.Zero));
    }

    private static FileInfo GetFileInfo(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new SourcePolicyException(SourceRejectionReason.MissingFile);
        }
        info.Refresh();
        return info;
    }

    private static void RejectReparsePointsInPath(string path)
    {
        var current = new FileInfo(path).Directory;
        while (current is not null)
        {
            current.Refresh();
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new SourcePolicyException(SourceRejectionReason.SymbolicLink);
            current = current.Parent;
        }
    }

    private static bool IsHttp(Uri uri) =>
        string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    private static string ValidateAndGetFullPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !string.Equals(Path.GetExtension(path), ".strm", StringComparison.OrdinalIgnoreCase) ||
            !Path.IsPathRooted(path))
        {
            throw new SourcePolicyException(SourceRejectionReason.NotLocalStrm);
        }
        return Path.GetFullPath(path);
    }

    private static string NormalizePath(string path)
    {
        var normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return Path.DirectorySeparatorChar == '\\' ? normalized.ToUpperInvariant() : normalized;
    }
}
