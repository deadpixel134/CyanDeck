using System.Reflection;
using DesktopDeck.Models;

namespace DesktopDeck.Services;

public sealed record RecoveryDiffSummary(int GroupDelta, int TabDelta, int ItemDelta, int ChangedGroups, int ChangedSettings)
{
    public bool HasChanges => GroupDelta != 0 || TabDelta != 0 || ItemDelta != 0 || ChangedGroups != 0 || ChangedSettings != 0;

    public string ToDisplayString()
    {
        static string Signed(int value) => value > 0 ? $"+{value}" : value.ToString();
        return $"현재 상태와 비교:\n" +
               $"• 그룹: {Signed(GroupDelta)}\n" +
               $"• 탭: {Signed(TabDelta)}\n" +
               $"• 항목 참조: {Signed(ItemDelta)}\n" +
               $"• 레이아웃/상태가 바뀐 기존 그룹: {ChangedGroups}\n" +
               $"• 변경된 전역 설정: {ChangedSettings}";
    }
}

public static class RecoveryDiffService
{
    public static RecoveryDiffSummary Compare(DeckState current, DeckState target)
    {
        var groupDelta = target.Fences.Count - current.Fences.Count;
        var tabDelta = target.Fences.Sum(f => f.Tabs.Count) - current.Fences.Sum(f => f.Tabs.Count);
        var itemDelta = target.Fences.Sum(f => f.Tabs.Sum(t => t.Items.Count)) - current.Fences.Sum(f => f.Tabs.Sum(t => t.Items.Count));

        var currentById = current.Fences.ToDictionary(f => f.Id);
        var changedGroups = 0;
        foreach (var fence in target.Fences)
        {
            if (!currentById.TryGetValue(fence.Id, out var existing)) continue;
            if (Math.Abs(existing.X - fence.X) > 0.5 || Math.Abs(existing.Y - fence.Y) > 0.5 ||
                Math.Abs(existing.Width - fence.Width) > 0.5 || Math.Abs(existing.Height - fence.Height) > 0.5 ||
                existing.PageIndex != fence.PageIndex || existing.RolledUp != fence.RolledUp ||
                existing.SelectedTabIndex != fence.SelectedTabIndex || existing.Tabs.Count != fence.Tabs.Count)
                changedGroups++;
        }

        var changedSettings = 0;
        foreach (var property in typeof(DeckSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || property.Name == nameof(DeckSettings.LastUpdateCheckUtc)) continue;
            var left = property.GetValue(current.Settings);
            var right = property.GetValue(target.Settings);
            if (!Equals(left, right)) changedSettings++;
        }
        return new RecoveryDiffSummary(groupDelta, tabDelta, itemDelta, changedGroups, changedSettings);
    }
}
