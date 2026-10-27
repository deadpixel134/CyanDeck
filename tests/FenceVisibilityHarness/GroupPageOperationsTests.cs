using CyanDeck.Services;
using CyanDeck.Topology;

// v8.0.44 group/page operations: dropping at screen edges on two monitors, seam fitting,
// free-spot placement, page sending, the template limit and the global item search.
internal static class GroupPageOperationsTests
{
    internal static void Run()
    {
        var count = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception("Group/page operations: " + message);
            count++;
        }

        // Two 1920x1080 monitors side by side, primary on the left.
        static DesktopMonitorSurfaceMetric MakeMonitor(string name, int left, int right, bool primary)
        {
            var bounds = new DesktopPixelRect(left, 0, right, 1080);
            var work = new DesktopPixelRect(left, 0, right, 1040);
            return new DesktopMonitorSurfaceMetric("monitor:" + name, name, bounds, bounds, work, 96, 96, 96, primary);
        }
        var topology = new DesktopMonitorTopology(1, new[]
        {
            MakeMonitor("DISPLAY1", 0, 1920, true),
            MakeMonitor("DISPLAY2", 1920, 3840, false)
        });
        var seamBlocked = new DesktopMonitorTopologyOptions();
        var seamAllowed = new DesktopMonitorTopologyOptions { AllowInteriorSeamPageNavigation = true };
        const string Left = "monitor:DISPLAY1";
        const string Right = "monitor:DISPLAY2";

        Check(GroupPageDropEdgePolicy.ResolveDirection(topology, Left, 1918, 500, seamBlocked) == 0,
            "dropping at the seam (left monitor side) must not switch pages by default");
        Check(GroupPageDropEdgePolicy.ResolveDirection(topology, Right, 1921, 500, seamBlocked) == 0,
            "dropping at the seam (right monitor side) must not switch pages by default");
        Check(GroupPageDropEdgePolicy.ResolveDirection(topology, Left, 1918, 500, seamAllowed) == 1,
            "the seam switches pages only when the seam setting is on");
        Check(GroupPageDropEdgePolicy.ResolveDirection(topology, Left, 1900, 500, seamAllowed) == 0,
            "a pointer 14-20 px from an edge (edge snapping zone) is not an edge");
        Check(GroupPageDropEdgePolicy.ResolveDirection(topology, Right, 3838, 500, seamBlocked) == 1,
            "the outer right edge moves the group to the next page");
        Check(GroupPageDropEdgePolicy.ResolveDirection(topology, Left, 1, 500, seamBlocked) == -1,
            "the outer left edge moves the group to the previous page");
        Check(GroupPageDropEdgePolicy.ResolveDirection(topology, Left, 960, 500, seamBlocked) == 0,
            "the middle of a monitor never switches pages");
        Check(GroupPageDropEdgePolicy.ResolveDirection(topology, "monitor:missing", 1, 500, seamBlocked) == 0 &&
              GroupPageDropEdgePolicy.ResolveDirection(null, Left, 1, 500, seamBlocked) == 0,
            "an unknown monitor or missing topology fails closed");

        Check(!GroupPageDropEdgePolicy.IsDwellSatisfied(1, 1000, 1, 1299), "a drop before the dwell time does not switch");
        Check(GroupPageDropEdgePolicy.IsDwellSatisfied(1, 1000, 1, 1300), "a drop after the dwell time switches");
        Check(!GroupPageDropEdgePolicy.IsDwellSatisfied(1, 1000, -1, 5000), "the dwell must be on the same edge");
        Check(!GroupPageDropEdgePolicy.IsDwellSatisfied(0, 0, 0, 5000), "no edge means no switch");

        var workAreas = new[] { new DesktopPixelRect(0, 0, 1920, 1040), new DesktopPixelRect(1920, 0, 3840, 1040) };
        var straddling = new DesktopPixelRect(1800, 100, 2160, 410);
        Check(GroupPageDropEdgePolicy.FindDominantWorkArea(straddling, workAreas, out var straddleCount) == 1 && straddleCount == 2,
            "a group straddling the seam belongs to the monitor holding most of it");
        Check(GroupPageDropEdgePolicy.FitInside(straddling, workAreas[1]) == (120, 0),
            "the straddling group is moved fully inside that monitor");
        Check(GroupPageDropEdgePolicy.FindDominantWorkArea(new DesktopPixelRect(100, 100, 460, 410), workAreas, out var insideCount) == 0 && insideCount == 1,
            "a group inside one monitor is not a seam case");
        Check(GroupPageDropEdgePolicy.FitInside(new DesktopPixelRect(100, 1000, 460, 1310), workAreas[0]) == (0, -270),
            "fitting also corrects the vertical overflow");

        var work = new GroupPlacementRect(0, 0, 1920, 1040);
        var occupied = new[] { new GroupPlacementRect(24, 24, 360, 310) };
        var placed = GroupPlacementPolicy.FindFreeSpot(work, 360, 310, occupied, 12);
        Check(placed == new GroupPlacementResult(408, 24, false), "a new group takes the first free spot next to an existing group");
        var near = GroupPlacementPolicy.FindFreeSpot(work, 360, 310, occupied, 12, 1700, 600);
        Check(!near.Overlaps && near.X > 960, "a group created from another group's menu is placed near that group");
        var full = GroupPlacementPolicy.FindFreeSpot(work, 360, 310, new[] { work }, 12, null, null, 2);
        Check(full.Overlaps && full.X >= work.Left && full.X + 360 <= work.Right, "a full monitor still gets a visible, cascaded group");
        var secondMonitor = GroupPlacementPolicy.FindFreeSpot(new GroupPlacementRect(1920, 0, 1920, 1040), 360, 310, occupied, 12);
        Check(secondMonitor == new GroupPlacementResult(1944, 24, false), "the free spot is on the requested monitor");

        Check(GroupPageSendPolicy.ClampTarget(7, 1) == 2 && GroupPageSendPolicy.ClampTarget(-3, 1) == 0 &&
              GroupPageSendPolicy.ClampTarget(1, 1) == 1, "send targets are the existing pages plus one new page");
        Check(GroupPageSendPolicy.NewPageTarget(2, false, 1) == -1 && GroupPageSendPolicy.NewPageTarget(0, false, -1) == -1,
            "a group alone on the last page gets no 'new page' that would leave an empty page behind");
        Check(GroupPageSendPolicy.NewPageTarget(0, false, 2) == 3 && GroupPageSendPolicy.NewPageTarget(1, false, 1) == 2 &&
              GroupPageSendPolicy.NewPageTarget(5, true, 1) == 2 && GroupPageSendPolicy.NewPageTarget(0, true, -1) == 0,
            "the new page follows the other groups' last page");
        Check(GroupPageSendPolicy.ShouldFollow(true, 0, 1, 3) && !GroupPageSendPolicy.ShouldFollow(false, 0, 1, 3) &&
              GroupPageSendPolicy.ShouldFollow(false, 0, 1, 0) && !GroupPageSendPolicy.ShouldFollow(true, 1, 1, 0),
            "following the sent group is a preference, forced only when the current page became empty");

        var fifty = Enumerable.Range(1, 50).Select(i => "t" + i).ToList();
        Check(GroupTemplateLimitPolicy.EvictedBySave(fifty, null).SequenceEqual(new[] { "t50" }),
            "saving a 51st template names the oldest one before deleting it");
        Check(GroupTemplateLimitPolicy.EvictedBySave(fifty, fifty[3]).Count == 0,
            "replacing an existing template removes nothing else");
        Check(GroupTemplateLimitPolicy.EvictedBySave(fifty.Take(10).ToList(), null).Count == 0,
            "under the limit nothing is removed");

        var groupId = Guid.NewGuid();
        var tabId = Guid.NewGuid();
        var entries = new[]
        {
            new GlobalItemSearchEntry("견적서 2026.xlsx", @"C:\Users\kim\Desktop\견적서 2026.xlsx", GlobalItemSearchSource.Collection, groupId, tabId, Guid.NewGuid(), "업무", "문서", 1, false),
            new GlobalItemSearchEntry("옛 견적서.pdf", @"C:\Users\kim\Desktop\옛 견적서.pdf", GlobalItemSearchSource.Collection, groupId, tabId, Guid.NewGuid(), "업무", "업무", 0, true),
            new GlobalItemSearchEntry("견적서", @"D:\Share\견적서", GlobalItemSearchSource.FolderPortal, groupId, tabId, null, "공유", "견적", 0, false),
            new GlobalItemSearchEntry("메모.txt", @"C:\Users\kim\Desktop\메모.txt", GlobalItemSearchSource.Desktop, null, null, Guid.NewGuid(), "", "", 0, false)
        };
        var found = GlobalItemSearchPolicy.Search(entries, "견적서");
        Check(found.Count == 3 && found[0].DisplayName == "견적서" && found[1].DisplayName == "견적서 2026.xlsx",
            "exact names come first, then names that start with the query");
        Check(GlobalItemSearchPolicy.Search(entries, "desktop").Count == 0 && GlobalItemSearchPolicy.Search(entries, "kim").Count == 0,
            "the folder path (Desktop, user name) is not searched");
        Check(GlobalItemSearchPolicy.Search(entries, "견적 2026").Count == 1, "every word must match");
        Check(GlobalItemSearchPolicy.Search(entries, "   ").Count == 0, "an empty query shows nothing");
        Check(entries[0].Location == "업무 › 문서 · 페이지 2" && entries[1].Location == "업무 · 모든 페이지" &&
              entries[2].Location == "공유 › 견적 (폴더 포털) · 페이지 1" && entries[3].Location == "바탕화면 (그룹 밖)",
            "each result shows group › tab · page");
        Check(entries[0].CanReveal && !entries[3].CanReveal, "only items inside groups can be revealed");

        var desktopRoots = new[] { @"C:\Users\kim\Desktop\", null };
        Check(GlobalItemSearchPolicy.SearchablePath(@"C:\Users\kim\Desktop\보고서.docx", desktopRoots) == "보고서.docx" &&
              GlobalItemSearchPolicy.SearchablePath(@"C:\Tools\kim.exe", desktopRoots) == @"C:\Tools\kim.exe" &&
              GlobalItemSearchPolicy.SearchablePath(@"C:\Users\kim\DesktopOld\a.txt", desktopRoots) == @"C:\Users\kim\DesktopOld\a.txt",
            "in-group search drops only the Desktop folder part of a path");

        Check(TrayMenuRoutingPolicy.Route("search.items", "다른 문구") == TrayMenuSection.Primary,
            "the tray item search stays in the first tray level");

        Console.WriteLine($"PASS: {count} group/page operation checks (two-monitor edge drops, placement, templates, item search)");
    }
}
