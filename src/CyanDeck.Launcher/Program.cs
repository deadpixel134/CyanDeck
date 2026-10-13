using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CyanDeck.Launcher;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var diagnostic = args.Length == 1 && string.Equals(
            args[0], CyanDeckLaunchPolicy.DiagnosticOption, StringComparison.OrdinalIgnoreCase);
        try
        {
            if (!string.Equals(Path.GetFileName(Environment.ProcessPath),
                    "CyanDeck.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The companion executable must be named CyanDeck.exe.");

            // Strict argument validation is shared with the real forwarding
            // harness. The diagnostic is the only non-forwarded option.
            var forwarded = diagnostic ? Array.Empty<string>() : CyanDeckLaunchPolicy.ValidateArguments(args).ToArray();
            var target = CyanDeckLaunchPolicy.ResolveLegacyExecutable(AppContext.BaseDirectory);
            if (diagnostic) return 0;

            var start = new ProcessStartInfo(target)
            {
                WorkingDirectory = Path.GetDirectoryName(target)!,
                UseShellExecute = false
            };
            foreach (var arg in forwarded)
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("Could not start the compatible DesktopDeck executable.");
            return 0;
        }
        catch (Exception error)
        {
            if (!diagnostic)
                MessageBoxW(nint.Zero,
                    "CyanDeck 호환 실행기를 시작할 수 없습니다.\n기존 사용자 데이터는 변경되지 않았습니다.\n\n"
                    + error.Message, "CyanDeck - 실행 파일 인계 오류", 0x10);
            return 1;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(nint window, string message, string caption, uint type);
}
