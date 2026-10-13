using System;
using System.IO;
using Microsoft.Win32;

namespace CyanDeck.Services;

/// <summary>
/// Registers a per-user Explorer desktop-background verb. Never installs a
/// low-level input hook or modifies machine-wide Explorer registration.
/// </summary>
internal static class DesktopBackgroundRecoveryMenu
{
    internal const string RegistryPath = @"Software\Classes\DesktopBackground\Shell\CyanDeck.RecoverGroups";
    internal const string Argument = "--recover-groups-at-cursor";
    internal const string Caption = "이 모니터로 그룹 전부 옮기기";

    internal static void EnsureRegistered()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe) ||
                !string.Equals(Path.GetFileName(exe), "CyanDeck.exe", StringComparison.OrdinalIgnoreCase))
                return;
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
            if (key is null) return;
            key.SetValue(null, Caption, RegistryValueKind.String);
            key.SetValue("Icon", $"\"{exe}\",0", RegistryValueKind.String);
            key.SetValue("Position", "Bottom", RegistryValueKind.String);
            using var command = key.CreateSubKey("command");
            command?.SetValue(null, $"\"{exe}\" {Argument}", RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            // Unsupported/restricted registry access must not prevent CyanDeck
            // from starting. The tray menu remains an independent recovery path.
            DiagnosticsLog.Error("Could not register Explorer desktop recovery menu", ex);
        }
    }
}
