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
    private void PopulateGroupContextMenu(ContextMenu menu)
    {
        menu.Items.Add(MakeItem("검색…   Ctrl+F", ShowSearch));
        menu.Items.Add(new Separator());
        var renameGroupItem = MakeItem("그룹 이름 바꾸기", () =>
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
        });
        renameGroupItem.IsEnabled = CapabilityAllowed("Settings.EditGroups");
        menu.Items.Add(renameGroupItem);
        var renameTabItem = MakeItem("현재 탭 이름 바꾸기", () =>
        {
            var tab = SelectedTab;
            if (tab is null) return;
            var text = TextPrompt.Show(this, "탭 이름 바꾸기", "이름", tab.Title);
            if (text is null) return;
            tab.Title = text;
            RebuildTabs();
            ModelChanged?.Invoke();
        });
        renameTabItem.IsEnabled = CapabilityAllowed("Settings.EditGroups");
        menu.Items.Add(renameTabItem);

        var groupTools = new MenuItem { Header = "그룹 도구" };
        var duplicateItem = MakeItem("그룹 복제", () => DuplicateRequested?.Invoke(this));
        duplicateItem.IsEnabled = CapabilityAllowed("Layout.CreateDeleteGroups");
        groupTools.Items.Add(duplicateItem);
        var saveTemplateItem = MakeItem("현재 그룹을 템플릿으로 저장…", () => SaveTemplateRequested?.Invoke(this));
        saveTemplateItem.IsEnabled = CapabilityAllowed("Templates.Edit");
        groupTools.Items.Add(saveTemplateItem);
        if (_templates.Count > 0)
        {
            var fromTemplate = new MenuItem { Header = "템플릿에서 새 그룹 만들기", IsEnabled = CapabilityAllowed("Layout.CreateDeleteGroups") };
            foreach (var template in _templates.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var localTemplate = template;
                fromTemplate.Items.Add(MakeItem(localTemplate.Name, () => CreateFromTemplateRequested?.Invoke(localTemplate.Id)));
            }
            groupTools.Items.Add(fromTemplate);
        }
        menu.Items.Add(groupTools);
        menu.Items.Add(new Separator());
        var addTabItem = MakeItem("탭 추가", AddCollectionTab);
        addTabItem.IsEnabled = CapabilityAllowed("Settings.EditGroups");
        menu.Items.Add(addTabItem);
        var addPortalItem = MakeItem("폴더 포털 추가…", AddPortalTab);
        addPortalItem.IsEnabled = CapabilityAllowed("FolderPortal.Edit");
        menu.Items.Add(addPortalItem);
        var tabsMenu = new MenuItem { Header = "탭" };
        foreach (var candidate in Model.Tabs)
        {
            var local = candidate;
            var item = MakeItem(local.Title, () => SelectTab(local));
            item.IsCheckable = true;
            item.IsChecked = ReferenceEquals(local, SelectedTab);
            tabsMenu.Items.Add(item);
        }
        if (Model.Tabs.Count > 1)
        {
            tabsMenu.Items.Add(new Separator());
            tabsMenu.Items.Add(MakeItem("현재 탭 왼쪽으로 이동", () => MoveCurrentTab(-1)));
            tabsMenu.Items.Add(MakeItem("현재 탭 오른쪽으로 이동", () => MoveCurrentTab(1)));
            tabsMenu.Items.Add(MakeItem("현재 탭 분리", DetachCurrentTab));
        }
        menu.Items.Add(tabsMenu);
        if (SelectedTab?.Kind == DeckTabKind.FolderPortal)
        {
            var portal = SelectedTab;
            var portalMenu = new MenuItem { Header = "폴더 포털" };
            portalMenu.Items.Add(MakeItem("뒤로   Alt+왼쪽", NavigatePortalBack));
            portalMenu.Items.Add(MakeItem("앞으로   Alt+오른쪽", NavigatePortalForward));
            portalMenu.Items.Add(MakeItem("상위 폴더   Alt+위쪽", NavigatePortalUp));
            portalMenu.Items.Add(MakeItem("홈 / 포털 루트", NavigatePortalHome));
            portalMenu.Items.Add(new Separator());
            var navigateInPortal = MakeItem("포털 안에서 폴더 탐색", () =>
            {
                portal.NavigateFoldersInPortal = !portal.NavigateFoldersInPortal;
                ModelChanged?.Invoke();
            });
            navigateInPortal.IsCheckable = true;
            navigateInPortal.IsChecked = portal.NavigateFoldersInPortal;
            portalMenu.Items.Add(navigateInPortal);
            var inheritView = MakeItem("하위 폴더에 보기 방식 상속", () =>
            {
                portal.InheritPortalViewMode = !portal.InheritPortalViewMode;
                RememberPortalView(portal, portal.PortalPath);
                ModelChanged?.Invoke();
            });
            inheritView.IsCheckable = true;
            inheritView.IsChecked = portal.InheritPortalViewMode;
            portalMenu.Items.Add(inheritView);
            var folderIcon = MakeItem("탭에 폴더 아이콘 표시", () =>
            {
                portal.ShowPortalFolderIcon = !portal.ShowPortalFolderIcon;
                RebuildTabs();
                ModelChanged?.Invoke();
            });
            folderIcon.IsCheckable = true;
            folderIcon.IsChecked = portal.ShowPortalFolderIcon;
            portalMenu.Items.Add(folderIcon);
            portalMenu.Items.Add(new Separator());
            var syncExplorer = MakeItem("일치하는 Explorer 창에서 보기 동기화", () =>
            {
                portal.SyncOpenExplorerView = !portal.SyncOpenExplorerView;
                if (portal.SyncOpenExplorerView) TrySyncPortalFromExplorer(portal, force: true);
                ModelChanged?.Invoke();
            });
            syncExplorer.IsCheckable = true;
            syncExplorer.IsChecked = portal.SyncOpenExplorerView;
            portalMenu.Items.Add(syncExplorer);
            if (portal.SyncOpenExplorerView)
            {
                var syncMode = MakeItem("  보기 방식 동기화", () => { portal.SyncExplorerViewMode = !portal.SyncExplorerViewMode; TrySyncPortalFromExplorer(portal, force: true); ModelChanged?.Invoke(); });
                syncMode.IsCheckable = true; syncMode.IsChecked = portal.SyncExplorerViewMode; portalMenu.Items.Add(syncMode);
                var syncSize = MakeItem("  아이콘 크기 동기화", () => { portal.SyncExplorerIconSize = !portal.SyncExplorerIconSize; TrySyncPortalFromExplorer(portal, force: true); ModelChanged?.Invoke(); });
                syncSize.IsCheckable = true; syncSize.IsChecked = portal.SyncExplorerIconSize; portalMenu.Items.Add(syncSize);
                var syncSort = MakeItem("  정렬 동기화", () => { portal.SyncExplorerSort = !portal.SyncExplorerSort; TrySyncPortalFromExplorer(portal, force: true); ModelChanged?.Invoke(); });
                syncSort.IsCheckable = true; syncSort.IsChecked = portal.SyncExplorerSort; portalMenu.Items.Add(syncSort);
                var syncDetailsColumns = MakeItem("  Details 열/순서 동기화", () =>
                {
                    portal.SyncExplorerDetailsColumns = !portal.SyncExplorerDetailsColumns;
                    TrySyncPortalFromExplorer(portal, force: true);
                    ModelChanged?.Invoke();
                });
                syncDetailsColumns.IsCheckable = true;
                syncDetailsColumns.IsChecked = portal.SyncExplorerDetailsColumns;
                syncDetailsColumns.IsEnabled = CapabilityAllowed("FolderPortal.Edit");
                portalMenu.Items.Add(syncDetailsColumns);
                var syncDetailsWidths = MakeItem("  Details 열 너비 동기화", () =>
                {
                    portal.SyncExplorerDetailsColumnWidths = !portal.SyncExplorerDetailsColumnWidths;
                    TrySyncPortalFromExplorer(portal, force: true);
                    ModelChanged?.Invoke();
                });
                syncDetailsWidths.IsCheckable = true;
                syncDetailsWidths.IsChecked = portal.SyncExplorerDetailsColumnWidths;
                syncDetailsWidths.IsEnabled = CapabilityAllowed("FolderPortal.Edit");
                portalMenu.Items.Add(syncDetailsWidths);
            }
            portalMenu.Items.Add(MakeItem("지금 Explorer 보기와 동기화", () => TrySyncPortalFromExplorer(portal, force: true)));
            portalMenu.Items.Add(new Separator());
            portalMenu.Items.Add(MakeItem("현재 폴더를 Explorer에서 열기", () => { if (!string.IsNullOrWhiteSpace(portal.PortalPath)) Open(portal.PortalPath); }));
            menu.Items.Add(portalMenu);
        }

        var automation = new MenuItem { Header = "자동 분류 규칙", IsEnabled = CapabilityAllowed("Automation.EditRules") };
        automation.Items.Add(MakeItem("규칙 편집…", OpenRuleEditor));
        automation.Items.Add(new Separator());
        automation.Items.Add(MakeItem("빠른 추가: 확장자…", () => AddRule(RuleField.Extension, "확장자(세미콜론으로 구분)", "pdf;docx")));
        automation.Items.Add(MakeItem("빠른 추가: 이름 포함…", () => AddRule(RuleField.NameContains, "이름에 포함할 텍스트", "invoice")));
        automation.Items.Add(MakeItem("빠른 추가: 정확한 이름…", () => AddRule(RuleField.NameExact, "정확한 파일 또는 폴더 이름", "Report.xlsx")));
        automation.Items.Add(MakeItem("빠른 추가: 최근 수정 일수…", () => AddRule(RuleField.ModifiedWithinDays, "일수", "7")));
        automation.Items.Add(MakeItem("빠른 추가: 바로 가기 대상…", () => AddRule(RuleField.ShortcutTargetContains, "바로 가기 대상에 포함할 텍스트", "steam")));
        automation.Items.Add(MakeItem("빠른 추가: 아이콘 형식…", () => AddRule(RuleField.IconTypeIs, "아이콘 형식(file/folder/shortcut/application/other)", "shortcut")));
        automation.Items.Add(MakeItem("현재 탭 규칙 모두 지우기", () => { var t = SelectedTab; if (t is null) return; t.Rules.Clear(); ModelChanged?.Invoke(); AutomationRulesChanged?.Invoke(); }));
        menu.Items.Add(automation);

        var sortMenu = new MenuItem { Header = "정렬" };
        foreach (var mode in Enum.GetValues<DeckSortMode>())
        {
            var local = mode;
            var sortLabel = mode switch
            {
                DeckSortMode.Name => "이름",
                DeckSortMode.Type => "형식",
                DeckSortMode.Modified => "수정한 날짜",
                DeckSortMode.Added => "추가한 날짜",
                DeckSortMode.Size => "크기",
                _ => "열기 횟수"
            };
            var item = MakeItem(sortLabel, () =>
            {
                var tab = SelectedTab;
                if (tab is null) return;
                if (tab.Sort == local) tab.SortDescending = !tab.SortDescending;
                else { tab.Sort = local; tab.SortDescending = false; }
                RefreshItems(IconMovementAnimationContext.SortOrArrange);
                ModelChanged?.Invoke();
            });
            item.IsCheckable = true;
            if (SelectedTab?.Sort == mode) item.IsChecked = true;
            sortMenu.Items.Add(item);
        }
        sortMenu.Items.Add(new Separator());
        var foldersFirst = MakeItem("폴더 먼저", () =>
        {
            var tab = SelectedTab;
            if (tab is null) return;
            tab.FoldersFirst = !tab.FoldersFirst;
            RefreshItems(IconMovementAnimationContext.SortOrArrange);
            ModelChanged?.Invoke();
        });
        foldersFirst.IsCheckable = true;
        foldersFirst.IsChecked = SelectedTab?.FoldersFirst == true;
        sortMenu.Items.Add(foldersFirst);
        menu.Items.Add(sortMenu);

        var viewMenu = new MenuItem { Header = "보기" };
        foreach (var mode in Enum.GetValues<DeckItemViewMode>())
        {
            var localMode = mode;
            var label = mode switch
            {
                DeckItemViewMode.Grid => "격자",
                DeckItemViewMode.List => "목록",
                _ => "자세히"
            };
            var item = MakeItem(label, () => SetItemViewMode(localMode));
            item.IsCheckable = true;
            item.IsChecked = SelectedTab?.ViewMode == mode;
            viewMenu.Items.Add(item);
        }
        if (SelectedTab?.Kind == DeckTabKind.FolderPortal)
        {
            viewMenu.Items.Add(new Separator());
            var iconSizeMenu = new MenuItem { Header = "아이콘 크기" };
            foreach (var preset in new double[] { 24, 32, 48, 64, 96, 128 })
            {
                var size = preset;
                var item = MakeItem($"{size:0} px", () => SetIconSize(size));
                item.IsCheckable = true;
                item.IsChecked = Math.Abs(Model.IconSize - size) < 0.5;
                iconSizeMenu.Items.Add(item);
            }
            if (!new double[] { 24, 32, 48, 64, 96, 128 }.Any(v => Math.Abs(Model.IconSize - v) < 0.5))
            {
                var custom = new MenuItem { Header = $"사용자 지정 ({Model.IconSize:0} px)", IsCheckable = true, IsChecked = true, IsEnabled = false };
                iconSizeMenu.Items.Add(new Separator());
                iconSizeMenu.Items.Add(custom);
            }
            viewMenu.Items.Add(iconSizeMenu);
        }
        if (SelectedTab?.ViewMode == DeckItemViewMode.List)
        {
            var listOrientation = new MenuItem { Header = "목록 방향" };
            foreach (var orientation in Enum.GetValues<DeckListOrientation>())
            {
                var localOrientation = orientation;
                var item = MakeItem(orientation == DeckListOrientation.Vertical ? "세로 목록" : "가로 목록", () => SetListOrientation(localOrientation));
                item.IsCheckable = true;
                item.IsChecked = SelectedTab?.ListOrientation == orientation;
                listOrientation.Items.Add(item);
            }
            viewMenu.Items.Add(new Separator());
            viewMenu.Items.Add(listOrientation);
        }
        if (SelectedTab?.ViewMode == DeckItemViewMode.Details)
        {
            viewMenu.Items.Add(new Separator());
            var detailsHeader = MakeItem("자세히 보기 헤더 표시", ToggleDetailsHeader);
            detailsHeader.IsCheckable = true;
            detailsHeader.IsChecked = SelectedTab?.ShowDetailsHeader == true;
            viewMenu.Items.Add(detailsHeader);
        }
        menu.Items.Add(viewMenu);

        var appearance = new MenuItem { Header = "모양", IsEnabled = CapabilityAllowed("Appearance.Edit") };
        appearance.Items.Add(MakeSubMenu("투명도", new (string, Action)[]
        {
            ("50%", () => SetOpacity(.50)), ("65%", () => SetOpacity(.65)), ("75%", () => SetOpacity(.75)),
            ("85%", () => SetOpacity(.85)), ("95%", () => SetOpacity(.95))
        }));
        appearance.Items.Add(MakeSubMenu("아이콘 색조", new (string, Action)[]
        {
            ("끄기", () => SetTint(0)), ("약하게", () => SetTint(.32)), ("중간", () => SetTint(.58)), ("강하게", () => SetTint(.82))
        }));
        appearance.Items.Add(MakeSubMenu("아이콘 크기", new (string, Action)[]
        {
            ("작게 (32)", () => SetIconSize(32)), ("중간 (46)", () => SetIconSize(46)),
            ("크게 (64)", () => SetIconSize(64)), ("매우 크게 (88)", () => SetIconSize(88))
        }));
        var iconSpacingMenu = MakeSubMenu("아이콘 간격", new (string, Action)[]
        {
            ("좁게", () => SetIconSpacing(4, 4)), ("보통", () => SetIconSpacing(14, 12)),
            ("여유롭게", () => SetIconSpacing(26, 22)), ("넓게", () => SetIconSpacing(42, 32))
        });
        iconSpacingMenu.IsEnabled = !_settings.UseSameSpacingInsideAndOutsideGroups
            && CapabilityAllowed(LinkedIconSpacingIntegrationContract.ManagedCapability);
        appearance.Items.Add(iconSpacingMenu);
        var labelMode = new MenuItem { Header = "아이콘 이름" };
        foreach (var mode in Enum.GetValues<DeckIconLabelMode>())
        {
            var localMode = mode;
            var item = MakeItem(mode switch
            {
                DeckIconLabelMode.Always => "항상 표시",
                DeckIconLabelMode.Mouseover => "마우스 오버 시 표시",
                _ => "숨김"
            }, () => SetIconLabelMode(localMode));
            item.IsCheckable = true;
            item.IsChecked = Model.IconLabelMode == mode;
            labelMode.Items.Add(item);
        }
        appearance.Items.Add(labelMode);
        var layoutMode = new MenuItem { Header = "아이콘 배치" };
        foreach (var mode in Enum.GetValues<DeckIconLayoutMode>())
        {
            var localMode = mode;
            var item = MakeItem(mode == DeckIconLayoutMode.Grid ? "격자 / 자동" : "자유 배치", () => SetIconLayout(localMode));
            item.IsCheckable = true;
            item.IsChecked = Model.IconLayoutMode == mode;
            layoutMode.Items.Add(item);
        }
        layoutMode.Items.Add(new Separator());
        layoutMode.Items.Add(MakeItem("자유 배치 아이콘을 왼쪽 위부터 정렬", PackFreePositions));
        layoutMode.Items.Add(MakeItem("자유 배치 아이콘을 간격 격자에 맞춤", SnapFreePositions));
        layoutMode.Items.Add(MakeItem("선택한 아이콘 왼쪽 맞춤", AlignSelectedFreeIconsLeft));
        layoutMode.Items.Add(MakeItem("선택한 아이콘 위쪽 맞춤", AlignSelectedFreeIconsTop));
        layoutMode.Items.Add(MakeItem("자유 배치 아이콘 위치 초기화", ResetFreePositions));
        appearance.Items.Add(layoutMode);
        appearance.Items.Add(MakeSubMenu("테마 색상", new (string, Action)[]
        {
            ("그래파이트", () => SetTheme("#17191E", "#7AA2FF")),
            ("슬레이트", () => SetTheme("#18202A", "#79B7FF")),
            ("바이올렛", () => SetTheme("#1C1924", "#A995FF")),
            ("에메랄드", () => SetTheme("#14201C", "#70D3A9")),
            ("앰버", () => SetTheme("#221C13", "#F0BA62")),
            ("로즈", () => SetTheme("#22171C", "#ED8FAF"))
        }));
        appearance.Items.Add(MakeSubMenu("모서리", new (string, Action)[]
        {
            ("각진 모서리", () => SetCornerRadius(0)),
            ("살짝 둥글게", () => SetCornerRadius(6)),
            ("둥글게", () => SetCornerRadius(12)),
            ("약하게", () => SetCornerRadius(18))
        }));
        appearance.Items.Add(MakeSubMenu("현재 탭 강조색", new (string, Action)[]
        {
            ("파랑", () => SetTabAccent("#7AA2FF")), ("Violet", () => SetTabAccent("#AA8EFF")),
            ("초록", () => SetTabAccent("#70D3A9")), ("Amber", () => SetTabAccent("#F0BA62")),
            ("Rose", () => SetTabAccent("#ED8FAF"))
        }));
        var titlebar = new MenuItem { Header = "제목 표시줄" };
        foreach (var mode in Enum.GetValues<DeckTitlebarMode>())
        {
            var localMode = mode;
            var item = MakeItem(mode switch
            {
                DeckTitlebarMode.Always => "항상 표시",
                DeckTitlebarMode.Mouseover => "마우스 오버 시 표시",
                _ => "숨김"
            }, () =>
            {
                Model.TitlebarMode = localMode;
                ApplyTitlebarMode(IsMouseOver);
                ModelChanged?.Invoke();
            });
            item.IsCheckable = true;
            item.IsChecked = Model.TitlebarMode == mode;
            titlebar.Items.Add(item);
        }
        titlebar.Items.Add(new Separator());
        var titleAtBottom = MakeItem("화면 위쪽에서는 제목 표시줄을 아래로 이동", () =>
        {
            Model.MoveTitlebarToBottomNearTop = !Model.MoveTitlebarToBottomNearTop;
            UpdateTitlebarPlacement();
            ModelChanged?.Invoke();
        });
        titleAtBottom.IsCheckable = true;
        titleAtBottom.IsChecked = Model.MoveTitlebarToBottomNearTop;
        titlebar.Items.Add(titleAtBottom);
        appearance.Items.Add(titlebar);
        appearance.Items.Add(MakeSubMenu("제목 글꼴", new (string, Action)[]
        {
            ("Segoe UI", () => SetTitleFont("Segoe UI")),
            ("Segoe UI Variable", () => SetTitleFont("Segoe UI Variable")),
            ("Arial", () => SetTitleFont("Arial")),
            ("Consolas", () => SetTitleFont("Consolas"))
        }));
        appearance.Items.Add(MakeSubMenu("제목 크기", new (string, Action)[]
        {
            ("Small (10)", () => SetTitleFontSize(10)),
            ("Normal (12)", () => SetTitleFontSize(12)),
            ("Large (14)", () => SetTitleFontSize(14)),
            ("Extra large (16)", () => SetTitleFontSize(16))
        }));
        appearance.Items.Add(MakeSubMenu("제목 색상", new (string, Action)[]
        {
            ("White", () => SetTitleColor("#F4F6FA")),
            ("Soft gray", () => SetTitleColor("#C8D0DA")),
            ("Blue", () => SetTitleColor("#AFC9FF")),
            ("Amber", () => SetTitleColor("#FFD28A")),
            ("Mint", () => SetTitleColor("#A9E4CD"))
        }));

        var tabStyle = new MenuItem { Header = "탭 스타일" };
        foreach (var style in Enum.GetValues<DeckTabStyle>())
        {
            var localStyle = style;
            var item = MakeItem(style switch
            {
                DeckTabStyle.MenuOnly => "메뉴만",
                DeckTabStyle.Flat => "평면",
                DeckTabStyle.Segmented => "분할",
                _ => "둥글게"
            }, () =>
            {
                Model.TabStyle = localStyle;
                RebuildTabs();
                ModelChanged?.Invoke();
            });
            item.IsCheckable = true;
            item.IsChecked = Model.TabStyle == style;
            tabStyle.Items.Add(item);
        }
        appearance.Items.Add(tabStyle);
        var fade = MakeItem("카멜레온 자동 흐림", () =>
        {
            Model.AutoFade = !Model.AutoFade;
            Shell.Opacity = 1;
            _lastAppliedChameleonOpacity = 1;
            ModelChanged?.Invoke();
        });
        fade.IsCheckable = true;
        fade.IsChecked = Model.AutoFade;
        appearance.Items.Add(fade);
        appearance.Items.Add(new Separator());
        appearance.Items.Add(MakeItem("그룹 모양 복사", CopyAppearance));
        var pasteAppearance = MakeItem("그룹 모양 붙여넣기", PasteAppearance);
        pasteAppearance.IsEnabled = _appearanceClipboard is not null;
        appearance.Items.Add(pasteAppearance);
        appearance.Items.Add(MakeItem("그룹 모양 초기화", ResetAppearance));
        menu.Items.Add(appearance);

        var always = MakeItem("이 그룹은 빠른 숨김에서 제외", () => { Model.AlwaysVisible = !Model.AlwaysVisible; ModelChanged?.Invoke(); VisibilitySettingsChanged?.Invoke(); });
        always.IsCheckable = true;
        always.IsChecked = Model.AlwaysVisible;
        menu.Items.Add(always);
        var allPages = MakeItem("모든 바탕화면 페이지에 표시", () => { Model.ShowOnAllPages = !Model.ShowOnAllPages; ModelChanged?.Invoke(); });
        allPages.IsCheckable = true;
        allPages.IsChecked = Model.ShowOnAllPages;
        menu.Items.Add(allPages);
        var locked = MakeItem("그룹 위치와 크기 잠금", () =>
        {
            Model.Locked = !Model.Locked;
            ApplyRolledState(false);
            ModelChanged?.Invoke();
        });
        locked.IsCheckable = true;
        locked.IsChecked = Model.Locked;
        menu.Items.Add(locked);
        menu.Items.Add(new MenuItem { Header = $"바탕화면 페이지: {Model.PageIndex + 1}", IsEnabled = false });
        var rollupBehavior = new MenuItem { Header = "접기 동작" };
        foreach (var mode in Enum.GetValues<DeckRollupActivation>())
        {
            var localMode = mode;
            var label = mode switch
            {
                DeckRollupActivation.Hover => "마우스 오버 시 열기",
                DeckRollupActivation.DelayedHover => "잠시 올려두면 열기",
                _ => "클릭해야 열기"
            };
            var item = MakeItem(label, () => SetRollupActivation(localMode));
            item.IsCheckable = true;
            item.IsChecked = Model.RollupActivation == mode;
            rollupBehavior.Items.Add(item);
        }
        menu.Items.Add(rollupBehavior);
        menu.Items.Add(MakeItem(Model.RolledUp ? "계속 펼쳐두기" : "접기", ToggleRollUp));

        menu.Items.Add(new Separator());
        if (Model.Tabs.Count > 1)
        {
            var removeTabItem = MakeItem("현재 탭 제거", RemoveCurrentTab);
            removeTabItem.IsEnabled = CapabilityAllowed("Settings.EditGroups");
            menu.Items.Add(removeTabItem);
        }
        var newFenceItem = MakeItem("새 그룹", () => NewFenceRequested?.Invoke());
        newFenceItem.IsEnabled = CapabilityAllowed("Layout.CreateDeleteGroups");
        menu.Items.Add(newFenceItem);
        menu.Items.Add(MakeItem("CyanDeck 설정…", () => SettingsRequested?.Invoke()));
        var deleteFenceItem = MakeItem("그룹 삭제", () => DeleteRequested?.Invoke(this));
        deleteFenceItem.IsEnabled = CapabilityAllowed("Layout.CreateDeleteGroups");
        menu.Items.Add(deleteFenceItem);
    }

    private static MenuItem MakeSubMenu(string header, IEnumerable<(string Label, Action Action)> actions)
    {
        var root = new MenuItem { Header = header };
        foreach (var (label, action) in actions) root.Items.Add(MakeItem(label, action));
        return root;
    }

    private static MenuItem MakeItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }
}
