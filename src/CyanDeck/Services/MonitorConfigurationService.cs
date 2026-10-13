using System.Security.Cryptography;
using System.Text;
using CyanDeck.Models;
using Forms = System.Windows.Forms;

namespace CyanDeck.Services;

public static class MonitorConfigurationService
{
    public static string GetSignature() => GetSignature(CaptureMonitors());

    public static string GetSignature(IReadOnlyList<DeckMonitorDescriptor> monitors)
    {
        var payload = string.Join("|", (monitors ?? Array.Empty<DeckMonitorDescriptor>())
            .OrderBy(m => m.DeviceName, StringComparer.OrdinalIgnoreCase)
            .Select(m => $"{m.DeviceName}:{m.X},{m.Y},{m.Width},{m.Height}:{m.WorkingX},{m.WorkingY},{m.WorkingWidth},{m.WorkingHeight}:{(m.Primary ? 1 : 0)}"));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash.AsSpan(0, 10));
    }

    public static List<DeckMonitorDescriptor> CaptureMonitors() => Forms.Screen.AllScreens
        .OrderBy(s => s.Bounds.X).ThenBy(s => s.Bounds.Y)
        .Select(s => new DeckMonitorDescriptor
        {
            DeviceName = s.DeviceName,
            X = s.Bounds.X,
            Y = s.Bounds.Y,
            Width = s.Bounds.Width,
            Height = s.Bounds.Height,
            WorkingX = s.WorkingArea.X,
            WorkingY = s.WorkingArea.Y,
            WorkingWidth = s.WorkingArea.Width,
            WorkingHeight = s.WorkingArea.Height,
            Primary = s.Primary
        }).ToList();
}
