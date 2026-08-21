using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Emby.StrmBridge.Persistence;

public sealed class HmacIdentityProvider
{
    public const int KeyLength = 32;
    private readonly byte[] key;

    public HmacIdentityProvider(byte[] key)
    {
        if (key is null || key.Length != KeyLength)
        {
            throw new ArgumentException("The identity key must contain exactly 256 bits.", nameof(key));
        }
        this.key = (byte[])key.Clone();
    }

    public static HmacIdentityProvider LoadOrCreate(string keyPath)
    {
        if (string.IsNullOrWhiteSpace(keyPath))
        {
            throw new ArgumentException("An identity-key path is required.", nameof(keyPath));
        }
        var directory = Path.GetDirectoryName(keyPath)
            ?? throw new ArgumentException("The identity-key path has no directory.", nameof(keyPath));
        Directory.CreateDirectory(directory);
        RemoveStaleTemporaryKeys(directory, Path.GetFileName(keyPath));

        if (File.Exists(keyPath))
        {
            return new HmacIdentityProvider(ReadValidatedKey(keyPath));
        }

        var generated = new byte[KeyLength];
        using (var random = RandomNumberGenerator.Create())
        {
            random.GetBytes(generated);
        }
        var temporary = keyPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                EnsurePrivatePermissions(temporary);
                stream.Write(generated, 0, generated.Length);
                stream.Flush(true);
            }
            try
            {
                File.Move(temporary, keyPath);
            }
            catch (IOException) when (File.Exists(keyPath))
            {
                generated = ReadValidatedKey(keyPath);
            }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        EnsurePrivatePermissions(keyPath);
        return new HmacIdentityProvider(generated);
    }

    public string Compute(string value) => Compute(Encoding.UTF8.GetBytes(value ?? string.Empty));

    public string Compute(byte[] value)
    {
        using var hmac = new HMACSHA256(key);
        return ToHex(hmac.ComputeHash(value));
    }

    internal static string ToHex(byte[] bytes)
    {
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (var value in bytes)
        {
            builder.Append(value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }

    private static byte[] ReadValidatedKey(string path)
    {
        var before = new FileInfo(path);
        before.Refresh();
        if (!before.Exists || (before.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The identity key must be a regular file.");
        EnsurePrivatePermissions(path);
        var existing = File.ReadAllBytes(path);
        var after = new FileInfo(path);
        after.Refresh();
        if (!after.Exists || (after.Attributes & FileAttributes.ReparsePoint) != 0 ||
            before.Length != after.Length || before.LastWriteTimeUtc != after.LastWriteTimeUtc ||
            existing.Length != KeyLength)
            throw new InvalidDataException("The identity key is invalid or changed during reading.");
        return existing;
    }

    private static void EnsurePrivatePermissions(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        try
        {
            if (chmod(path, Convert.ToUInt32("600", 8)) != 0)
                throw new IOException("Unable to restrict identity-key permissions.");
        }
        catch (Exception exception) when (
            exception is DllNotFoundException || exception is EntryPointNotFoundException || exception is BadImageFormatException)
        {
            throw new IOException("The platform cannot enforce identity-key permissions.", exception);
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int chmod(string path, uint mode);

    private static void RemoveStaleTemporaryKeys(string directory, string keyFileName)
    {
        var prefix = keyFileName + ".tmp-";
        foreach (var path in Directory.EnumerateFiles(directory, prefix + "*"))
        {
            var name = Path.GetFileName(path);
            var suffix = name.Substring(prefix.Length);
            if (suffix.Length == 32 && suffix.All(character =>
                    character >= '0' && character <= '9' || character >= 'a' && character <= 'f') &&
                File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddMinutes(-5))
                File.Delete(path);
        }
    }
}
