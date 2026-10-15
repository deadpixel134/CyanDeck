using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CyanDeck.Calendar;
using CyanDeck.Models;
using CyanDeck.Services;

namespace CyanDeck.Views;

/// <summary>Detached four-step setup. Nothing mutates Explorer until the
/// caller commits settings and starts the desktop runtime.</summary>
public sealed class FirstRunWizardWindow : Window
{
    private readonly DeckSettings _settings;
    private readonly Func<DeckSettings, bool> _save;
    private readonly bool _firstRun;
    private readonly StackPanel _page = new() { Margin = new Thickness(22) };
    private readonly TextBlock _heading = new() { FontSize = 21,
        FontWeight = FontWeights.SemiBold, Margin = new Thickness(22, 20, 22, 0) };
    private readonly TextBlock _message = new() { Foreground = Brushes.LightBlue,
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(22, 4, 22, 8) };
    private readonly TextBlock _progress = new() { Foreground = Brushes.LightGray };
    private readonly Button _back = new() { Content = "이전", MinWidth = 75, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _next = new() { Content = "다음", MinWidth = 132 };
    private readonly CalendarPreferencesStore _prefs = new();
    private readonly CalendarEventStore _events = new();
    private GoogleCalendarSync? _google;
    private CancellationTokenSource? _cancel;
    private bool _busy, _closed, _googleWanted, _connected;
    private int _step;
    private IReadOnlyList<GoogleCalendarSource> _catalog = [];
    private HashSet<string> _visibleIds = new(StringComparer.Ordinal);
    private string? _saveTo;
    private bool _saveLocal;

    public FirstRunWizardWindow(DeckSettings settings, Func<DeckSettings, bool> save, bool firstRun = true)
    {
        _settings = settings.Clone();
        _save = save;
        _firstRun = firstRun;
        _saveLocal = _prefs.Data.DefaultNewEventsLocalOnly;
        Title = "CyanDeck 시작 가이드";
        Width = 640; Height = 535; MinWidth = 500; MinHeight = 430;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(28, 34, 45));
        Foreground = Brushes.White;
        var root = new DockPanel(); Content = root;
        var footer = new DockPanel { Margin = new Thickness(22, 10, 22, 18) };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(buttons, Dock.Right); footer.Children.Add(buttons);
        buttons.Children.Add(_back); buttons.Children.Add(_next);
        _progress.VerticalAlignment = VerticalAlignment.Center; footer.Children.Add(_progress);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        root.Children.Add(scroll);
        var content = new StackPanel(); scroll.Content = content;
        content.Children.Add(_heading); content.Children.Add(_page); content.Children.Add(_message);
        _back.Click += (_, _) => { if (!_busy && _step > 0) { _step--; Draw(); } };
        _next.Click += (_, _) =>
        {
            if (_busy) return;
            if (!_settings.EnableCyanDeck && !_settings.EnableCalendar)
            { _message.Text = "그룹 관리와 캘린더 중 하나 이상 선택하세요."; return; }
            if (_step < 3) { _step++; Draw(); }
            else Complete();
        };
        Closed += (_, _) => { _closed = true; _cancel?.Cancel(); _google?.Dispose(); };
        Draw();
    }

    private static TextBlock Hint(string text) => new()
    { Text = text, Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap,
      Margin = new Thickness(0, 6, 0, 12) };

    private void Check(string title, bool value, Action<bool> set)
    {
        var check = new CheckBox { Content = title, IsChecked = value,
            Foreground = Brushes.White, FontSize = 14, Margin = new Thickness(0, 7, 0, 7) };
        check.Checked += (_, _) => set(true);
        check.Unchecked += (_, _) => set(false);
        _page.Children.Add(check);
    }

    private Button Button(string title, Action click)
    {
        var b = new Button { Content = title, Padding = new Thickness(12, 7, 12, 7),
            HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 150,
            Margin = new Thickness(0, 7, 0, 11) };
        b.Click += (_, _) => click(); _page.Children.Add(b); return b;
    }

    private void Draw()
    {
        if (_closed) return;
        _page.Children.Clear(); _message.Text = "";
        _heading.Text = new[] { "사용할 기능 선택", "바탕화면 설정", "Google 캘린더 연결", "완료 및 시작" }[_step];
        _progress.Text = $"단계 {_step + 1} / 4";
        _back.IsEnabled = !_busy && _step > 0;
        _next.IsEnabled = !_busy;
        _next.Content = _step == 3 ? "저장 후 시작" : "다음";
        if (_step == 0)
        {
            _page.Children.Add(Hint("CyanDeck의 기능을 선택하세요. 나중에 환경설정에서 변경할 수 있습니다."));
            Check("바탕화면 그룹 및 아이콘 관리", _settings.EnableCyanDeck, v => _settings.EnableCyanDeck = v);
            Check("데스크톱 캘린더", _settings.EnableCalendar, v => _settings.EnableCalendar = v);
            _page.Children.Add(Hint("설정을 완료하기 전까지 기존 Windows 바탕화면과 파일은 변경하지 않습니다."));
        }
        else if (_step == 1)
        {
            if (!_settings.EnableCyanDeck) { _page.Children.Add(Hint("그룹 관리를 사용하지 않습니다.")); return; }
            _page.Children.Add(Hint("그룹을 활성화한 뒤 적용할 아이콘 정리 방식을 선택하세요."));
            Check("Windows 기본 바탕화면 아이콘 숨기기", _settings.HideNativeDesktopIcons,
                v => _settings.HideNativeDesktopIcons = v);
            Check("기존 바탕화면 항목 자동 분류", _settings.AutoImportDesktop,
                v => _settings.AutoImportDesktop = v);
            _page.Children.Add(Hint("기본 그룹: 앱, 작업, 미디어, Windows 기본 아이콘. 트레이 메뉴에서 언제든 기본 아이콘을 복구할 수 있습니다."));
        }
        else if (_step == 2)
        {
            if (!_settings.EnableCalendar) { _page.Children.Add(Hint("캘린더를 사용하지 않습니다.")); return; }
            _page.Children.Add(Hint("Google 로그인은 선택 사항입니다. 건너뛰어도 로컬 일정은 사용할 수 있습니다."));
            Check("Google 계정 연결하기", _googleWanted, v => { _googleWanted = v; Draw(); });
            if (!_googleWanted) return;
            Button(_busy ? "Google 로그인 취소" : _connected ? "캘린더 목록 새로고침" : "Google 계정 로그인",
                () => { if (_busy) _cancel?.Cancel(); else _ = ConnectAsync(); });
            if (!_connected)
            { _page.Children.Add(Hint("로그인 후 캘린더 목록과 색상, 읽기·쓰기 권한이 표시됩니다.")); return; }
            _page.Children.Add(Hint("CyanDeck에 표시할 캘린더를 선택하세요. Google 원본 설정은 변경되지 않습니다."));
            foreach (var source in _catalog)
            {
                var c = source;
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
                var box = new CheckBox { IsChecked = _visibleIds.Contains(c.Id),
                    Content = c.Name + (c.IsPrimary ? " (기본)" : "") + (c.CanWrite ? "" : " (읽기 전용)"),
                    Foreground = Brushes.White };
                box.Checked += (_, _) => { _visibleIds.Add(c.Id); Draw(); };
                box.Unchecked += (_, _) =>
                { _visibleIds.Remove(c.Id); if (_saveTo == c.Id) _saveTo = null; Draw(); };
                row.Children.Add(box);
                var swatch = new Border { Width = 12, Height = 12, Margin = new Thickness(10, 0, 0, 0) };
                try { swatch.Background = (Brush)new BrushConverter().ConvertFromString(c.Color)!; }
                catch { swatch.Background = Brushes.SteelBlue; }
                row.Children.Add(swatch); _page.Children.Add(row);
            }
            _page.Children.Add(Hint("새 일정의 기본 저장 대상"));
            var targets = new ComboBox { Width = 300, HorizontalAlignment = HorizontalAlignment.Left };
            targets.Items.Add(new ComboBoxItem { Content = "로컬에만 저장", Tag = "" });
            foreach (var c in _catalog.Where(x => x.CanWrite && _visibleIds.Contains(x.Id)))
                targets.Items.Add(new ComboBoxItem { Content = c.Name, Tag = c.Id });
            var selectedId = _saveLocal ? "" : _saveTo ?? "";
            targets.SelectedItem = targets.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(x => (string?)x.Tag == selectedId) ?? targets.Items[0];
            targets.SelectionChanged += (_, _) =>
            {
                var id = (targets.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
                _saveLocal = id.Length == 0; _saveTo = id.Length == 0 ? null : id;
            };
            _page.Children.Add(targets);
        }
        else
        {
            _page.Children.Add(Hint($"그룹 관리: {(_settings.EnableCyanDeck ? "켜짐" : "꺼짐")}; 캘린더: {(_settings.EnableCalendar ? "켜짐" : "꺼짐")}"));
            _page.Children.Add(Hint($"Google 연결: {(_googleWanted && _connected && _settings.EnableCalendar ? $"{_visibleIds.Count}개 표시" : "나중에 연결")}"));
            Check("Windows 로그인 시 CyanDeck 자동 실행", _settings.StartWithWindows,
                v => _settings.StartWithWindows = v);
            Check("정식 업데이트 자동 확인 및 설치 (기본 켜짐)",
                _settings.EnableAutomaticUpdateChecks && _settings.InstallUpdatesAutomatically,
                v => { _settings.EnableAutomaticUpdateChecks = v; _settings.InstallUpdatesAutomatically = v; });
            Check("베타 업데이트도 받기 (선택)", _settings.EnableBetaUpdateChannel,
                v => _settings.EnableBetaUpdateChannel = v);
            _page.Children.Add(Hint("기본 사용법: 그룹 드래그 이동 · 캘린더 더블클릭 편집 · 일정 1초 길게 눌러 이동 · 트레이 아이콘에서 설정/복구."));
        }
    }

    private async Task ConnectAsync()
    {
        if (_busy || _closed) return;
        if (string.IsNullOrWhiteSpace(GoogleOAuthClientConfiguration.Resolve(_settings.GoogleCalendarClientId)))
        { _message.Text = "Google 인증 설정이 없는 배포본입니다. 연결을 건너뛸 수 있습니다."; return; }
        _busy = true;
        using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        _cancel = cancel; Draw();
        _message.Text = "기본 브라우저에서 로그인하세요. 3분이 지나거나 취소하면 중단됩니다.";
        try
        {
            _google ??= new GoogleCalendarSync(_events, _prefs,
                () => GoogleOAuthClientConfiguration.Resolve(_settings.GoogleCalendarClientId));
            _catalog = await _google.LoadCalendarsAsync(cancel.Token);
            if (_closed) return;
            _visibleIds = new HashSet<string>(_prefs.Data.SelectedCalendarIds ?? [], StringComparer.Ordinal);
            _saveTo = _prefs.Data.TargetCalendarId;
            _saveLocal = _prefs.Data.DefaultNewEventsLocalOnly;
            _connected = true;
            _message.Text = $"캘린더 {_catalog.Count}개를 불러왔습니다.";
        }
        catch (OperationCanceledException)
        { if (!_closed) _message.Text = "로그인이 취소되었거나 시간이 초과되었습니다."; }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("Google setup login failed", ex);
            if (!_closed) _message.Text = "연결 실패: " + ex.Message;
        }
        finally
        {
            _cancel = null; _busy = false;
            if (!_closed) { var message = _message.Text; Draw(); _message.Text = message; }
        }
    }

    private void Complete()
    {
        try
        {
            if (_settings.EnableCalendar && _googleWanted && _connected)
            {
                var visible = _visibleIds.Where(id => _catalog.Any(x => x.Id == id)).ToList();
                var target = !_saveLocal && _saveTo is not null &&
                    _catalog.Any(x => x.Id == _saveTo && x.CanWrite && visible.Contains(x.Id))
                    ? _saveTo : null;
                _prefs.CommitUpdate(next =>
                {
                    next.SelectedCalendarIds = visible;
                    next.TargetCalendarId = target;
                    next.DefaultNewEventsLocalOnly = target is null;
                });
            }
            if (_firstRun && _settings.EnableCalendar && !(_googleWanted && _connected))
            {
                // No Google account was selected. Ensure brand-new local events
                // do not default to the placeholder "primary" Google calendar.
                // Replaying the guide must not overwrite existing user preferences.
                _prefs.CommitUpdate(next => next.DefaultNewEventsLocalOnly = true);
            }
            if (!_save(_settings.Clone()))
            { _message.Text = "설정 저장에 실패해 실행 구성을 변경하지 않았습니다."; return; }
            DialogResult = true;
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("First-run setup save failed", ex);
            _message.Text = "설정 저장 오류: " + ex.Message;
        }
    }
}
