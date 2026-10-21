using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AniMetaPlus.Core;
using MediaBrowser.Common.Configuration;

namespace AniMetaPlus.Providers.AniDB.Metadata;

/// <summary>
/// Signs the addresses of identify pictures on Jellyfin. A browser asks for them without a
/// login, so the signature is what keeps the address from drawing, and fetching, anything but
/// what this server's own identify offered.
/// </summary>
internal static class IdentifySigner
{
    private const string KeyFileName = "identify-label.key";
    private const int KeyBytes = 32;

    private static readonly object _keyLock = new();
    private static byte[]? _key;

    /// <summary>
    /// Signs a payload.
    /// </summary>
    /// <param name="payload">The payload, exactly as it is verified.</param>
    /// <param name="key">The signing key.</param>
    /// <returns>The signature, in base64url.</returns>
    internal static string Sign(string payload, byte[] key)
        => ToBase64Url(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(payload)));

    /// <summary>
    /// Checks a payload's signature.
    /// </summary>
    /// <param name="payload">The payload.</param>
    /// <param name="signature">The signature, in base64url.</param>
    /// <param name="key">The signing key.</param>
    /// <returns><c>true</c> when the signature is the payload's.</returns>
    internal static bool Verify(string payload, string signature, byte[] key)
    {
        byte[] given;

        try
        {
            given = FromBase64Url(signature);
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(payload)), given);
    }

    /// <summary>
    /// Gets this install's signing key: random, made on first use, kept in the plugin's data
    /// folder and never written to a log or exported with the settings.
    /// </summary>
    /// <param name="paths">The server paths.</param>
    /// <returns>The key.</returns>
    internal static byte[] Key(IApplicationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (_key is { } known)
        {
            return known;
        }

        lock (_keyLock)
        {
            return _key ??= ReadOrCreate(Path.Combine(PluginPaths.DataFolder(paths), KeyFileName));
        }
    }

    // A file that is not a whole key - cut short, or edited - is replaced: it only makes addresses already handed out stop working.
    private static byte[] ReadOrCreate(string path)
    {
        if (File.Exists(path))
        {
            var existing = File.ReadAllBytes(path);

            if (existing.Length == KeyBytes)
            {
                return existing;
            }
        }

        var key = RandomNumberGenerator.GetBytes(KeyBytes);
        File.WriteAllBytes(path, key);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return key;
    }

    private static string ToBase64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        var base64 = text.Replace('-', '+').Replace('_', '/');

        return Convert.FromBase64String(base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '='));
    }
}
