using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.CSharp.RuntimeBinder;
using System.Runtime.InteropServices;
using System.Threading;

namespace CyanDeck.ShellIntegration;

/// <summary>
/// Reads display state from an Explorer window that is already showing the same filesystem folder.
/// It never opens Explorer, never parses persisted Shell-view registry state, never writes Shell view state, and never polls.
/// Call from CyanDeck's STA/WPF dispatcher thread.
/// </summary>
internal sealed class ExplorerFolderViewReader
{
    private static readonly Guid IidServiceProvider = new("6D5140C1-7436-11CE-8034-00AA006009FA");
    private static readonly Guid SidTopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid IidShellBrowser = new("000214E2-0000-0000-C000-000000000046");
    private static readonly Guid IidFolderView2 = new("1AF3A467-214F-4298-908E-06B03E0B39F9");

    // IServiceProvider adds QueryService at slot 3. IShellBrowser::QueryActiveShellView is slot 15.
    private const int QueryServiceSlot = 3;
    private const int QueryActiveShellViewSlot = 15;

    // IUnknown = 0..2, IFolderView = 3..16, IFolderView2 starts at slot 17.
    private const int GetSortColumnCountSlot = 26;
    private const int GetSortColumnsSlot = 28;
    private const int GetViewModeAndIconSizeSlot = 36;

    internal ExplorerFolderViewReadResult TryRead(string folderPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return ExplorerFolderViewReadResult.Unavailable(ExplorerFolderViewReadStatus.NotWindows);
        }

        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            return ExplorerFolderViewReadResult.Unavailable(ExplorerFolderViewReadStatus.RequiresStaThread);
        }

        if (!TryNormalizeFilesystemPath(folderPath, out string targetPath))
        {
            return ExplorerFolderViewReadResult.Unavailable(ExplorerFolderViewReadStatus.InvalidPath);
        }

        Type? shellType = Type.GetTypeFromProgID("Shell.Application", throwOnError: false);
        if (shellType is null)
        {
            return ExplorerFolderViewReadResult.Unavailable(ExplorerFolderViewReadStatus.ShellAutomationUnavailable);
        }

        object? shellObject = null;
        object? windowsObject = null;
        try
        {
            shellObject = Activator.CreateInstance(shellType);
            if (shellObject is null)
            {
                return ExplorerFolderViewReadResult.Unavailable(ExplorerFolderViewReadStatus.ShellAutomationUnavailable);
            }

            dynamic shell = shellObject;
            windowsObject = shell.Windows();
            if (windowsObject is null)
            {
                return ExplorerFolderViewReadResult.Unavailable(ExplorerFolderViewReadStatus.NoMatchingExplorerWindow);
            }

            dynamic windows = windowsObject;
            int count;
            try
            {
                count = Convert.ToInt32(windows.Count);
            }
            catch (Exception ex) when (IsAutomationFailure(ex))
            {
                return ExplorerFolderViewReadResult.Unavailable(ExplorerFolderViewReadStatus.ShellAutomationUnavailable);
            }

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

                    if (TryReadFolderView2(windowObject, targetPath, out ExplorerFolderViewSnapshot? snapshot))
                    {
                        return ExplorerFolderViewReadResult.Success(snapshot!);
                    }

                    // Fallback: the Automation interface still exposes CurrentViewMode on common Explorer views.
                    // This keeps the feature useful on a Shell implementation that does not QI to IFolderView2.
                    try
                    {
                        int currentViewMode = Convert.ToInt32(document.CurrentViewMode);
                        if (Enum.IsDefined(typeof(ExplorerFolderViewMode), currentViewMode))
                        {
                            return ExplorerFolderViewReadResult.Success(
                                new ExplorerFolderViewSnapshot(
                                    targetPath,
                                    (ExplorerFolderViewMode)currentViewMode,
                                    0,
                                    Array.Empty<ExplorerSortColumn>()));
                        }
                    }
                    catch (Exception ex) when (IsAutomationFailure(ex))
                    {
                        // The matching window exists, but its document does not expose usable view state.
                    }

                    return ExplorerFolderViewReadResult.Unavailable(ExplorerFolderViewReadStatus.ViewInterfaceUnavailable);
                }
                catch (Exception ex) when (IsAutomationFailure(ex))
                {
                    // A stale/closing Explorer window is expected during enumeration. Skip it.
                }
                finally
                {
                    ReleaseCom(selfObject);
                    ReleaseCom(folderObject);
                    ReleaseCom(documentObject);
                    ReleaseCom(windowObject);
                }
            }

            return ExplorerFolderViewReadResult.Unavailable(ExplorerFolderViewReadStatus.NoMatchingExplorerWindow);
        }
        catch (Exception ex) when (IsAutomationFailure(ex))
        {
            return ExplorerFolderViewReadResult.Unavailable(ExplorerFolderViewReadStatus.ShellAutomationUnavailable);
        }
        finally
        {
            ReleaseCom(windowsObject);
            ReleaseCom(shellObject);
        }
    }

    private static bool TryReadFolderView2(
        object explorerWindowObject,
        string canonicalPath,
        out ExplorerFolderViewSnapshot? snapshot)
    {
        snapshot = null;
        IntPtr windowUnknown = IntPtr.Zero;
        IntPtr serviceProvider = IntPtr.Zero;
        IntPtr shellBrowser = IntPtr.Zero;
        IntPtr shellView = IntPtr.Zero;
        IntPtr folderView2 = IntPtr.Zero;
        try
        {
            // Microsoft documents this site-chain pattern for Explorer windows:
            // IWebBrowserApp/IUnknown -> IServiceProvider -> SID_STopLevelBrowser/IShellBrowser
            // -> QueryActiveShellView -> QI IFolderView2.
            windowUnknown = Marshal.GetIUnknownForObject(explorerWindowObject);

            Guid serviceProviderIid = IidServiceProvider;
            int hr = Marshal.QueryInterface(windowUnknown, ref serviceProviderIid, out serviceProvider);
            if (hr < 0 || serviceProvider == IntPtr.Zero)
            {
                return false;
            }

            IntPtr queryServicePointer = GetVtableMethod(serviceProvider, QueryServiceSlot);
            var queryService = Marshal.GetDelegateForFunctionPointer<QueryServiceDelegate>(queryServicePointer);
            Guid service = SidTopLevelBrowser;
            Guid shellBrowserIid = IidShellBrowser;
            hr = queryService(serviceProvider, ref service, ref shellBrowserIid, out shellBrowser);
            if (hr < 0 || shellBrowser == IntPtr.Zero)
            {
                return false;
            }

            IntPtr queryActiveViewPointer = GetVtableMethod(shellBrowser, QueryActiveShellViewSlot);
            var queryActiveView = Marshal.GetDelegateForFunctionPointer<QueryActiveShellViewDelegate>(queryActiveViewPointer);
            hr = queryActiveView(shellBrowser, out shellView);
            if (hr < 0 || shellView == IntPtr.Zero)
            {
                return false;
            }

            Guid folderView2Iid = IidFolderView2;
            hr = Marshal.QueryInterface(shellView, ref folderView2Iid, out folderView2);
            if (hr < 0 || folderView2 == IntPtr.Zero)
            {
                return false;
            }

            if (!TryGetViewModeAndIconSize(folderView2, out ExplorerFolderViewMode viewMode, out int iconSize))
            {
                return false;
            }

            IReadOnlyList<ExplorerSortColumn> sortColumns = ReadSortColumns(folderView2);
            snapshot = new ExplorerFolderViewSnapshot(canonicalPath, viewMode, iconSize, sortColumns);
            return true;
        }
        catch (COMException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
        finally
        {
            if (folderView2 != IntPtr.Zero)
            {
                Marshal.Release(folderView2);
            }

            if (shellView != IntPtr.Zero)
            {
                Marshal.Release(shellView);
            }

            if (shellBrowser != IntPtr.Zero)
            {
                Marshal.Release(shellBrowser);
            }

            if (serviceProvider != IntPtr.Zero)
            {
                Marshal.Release(serviceProvider);
            }

            if (windowUnknown != IntPtr.Zero)
            {
                Marshal.Release(windowUnknown);
            }
        }
    }

    private static bool TryGetViewModeAndIconSize(
        IntPtr folderView2,
        out ExplorerFolderViewMode viewMode,
        out int iconSize)
    {
        viewMode = ExplorerFolderViewMode.Auto;
        iconSize = 0;

        IntPtr methodPointer = GetVtableMethod(folderView2, GetViewModeAndIconSizeSlot);
        var method = Marshal.GetDelegateForFunctionPointer<GetViewModeAndIconSizeDelegate>(methodPointer);
        int hr = method(folderView2, out uint rawViewMode, out iconSize);
        if (hr < 0 || rawViewMode > (uint)ExplorerFolderViewMode.Content)
        {
            return false;
        }

        viewMode = (ExplorerFolderViewMode)rawViewMode;
        return true;
    }

    private static IReadOnlyList<ExplorerSortColumn> ReadSortColumns(IntPtr folderView2)
    {
        IntPtr countPointer = GetVtableMethod(folderView2, GetSortColumnCountSlot);
        var getCount = Marshal.GetDelegateForFunctionPointer<GetSortColumnCountDelegate>(countPointer);
        if (getCount(folderView2, out int count) < 0 || count <= 0)
        {
            return Array.Empty<ExplorerSortColumn>();
        }

        // Explorer normally uses a very small list. Clamp malformed/custom implementations defensively.
        count = Math.Min(count, 16);
        int elementSize = Marshal.SizeOf<NativeSortColumn>();
        IntPtr buffer = Marshal.AllocCoTaskMem(checked(elementSize * count));
        try
        {
            IntPtr columnsPointer = GetVtableMethod(folderView2, GetSortColumnsSlot);
            var getColumns = Marshal.GetDelegateForFunctionPointer<GetSortColumnsDelegate>(columnsPointer);
            if (getColumns(folderView2, buffer, count) < 0)
            {
                return Array.Empty<ExplorerSortColumn>();
            }

            var result = new List<ExplorerSortColumn>(count);
            for (int i = 0; i < count; i++)
            {
                IntPtr itemPointer = IntPtr.Add(buffer, elementSize * i);
                NativeSortColumn native = Marshal.PtrToStructure<NativeSortColumn>(itemPointer);
                string? canonicalName = TryGetCanonicalPropertyName(native.PropertyKey);
                if (string.IsNullOrWhiteSpace(canonicalName))
                {
                    continue;
                }

                ExplorerSortDirection direction = native.Direction < 0
                    ? ExplorerSortDirection.Descending
                    : ExplorerSortDirection.Ascending;
                result.Add(new ExplorerSortColumn(canonicalName, direction));
            }

            return result;
        }
        finally
        {
            Marshal.FreeCoTaskMem(buffer);
        }
    }

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
                // The RCW was already disconnected while Explorer was closing.
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePropertyKey
    {
        internal Guid FormatId;
        internal uint PropertyId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSortColumn
    {
        internal NativePropertyKey PropertyKey;
        internal int Direction;
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
    private delegate int GetSortColumnCountDelegate(IntPtr @this, out int count);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetSortColumnsDelegate(IntPtr @this, IntPtr sortColumns, int count);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetViewModeAndIconSizeDelegate(
        IntPtr @this,
        out uint viewMode,
        out int iconSize);

    [DllImport("propsys.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int PSGetNameFromPropertyKey(
        ref NativePropertyKey propertyKey,
        out IntPtr canonicalName);
}

internal enum ExplorerFolderViewReadStatus
{
    Success,
    NotWindows,
    RequiresStaThread,
    InvalidPath,
    ShellAutomationUnavailable,
    NoMatchingExplorerWindow,
    ViewInterfaceUnavailable,
}

internal readonly record struct ExplorerFolderViewReadResult(
    ExplorerFolderViewReadStatus Status,
    ExplorerFolderViewSnapshot? Snapshot)
{
    internal bool IsSuccess => Status == ExplorerFolderViewReadStatus.Success && Snapshot is not null;

    internal static ExplorerFolderViewReadResult Success(ExplorerFolderViewSnapshot snapshot) =>
        new(ExplorerFolderViewReadStatus.Success, snapshot);

    internal static ExplorerFolderViewReadResult Unavailable(ExplorerFolderViewReadStatus status) =>
        new(status, null);
}
