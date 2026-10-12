using System.Reflection;
using System.Runtime.InteropServices;

namespace DesktopDeck.Services;

/// <summary>
/// Uses the Windows Shell's own MinimizeAll/UndoMinimizeALL implementation so DesktopDeck
/// does not have to maintain a fragile list of application window handles.
/// </summary>
public sealed class DesktopDisplayService : IDisposable
{
    private bool _active;

    public bool IsActive => _active;

    public void Toggle() => SetActive(!_active);

    public void SetActive(bool active)
    {
        if (_active == active) return;
        if (!InvokeShell(active ? "MinimizeAll" : "UndoMinimizeALL")) return;
        _active = active;
    }

    private static bool InvokeShell(string method)
    {
        object? shell = null;
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("13709620-C279-11CE-A49E-444553540000"), throwOnError: false);
            if (type is null) return false;
            shell = Activator.CreateInstance(type);
            if (shell is null) return false;
            type.InvokeMember(method, BindingFlags.InvokeMethod, null, shell, null);
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error($"Desktop Display shell command failed: {method}", ex);
            return false;
        }
        finally
        {
            if (shell is not null && Marshal.IsComObject(shell))
            {
                try { Marshal.FinalReleaseComObject(shell); } catch { }
            }
        }
    }

    public void Dispose()
    {
        if (_active) SetActive(false);
    }
}
