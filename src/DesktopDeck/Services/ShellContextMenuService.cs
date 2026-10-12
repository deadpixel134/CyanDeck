using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DesktopDeck.Interop;

namespace DesktopDeck.Services;

public static class ShellContextMenuService
{
    private const uint CMF_NORMAL = 0x00000000;
    private const uint CMF_EXTENDEDVERBS = 0x00000100;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint MF_SEPARATOR = 0x0800;
    private const uint MF_STRING = 0x0000;
    private const uint CMIC_MASK_UNICODE = 0x00004000;
    private const uint GCS_VERBW = 0x00000004;
    private const int SW_SHOWNORMAL = 1;
    private const int WM_DRAWITEM = 0x002B;
    private const int WM_MEASUREITEM = 0x002C;
    private const int WM_INITMENUPOPUP = 0x0117;
    private const int WM_MENUCHAR = 0x0120;
    private const int WM_NULL = 0x0000;

    private static readonly Guid IID_IShellFolder = new("000214E6-0000-0000-C000-000000000046");
    private static readonly Guid IID_IContextMenu = new("000214E4-0000-0000-C000-000000000046");

    public sealed record ExtraCommand(string Label, Action Execute);

    public static bool TryShow(Window owner, string path, Point screenPoint, IReadOnlyList<ExtraCommand>? extras = null, Func<string, bool>? canonicalVerbInterceptor = null)
        => TryShow(owner, new[] { path }, screenPoint, extras, canonicalVerbInterceptor);

    /// <summary>
    /// Shows the Windows Shell context menu for filesystem items or a single Shell namespace object.
    /// Filesystem multi-selection requires one shared parent folder. Collections can contain
    /// arbitrary references, so mixed-parent selections deliberately fall back to the
    /// DesktopDeck batch menu instead of manufacturing an invalid PIDL array.
    /// </summary>
    public static bool TryShow(Window owner, IReadOnlyList<string> paths, Point screenPoint, IReadOnlyList<ExtraCommand>? extras = null, Func<string, bool>? canonicalVerbInterceptor = null)
    {
        var valid = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(NormalizeParsingName)
            .Where(IsSupportedContextTarget)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (valid.Length == 0) return false;

        // Filesystem multi-selection is valid only when every item shares the same parent.
        // A single Shell namespace object (for example the Recycle Bin) does not have a
        // filesystem parent path, but SHParseDisplayName + SHBindToParent can still obtain
        // the owning IShellFolder and its native IContextMenu.
        if (valid.Length > 1)
        {
            if (valid.Any(DesktopSystemItems.IsShellNamespacePath)) return false;
            var parentKey = GetParentKey(valid[0]);
            if (string.IsNullOrWhiteSpace(parentKey)
                || valid.Any(p => !string.Equals(GetParentKey(p), parentKey, StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        var absolutePidls = new List<IntPtr>(valid.Length);
        var childPidls = new List<IntPtr>(valid.Length);
        IntPtr menu = IntPtr.Zero;
        IShellFolder? parent = null;
        IContextMenu? contextMenu = null;
        HwndSource? source = null;
        HwndSource? popupOwnerSource = null;
        HwndSourceHook? hook = null;

        try
        {
            foreach (var path in valid)
            {
                var hr = SHParseDisplayName(path, IntPtr.Zero, out var absolutePidl, 0, out _);
                if (hr < 0 || absolutePidl == IntPtr.Zero) return false;
                absolutePidls.Add(absolutePidl);

                var iidFolder = IID_IShellFolder;
                hr = SHBindToParent(absolutePidl, ref iidFolder, out var parsedParent, out var childPidl);
                if (hr < 0 || parsedParent is null || childPidl == IntPtr.Zero) return false;
                childPidls.Add(childPidl);
                if (parent is null) parent = parsedParent;
                else if (Marshal.IsComObject(parsedParent)) Marshal.FinalReleaseComObject(parsedParent);
            }

            if (parent is null) return false;
            var iidContext = IID_IContextMenu;
            var apidl = childPidls.ToArray();
            var ownerHwnd = new WindowInteropHelper(owner).EnsureHandle();
            var hwnd = CreateMenuOwnerIfNeeded(ownerHwnd, out popupOwnerSource);
            var getHr = parent.GetUIObjectOf(hwnd, (uint)apidl.Length, apidl, ref iidContext, IntPtr.Zero, out var contextObject);
            if (getHr < 0 || contextObject is not IContextMenu cm) return false;
            contextMenu = cm;

            menu = NativeMethods.CreatePopupMenu();
            if (menu == IntPtr.Zero) return false;

            var firstShellId = 1u;
            var lastShellId = 0x6FFFu;
            var flags = CMF_NORMAL;
            if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0) flags |= CMF_EXTENDEDVERBS;
            var queryHr = contextMenu.QueryContextMenu(menu, 0, firstShellId, lastShellId, flags);
            if (queryHr < 0) return false;

            const uint extraBase = 0x7000;
            if (extras is { Count: > 0 })
            {
                NativeMethods.AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
                for (var i = 0; i < extras.Count && i < 0x0FFF; i++)
                    NativeMethods.AppendMenu(menu, MF_STRING, new UIntPtr(extraBase + (uint)i), extras[i].Label);
            }

            source = popupOwnerSource ?? HwndSource.FromHwnd(hwnd);
            var cm2 = contextMenu as IContextMenu2;
            var cm3 = contextMenu as IContextMenu3;
            hook = (IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (cm3 is not null && (msg is WM_INITMENUPOPUP or WM_DRAWITEM or WM_MEASUREITEM or WM_MENUCHAR))
                {
                    var result = cm3.HandleMenuMsg2((uint)msg, wParam, lParam, out var menuResult);
                    if (result >= 0)
                    {
                        handled = true;
                        return menuResult;
                    }
                }
                else if (cm2 is not null && (msg is WM_INITMENUPOPUP or WM_DRAWITEM or WM_MEASUREITEM))
                {
                    if (cm2.HandleMenuMsg((uint)msg, wParam, lParam) >= 0) handled = true;
                }
                return IntPtr.Zero;
            };
            source?.AddHook(hook);

            NativeMethods.SetForegroundWindow(hwnd);
            var cmd = NativeMethods.TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD,
                (int)Math.Round(screenPoint.X), (int)Math.Round(screenPoint.Y), hwnd, IntPtr.Zero);
            NativeMethods.PostMessage(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);

            if (cmd == 0) return true;
            if (cmd >= extraBase)
            {
                var index = (int)(cmd - extraBase);
                if (extras is not null && index >= 0 && index < extras.Count) extras[index].Execute();
                return true;
            }

            var canonicalVerb = TryGetCanonicalVerb(contextMenu, cmd - firstShellId);
            if (!string.IsNullOrWhiteSpace(canonicalVerb) && canonicalVerbInterceptor is not null)
            {
                try
                {
                    if (canonicalVerbInterceptor(canonicalVerb.Trim())) return true;
                }
                catch (Exception ex)
                {
                    DiagnosticsLog.Error("Shell canonical-verb interceptor failed", ex);
                }
            }

            var invoke = new CMINVOKECOMMANDINFOEX
            {
                cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                fMask = CMIC_MASK_UNICODE,
                hwnd = hwnd,
                lpVerb = new IntPtr(cmd - firstShellId),
                lpVerbW = new IntPtr(cmd - firstShellId),
                nShow = SW_SHOWNORMAL,
                ptInvoke = new NativeMethods.POINT { X = (int)Math.Round(screenPoint.X), Y = (int)Math.Round(screenPoint.Y) }
            };
            contextMenu.InvokeCommand(ref invoke);
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("Shell context menu failed", ex);
            return false;
        }
        finally
        {
            if (source is not null && hook is not null) source.RemoveHook(hook);
            try { popupOwnerSource?.Dispose(); } catch { }
            if (menu != IntPtr.Zero) NativeMethods.DestroyMenu(menu);
            if (contextMenu is not null && Marshal.IsComObject(contextMenu)) Marshal.FinalReleaseComObject(contextMenu);
            if (parent is not null && Marshal.IsComObject(parent)) Marshal.FinalReleaseComObject(parent);
            foreach (var pidl in absolutePidls) if (pidl != IntPtr.Zero) ILFree(pidl);
        }
    }

    private static IntPtr CreateMenuOwnerIfNeeded(IntPtr ownerHwnd, out HwndSource? popupOwnerSource)
    {
        popupOwnerSource = null;
        if (ownerHwnd == IntPtr.Zero) return ownerHwnd;

        var style = NativeMethods.GetWindowLongPtr(ownerHwnd, NativeMethods.GWL_STYLE).ToInt64();
        if ((style & NativeMethods.WS_CHILD) == 0) return ownerHwnd;

        // Loose desktop icons are intentionally reparented into Explorer's SHELLDLL_DefView.
        // TrackPopupMenu requires an application-owned foreground top-level HWND so the menu
        // dismisses correctly when the user clicks outside it. A tiny off-screen HwndSource
        // supplies that owner without promoting the desktop icon itself into a top-level window.
        const long WS_VISIBLE = 0x10000000L;
        var parameters = new HwndSourceParameters("DesktopDeck.ShellContextMenuOwner")
        {
            Width = 1,
            Height = 1,
            PositionX = -32000,
            PositionY = -32000,
            WindowStyle = unchecked((int)(NativeMethods.WS_POPUP | WS_VISIBLE)),
            ExtendedWindowStyle = NativeMethods.WS_EX_TOOLWINDOW,
        };
        popupOwnerSource = new HwndSource(parameters);
        return popupOwnerSource.Handle;
    }

    private static string? TryGetCanonicalVerb(IContextMenu contextMenu, uint commandOffset)
    {
        const int chars = 260;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            buffer = Marshal.AllocHGlobal(chars * sizeof(char));
            for (var i = 0; i < chars * sizeof(char); i++) Marshal.WriteByte(buffer, i, 0);
            var hr = contextMenu.GetCommandString(new UIntPtr(commandOffset), GCS_VERBW, IntPtr.Zero, buffer, chars);
            if (hr < 0) return null;
            return Marshal.PtrToStringUni(buffer);
        }
        catch { return null; }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool IsSupportedContextTarget(string path)
        => File.Exists(path) || Directory.Exists(path) || DesktopSystemItems.IsShellNamespacePath(path);

    private static string NormalizeParsingName(string path)
    {
        var trimmed = path.Trim();
        if (DesktopSystemItems.IsShellNamespacePath(trimmed)) return trimmed;
        try { return Path.GetFullPath(trimmed); }
        catch { return trimmed; }
    }

    private static string? GetParentKey(string path)
    {
        try
        {
            var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return Path.GetDirectoryName(trimmed)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch { return null; }
    }

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName, ref uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
        [PreserveSig] int EnumObjects(IntPtr hwnd, int grfFlags, out IntPtr ppenumIDList);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        [PreserveSig] int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetAttributesOf(uint cidl, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IntPtr[] apidl, ref uint rgfInOut);
        [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint cidl, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] IntPtr[] apidl, ref Guid riid, IntPtr rgfReserved, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint uFlags, IntPtr pName);
        [PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszName, uint uFlags, out IntPtr ppidlOut);
    }

    [ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
    }

    [ComImport, Guid("000214F4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu2 : IContextMenu
    {
        [PreserveSig] new int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] new int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        [PreserveSig] new int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
        [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
    }

    [ComImport, Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3 : IContextMenu2
    {
        [PreserveSig] new int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] new int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        [PreserveSig] new int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
        [PreserveSig] new int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
        [PreserveSig] int HandleMenuMsg2(uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr plResult);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        public IntPtr lpParameters;
        public IntPtr lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr lpTitle;
        public IntPtr lpVerbW;
        public IntPtr lpParametersW;
        public IntPtr lpDirectoryW;
        public IntPtr lpTitleW;
        public NativeMethods.POINT ptInvoke;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SHBindToParent(IntPtr pidl, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellFolder ppv, out IntPtr ppidlLast);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);
}
