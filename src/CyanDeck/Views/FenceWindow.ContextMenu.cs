using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using Rectangle = System.Windows.Shapes.Rectangle;
using System.Windows.Threading;
using CyanDeck.Interop;
using CyanDeck.Animations;
using CyanDeck.Appearance;
using CyanDeck.Models;
using CyanDeck.Layout;
using CyanDeck.ShellIntegration;
using CyanDeck.Services;
using Forms = System.Windows.Forms;

namespace CyanDeck.Views;

/// <summary>Group menu composition and commands, separate from window rendering.</summary>
public partial class FenceWindow
{
    // One menu serves both the right-click and the header "..." button. Frequent commands
    // stay at the top level; options are grouped by intent (tabs, sort, view, appearance,
    // placement, rules) and every option list shows its current value with a check mark.
    private void PopulateGroupContextMenu(ContextMenu menu)
    {
        var canEditGroups = CapabilityAllowed("Settings.EditGroups");
        var canCreateDelete = CapabilityAllowed("Layout.CreateDeleteGroups");

        menu.Items.Add(WithGesture(MakeItem("검색…", ShowSearch), "Ctrl+F"));
        menu.Items.Add(WithGesture(MakeItem("모든 그룹에서 찾기…", () => GlobalSearchRequested?.Invoke()), "Ctrl+Shift+F"));
        var renameGroupItem = MakeItem("그룹 이름 바꾸기…", RenameGroup);
        renameGroupItem.IsEnabled = canEditGroups;
        menu.Items.Add(renameGroupItem);
        menu.Items.Add(new Separator());

        menu.Items.Add(BuildTabsMenu(canEditGroups));
        if (SelectedTab is { Kind: DeckTabKind.FolderPortal } portal)
            menu.Items.Add(BuildPortalMenu(portal));
        menu.Items.Add(BuildSortMenu());
        menu.Items.Add(BuildViewMenu());
        menu.Items.Add(BuildAppearanceMenu());
        menu.Items.Add(BuildBehaviorMenu());
        menu.Items.Add(BuildAutomationMenu());

        if (_settings.EnableRollup)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeItem(Model.RolledUp ? "그룹 펼치기" : "그룹 접기", ToggleRollUp));
        }

        menu.Items.Add(new Separator());
        var newFenceItem = MakeItem("새 그룹 만들기", () => NewFenceRequested?.Invoke());
        newFenceItem.IsEnabled = canCreateDelete;
        menu.Items.Add(newFenceItem);
        menu.Items.Add(BuildSendToPageMenu());
        menu.Items.Add(BuildGroupToolsMenu(canCreateDelete));
        menu.Items.Add(MakeItem("CyanDeck 설정…", () => SettingsRequested?.Invoke()));

        // Destructive command is isolated at the bottom and always confirmed.
        menu.Items.Add(new Separator());
        var deleteFenceItem = MakeItem("그룹 삭제…", ConfirmDeleteGroup);
        deleteFenceItem.IsEnabled = canCreateDelete;
        deleteFenceItem.Tag = MenuDangerTag;
        menu.Items.Add(deleteFenceItem);
    }

    private MenuItem BuildTabsMenu(bool canEditGroups)
    {
        var tabsMenu = new MenuItem { Header = "탭" };
        foreach (var candidate in Model.Tabs)
        {
            var local = candidate;
            var label = local.Kind == DeckTabKind.FolderPortal ? $"{local.Title}  (폴더 포털)" : local.Title;
            tabsMenu.Items.Add(MakeCheckItem(label, ReferenceEquals(local, SelectedTab), () => SelectTab(local)));
        }
        tabsMenu.Items.Add(new Separator());
        var addTabItem = MakeItem("새 탭 추가", AddCollectionTab);
        addTabItem.IsEnabled = canEditGroups;
        tabsMenu.Items.Add(addTabItem);
        var addPortalItem = MakeItem("폴더 포털 탭 추가…", AddPortalTab);
        addPortalItem.IsEnabled = CapabilityAllowed("FolderPortal.Edit");
        tabsMenu.Items.Add(addPortalItem);
        if (Model.Tabs.Count > 1)
        {
            var index = SelectedTab is null ? -1 : Model.Tabs.IndexOf(SelectedTab);
            tabsMenu.Items.Add(new Separator());
            var renameTabItem = MakeItem("현재 탭 이름 바꾸기…", RenameCurrentTab);
            renameTabItem.IsEnabled = canEditGroups;
            tabsMenu.Items.Add(renameTabItem);
            var moveLeft = MakeItem("현재 탭 왼쪽으로 이동", () => MoveCurrentTab(-1));
            moveLeft.IsEnabled = index > 0;
            tabsMenu.Items.Add(moveLeft);
            var moveRight = MakeItem("현재 탭 오른쪽으로 이동", () => MoveCurrentTab(1));
            moveRight.IsEnabled = index >= 0 && index < Model.Tabs.Count - 1;
            tabsMenu.Items.Add(moveRight);
            tabsMenu.Items.Add(MakeItem("현재 탭을 새 그룹으로 분리", DetachCurrentTab));
            tabsMenu.Items.Add(new Separator());
            var removeTabItem = MakeItem("현재 탭 제거…", RemoveCurrentTab);
            removeTabItem.Tag = MenuDangerTag; // asks first, then re-places desktop files
            removeTabItem.IsEnabled = canEditGroups;
            tabsMenu.Items.Add(removeTabItem);
        }
        return tabsMenu;
    }

    private MenuItem BuildPortalMenu(DeckTab portal)
    {
        var portalMenu = new MenuItem { Header = "폴더 포털" };
        portalMenu.Items.Add(WithGesture(MakeItem("뒤로", NavigatePortalBack), "Alt+왼쪽"));
        portalMenu.Items.Add(WithGesture(MakeItem("앞으로", NavigatePortalForward), "Alt+오른쪽"));
        portalMenu.Items.Add(WithGesture(MakeItem("상위 폴더", NavigatePortalUp), "Alt+위쪽"));
        portalMenu.Items.Add(MakeItem("포털의 처음 폴더로", NavigatePortalHome));
        portalMenu.Items.Add(new Separator());
        portalMenu.Items.Add(MakeItem("현재 폴더를 Explorer에서 열기", () => { if (!string.IsNullOrWhiteSpace(portal.PortalPath)) Open(portal.PortalPath); }));
        portalMenu.Items.Add(new Separator());
        portalMenu.Items.Add(MakeCheckItem("하위 폴더를 포털 안에서 열기", portal.NavigateFoldersInPortal, () =>
        {
            portal.NavigateFoldersInPortal = !portal.NavigateFoldersInPortal;
            ModelChanged?.Invoke();
        }));
        portalMenu.Items.Add(MakeCheckItem("하위 폴더에도 같은 보기 방식 사용", portal.InheritPortalViewMode, () =>
        {
            portal.InheritPortalViewMode = !portal.InheritPortalViewMode;
            RememberPortalView(portal, portal.PortalPath);
            ModelChanged?.Invoke();
        }));
        portalMenu.Items.Add(MakeCheckItem("탭에 폴더 아이콘 표시", portal.ShowPortalFolderIcon, () =>
        {
            portal.ShowPortalFolderIcon = !portal.ShowPortalFolderIcon;
            RebuildTabs();
            ModelChanged?.Invoke();
        }));

        var sync = new MenuItem { Header = "Explorer 보기 동기화" };
        sync.Items.Add(MakeCheckItem("같은 폴더를 연 Explorer 창과 자동 동기화", portal.SyncOpenExplorerView, () =>
        {
            portal.SyncOpenExplorerView = !portal.SyncOpenExplorerView;
            if (portal.SyncOpenExplorerView) TrySyncPortalFromExplorer(portal, force: true);
            ModelChanged?.Invoke();
        }));
        if (portal.SyncOpenExplorerView)
        {
            sync.Items.Add(new Separator());
            sync.Items.Add(MakeMenuSectionHeader("함께 맞출 항목"));
            sync.Items.Add(MakeCheckItem("보기 방식", portal.SyncExplorerViewMode, () => { portal.SyncExplorerViewMode = !portal.SyncExplorerViewMode; TrySyncPortalFromExplorer(portal, force: true); ModelChanged?.Invoke(); }));
            sync.Items.Add(MakeCheckItem("아이콘 크기", portal.SyncExplorerIconSize, () => { portal.SyncExplorerIconSize = !portal.SyncExplorerIconSize; TrySyncPortalFromExplorer(portal, force: true); ModelChanged?.Invoke(); }));
            sync.Items.Add(MakeCheckItem("정렬", portal.SyncExplorerSort, () => { portal.SyncExplorerSort = !portal.SyncExplorerSort; TrySyncPortalFromExplorer(portal, force: true); ModelChanged?.Invoke(); }));
            var detailsColumns = MakeCheckItem("자세히 보기 열·순서", portal.SyncExplorerDetailsColumns, () =>
            {
                portal.SyncExplorerDetailsColumns = !portal.SyncExplorerDetailsColumns;
                TrySyncPortalFromExplorer(portal, force: true);
                ModelChanged?.Invoke();
            });
            detailsColumns.IsEnabled = CapabilityAllowed("FolderPortal.Edit");
            sync.Items.Add(detailsColumns);
            var detailsWidths = MakeCheckItem("자세히 보기 열 너비", portal.SyncExplorerDetailsColumnWidths, () =>
            {
                portal.SyncExplorerDetailsColumnWidths = !portal.SyncExplorerDetailsColumnWidths;
                TrySyncPortalFromExplorer(portal, force: true);
                ModelChanged?.Invoke();
            });
            detailsWidths.IsEnabled = CapabilityAllowed("FolderPortal.Edit");
            sync.Items.Add(detailsWidths);
        }
        sync.Items.Add(new Separator());
        sync.Items.Add(MakeItem("지금 한 번 동기화", () => TrySyncPortalFromExplorer(portal, force: true)));
        portalMenu.Items.Add(new Separator());
        portalMenu.Items.Add(sync);
        return portalMenu;
    }

    private MenuItem BuildSortMenu()
    {
        var sortMenu = new MenuItem { Header = "정렬" };
        var tab = SelectedTab;
        foreach (var mode in Enum.GetValues<DeckSortMode>())
        {
            var local = mode;
            var label = mode switch
            {
                DeckSortMode.Name => "이름",
                DeckSortMode.Type => "형식",
                DeckSortMode.Modified => "수정한 날짜",
                DeckSortMode.Added => "추가한 날짜",
                DeckSortMode.Size => "크기",
                _ => "열기 횟수"
            };
            // Re-selecting the active key still flips the direction, as before.
            sortMenu.Items.Add(MakeCheckItem(label, tab?.Sort == mode, () => ApplyGroupSort(t =>
            {
                if (t.Sort == local) t.SortDescending = !t.SortDescending;
                else { t.Sort = local; t.SortDescending = false; }
            })));
        }
        sortMenu.Items.Add(new Separator());
        sortMenu.Items.Add(MakeCheckItem("오름차순", tab is { SortDescending: false }, () => ApplyGroupSort(t => t.SortDescending = false)));
        sortMenu.Items.Add(MakeCheckItem("내림차순", tab is { SortDescending: true }, () => ApplyGroupSort(t => t.SortDescending = true)));
        sortMenu.Items.Add(new Separator());
        sortMenu.Items.Add(MakeCheckItem("폴더 먼저", tab?.FoldersFirst == true, () => ApplyGroupSort(t => t.FoldersFirst = !t.FoldersFirst)));
        return sortMenu;
    }

    private void ApplyGroupSort(Action<DeckTab> change)
    {
        var tab = SelectedTab;
        if (tab is null) return;
        change(tab);
        RefreshItems(IconMovementAnimationContext.SortOrArrange);
        ModelChanged?.Invoke();
    }

    private MenuItem BuildViewMenu()
    {
        var viewMenu = new MenuItem { Header = "보기" };
        var tab = SelectedTab;
        foreach (var mode in Enum.GetValues<DeckItemViewMode>())
        {
            var localMode = mode;
            var label = mode switch
            {
                DeckItemViewMode.Grid => "격자",
                DeckItemViewMode.List => "목록",
                _ => "자세히"
            };
            viewMenu.Items.Add(MakeCheckItem(label, tab?.ViewMode == mode, () => SetItemViewMode(localMode)));
        }
        if (tab?.ViewMode == DeckItemViewMode.List)
        {
            viewMenu.Items.Add(new Separator());
            foreach (var orientation in Enum.GetValues<DeckListOrientation>())
            {
                var localOrientation = orientation;
                viewMenu.Items.Add(MakeCheckItem(orientation == DeckListOrientation.Vertical ? "세로로 나열" : "가로로 나열",
                    tab.ListOrientation == orientation, () => SetListOrientation(localOrientation)));
            }
        }
        if (tab?.ViewMode == DeckItemViewMode.Details)
        {
            viewMenu.Items.Add(new Separator());
            viewMenu.Items.Add(MakeCheckItem("열 제목 표시", tab.ShowDetailsHeader, ToggleDetailsHeader));
        }

        // A single icon-size list for every tab kind (previously split between View and Appearance).
        viewMenu.Items.Add(new Separator());
        var presets = new (double Size, string Label)[]
        {
            (24, "아주 작게 (24)"), (32, "작게 (32)"), (46, "보통 (46)"),
            (64, "크게 (64)"), (88, "매우 크게 (88)"), (128, "최대 (128)")
        };
        var iconSizeMenu = new MenuItem { Header = "아이콘 크기" };
        foreach (var (size, label) in presets)
        {
            var localSize = size;
            iconSizeMenu.Items.Add(MakeCheckItem(label, NearlyEqual(Model.IconSize, size, 0.5), () => SetIconSize(localSize)));
        }
        if (!presets.Any(p => NearlyEqual(Model.IconSize, p.Size, 0.5)))
        {
            iconSizeMenu.Items.Add(new Separator());
            iconSizeMenu.Items.Add(new MenuItem { Header = $"사용자 지정 ({Model.IconSize:0})", IsCheckable = true, IsChecked = true, IsEnabled = false });
        }
        viewMenu.Items.Add(iconSizeMenu);
        return viewMenu;
    }

    private MenuItem BuildAppearanceMenu()
    {
        var appearance = new MenuItem { Header = "모양", IsEnabled = CapabilityAllowed("Appearance.Edit") };

        appearance.Items.Add(MakeChoiceMenu("색상 테마", new (string, bool, Action)[]
        {
            ("그래파이트", IsTheme("#17191E", "#7AA2FF"), () => SetTheme("#17191E", "#7AA2FF")),
            ("슬레이트", IsTheme("#18202A", "#79B7FF"), () => SetTheme("#18202A", "#79B7FF")),
            ("바이올렛", IsTheme("#1C1924", "#A995FF"), () => SetTheme("#1C1924", "#A995FF")),
            ("에메랄드", IsTheme("#14201C", "#70D3A9"), () => SetTheme("#14201C", "#70D3A9")),
            ("앰버", IsTheme("#221C13", "#F0BA62"), () => SetTheme("#221C13", "#F0BA62")),
            ("로즈", IsTheme("#22171C", "#ED8FAF"), () => SetTheme("#22171C", "#ED8FAF"))
        }));
        appearance.Items.Add(MakeChoiceMenu("배경 불투명도", new[] { .50, .65, .75, .85, .95 }
            .Select(v => ($"{v:P0}", NearlyEqual(Model.Opacity, v, 0.005), (Action)(() => SetOpacity(v))))));
        appearance.Items.Add(MakeChoiceMenu("모서리", new (string, bool, Action)[]
        {
            ("각지게", NearlyEqual(Model.CornerRadius, 0, 0.5), () => SetCornerRadius(0)),
            ("살짝 둥글게", NearlyEqual(Model.CornerRadius, 6, 0.5), () => SetCornerRadius(6)),
            ("둥글게", NearlyEqual(Model.CornerRadius, 12, 0.5), () => SetCornerRadius(12)),
            ("많이 둥글게", NearlyEqual(Model.CornerRadius, 18, 0.5), () => SetCornerRadius(18))
        }));

        appearance.Items.Add(new Separator());
        var spacingLinked = _settings.UseSameSpacingInsideAndOutsideGroups;
        var iconSpacingMenu = MakeChoiceMenu("아이콘 간격", new (string, bool, Action)[]
        {
            ("좁게", IsSpacing(4, 4), () => SetIconSpacing(4, 4)),
            ("보통", IsSpacing(14, 12), () => SetIconSpacing(14, 12)),
            ("여유롭게", IsSpacing(26, 22), () => SetIconSpacing(26, 22)),
            ("넓게", IsSpacing(42, 32), () => SetIconSpacing(42, 32))
        });
        iconSpacingMenu.IsEnabled = !spacingLinked
            && CapabilityAllowed(LinkedIconSpacingIntegrationContract.ManagedCapability);
        if (spacingLinked)
        {
            // Explain why the item is unavailable instead of silently greying it out.
            iconSpacingMenu.Header = "아이콘 간격 (바탕화면과 같게 사용 중)";
            ToolTipService.SetShowOnDisabled(iconSpacingMenu, true);
            iconSpacingMenu.ToolTip = "설정 > 그룹 모양 > 모든 그룹 공통 간격에서 '모든 그룹에 바탕화면 아이콘 간격 사용'을 끄면 그룹별로 바꿀 수 있습니다.";
        }
        appearance.Items.Add(iconSpacingMenu);
        appearance.Items.Add(MakeChoiceMenu("아이콘 이름", Enum.GetValues<DeckIconLabelMode>().Select(mode => (mode switch
        {
            DeckIconLabelMode.Always => "항상 표시",
            DeckIconLabelMode.Mouseover => "마우스를 올릴 때만 표시",
            _ => "숨김"
        }, Model.IconLabelMode == mode, (Action)(() => SetIconLabelMode(mode))))));
        appearance.Items.Add(MakeChoiceMenu("아이콘 색조", new (string, bool, Action)[]
        {
            ("끄기", NearlyEqual(Model.IconTintStrength, 0), () => SetTint(0)),
            ("약하게", NearlyEqual(Model.IconTintStrength, .32), () => SetTint(.32)),
            ("중간", NearlyEqual(Model.IconTintStrength, .58), () => SetTint(.58)),
            ("강하게", NearlyEqual(Model.IconTintStrength, .82), () => SetTint(.82))
        }));
        var layoutMode = new MenuItem { Header = "아이콘 배치" };
        foreach (var mode in Enum.GetValues<DeckIconLayoutMode>())
        {
            var localMode = mode;
            layoutMode.Items.Add(MakeCheckItem(mode == DeckIconLayoutMode.Grid ? "자동 격자" : "자유 배치",
                Model.IconLayoutMode == mode, () => SetIconLayout(localMode)));
        }
        if (Model.IconLayoutMode != DeckIconLayoutMode.Grid)
        {
            layoutMode.Items.Add(new Separator());
            layoutMode.Items.Add(MakeMenuSectionHeader("자유 배치 정리"));
            layoutMode.Items.Add(MakeItem("왼쪽 위부터 빈틈없이 정렬", PackFreePositions));
            layoutMode.Items.Add(MakeItem("간격 격자에 맞춤", SnapFreePositions));
            layoutMode.Items.Add(MakeItem("선택한 아이콘 왼쪽 맞춤", AlignSelectedFreeIconsLeft));
            layoutMode.Items.Add(MakeItem("선택한 아이콘 위쪽 맞춤", AlignSelectedFreeIconsTop));
            layoutMode.Items.Add(MakeItem("아이콘 위치 초기화", ResetFreePositions));
        }
        appearance.Items.Add(layoutMode);

        appearance.Items.Add(new Separator());
        var titlebar = new MenuItem { Header = "제목 표시줄" };
        foreach (var mode in Enum.GetValues<DeckTitlebarMode>())
        {
            var localMode = mode;
            titlebar.Items.Add(MakeCheckItem(mode switch
            {
                DeckTitlebarMode.Always => "항상 표시",
                DeckTitlebarMode.Mouseover => "마우스를 올릴 때만 표시",
                _ => "숨김"
            }, Model.TitlebarMode == mode, () =>
            {
                Model.TitlebarMode = localMode;
                ApplyTitlebarMode(DesktopPointerHover.IsActuallyOver(this));
                ModelChanged?.Invoke();
            }));
        }
        titlebar.Items.Add(new Separator());
        titlebar.Items.Add(MakeCheckItem("화면 위쪽에 붙으면 제목 표시줄을 아래로", Model.MoveTitlebarToBottomNearTop, () =>
        {
            Model.MoveTitlebarToBottomNearTop = !Model.MoveTitlebarToBottomNearTop;
            UpdateTitlebarPlacement();
            ModelChanged?.Invoke();
        }));
        appearance.Items.Add(titlebar);

        var titleText = new MenuItem { Header = "제목 글자" };
        titleText.Items.Add(MakeMenuSectionHeader("글꼴"));
        foreach (var family in new[] { "Segoe UI", "Segoe UI Variable", "Arial", "Consolas" })
            titleText.Items.Add(MakeCheckItem(family, SameMenuText(Model.TitleFontFamily, family), () => SetTitleFont(family)));
        titleText.Items.Add(new Separator());
        titleText.Items.Add(MakeMenuSectionHeader("크기"));
        foreach (var (size, label) in new (double, string)[] { (10, "작게 (10)"), (12, "보통 (12)"), (14, "크게 (14)"), (16, "매우 크게 (16)") })
            titleText.Items.Add(MakeCheckItem(label, NearlyEqual(Model.TitleFontSize, size, 0.5), () => SetTitleFontSize(size)));
        titleText.Items.Add(new Separator());
        titleText.Items.Add(MakeMenuSectionHeader("색상"));
        foreach (var (color, label) in new[] { ("#F4F6FA", "흰색"), ("#C8D0DA", "연한 회색"), ("#AFC9FF", "파랑"), ("#FFD28A", "앰버"), ("#A9E4CD", "민트") })
            titleText.Items.Add(MakeCheckItem(label, SameMenuText(Model.TitleForeground, color), () => SetTitleColor(color)));
        appearance.Items.Add(titleText);

        var tabLook = new MenuItem { Header = "탭 모양" };
        foreach (var style in Enum.GetValues<DeckTabStyle>())
        {
            var localStyle = style;
            tabLook.Items.Add(MakeCheckItem(style switch
            {
                DeckTabStyle.MenuOnly => "메뉴만",
                DeckTabStyle.Flat => "평면",
                DeckTabStyle.Segmented => "분할",
                _ => "둥글게"
            }, Model.TabStyle == style, () =>
            {
                Model.TabStyle = localStyle;
                RebuildTabs();
                ModelChanged?.Invoke();
            }));
        }
        tabLook.Items.Add(new Separator());
        tabLook.Items.Add(MakeMenuSectionHeader("현재 탭 강조색"));
        foreach (var (color, label) in new[] { ("#7AA2FF", "파랑"), ("#AA8EFF", "바이올렛"), ("#70D3A9", "초록"), ("#F0BA62", "앰버"), ("#ED8FAF", "로즈") })
            tabLook.Items.Add(MakeCheckItem(label, SameMenuText(SelectedTab?.Accent, color), () => SetTabAccent(color)));
        appearance.Items.Add(tabLook);

        appearance.Items.Add(new Separator());
        appearance.Items.Add(MakeCheckItem("마우스가 멀어지면 흐리게 (카멜레온)", Model.AutoFade, () =>
        {
            Model.AutoFade = !Model.AutoFade;
            Shell.Opacity = 1;
            _lastAppliedChameleonOpacity = 1;
            ModelChanged?.Invoke();
        }));
        appearance.Items.Add(new Separator());
        appearance.Items.Add(MakeItem("이 그룹 모양 복사", CopyAppearance));
        var pasteAppearance = MakeItem("복사한 모양 붙여넣기", PasteAppearance);
        pasteAppearance.IsEnabled = _appearanceClipboard is not null;
        appearance.Items.Add(pasteAppearance);
        appearance.Items.Add(MakeItem("기본 모양으로 되돌리기", ResetAppearance));
        return appearance;
    }

    private MenuItem BuildBehaviorMenu()
    {
        var behavior = new MenuItem { Header = "배치 및 동작" };
        behavior.Items.Add(MakeCheckItem("위치와 크기 잠금", Model.Locked, () =>
        {
            Model.Locked = !Model.Locked;
            ApplyRolledState(false);
            ModelChanged?.Invoke();
        }));
        behavior.Items.Add(MakeCheckItem("모든 바탕화면 페이지에 표시", Model.ShowOnAllPages, () =>
        {
            Model.ShowOnAllPages = !Model.ShowOnAllPages;
            ModelChanged?.Invoke();
        }));
        behavior.Items.Add(MakeCheckItem("빠른 숨김을 해도 계속 표시", Model.AlwaysVisible, () =>
        {
            Model.AlwaysVisible = !Model.AlwaysVisible;
            ModelChanged?.Invoke();
            VisibilitySettingsChanged?.Invoke();
        }));
        if (_settings.EnableRollup)
        {
            behavior.Items.Add(new Separator());
            var rollup = new MenuItem { Header = "접힌 그룹 여는 방법" };
            foreach (var mode in Enum.GetValues<DeckRollupActivation>())
            {
                var localMode = mode;
                rollup.Items.Add(MakeCheckItem(mode switch
                {
                    DeckRollupActivation.Hover => "마우스를 올리면 바로 열기",
                    DeckRollupActivation.DelayedHover => "잠시 올려두면 열기",
                    _ => "클릭해야 열기"
                }, Model.RollupActivation == mode, () => SetRollupActivation(localMode)));
            }
            behavior.Items.Add(rollup);
        }
        // The current page is shown and changed in the top-level "다른 페이지로 보내기" submenu.
        return behavior;
    }

    private MenuItem BuildAutomationMenu()
    {
        var automation = new MenuItem { Header = "자동 분류 규칙", IsEnabled = CapabilityAllowed("Automation.EditRules") };
        automation.Items.Add(MakeItem("규칙 편집…", OpenRuleEditor));
        automation.Items.Add(new Separator());
        automation.Items.Add(MakeMenuSectionHeader("현재 탭에 빠르게 추가"));
        automation.Items.Add(MakeItem("확장자로…", () => AddRule(RuleField.Extension, "확장자(세미콜론으로 구분)", "pdf;docx")));
        automation.Items.Add(MakeItem("이름에 포함된 글자로…", () => AddRule(RuleField.NameContains, "이름에 포함할 텍스트", "invoice")));
        automation.Items.Add(MakeItem("정확한 이름으로…", () => AddRule(RuleField.NameExact, "정확한 파일 또는 폴더 이름", "Report.xlsx")));
        automation.Items.Add(MakeItem("최근 수정한 항목(일수)으로…", () => AddRule(RuleField.ModifiedWithinDays, "일수", "7")));
        automation.Items.Add(MakeItem("바로 가기 대상으로…", () => AddRule(RuleField.ShortcutTargetContains, "바로 가기 대상에 포함할 텍스트", "steam")));
        automation.Items.Add(MakeItem("아이콘 형식으로…", () => AddRule(RuleField.IconTypeIs, "아이콘 형식(file/folder/shortcut/application/other)", "shortcut")));
        automation.Items.Add(new Separator());
        var clear = MakeItem("현재 탭 규칙 모두 지우기…", ConfirmClearTabRules);
        clear.IsEnabled = SelectedTab is { Rules.Count: > 0 };
        automation.Items.Add(clear);
        return automation;
    }

    private MenuItem BuildGroupToolsMenu(bool canCreateDelete)
    {
        var groupTools = new MenuItem { Header = "그룹 복제 및 템플릿" };
        var duplicateItem = MakeItem("이 그룹 복제", () => DuplicateRequested?.Invoke(this));
        duplicateItem.IsEnabled = canCreateDelete;
        groupTools.Items.Add(duplicateItem);
        var saveTemplateItem = MakeItem("이 그룹을 템플릿으로 저장…", () => SaveTemplateRequested?.Invoke(this));
        saveTemplateItem.IsEnabled = CapabilityAllowed("Templates.Edit");
        groupTools.Items.Add(saveTemplateItem);
        if (_templates.Count > 0)
        {
            // Each template has create / rename / delete. The list itself stays open when group
            // creation is locked by policy so templates can still be renamed or deleted.
            var fromTemplate = new MenuItem { Header = "저장한 템플릿" };
            foreach (var template in _templates.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var localTemplate = template;
                fromTemplate.Items.Add(BuildTemplateEntryMenu(localTemplate, canCreateDelete));
            }
            groupTools.Items.Add(fromTemplate);
        }
        return groupTools;
    }

    private void RenameGroup()
    {
        var text = TextPrompt.Show(this, "그룹 이름 바꾸기", "이름", Model.Title);
        if (text is null) return;
        Model.Title = text;
        if (Model.Tabs.Count == 1)
        {
            Model.Tabs[0].Title = text;
            RebuildTabs();
        }
        ModelChanged?.Invoke();
    }

    private void RenameCurrentTab()
    {
        var tab = SelectedTab;
        if (tab is null) return;
        var text = TextPrompt.Show(this, "탭 이름 바꾸기", "이름", tab.Title);
        if (text is null) return;
        tab.Title = text;
        RebuildTabs();
        ModelChanged?.Invoke();
    }

    private void ConfirmDeleteGroup()
    {
        var name = string.IsNullOrWhiteSpace(Model.Title) ? "이 그룹" : $"'{Model.Title}' 그룹";
        var answer = System.Windows.MessageBox.Show(this,
            $"{name}을(를) 삭제하시겠습니까?\n\n그룹과 그 안의 탭, 모양 설정이 삭제됩니다. 바탕화면의 실제 파일과 폴더는 삭제되지 않습니다.\n그룹 안의 바탕화면 항목은 다른 탭이나 그룹 밖 바탕화면 아이콘으로 다시 배치됩니다.",
            "그룹 삭제", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes) DeleteRequested?.Invoke(this);
    }

    private void ConfirmClearTabRules()
    {
        var tab = SelectedTab;
        if (tab is null || tab.Rules.Count == 0) return;
        var answer = System.Windows.MessageBox.Show(this,
            $"'{tab.Title}' 탭의 자동 분류 규칙 {tab.Rules.Count}개를 모두 지우시겠습니까?\n\n이미 분류된 항목과 실제 파일은 그대로 유지됩니다.",
            "자동 분류 규칙 지우기", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        tab.Rules.Clear();
        ModelChanged?.Invoke();
        AutomationRulesChanged?.Invoke();
    }

    private bool IsTheme(string background, string accent)
        => SameMenuText(Model.Background, background) && SameMenuText(Model.Accent, accent);

    private bool IsSpacing(double horizontal, double vertical)
        => NearlyEqual(Model.IconSpacingX, horizontal, 0.5) && NearlyEqual(Model.IconSpacingY, vertical, 0.5);

    private static bool NearlyEqual(double value, double expected, double tolerance = 0.01)
        => double.IsFinite(value) && Math.Abs(value - expected) <= tolerance;

    private static bool SameMenuText(string? value, string expected)
        => string.Equals(value?.Trim(), expected, StringComparison.OrdinalIgnoreCase);

    // Tag values understood by the menu theme (Themes/DeckMenu.xaml).
    private const string MenuSectionTag = "section";
    private const string MenuDangerTag = "danger";

    private static MenuItem MakeMenuSectionHeader(string text)
        => new() { Header = text, IsEnabled = false, FontWeight = FontWeights.SemiBold, Tag = MenuSectionTag };

    // Shortcut text is shown right-aligned by the menu template instead of padded into the label.
    private static MenuItem WithGesture(MenuItem item, string gesture)
    {
        item.InputGestureText = gesture;
        return item;
    }

    // Commands that delete or recycle real files are shown in the danger colour.
    private static MenuItem MakeDangerItem(string header, string? gesture, Action action)
    {
        var item = MakeItem(header, action);
        item.Tag = MenuDangerTag;
        if (gesture is not null) item.InputGestureText = gesture;
        return item;
    }

    private static MenuItem MakeCheckItem(string header, bool isChecked, Action action)
    {
        var item = MakeItem(header, action);
        item.IsCheckable = true;
        item.IsChecked = isChecked;
        return item;
    }

    private static MenuItem MakeChoiceMenu(string header, IEnumerable<(string Label, bool Checked, Action Action)> choices)
    {
        var root = new MenuItem { Header = header };
        foreach (var (label, isChecked, action) in choices) root.Items.Add(MakeCheckItem(label, isChecked, action));
        return root;
    }

    private static MenuItem MakeItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }
}
