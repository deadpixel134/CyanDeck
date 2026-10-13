using System.Diagnostics;
using System.Windows.Forms;

namespace CyanDeck.Launcher;

// Compatibility entry point, NOT a profile cutover. The launched DesktopDeck
// process owns the legacy writer mutexes and all persistent data.
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var forwarded = CyanDeckLaunchPolicy.ValidateArguments(args);
            var target = CyanDeckLaunchPolicy.ResolveLegacyExecutable(AppContext.BaseDirectory);
            var start = new ProcessStartInfo(target)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(target)!
            };
            foreach (var argument in forwarded)
                start.ArgumentList.Add(argument);
            using var child = Process.Start(start)
                ?? throw new InvalidOperationException("The compatible DesktopDeck executable could not start.");
            // Do not hold a writer lease in this short-lived forwarding process.
            return 0;
        }
        catch (Exception error)
        {
            MessageBox.Show(
                "CyanDeck 호환 실행을 시작하지 못했습니다. 기존 DesktopDeck.exe와 함께 실행하세요.\n\n" +
                error.Message,
                "CyanDeck 호환 런처", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
