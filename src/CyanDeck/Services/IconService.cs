using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CyanDeck.Services;

public readonly record struct IconCacheStats(int Count, int Capacity, long Hits, long Misses, long Evictions);

public sealed class IconService
{
    private sealed class CacheEntry
    {
        public required ImageSource Image { get; init; }
        public required LinkedListNode<string> Node { get; init; }
        public required string Path { get; init; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _lru = new();
    private int _capacity = 1024;
    private bool _showFilePreviews = true;
    private long _hits;
    private long _misses;
    private long _evictions;

    public void Configure(int capacity, bool showFilePreviews = true)
    {
        lock (_gate)
        {
            _capacity = Math.Clamp(capacity, 128, 4096);
            if (_showFilePreviews != showFilePreviews)
            {
                _showFilePreviews = showFilePreviews;
                _cache.Clear();
                _lru.Clear();
            }
            TrimLocked();
        }
    }

    public ImageSource Get(string path, double requestedSize = 48, Color? tint = null, double tintStrength = 0, bool suppressShortcutOverlay = false)
    {
        bool showPreviews;
        lock (_gate) showPreviews = _showFilePreviews;
        var key = $"{path}|{(int)requestedSize}|{tint?.ToString() ?? "none"}|{tintStrength:F2}|preview={showPreviews}|shortcutOverlay={!suppressShortcutOverlay}";
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var cached))
            {
                _hits++;
                _lru.Remove(cached.Node);
                _lru.AddFirst(cached.Node);
                return cached.Image;
            }
            _misses++;
        }

        // Shell icon extraction can be relatively expensive (especially for network paths).
        // Load outside the cache lock so other already-cached icons remain responsive.
        var loaded = Load(path, requestedSize, tint, tintStrength, showPreviews, suppressShortcutOverlay);
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var raced))
            {
                _lru.Remove(raced.Node);
                _lru.AddFirst(raced.Node);
                return raced.Image;
            }
            var node = new LinkedListNode<string>(key);
            _lru.AddFirst(node);
            _cache[key] = new CacheEntry { Image = loaded, Node = node, Path = path };
            TrimLocked();
            return loaded;
        }
    }

    public void Invalidate(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        lock (_gate)
        {
            var keys = _cache.Where(kv => string.Equals(kv.Value.Path, path, StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Key).ToArray();
            foreach (var key in keys) RemoveLocked(key, countEviction: false);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _cache.Clear();
            _lru.Clear();
        }
    }

    public IconCacheStats GetStats()
    {
        lock (_gate) return new IconCacheStats(_cache.Count, _capacity, _hits, _misses, _evictions);
    }

    private void TrimLocked()
    {
        while (_cache.Count > _capacity && _lru.Last is not null)
            RemoveLocked(_lru.Last.Value, countEviction: true);
    }

    private void RemoveLocked(string key, bool countEviction)
    {
        if (!_cache.Remove(key, out var entry)) return;
        _lru.Remove(entry.Node);
        if (countEviction) _evictions++;
    }

    private static ImageSource Load(string path, double requestedSize, Color? tint, double tintStrength, bool showFilePreviews, bool suppressShortcutOverlay)
    {
        if (DesktopSystemItems.IsRecycleBinPath(path))
        {
            // Use the same system image-list tier Explorer uses for desktop icons. The generic
            // IShellItemImageFactory result for the Recycle Bin can be tightly cropped on some
            // Windows builds/themes, which becomes obvious when the surrogate is enlarged.
            var recycleIcon = TryLoadRecycleBinStockIcon();
            if (recycleIcon is not null)
            {
                if (tint is null || tintStrength <= 0.001) return recycleIcon;
                return Tint(recycleIcon, tint.Value, Math.Clamp(tintStrength, 0, 1));
            }
        }

        if (DesktopSystemItems.IsShellNamespacePath(path))
        {
            var shellIcon = TryLoadShellNamespaceIcon(path, requestedSize);
            if (shellIcon is not null)
            {
                if (tint is null || tintStrength <= 0.001) return shellIcon;
                return Tint((BitmapSource)shellIcon, tint.Value, Math.Clamp(tintStrength, 0, 1));
            }
        }

        var exists = File.Exists(path) || Directory.Exists(path);
        if (showFilePreviews && File.Exists(path) && !IsShortcut(path))
        {
            var preview = TryLoadShellPreview(path, requestedSize);
            if (preview is not null)
            {
                if (tint is null || tintStrength <= 0.001) return preview;
                return Tint((BitmapSource)preview, tint.Value, Math.Clamp(tintStrength, 0, 1));
            }
        }
        var flags = SHGFI_ICON | (requestedSize >= 40 ? SHGFI_LARGEICON : SHGFI_SMALLICON);
        if (IsShortcut(path) && !suppressShortcutOverlay) flags |= SHGFI_LINKOVERLAY;
        if (!exists) flags |= SHGFI_USEFILEATTRIBUTES;
        var attrs = Directory.Exists(path) ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
        var result = SHGetFileInfo(path, attrs, out var info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);

        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            return CreateFallback();

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            if (tint is null || tintStrength <= 0.001) return source;
            return Tint(source, tint.Value, Math.Clamp(tintStrength, 0, 1));
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }


    /// <summary>
    /// True when the Recycle Bin holds at least one item, false when empty, null when the
    /// state could not be read. Used by <see cref="RecycleBinStateMonitor"/> on a worker thread.
    /// </summary>
    internal static bool? QueryRecycleBinHasItems()
    {
        try
        {
            var recycleInfo = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
            return SHQueryRecycleBin(null, ref recycleInfo) == 0 ? recycleInfo.i64NumItems > 0 : null;
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource? TryLoadRecycleBinStockIcon()
    {
        object? imageListObject = null;
        IntPtr icon = IntPtr.Zero;
        try
        {
            var recycleInfo = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
            var stockId = SHQueryRecycleBin(null, ref recycleInfo) == 0 && recycleInfo.i64NumItems > 0
                ? SIID_RECYCLERFULL
                : SIID_RECYCLER;

            var stock = new SHSTOCKICONINFO { cbSize = (uint)Marshal.SizeOf<SHSTOCKICONINFO>() };
            var hr = SHGetStockIconInfo(stockId, SHGSI_SYSICONINDEX, ref stock);
            if (hr < 0 || stock.iSysImageIndex < 0) return null;

            var iid = typeof(IImageList).GUID;
            hr = SHGetImageList(SHIL_EXTRALARGE, ref iid, out imageListObject);
            if (hr < 0 || imageListObject is not IImageList imageList) return null;

            hr = imageList.GetIcon(stock.iSysImageIndex, ILD_TRANSPARENT, out icon);
            if (hr < 0 || icon == IntPtr.Zero) return null;

            var source = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (icon != IntPtr.Zero) DestroyIcon(icon);
            if (imageListObject is not null && Marshal.IsComObject(imageListObject))
            {
                try { Marshal.FinalReleaseComObject(imageListObject); } catch { }
            }
        }
    }

    private static BitmapSource? TryLoadShellNamespaceIcon(string parsingName, double requestedSize)
    {
        // SHGFI_LARGEICON is only the system "large" icon size (typically 32 px at 96 DPI),
        // which is smaller than Explorer's desktop icon tier.  Prefer IShellItemImageFactory so
        // virtual Shell items (Recycle Bin, This PC, etc.) can provide a true large icon.
        var factoryImage = TryLoadShellItemIcon(parsingName, requestedSize);
        if (factoryImage is not null) return factoryImage;

        // Compatibility fallback for namespace extensions that do not expose
        // IShellItemImageFactory.  This preserves the old behaviour rather than showing no icon.
        IntPtr pidl = IntPtr.Zero;
        try
        {
            var hr = SHParseDisplayName(parsingName, IntPtr.Zero, out pidl, 0, out _);
            if (hr < 0 || pidl == IntPtr.Zero) return null;

            var flags = SHGFI_PIDL | SHGFI_ICON | (requestedSize >= 40 ? SHGFI_LARGEICON : SHGFI_SMALLICON);
            var result = SHGetFileInfoPidl(pidl, 0, out var info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
            if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                DestroyIcon(info.hIcon);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
        }
    }

    private static BitmapSource? TryLoadShellItemIcon(string parsingName, double requestedSize)
    {
        object? factoryObject = null;
        IntPtr bitmap = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            var hr = SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out factoryObject);
            if (hr < 0 || factoryObject is not IShellItemImageFactory factory) return null;

            // Request roughly 2x the WPF display size.  This gives the compositor a proper
            // desktop-class source image to downsample instead of stretching a 32 px HICON.
            var px = Math.Clamp((int)Math.Ceiling(Math.Max(64, requestedSize * 2)), 64, 256);
            var flags = SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK | SIIGBF_SCALEUP;
            if (Application.Current?.Dispatcher?.CheckAccess() == true) flags |= SIIGBF_INCACHEONLY;

            hr = factory.GetImage(new NativeSize(px, px), flags, out bitmap);
            if (hr < 0 || bitmap == IntPtr.Zero) return null;

            var source = Imaging.CreateBitmapSourceFromHBitmap(
                bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (factoryObject is not null && Marshal.IsComObject(factoryObject))
            {
                try { Marshal.FinalReleaseComObject(factoryObject); } catch { }
            }
        }
    }

    private static bool IsShortcut(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".url", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".website", StringComparison.OrdinalIgnoreCase);
    }

    private static BitmapSource? TryLoadShellPreview(string path, double requestedSize)
    {
        object? factoryObject = null;
        IntPtr bitmap = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            var hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factoryObject);
            if (hr < 0 || factoryObject is not IShellItemImageFactory factory) return null;

            var px = Math.Clamp((int)Math.Ceiling(Math.Max(48, requestedSize * 2)), 48, 512);
            // Full extraction runs only from the existing background item-loading paths.
            // If a caller reaches us on the WPF UI thread, use cache-only mode and fall back
            // to the normal shell icon rather than blocking the UI on a thumbnail provider.
            var flags = SIIGBF_BIGGERSIZEOK;
            if (Application.Current?.Dispatcher?.CheckAccess() == true) flags |= SIIGBF_INCACHEONLY;
            hr = factory.GetImage(new NativeSize(px, px), flags, out bitmap);
            if (hr < 0 || bitmap == IntPtr.Zero) return null;

            var source = Imaging.CreateBitmapSourceFromHBitmap(
                bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (factoryObject is not null && Marshal.IsComObject(factoryObject))
            {
                try { Marshal.FinalReleaseComObject(factoryObject); } catch { }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int cx;
        public int cy;
        public NativeSize(int width, int height) { cx = width; cy = height; }
    }

    [ComImport]
    [Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, uint flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHSTOCKICONINFO
    {
        public uint cbSize;
        public IntPtr hIcon;
        public int iSysImageIndex;
        public int iIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szPath;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [ComImport]
    [Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IImageList
    {
        [PreserveSig] int Add(IntPtr hbmImage, IntPtr hbmMask, out int pi);
        [PreserveSig] int ReplaceIcon(int i, IntPtr hicon, out int pi);
        [PreserveSig] int SetOverlayImage(int iImage, int iOverlay);
        [PreserveSig] int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);
        [PreserveSig] int AddMasked(IntPtr hbmImage, int crMask, out int pi);
        [PreserveSig] int Draw(IntPtr pimldp);
        [PreserveSig] int Remove(int i);
        [PreserveSig] int GetIcon(int i, int flags, out IntPtr picon);
    }

    private const int SIID_RECYCLER = 31;
    private const int SIID_RECYCLERFULL = 32;
    private const uint SHGSI_SYSICONINDEX = 0x00004000;
    private const int SHIL_EXTRALARGE = 0x2;
    private const int ILD_TRANSPARENT = 0x00000001;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHGetStockIconInfo(int siid, uint uFlags, ref SHSTOCKICONINFO psii);

    [DllImport("shell32.dll", EntryPoint = "#727", PreserveSig = true)]
    private static extern int SHGetImageList(int iImageList, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object? ppvObj);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHQueryRecycleBin([MarshalAs(UnmanagedType.LPWStr)] string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    private const uint SIIGBF_BIGGERSIZEOK = 0x00000001;
    private const uint SIIGBF_ICONONLY = 0x00000004;
    private const uint SIIGBF_INCACHEONLY = 0x00000010;
    private const uint SIIGBF_SCALEUP = 0x00000100;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object? ppv);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    private static BitmapSource Tint(BitmapSource source, Color tint, double strength)
    {
        var formatted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = formatted.PixelWidth * 4;
        var pixels = new byte[stride * formatted.PixelHeight];
        formatted.CopyPixels(pixels, stride, 0);

        for (var i = 0; i < pixels.Length; i += 4)
        {
            var a = pixels[i + 3];
            if (a == 0) continue;
            pixels[i] = Blend(pixels[i], tint.B, strength);
            pixels[i + 1] = Blend(pixels[i + 1], tint.G, strength);
            pixels[i + 2] = Blend(pixels[i + 2], tint.R, strength);
        }

        var output = BitmapSource.Create(formatted.PixelWidth, formatted.PixelHeight, formatted.DpiX, formatted.DpiY,
            PixelFormats.Bgra32, null, pixels, stride);
        output.Freeze();
        return output;
    }

    private static byte Blend(byte source, byte target, double strength)
        => (byte)Math.Clamp((int)Math.Round(source * (1 - strength) + target * strength), 0, 255);

    private static ImageSource CreateFallback()
    {
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(110, 120, 140)), null,
            new RectangleGeometry(new Rect(4, 4, 24, 24), 4, 4)));
        drawing.Freeze();
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_PIDL = 0x000000008;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_SMALLICON = 0x000000001;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    private const uint SHGFI_LINKOVERLAY = 0x000008000;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, out SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHGetFileInfoW")]
    private static extern IntPtr SHGetFileInfoPidl(IntPtr pidl, uint dwFileAttributes, out SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszName,
        IntPtr pbc,
        out IntPtr ppidl,
        uint sfgaoIn,
        out uint psfgaoOut);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
