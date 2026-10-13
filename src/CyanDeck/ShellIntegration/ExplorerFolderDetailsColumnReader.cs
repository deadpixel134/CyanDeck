using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.CSharp.RuntimeBinder;
using System.Runtime.InteropServices;
using System.Threading;

namespace CyanDeck.ShellIntegration;

/// <summary>
/// Reads visible Details columns from a File Explorer window that is already showing the same
/// filesystem path. This companion to v6.7 is read-only: it never opens Explorer, never reads or
/// writes persisted Bags/BagMRU state, and never polls.
/// </summary>
internal sealed class ExplorerFolderDetailsColumnReader
{
    private static readonly Guid IidServiceProvider = new("6D5140C1-7436-11CE-8034-00AA006009FA");
    private static readonly Guid SidTopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid IidShellBrowser = new("000214E2-0000-0000-C000-000000000046");
    private static readonly Guid IidColumnManager = new("D8EC27BB-3F3B-4042-B10A-4ACFD924D453");

    private const int ExplorerDetailsViewMode = 4;
    private const uint CmEnumVisible = 0x2;
    private const uint CmMaskWidth = 0x1;
    private const uint CmMaskState = 0x10;
    private const uint MaxVisibleColumns = 64;
    private const uint DefaultDpi = 96;

    // IServiceProvider::QueryService and IShellBrowser::QueryActiveShellView.
    private const int QueryServiceSlot = 3;
    private const int QueryActiveShellViewSlot = 15;

    // IColumnManager inherits IUnknown: SetColumnInfo=3, GetColumnInfo=4,
    // GetColumnCount=5, GetColumns=6, SetColumns=7.
    private const int GetColumnInfoSlot = 4;
    private const int GetColumnCountSlot = 5;
    private const int GetColumnsSlot = 6;

    internal ExplorerFolderDetailsColumnReadResult TryRead(string folderPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return ExplorerFolderDetailsColumnReadResult.Unavailable(
                ExplorerFolderDetailsColumnReadStatus.NotWindows);
        }

        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            return ExplorerFolderDetailsColumnReadResult.Unavailable(
                ExplorerFolderDetailsColumnReadStatus.RequiresStaThread);
        }

        if (!TryNormalizeFilesystemPath(folderPath, out string targetPath))
        {
            return ExplorerFolderDetailsColumnReadResult.Unavailable(
                ExplorerFolderDetailsColumnReadStatus.InvalidPath);
        }

        Type? shellType = Type.GetTypeFromProgID("Shell.Application", throwOnError: false);
        if (shellType is null)
        {
            return ExplorerFolderDetailsColumnReadResult.Unavailable(
                ExplorerFolderDetailsColumnReadStatus.ShellAutomationUnavailable);
        }

        object? shellObject = null;
        object? windowsObject = null;
        try
        {
            shellObject = Activator.CreateInstance(shellType);
            if (shellObject is null)
            {
                return ExplorerFolderDetailsColumnReadResult.Unavailable(
                    ExplorerFolderDetailsColumnReadStatus.ShellAutomationUnavailable);
            }

            dynamic shell = shellObject;
            windowsObject = shell.Windows();
            if (windowsObject is null)
            {
                return ExplorerFolderDetailsColumnReadResult.Unavailable(
                    ExplorerFolderDetailsColumnReadStatus.NoMatchingExplorerWindow);
            }

            dynamic windows = windowsObject;
            int count = Convert.ToInt32(windows.Count);
            bool sawMatchingNonDetails = false;
            ExplorerFolderDetailsColumnReadStatus? matchingDetailsFailure = null;
            for (int i = 0; i < count; i++)
            {
                object? windowObject = null;
                object? documentObject = null;
                object? folderObject = null;
                object? selfObject = null;
                try
                {
                    windowObject = windows.Item(i);
                    if (windowObject is null)
                    {
                        continue;
                    }

                    dynamic window = windowObject;
                    documentObject = window.Document;
                    if (documentObject is null)
                    {
                        continue;
                    }

                    dynamic document = documentObject;
                    folderObject = document.Folder;
                    if (folderObject is null)
                    {
                        continue;
                    }

                    dynamic folder = folderObject;
                    selfObject = folder.Self;
                    if (selfObject is null)
                    {
                        continue;
                    }

                    dynamic self = selfObject;
                    string? explorerPath = Convert.ToString(self.Path);
                    if (!TryNormalizeFilesystemPath(explorerPath, out string normalizedExplorerPath) ||
                        !PathsEqual(targetPath, normalizedExplorerPath))
                    {
                        continue;
                    }

                    int viewMode;
                    try
                    {
                        viewMode = Convert.ToInt32(document.CurrentViewMode);
                    }
                    catch (Exception ex) when (IsAutomationFailure(ex))
                    {
                        sawMatchingNonDetails = true;
                        continue;
                    }

                    if (viewMode != ExplorerDetailsViewMode)
                    {
                        sawMatchingNonDetails = true;
                        continue;
                    }

                    long rawHwnd = 0;
                    try
                    {
                        rawHwnd = Convert.ToInt64(window.HWND);
                    }
                    catch (Exception ex) when (IsAutomationFailure(ex))
                    {
                        // DPI falls back to 96 below. Column enumeration remains useful.
                    }

                    ExplorerFolderDetailsColumnReadResult candidate = TryReadColumnManager(
                        windowObject,
                        new IntPtr(rawHwnd),
                        targetPath);
                    if (candidate.IsSuccess)
                    {
                        return candidate;
                    }

                    matchingDetailsFailure ??= candidate.Status;
                }
                catch (Exception ex) when (IsAutomationFailure(ex))
                {
                    // Explorer can close while Shell.Application.Windows is being enumerated.
                }
                finally
                {
                    ReleaseCom(selfObject);
                    ReleaseCom(folderObject);
                    ReleaseCom(documentObject);
                    ReleaseCom(windowObject);
                }
            }

            if (matchingDetailsFailure.HasValue)
            {
                return ExplorerFolderDetailsColumnReadResult.Unavailable(matchingDetailsFailure.Value);
            }

            if (sawMatchingNonDetails)
            {
                return ExplorerFolderDetailsColumnReadResult.Unavailable(
                    ExplorerFolderDetailsColumnReadStatus.MatchingExplorerNotDetails);
            }

            return ExplorerFolderDetailsColumnReadResult.Unavailable(
                ExplorerFolderDetailsColumnReadStatus.NoMatchingExplorerWindow);
        }
        catch (Exception ex) when (IsAutomationFailure(ex))
        {
            return ExplorerFolderDetailsColumnReadResult.Unavailable(
                ExplorerFolderDetailsColumnReadStatus.ShellAutomationUnavailable);
        }
        finally
        {
            ReleaseCom(windowsObject);
            ReleaseCom(shellObject);
        }
    }

    private static ExplorerFolderDetailsColumnReadResult TryReadColumnManager(
        object explorerWindowObject,
        IntPtr explorerHwnd,
        string canonicalPath)
    {
        IntPtr windowUnknown = IntPtr.Zero;
        IntPtr serviceProvider = IntPtr.Zero;
        IntPtr shellBrowser = IntPtr.Zero;
        IntPtr shellView = IntPtr.Zero;
        IntPtr columnManager = IntPtr.Zero;
        try
        {
            windowUnknown = Marshal.GetIUnknownForObject(explorerWindowObject);

            Guid serviceProviderIid = IidServiceProvider;
            int hr = Marshal.QueryInterface(windowUnknown, ref serviceProviderIid, out serviceProvider);
            if (hr < 0 || serviceProvider == IntPtr.Zero)
            {
                return ExplorerFolderDetailsColumnReadResult.Unavailable(
                    ExplorerFolderDetailsColumnReadStatus.ColumnManagerUnavailable);
            }

            IntPtr queryServicePointer = GetVtableMethod(serviceProvider, QueryServiceSlot);
            var queryService = Marshal.GetDelegateForFunctionPointer<QueryServiceDelegate>(queryServicePointer);
            Guid service = SidTopLevelBrowser;
            Guid shellBrowserIid = IidShellBrowser;
            hr = queryService(serviceProvider, ref service, ref shellBrowserIid, out shellBrowser);
            if (hr < 0 || shellBrowser == IntPtr.Zero)
            {
                return ExplorerFolderDetailsColumnReadResult.Unavailable(
                    ExplorerFolderDetailsColumnReadStatus.ColumnManagerUnavailable);
            }

            IntPtr queryActiveViewPointer = GetVtableMethod(shellBrowser, QueryActiveShellViewSlot);
            var queryActiveView = Marshal.GetDelegateForFunctionPointer<QueryActiveShellViewDelegate>(queryActiveViewPointer);
            hr = queryActiveView(shellBrowser, out shellView);
            if (hr < 0 || shellView == IntPtr.Zero)
            {
                return ExplorerFolderDetailsColumnReadResult.Unavailable(
                    ExplorerFolderDetailsColumnReadStatus.ColumnManagerUnavailable);
            }

            Guid columnManagerIid = IidColumnManager;
            hr = Marshal.QueryInterface(shellView, ref columnManagerIid, out columnManager);
            if (hr < 0 || columnManager == IntPtr.Zero)
            {
                return ExplorerFolderDetailsColumnReadResult.Unavailable(
                    ExplorerFolderDetailsColumnReadStatus.ColumnManagerUnavailable);
            }

            IReadOnlyList<ExplorerFolderDetailsColumn> columns = ReadVisibleColumns(columnManager, explorerHwnd);
            if (columns.Count == 0)
            {
                return ExplorerFolderDetailsColumnReadResult.Unavailable(
                    ExplorerFolderDetailsColumnReadStatus.NoVisibleColumns);
            }

            uint dpi = GetExplorerDpi(explorerHwnd);
            return ExplorerFolderDetailsColumnReadResult.Success(
                new ExplorerFolderDetailsColumnSnapshot(canonicalPath, dpi, columns));
        }
        catch (COMException)
        {
            return ExplorerFolderDetailsColumnReadResult.Unavailable(
                ExplorerFolderDetailsColumnReadStatus.ColumnManagerUnavailable);
        }
        catch (PlatformNotSupportedException)
        {
            return ExplorerFolderDetailsColumnReadResult.Unavailable(
                ExplorerFolderDetailsColumnReadStatus.ColumnManagerUnavailable);
        }
        finally
        {
            if (columnManager != IntPtr.Zero) Marshal.Release(columnManager);
            if (shellView != IntPtr.Zero) Marshal.Release(shellView);
            if (shellBrowser != IntPtr.Zero) Marshal.Release(shellBrowser);
            if (serviceProvider != IntPtr.Zero) Marshal.Release(serviceProvider);
            if (windowUnknown != IntPtr.Zero) Marshal.Release(windowUnknown);
        }
    }

    private static IReadOnlyList<ExplorerFolderDetailsColumn> ReadVisibleColumns(
        IntPtr columnManager,
        IntPtr explorerHwnd)
    {
        IntPtr countPointer = GetVtableMethod(columnManager, GetColumnCountSlot);
        var getCount = Marshal.GetDelegateForFunctionPointer<GetColumnCountDelegate>(countPointer);
        if (getCount(columnManager, CmEnumVisible, out uint count) < 0 || count == 0)
        {
            return Array.Empty<ExplorerFolderDetailsColumn>();
        }

        count = Math.Min(count, MaxVisibleColumns);
        int elementSize = Marshal.SizeOf<NativePropertyKey>();
        IntPtr buffer = Marshal.AllocCoTaskMem(checked(elementSize * (int)count));
        try
        {
            IntPtr columnsPointer = GetVtableMethod(columnManager, GetColumnsSlot);
            var getColumns = Marshal.GetDelegateForFunctionPointer<GetColumnsDelegate>(columnsPointer);
            if (getColumns(columnManager, CmEnumVisible, buffer, count) < 0)
            {
                return Array.Empty<ExplorerFolderDetailsColumn>();
            }

            uint dpi = GetExplorerDpi(explorerHwnd);
            var result = new List<ExplorerFolderDetailsColumn>((int)count);
            for (int i = 0; i < count; i++)
            {
                IntPtr keyPointer = IntPtr.Add(buffer, checked(elementSize * i));
                NativePropertyKey key = Marshal.PtrToStructure<NativePropertyKey>(keyPointer);
                string? canonicalName = TryGetCanonicalPropertyName(key);
                if (string.IsNullOrWhiteSpace(canonicalName))
                {
                    continue;
                }

                NativeColumnInfo info = new()
                {
                    CbSize = checked((uint)Marshal.SizeOf<NativeColumnInfo>()),
                    DwMask = CmMaskWidth | CmMaskState,
                    Name = string.Empty,
                };

                IntPtr infoPointer = GetVtableMethod(columnManager, GetColumnInfoSlot);
                var getInfo = Marshal.GetDelegateForFunctionPointer<GetColumnInfoDelegate>(infoPointer);
                int hr = getInfo(columnManager, ref key, ref info);

                uint widthPixels = 0;
                uint state = 0;
                if (hr >= 0)
                {
                    state = info.DwState;
                    // -1/-2 are CM_WIDTH_USEDEFAULT / CM_WIDTH_AUTOSIZE represented as UINT.
                    if (info.UWidth is > 0 and < 0xFFFFFFFE)
                    {
                        widthPixels = info.UWidth;
                    }
                }

                double widthDip = widthPixels == 0
                    ? 0
                    : PixelsToDip(widthPixels, dpi);

                result.Add(new ExplorerFolderDetailsColumn(
                    canonicalName,
                    widthPixels,
                    widthDip,
                    state));
            }

            return result;
        }
        finally
        {
            Marshal.FreeCoTaskMem(buffer);
        }
    }

    private static uint GetExplorerDpi(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return DefaultDpi;
        }

        uint dpi = GetDpiForWindow(hwnd);
        return dpi == 0 ? DefaultDpi : dpi;
    }

    private static double PixelsToDip(uint pixels, uint dpi) =>
        pixels * (double)DefaultDpi / Math.Max(DefaultDpi, dpi);

    private static string? TryGetCanonicalPropertyName(NativePropertyKey propertyKey)
    {
        IntPtr namePointer = IntPtr.Zero;
        try
        {
            int hr = PSGetNameFromPropertyKey(ref propertyKey, out namePointer);
            return hr >= 0 && namePointer != IntPtr.Zero
                ? Marshal.PtrToStringUni(namePointer)
                : null;
        }
        finally
        {
            if (namePointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(namePointer);
            }
        }
    }

    private static IntPtr GetVtableMethod(IntPtr comInterface, int slot)
    {
        IntPtr vtable = Marshal.ReadIntPtr(comInterface);
        return Marshal.ReadIntPtr(vtable, checked(slot * IntPtr.Size));
    }

    private static bool TryNormalizeFilesystemPath(string? path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            string candidate = path.Trim();
            if (!Path.IsPathRooted(candidate) ||
                candidate.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith("search-ms:", StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith("::{", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string full = Path.GetFullPath(candidate);
            string root = Path.GetPathRoot(full) ?? string.Empty;
            while (full.Length > root.Length &&
                   (full.EndsWith(Path.DirectorySeparatorChar) || full.EndsWith(Path.AltDirectorySeparatorChar)))
            {
                full = full[..^1];
            }

            normalized = full;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool IsAutomationFailure(Exception ex) =>
        ex is COMException or InvalidCastException or MissingMemberException or InvalidOperationException or RuntimeBinderException;

    private static void ReleaseCom(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            try
            {
                Marshal.ReleaseComObject(value);
            }
            catch (InvalidComObjectException)
            {
                // Explorer closed and disconnected the RCW first.
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePropertyKey
    {
        internal Guid FormatId;
        internal uint PropertyId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeColumnInfo
    {
        internal uint CbSize;
        internal uint DwMask;
        internal uint DwState;
        internal uint UWidth;
        internal uint UDefaultWidth;
        internal uint UIdealWidth;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        internal string Name;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryServiceDelegate(
        IntPtr @this,
        ref Guid serviceId,
        ref Guid interfaceId,
        out IntPtr serviceObject);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryActiveShellViewDelegate(IntPtr @this, out IntPtr shellView);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetColumnCountDelegate(IntPtr @this, uint flags, out uint count);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetColumnsDelegate(IntPtr @this, uint flags, IntPtr propertyKeys, uint count);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetColumnInfoDelegate(
        IntPtr @this,
        ref NativePropertyKey propertyKey,
        ref NativeColumnInfo columnInfo);

    [DllImport("propsys.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int PSGetNameFromPropertyKey(
        ref NativePropertyKey propertyKey,
        out IntPtr canonicalName);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
