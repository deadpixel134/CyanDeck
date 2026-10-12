using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DesktopDeck.Models;
using DesktopDeck.Services;

namespace DesktopDeck.Views;

public partial class RuleEditorWindow : Window
{
    private readonly DeckFence _fence;
    private readonly Action _applied;
    private readonly Dictionary<Guid, TabDraft> _drafts = new();
    private DeckTab? _currentTab;
    private bool _loading;

    public RuleEditorWindow(DeckFence fence, Action applied)
    {
        _fence = fence;
        _applied = applied;
        InitializeComponent();

        FieldColumn.ItemsSource = RuleFieldChoices;
        FieldColumn.DisplayMemberPath = nameof(RuleFieldChoice.Label);
        FieldColumn.SelectedValuePath = nameof(RuleFieldChoice.Value);
        FieldColumn.SelectedValueBinding = new Binding(nameof(DeckRule.Field)) { Mode = BindingMode.TwoWay };
        MatchModeCombo.ItemsSource = new[] { "하나라도 일치", "모두 일치" };
        foreach (var tab in fence.Tabs.Where(t => t.Kind == DeckTabKind.Collection))
            _drafts[tab.Id] = TabDraft.From(tab);

        TabPicker.ItemsSource = fence.Tabs.Where(t => t.Kind == DeckTabKind.Collection).ToArray();
        TabPicker.DisplayMemberPath = nameof(DeckTab.Title);
        if (TabPicker.Items.Count > 0) TabPicker.SelectedIndex = 0;
        else
        {
            RulesGrid.IsEnabled = false;
            MatchModeCombo.IsEnabled = false;
            PriorityBox.IsEnabled = false;
        }
    }

    private void TabPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SaveDraftFromUi();
        _currentTab = TabPicker.SelectedItem as DeckTab;
        LoadDraftToUi();
    }

    private void LoadDraftToUi()
    {
        _loading = true;
        try
        {
            if (_currentTab is null)
            {
                RulesGrid.ItemsSource = null;
                return;
            }
            var d = _drafts[_currentTab.Id];
            RulesGrid.ItemsSource = d.Rules;
            MatchModeCombo.SelectedIndex = (int)d.MatchMode;
            PriorityBox.Text = d.Priority.ToString();
        }
        finally { _loading = false; }
    }

    private void SaveDraftFromUi()
    {
        if (_loading || _currentTab is null) return;
        RulesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        RulesGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (!_drafts.TryGetValue(_currentTab.Id, out var d)) return;
        d.MatchMode = MatchModeCombo.SelectedIndex == (int)DeckRuleMatchMode.All ? DeckRuleMatchMode.All : DeckRuleMatchMode.Any;
        d.Priority = int.TryParse(PriorityBox.Text, out var p) ? Math.Clamp(p, -1000, 1000) : 0;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_currentTab is null || !_drafts.TryGetValue(_currentTab.Id, out var d)) return;
        var rule = new DeckRule { Enabled = true, Field = RuleField.Extension, Pattern = "pdf" };
        d.Rules.Add(rule);
        RulesGrid.SelectedItem = rule;
        RulesGrid.ScrollIntoView(rule);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_currentTab is null || RulesGrid.SelectedItem is not DeckRule rule || !_drafts.TryGetValue(_currentTab.Id, out var d)) return;
        d.Rules.Remove(rule);
    }

    private void Up_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);
    private void Down_Click(object sender, RoutedEventArgs e) => MoveSelected(1);

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        SaveDraftFromUi();
        if (_currentTab is null || !_drafts.TryGetValue(_currentTab.Id, out var d)) return;
        var active = d.Rules.Where(r => r.Enabled).ToArray();
        if (active.Length == 0)
        {
            PreviewText.Text = "이 탭에 활성화된 규칙이 없습니다.";
            return;
        }

        var matched = RuleEngine.EnumerateDesktopItems().Where(path =>
            d.MatchMode == DeckRuleMatchMode.All
                ? active.All(rule => RuleEngine.Matches(rule, path))
                : active.Any(rule => RuleEngine.Matches(rule, path)))
            .Select(path => Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        var sample = string.Join(", ", matched.Take(6));
        PreviewText.Text = matched.Length == 0
            ? "미리보기: 일치하는 바탕화면 항목이 없습니다."
            : $"미리보기: {matched.Length}개 일치. {sample}{(matched.Length > 6 ? ", …" : "")}";
    }

    private void MoveSelected(int delta)
    {
        if (_currentTab is null || RulesGrid.SelectedItem is not DeckRule rule || !_drafts.TryGetValue(_currentTab.Id, out var d)) return;
        var index = d.Rules.IndexOf(rule);
        var target = Math.Clamp(index + delta, 0, d.Rules.Count - 1);
        if (index < 0 || target == index) return;
        d.Rules.Move(index, target);
        RulesGrid.SelectedItem = rule;
    }

    private void ApplyChanges()
    {
        SaveDraftFromUi();
        foreach (var tab in _fence.Tabs.Where(t => t.Kind == DeckTabKind.Collection))
        {
            if (!_drafts.TryGetValue(tab.Id, out var d)) continue;
            tab.RuleMatchMode = d.MatchMode;
            tab.RulePriority = d.Priority;
            tab.Rules = d.Rules.Select(CloneRule).ToList();
        }
        _applied();
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => ApplyChanges();
    private void Ok_Click(object sender, RoutedEventArgs e) { ApplyChanges(); DialogResult = true; Close(); }
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private static DeckRule CloneRule(DeckRule r) => new() { Enabled = r.Enabled, Field = r.Field, Pattern = r.Pattern };


    private static readonly RuleFieldChoice[] RuleFieldChoices =
    [
        new(RuleField.Extension, "확장자"),
        new(RuleField.NameContains, "이름에 포함"),
        new(RuleField.NameDoesNotContain, "이름에 포함하지 않음"),
        new(RuleField.NameExact, "이름 정확히 일치"),
        new(RuleField.NameStartsWith, "이름 시작"),
        new(RuleField.NameEndsWith, "이름 끝"),
        new(RuleField.PathContains, "경로에 포함"),
        new(RuleField.Folder, "폴더"),
        new(RuleField.Shortcut, "바로 가기"),
        new(RuleField.Executable, "실행 파일"),
        new(RuleField.FileSizeAtLeastMB, "파일 크기 이상(MB)"),
        new(RuleField.FileSizeAtMostMB, "파일 크기 이하(MB)"),
        new(RuleField.ModifiedWithinDays, "최근 수정(일)"),
        new(RuleField.CreatedWithinDays, "최근 생성(일)"),
        new(RuleField.CreatedDayOfWeek, "생성 요일"),
        new(RuleField.CreatedTimeRange, "생성 시간 범위"),
        new(RuleField.ShortcutTargetContains, "바로 가기 대상에 포함"),
        new(RuleField.ShortcutTargetDoesNotContain, "바로 가기 대상에 포함하지 않음"),
        new(RuleField.ShortcutTargetStartsWith, "바로 가기 대상 시작"),
        new(RuleField.ShortcutTargetEndsWith, "바로 가기 대상 끝"),
        new(RuleField.IconTypeIs, "아이콘 형식"),
        new(RuleField.IconTypeIsNot, "아이콘 형식 제외"),
        new(RuleField.FileCategoryIs, "파일 분류"),
        new(RuleField.FileCategoryIsNot, "파일 분류 제외")
    ];

    private sealed record RuleFieldChoice(RuleField Value, string Label);

    private sealed class TabDraft
    {
        public DeckRuleMatchMode MatchMode { get; set; }
        public int Priority { get; set; }
        public ObservableCollection<DeckRule> Rules { get; } = [];

        public static TabDraft From(DeckTab tab)
        {
            var draft = new TabDraft { MatchMode = tab.RuleMatchMode, Priority = tab.RulePriority };
            foreach (var rule in tab.Rules) draft.Rules.Add(CloneRule(rule));
            return draft;
        }
    }
}
