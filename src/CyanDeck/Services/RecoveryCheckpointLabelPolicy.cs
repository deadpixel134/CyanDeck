using System.Text.RegularExpressions;

namespace CyanDeck.Services;

/// <summary>
/// Turns recovery checkpoint file names written by StateStore ("{reason}-yyyyMMdd-HHmmss")
/// into short Korean descriptions for the tray menu, instead of internal ids and GUIDs.
/// </summary>
internal static class RecoveryCheckpointLabelPolicy
{
    private static readonly (string Prefix, string Label)[] Known =
    {
        ("before-auto-monitor-reduction", "모니터 연결 해제로 그룹을 옮기기 전"),
        ("before-monitor-profile-restore", "모니터 배치를 복원하기 전"),
        ("before-screen-content-swap", "화면 내용 교환 전"),
        ("before-automatic-layout", "자동 레이아웃 실행 전"),
        ("before-geometry-reset", "창 위치·크기 초기화 전"),
        ("before-factory-reset", "전체 초기화 전"),
        ("before-import", "백업 가져오기 전"),
        ("before-integrity-check", "설정 검사·복구 전"),
        ("before-recovery-restore", "복구 지점으로 되돌리기 전"),
        ("before-app-rollback", "앱 업데이트 되돌리기 전"),
        ("before-github-auto-update", "자동 업데이트 설치 전"),
        ("before-startup-auto-update", "시작 시 자동 업데이트 전"),
        ("before-update", "업데이트 설치 전"),
    };

    internal static string Describe(string? fileNameWithoutExtension)
    {
        var name = (fileNameWithoutExtension ?? "").Trim();
        if (name.Length == 0) return "복구 지점";
        var stem = Regex.Replace(name, @"-\d{8}-\d{6}$", "");
        foreach (var (prefix, label) in Known)
        {
            if (!stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = stem.Substring(prefix.Length);
            if (rest.Length > 0 && rest[0] != '-') continue; // a longer, different reason
            var version = Regex.Match(rest.Trim('-'), @"^(\d+)-(\d+)-(\d+)$");
            return version.Success
                ? $"{label} (v{version.Groups[1].Value}.{version.Groups[2].Value}.{version.Groups[3].Value})"
                : label;
        }
        // Unknown reason: hide machine-generated GUIDs and keep the readable part.
        var readable = Regex.Replace(stem, @"-?[0-9a-fA-F]{32}", "").Trim('-');
        return readable.Length == 0 ? "복구 지점" : readable;
    }
}
