using System;
using System.Runtime.InteropServices;

namespace DesktopDeck.Peek;

internal static class WindowsAppUserModelPropertyStore
{
    private const uint GPS_DEFAULT = 0;

    private static readonly PropertyKey PkeyAppUserModelRelaunchCommand = new(
        new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 2);
    private static readonly PropertyKey PkeyAppUserModelRelaunchIconResource = new(
        new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 3);
    private static readonly PropertyKey PkeyAppUserModelRelaunchDisplayNameResource = new(
        new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 4);
    private static readonly PropertyKey PkeyAppUserModelId = new(
        new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);

    public static void ApplyPeekIdentity(
        IntPtr hwnd,
        string appUserModelId,
        string relaunchCommand,
        string displayName,
        string iconResource)
    {
        if (hwnd == IntPtr.Zero)
        {
            throw new ArgumentException("A valid HWND is required.", nameof(hwnd));
        }

        var iid = typeof(IPropertyStore).GUID;
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(hwnd, ref iid, out var store));
        try
        {
            SetString(store, PkeyAppUserModelRelaunchCommand, relaunchCommand);
            SetString(store, PkeyAppUserModelRelaunchIconResource, iconResource);
            SetString(store, PkeyAppUserModelRelaunchDisplayNameResource, displayName);
            // Microsoft recommends applying relaunch properties before the explicit ID.
            SetString(store, PkeyAppUserModelId, appUserModelId);
            Marshal.ThrowExceptionForHR(store.Commit());
        }
        finally
        {
            if (Marshal.IsComObject(store))
            {
                Marshal.FinalReleaseComObject(store);
            }
        }
    }

    private static void SetString(IPropertyStore store, PropertyKey key, string value)
    {
        Marshal.ThrowExceptionForHR(InitPropVariantFromString(value, out var variant));
        try
        {
            Marshal.ThrowExceptionForHR(store.SetValue(ref key, ref variant));
        }
        finally
        {
            PropVariantClear(ref variant);
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;

        public PropertyKey(Guid formatId, uint propertyId)
        {
            FormatId = formatId;
            PropertyId = propertyId;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        private ushort _vt;
        private ushort _wReserved1;
        private ushort _wReserved2;
        private ushort _wReserved3;
        private IntPtr _pointer;
        private int _padding;
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint propertyCount);

        [PreserveSig]
        int GetAt(uint propertyIndex, out PropertyKey key);

        [PreserveSig]
        int GetValue(ref PropertyKey key, out PropVariant value);

        [PreserveSig]
        int SetValue(ref PropertyKey key, ref PropVariant value);

        [PreserveSig]
        int Commit();
    }

    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SHGetPropertyStoreForWindow(
        IntPtr hwnd,
        ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore propertyStore);

    [DllImport("propsys.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int InitPropVariantFromString(
        [MarshalAs(UnmanagedType.LPWStr)] string text,
        out PropVariant propVariant);

    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int PropVariantClear(ref PropVariant propVariant);
}
