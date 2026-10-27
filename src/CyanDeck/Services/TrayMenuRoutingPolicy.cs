using System;

namespace CyanDeck.Services;

// Pure command-routing policy: the existing WinForms menu item and its handler
// are moved intact. Tag IDs take precedence over localized text; older and
// conditional commands use an explicit compatibility fallback.
internal enum TrayMenuSection
{
    Primary,
    Calendar,
    Groups,
    IconTools,
    Recovery,
    Tools,
    SettingsOnly,
    Exit,
    // Calendar repair/diagnostic commands, nested as "캘린더 문제 해결" inside the calendar menu.
    CalendarRepair
}

internal static class TrayMenuRoutingPolicy
{
    internal static TrayMenuSection Route(string? commandId, string? label)
    {
        switch (commandId)
        {
            case "brand.identity": return TrayMenuSection.Tools;
            case "calendar.status":
            case "calendar.enable": return TrayMenuSection.Calendar;
            case "calendar.repair": return TrayMenuSection.CalendarRepair;
            case "group.new":
            case "search.items":
            case "desktop.pages":
            case "desktop.peek":
            case "desktop.quick-hide": return TrayMenuSection.Primary;
            case "group.enable":
            case "group.snapshots":
            case "desktop.display": return TrayMenuSection.Groups;
            case "desktop.native-icons": return TrayMenuSection.IconTools;
            case "recovery.backup": return TrayMenuSection.Recovery;
            case "settings.startup": return TrayMenuSection.SettingsOnly;
        }
        if (string.IsNullOrEmpty(label)) return TrayMenuSection.Groups;
        if (label == "종료") return TrayMenuSection.Exit;
        if (label == "설정…" || label == "새 그룹" ||
            label.StartsWith("바탕화면 페이지 (", StringComparison.Ordinal) ||
            label is "빠른 숨김" or "빠른 숨김 종료" or "Peek" or "Peek 종료" or
                "Peek: 창 위로 그룹 보기" or "Peek 끝내기")
            return TrayMenuSection.Primary;
        if (label.StartsWith("캘린더", StringComparison.Ordinal) ||
            label is "바탕화면 캘린더 사용" or "Google Calendar 동기화")
            return TrayMenuSection.Calendar;
        if (label.StartsWith("CyanDeck ·", StringComparison.Ordinal))
            return TrayMenuSection.Tools;
        if (label is "휴지통 위치 초기화" or "Windows 기본 아이콘 그룹 추가" or
            "바탕화면 분류 규칙 다시 적용" or "바탕화면 항목 지금 다시 검색" or
            "Windows 바탕화면 아이콘 표시" or "Windows 바탕화면 아이콘 숨기기")
            return TrayMenuSection.IconTools;
        if (label is "Windows Explorer 기본 아이콘 긴급 복구" or
            "미완료 업데이트 백업 복구…" or "설정 검사 / 복구" or
            "바탕화면 연결 / 레이아웃 복구" or
            "이전 세션 복구 데이터 사용 가능…" or
            "마지막 앱 업데이트 되돌리기…" or
            "표시 상태 복구" or "백업 / 복원")
            return TrayMenuSection.Recovery;
        // These exact commands already exist in Settings (General/Updates/
        // Troubleshooting). Avoid a second tray command surface.
        if (label is "설정 폴더 열기" or "진단 로그 열기" or
            "진단 묶음 만들기…" or "Windows 시작 시 실행" or
            "자동 분류 규칙 지금 적용")
            return TrayMenuSection.SettingsOnly;
        if (label is "시작 가이드 다시 보기…" or
            "시작 가이드 다시 보기… (일반 모드에서 사용)" or
            "업데이트 확인…" or
            "안전 모드 활성화 — 바탕화면 통합 비활성" or
            "일반 모드로 다시 시작")
            return TrayMenuSection.Tools;
        // Unrecognized commands are not lost: keep them accessible in the
        // general group/display submenu until deliberately classified.
        return TrayMenuSection.Groups;
    }
}
