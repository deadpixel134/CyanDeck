using System;
using System.Linq;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace DesktopDeck.Services;

/// <summary>
/// A deliberately small, shareable report. Do not include raw log files,
/// configuration JSON, tokens, user profile paths or exception messages.
/// </summary>
internal static class ProblemReportText
{
    public static string Create(string area, string fileName, Exception ex, string situation, string? lockOwners = null)
    {
        var buffer = new StringBuilder()
            .AppendLine("[CyanDeck 오류 진단 — 개발자 전달용]")
            .AppendLine($"발생 시각: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}")
            .AppendLine("제품: CyanDeck (내부 호환성 식별자: DesktopDeck)")
            .AppendLine($"실행 파일 버전: {typeof(ProblemReportText).Assembly.GetName().Version}")
            .AppendLine($"운영 체제: {RuntimeInformation.OSDescription}")
            .AppendLine($"영역: {area}")
            .AppendLine($"대상 파일: {fileName}")
            .AppendLine($"상황: {situation}")
            .AppendLine($"예외 유형: {ex.GetType().FullName}")
            .AppendLine($"HRESULT: 0x{ex.HResult:X8}")
            .AppendLine($"Win32 하위 오류 코드: {ex.HResult & 0xFFFF}");

        if (lockOwners is not null)
        {
            buffer.AppendLine("관련 파일을 사용 중인 프로세스 (Restart Manager, 확정 원인 아님):")
                .AppendLine(lockOwners);
        }

        // Method names are useful for diagnostics without leaking exception
        // messages, local source file paths, OAuth credentials or event text.
        var frames = new StackTrace(ex, false).GetFrames();
        if (frames is { Length: > 0 })
        {
            buffer.AppendLine("호출 위치 (파일 경로 제외):");
            foreach (var frame in frames.Take(8))
            {
                var method = frame.GetMethod();
                if (method is null) continue;
                buffer.AppendLine("- " + (method.DeclaringType?.FullName ?? "unknown") + "." + method.Name);
            }
        }
        buffer.AppendLine("※ 원본 로그, 개인 파일 경로, Google 인증 정보 및 일정 내용은 자동 포함하지 않았습니다.");
        return buffer.ToString();
    }
}
