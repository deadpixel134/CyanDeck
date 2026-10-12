using System;
using System.IO;

namespace DesktopDeck.ManagedDeployment;

public sealed class ManagedPolicyCache
{
    private readonly string _cachePath;

    public ManagedPolicyCache(string cachePath)
    {
        if (!Path.IsPathFullyQualified(cachePath))
            throw new ArgumentException("Cache path must be fully qualified.", nameof(cachePath));
        _cachePath = cachePath;
    }

    public string CachePath => _cachePath;

    public void Store(byte[] rawUtf8)
    {
        ArgumentNullException.ThrowIfNull(rawUtf8);
        if (rawUtf8.Length > ManagedPolicyReader.MaxPolicyBytes)
            throw new IOException("Managed policy cache payload is too large.");

        var directory = Path.GetDirectoryName(_cachePath) ?? throw new IOException("Cache directory is unavailable.");
        Directory.CreateDirectory(directory);
        var tempPath = _cachePath + ".tmp";
        using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.WriteThrough))
        {
            stream.Write(rawUtf8);
            stream.Flush(flushToDisk: true);
        }
        try
        {
            if (File.Exists(_cachePath))
            {
                try
                {
                    File.Replace(tempPath, _cachePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                    return;
                }
                catch (PlatformNotSupportedException)
                {
                    // Fall through to Move with overwrite.
                }
                catch (IOException)
                {
                    // Fall through to Move with overwrite.
                }
            }
            File.Move(tempPath, _cachePath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    public ManagedPolicyLoadResult TryLoad(ManagedPolicyReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (!File.Exists(_cachePath))
            return ManagedPolicyLoadResult.Fail("No managed policy cache exists.");
        return reader.Load(_cachePath, ManagedPolicySourceKind.Cache);
    }
}
