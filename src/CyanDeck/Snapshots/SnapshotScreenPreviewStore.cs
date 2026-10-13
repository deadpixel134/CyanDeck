using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;

namespace CyanDeck.Snapshots;

/// <summary>
/// Sidecar store for actual-screen snapshot thumbnails. Snapshot names and paths are never
/// used as filenames; a deterministic SHA-256 digest of the stable snapshot ID is used.
/// </summary>
internal sealed class SnapshotScreenPreviewStore
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private readonly string _root;

    internal SnapshotScreenPreviewStore(string appDataRoot)
    {
        if (string.IsNullOrWhiteSpace(appDataRoot))
        {
            throw new ArgumentException("App-data root is required.", nameof(appDataRoot));
        }

        _root = Path.Combine(Path.GetFullPath(appDataRoot), "snapshot-previews");
    }

    internal string RootDirectory => _root;

    internal bool TryWriteAtomic(string snapshotId, ReadOnlySpan<byte> pngBytes, long maxBytes)
    {
        if (!TryGetPath(snapshotId, out string path) ||
            pngBytes.Length <= PngSignature.Length ||
            pngBytes.Length > maxBytes ||
            !pngBytes[..PngSignature.Length].SequenceEqual(PngSignature))
        {
            return false;
        }

        Directory.CreateDirectory(_root);
        string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(
                       temp,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       64 * 1024,
                       FileOptions.WriteThrough))
            {
                stream.Write(pngBytes);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                try
                {
                    File.Replace(temp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Move(temp, path, overwrite: true);
                }
            }
            else
            {
                File.Move(temp, path);
            }
            return true;
        }
        catch (IOException)
        {
            TryDeleteFile(temp);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            TryDeleteFile(temp);
            return false;
        }
    }

    internal BitmapSource? TryLoad(string snapshotId, long maxBytes)
    {
        if (!TryGetPath(snapshotId, out string path))
        {
            return null;
        }

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= PngSignature.Length || info.Length > maxBytes)
            {
                return null;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> signature = stackalloc byte[PngSignature.Length];
            if (stream.Read(signature) != signature.Length || !signature.SequenceEqual(PngSignature))
            {
                return null;
            }

            stream.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    internal void Delete(string snapshotId)
    {
        if (TryGetPath(snapshotId, out string path))
        {
            TryDeleteFile(path);
        }
    }

    internal int PruneToSnapshotIds(IEnumerable<string> liveSnapshotIds)
    {
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string id in liveSnapshotIds ?? Array.Empty<string>())
        {
            if (TryGetPath(id, out string path))
            {
                keep.Add(Path.GetFileName(path));
            }
        }

        if (!Directory.Exists(_root))
        {
            return 0;
        }

        int deleted = 0;
        foreach (string path in Directory.EnumerateFiles(_root, "preview-*.png", SearchOption.TopDirectoryOnly))
        {
            if (keep.Contains(Path.GetFileName(path)))
            {
                continue;
            }

            if (TryDeleteFile(path))
            {
                deleted++;
            }
        }

        foreach (string path in Directory.EnumerateFiles(_root, "preview-*.png.tmp-*", SearchOption.TopDirectoryOnly))
        {
            if (TryDeleteFile(path))
            {
                deleted++;
            }
        }

        return deleted;
    }

    internal bool TryGetPath(string snapshotId, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(snapshotId) || snapshotId.Length > 4096)
        {
            return false;
        }

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(snapshotId));
        string key = Convert.ToHexString(digest).ToLowerInvariant();
        path = Path.Combine(_root, $"preview-{key}.png");
        return true;
    }

    private static bool TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
