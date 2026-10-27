using System;
using System.Text.RegularExpressions;

namespace CyanDeck.Services;

/// <summary>
/// v8.0.43: Korean names for automatic layout snapshots and the separate retention bucket for
/// undo checkpoints taken before align/distribute/pack/restore. Undo checkpoints keep only
/// the newest few and never count against the user's own snapshot limit. Snapshots created
/// by older builds ("Before align-left 2026-10-27 14:03:00", "Automatic 2026-10-27") are
/// recognized by their exact machine-generated form and shown in Korean; renamed snapshots
/// never match and stay user snapshots.
/// </summary>
internal static class LayoutSnapshotNamePolicy
{
    public const int UndoRetention = 5;

    public const string AlignLeft = "왼쪽 맞춤";
    public const string AlignTop = "위쪽 맞춤";
    public const string DistributeHorizontally = "가로 간격 균등 배치";
    public const string DistributeVertically = "세로 간격 균등 배치";
    public const string PackGroups = "각 모니터 안에 정리";
    public const string SnapshotRestore = "스냅샷 복원";
    public const string RecoverAllGroups = "모든 그룹을 이 모니터로 옮기기";

    private static readonly Regex LegacyUndoName = new(
        @"^Before (align-left|align-top|horizontal-distribute|vertical-distribute|pack-groups|snapshot restore|recover all groups to monitor) \d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$",
        RegexOptions.CultureInvariant);
    private static readonly Regex LegacyAutomaticName = new(@"^Automatic (\d{4})-(\d{2})-(\d{2})$", RegexOptions.CultureInvariant);

    // Explicit digits: the ":" of a custom time format follows the culture's separator.
    internal static string FormatShortDateTime(DateTime local) => $"{local.Month}월 {local.Day}일 {local.Hour:D2}:{local.Minute:D2}";

    internal static string BuildUndoName(string action, DateTime local) => $"{action} 전 · {FormatShortDateTime(local)}";

    /// <summary>
    /// Tray label of the one-step undo command. A checkpoint from an earlier day shows its
    /// date, so an old checkpoint is never mistaken for today's change.
    /// </summary>
    internal static string BuildUndoCommandLabel(string action, DateTime createdLocal, DateTime nowLocal)
    {
        var when = createdLocal.Date == nowLocal.Date
            ? $"{createdLocal.Hour:D2}:{createdLocal.Minute:D2}"
            : FormatShortDateTime(createdLocal);
        return $"마지막 배치 변경 되돌리기 ({action}, {when})";
    }

    internal static string BuildAutomaticName(DateTime localDate) => $"자동 · {localDate.Month}월 {localDate.Day}일";

    internal static string BuildManualDefaultName(DateTime local) =>
        $"레이아웃 {local.Year:D4}-{local.Month:D2}-{local.Day:D2} {local.Hour:D2}:{local.Minute:D2}";

    /// <summary>The Korean action of a legacy "Before …" undo snapshot, or null.</summary>
    internal static string? LegacyUndoAction(string? name)
    {
        var match = LegacyUndoName.Match(name ?? "");
        if (!match.Success) return null;
        return match.Groups[1].Value switch
        {
            "align-left" => AlignLeft,
            "align-top" => AlignTop,
            "horizontal-distribute" => DistributeHorizontally,
            "vertical-distribute" => DistributeVertically,
            "pack-groups" => PackGroups,
            "snapshot restore" => SnapshotRestore,
            _ => RecoverAllGroups
        };
    }

    internal static bool IsUndoCheckpoint(bool flagged, bool automatic, string? name) =>
        flagged || (!automatic && LegacyUndoAction(name) is not null);

    /// <summary>Name shown in the tray and the snapshot gallery.</summary>
    internal static string Display(string? name, bool automatic, DateTime createdLocal)
    {
        var value = (name ?? "").Trim();
        if (automatic && LegacyAutomaticName.IsMatch(value)) return BuildAutomaticName(createdLocal);
        var legacyAction = automatic ? null : LegacyUndoAction(value);
        if (legacyAction is not null) return BuildUndoName(legacyAction, createdLocal);
        return value.Length == 0 ? "스냅샷" : value;
    }
}
