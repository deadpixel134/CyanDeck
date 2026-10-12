using DesktopDeck.Models;

namespace DesktopDeck.Services;

public sealed record StateIntegrityReport(int Repairs, IReadOnlyList<string> Messages)
{
    public bool Healthy => Repairs == 0;
    public string Summary => Healthy ? "설정 상태가 정상입니다." : $"설정 문제 {Repairs}개를 복구했습니다.";
}

public static class StateIntegrityService
{
    public static StateIntegrityReport Repair(DeckState state)
    {
        var messages = new List<string>();
        var repairs = 0;
        var fenceIds = new HashSet<Guid>();
        var tabIds = new HashSet<Guid>();
        var itemIds = new HashSet<Guid>();
        var templateIds = new HashSet<Guid>();
        var snapshotIds = new HashSet<Guid>();

        void RepairId(ref Guid id, HashSet<Guid> used, string label)
        {
            if (id != Guid.Empty && used.Add(id)) return;
            id = Guid.NewGuid();
            used.Add(id);
            repairs++;
            messages.Add($"{label}에 새 ID를 할당했습니다.");
        }

        static double Safe(double value, double fallback, double min, double max)
            => double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

        foreach (var fence in state.Fences)
        {
            var fid = fence.Id;
            RepairId(ref fid, fenceIds, $"그룹 '{fence.Title}'");
            fence.Id = fid;

            var oldX = fence.X; var oldY = fence.Y; var oldW = fence.Width; var oldH = fence.Height; var oldEH = fence.ExpandedHeight;
            fence.X = Safe(fence.X, 48, -200000, 200000);
            fence.Y = Safe(fence.Y, 48, -200000, 200000);
            fence.Width = Safe(fence.Width, 360, 220, 10000);
            fence.Height = Safe(fence.Height, 310, 42, 10000);
            fence.ExpandedHeight = Safe(fence.ExpandedHeight, Math.Max(140, fence.Height), 140, 10000);
            if (oldX != fence.X || oldY != fence.Y || oldW != fence.Width || oldH != fence.Height || oldEH != fence.ExpandedHeight)
            {
                repairs++;
                messages.Add($"그룹 '{fence.Title}'의 잘못된 위치/크기를 정규화했습니다.");
            }

            if (fence.Tabs.Count == 0)
            {
                fence.Tabs.Add(new DeckTab { Title = "항목" });
                repairs++;
                messages.Add($"그룹 '{fence.Title}'에 누락된 탭을 추가했습니다.");
            }
            var selected = Math.Clamp(fence.SelectedTabIndex, 0, Math.Max(0, fence.Tabs.Count - 1));
            if (selected != fence.SelectedTabIndex)
            {
                fence.SelectedTabIndex = selected;
                repairs++;
                messages.Add($"그룹 '{fence.Title}'의 선택 탭 인덱스를 수정했습니다.");
            }

            foreach (var tab in fence.Tabs)
            {
                var tid = tab.Id;
                RepairId(ref tid, tabIds, $"탭 '{tab.Title}'");
                tab.Id = tid;
                foreach (var item in tab.Items)
                {
                    var iid = item.Id;
                    RepairId(ref iid, itemIds, $"탭 '{tab.Title}'의 항목");
                    item.Id = iid;
                    if (item.PositionX is double px && !double.IsFinite(px)) { item.PositionX = null; repairs++; messages.Add($"탭 '{tab.Title}'의 잘못된 자유 배치 X 좌표를 초기화했습니다."); }
                    if (item.PositionY is double py && !double.IsFinite(py)) { item.PositionY = null; repairs++; messages.Add($"탭 '{tab.Title}'의 잘못된 자유 배치 Y 좌표를 초기화했습니다."); }
                }
            }
        }

        foreach (var item in state.LooseDesktopItems)
        {
            var iid = item.Id;
            RepairId(ref iid, itemIds, "그룹 밖 바탕화면 항목");
            item.Id = iid;
            if (item.PositionX is double px && !double.IsFinite(px)) { item.PositionX = null; repairs++; messages.Add("그룹 밖 바탕화면 항목의 잘못된 X 좌표를 초기화했습니다."); }
            if (item.PositionY is double py && !double.IsFinite(py)) { item.PositionY = null; repairs++; messages.Add("그룹 밖 바탕화면 항목의 잘못된 Y 좌표를 초기화했습니다."); }
        }

        foreach (var template in state.Templates)
        {
            var id = template.Id;
            RepairId(ref id, templateIds, $"템플릿 '{template.Name}'");
            template.Id = id;
        }
        foreach (var snapshot in state.Snapshots)
        {
            var id = snapshot.Id;
            RepairId(ref id, snapshotIds, $"스냅샷 '{snapshot.Name}'");
            snapshot.Id = id;
        }

        return new StateIntegrityReport(repairs, messages);
    }
}
