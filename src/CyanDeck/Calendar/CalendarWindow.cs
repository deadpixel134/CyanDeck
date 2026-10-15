using System.Globalization;
using System.Windows.Markup;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using CyanDeck.Models;
using CyanDeck.Services;
using Forms = System.Windows.Forms;

namespace CyanDeck.Calendar;

// Single shell-attached window: no topmost external editor windows.
// Each multi-day event is a Grid column-spanning bar, clipped to week boundaries.
public sealed class CalendarWindow : Window
{
    private readonly DeckSettings _settings;
    private readonly CalendarEventStore _store;
    private readonly CalendarPreferencesStore _preferences;
    private readonly CalendarEventAppearanceStore _eventAppearance = new();
    private readonly Action _onPositionChanged;
    private readonly Func<Task> _sync;
    private readonly Func<CancellationToken, bool, Task<IReadOnlyList<GoogleCalendarSource>>> _loadSources;
    private readonly Action _onAutoSyncIntervalChanged;
    private readonly KoreanHolidayFeedService _holidayFeeds;
    private readonly Func<Task<string>> _onHolidaySourcesChanged;
    private readonly Grid _root = new();
    private readonly Grid _body = new();
    private readonly TextBlock _monthCaption = new();
    private readonly TextBlock _status = new();
    private readonly StackPanel _weeks = new();
    private readonly List<Grid> _renderedWeekRows = new();
    private double _dayHeadingHeight = 24;
    private readonly ScrollViewer _weekScroll;
    // One shared hint for the empty date surface, the day number and the
    // scroll viewport. Describe only gestures actually implemented below.
    private const string EmptyDateInteractionHelp =
        "빈 날짜 더블클릭 → 새 일정 추가\n" +
        "기존 일정 더블클릭 → 일정 수정 (읽기 전용 일정 제외)\n" +
        "일정 막대 우클릭 → 배경색·글자색 변경\n" +
        "수정 가능한 일정 막대를 왼쪽 버튼으로 1초 누른 채 드래그 → 날짜 이동\n" +
        "마우스 휠·터치 스크롤 → 다른 주 보기";
    private DateTime _visibleMonth = DateTime.Today;
    private readonly SolidColorBrush _frameBackground = new();
    private readonly SolidColorBrush _dayBackground = new();
    private readonly SolidColorBrush _todayBackground = new();
    private Border? _editorOverlay;
    private SizeChangedEventHandler? _editorSizeHandler;
    private readonly DispatcherTimer _longPressTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private CalendarEvent? _moveCandidate;
    private Border? _sourceBar;
    private Point _pressPoint;
    private bool _isMoving;
    private Brush? _savedBarBackground;
    private UIElement? _savedBarChild;
    private object? _savedBarTooltip;
    private Canvas? _moveLayer;
    private Border? _moveGhost;
    private DateTime? _moveDropDate;


    private bool _allowFinalClose;
    private bool _editing;
    public bool IsSynchronizing { get; set; }
    public bool IsGoogleConnecting => _connectAttempt is not null;
    internal void AllowFinalClose() => _allowFinalClose = true;
    private bool _showingPreferences;
    public bool IsShowingPreferences => _showingPreferences;
    private CancellationTokenSource? _connectAttempt;
    private bool _connectHadFailure;

    public CalendarWindow(DeckSettings settings, CalendarEventStore store, CalendarPreferencesStore preferences,
        Action onPositionChanged, Func<Task> sync,
        Func<CancellationToken, bool, Task<IReadOnlyList<GoogleCalendarSource>>> loadSources,
        Action onAutoSyncIntervalChanged, KoreanHolidayFeedService holidayFeeds,
        Func<Task<string>> onHolidaySourcesChanged, bool standaloneWindowMode = false)
    {
        _settings = settings;
        _store = store;
        _preferences = preferences;
        _onPositionChanged = onPositionChanged;
        _sync = sync;
        _loadSources = loadSources;
        _onAutoSyncIntervalChanged = onAutoSyncIntervalChanged;
        _holidayFeeds = holidayFeeds;
        _onHolidaySourcesChanged = onHolidaySourcesChanged;
        Title = "CyanDeck Calendar";
        // An ordinary window is available ONLY after the user explicitly
        // selects the troubleshooting fallback from the tray. It participates
        // in standard Windows taskbar/Z-order behavior; never Topmost.
        WindowStyle = standaloneWindowMode ? WindowStyle.SingleBorderWindow : WindowStyle.None;
        ResizeMode = standaloneWindowMode ? ResizeMode.CanResize : ResizeMode.NoResize;
        AllowsTransparency = !standaloneWindowMode;
        Background = standaloneWindowMode ? new SolidColorBrush(Color.FromRgb(33, 39, 50)) : Brushes.Transparent;
        Topmost = false;
        // Do not use ShowInTaskbar=false for a desktop-band surface. WPF creates
        // a hidden owner HWND in that case; Windows then forces this HWND above
        // that owner in the global Z-order, defeating our WorkerW placement.
        // DesktopHost applies WS_EX_TOOLWINDOW and removes WS_EX_APPWINDOW BEFORE
        // this window is shown to keep it off both the taskbar and Alt+Tab.
        ShowInTaskbar = true;
        ShowActivated = standaloneWindowMode;
        if (standaloneWindowMode)
            Closing += (_, e) => { if (!_allowFinalClose) { e.Cancel = true; Hide(); } };
        MinWidth = 390;
        MinHeight = 310;
        // No fixed calendar width ceiling. WPF supports MaxWidth = +Infinity.
        // Users may span multiple monitors; keep only the minimum usable width.
        MaxWidth = double.PositiveInfinity;
        MaxHeight = 1200;
        Width = Math.Max(settings.CalendarWidth, MinWidth);
        Height = Math.Clamp(settings.CalendarHeight, MinHeight, MaxHeight);
        Left = settings.CalendarLeft;
        Top = settings.CalendarTop;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Opacity = settings.CalendarOpacity;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        // The calendar viewport must never gain a visible scrollbar when the
        // user drags its resize grip. Keep overflow scrollable (mouse wheel,
        // trackpad or touch) instead of clipping dense event lanes or dates.
        // The event editor and preferences retain their own scrollbars.
        _weekScroll = new ScrollViewer
        {
            Content = _weeks,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.VerticalOnly,
            ToolTip = EmptyDateInteractionHelp
        };
        // ScrollViewer measures StackPanel content with unbounded vertical space.
        // Stretch normal weeks to fill the viewport. Dense event lanes can
        // still overflow internally, but never introduce a visible scrollbar.
        _weekScroll.SizeChanged += (_, _) => StretchWeekRowsToViewport();
        LocationChanged += (_, _) =>
        {
            if (!double.IsFinite(Left) || !double.IsFinite(Top)) return;
            _settings.CalendarLeft = Left; _settings.CalendarTop = Top; _onPositionChanged();
        };
        SizeChanged += (_, _) =>
        {
            if (!double.IsFinite(Width) || !double.IsFinite(Height)) return;
            _settings.CalendarWidth = Width; _settings.CalendarHeight = Height; _onPositionChanged();
        };
        _longPressTimer.Tick += (_, _) => ActivateLongPressMove();
        Closed += (_, _) => _connectAttempt?.Cancel();
        _body.PreviewMouseMove += OnMoveMouseMotion;
        _body.PreviewMouseLeftButtonUp += OnMoveMouseRelease;
        _body.LostMouseCapture += (_, _) => { if (_isMoving) CancelEventMove(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _moveCandidate is not null)
            { e.Handled = true; CancelEventMove(); SetStatus("일정 이동 취소"); }
            // Avoid synchronous WPF clipboard retries on this desktop surface's
            // input dispatcher. An STA worker performs the actual copy; the
            // shortcut remains a no-op for nontext calendar controls.
            if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (Keyboard.FocusedElement is TextBox focused && focused.SelectionLength > 0)
                    CalendarClipboardCopy.Queue(focused.SelectedText, Dispatcher, SetStatus);
                e.Handled = true;
            }
        };
        BuildUI();
        // Presentation-only hooks: NEVER call native Show/Hide/SetWindowPos here.
        MouseEnter += (_, _) => ApplyCalendarAppearance();
        MouseLeave += (_, _) => ApplyCalendarAppearance();
        Activated += (_, _) => ApplyCalendarAppearance();
        Deactivated += (_, _) => ApplyCalendarAppearance();
        ApplyCalendarAppearance();
        // A previously chosen monitor may have been disconnected between launches.
        Loaded += (_, _) => EnsureWithinVisibleWorkArea("loaded");
        RenderMonth();
    }

    private static readonly Brush Panel = new SolidColorBrush(Color.FromRgb(33, 39, 50));
    private static readonly Brush TextLight = Brushes.White;
    // High-luminance accents remain readable against the dark/partly transparent calendar.
    private static readonly Brush SundayText = new SolidColorBrush(Color.FromRgb(255, 146, 154));
    private static readonly Brush SaturdayText = new SolidColorBrush(Color.FromRgb(129, 193, 255));
    private static readonly Brush SundayOutsideMonth = new SolidColorBrush(Color.FromRgb(200, 119, 126));
    private static readonly Brush SaturdayOutsideMonth = new SolidColorBrush(Color.FromRgb(103, 154, 204));
    // Readable but restrained accent even with the user's translucent dark theme.
    private static readonly Brush TodayOutline = new SolidColorBrush(Color.FromRgb(116, 171, 211));

    private static Brush DateForeground(DayOfWeek weekday, bool outsideMonth)
        => weekday switch
        {
            DayOfWeek.Sunday => outsideMonth ? SundayOutsideMonth : SundayText,
            DayOfWeek.Saturday => outsideMonth ? SaturdayOutsideMonth : SaturdayText,
            _ => outsideMonth ? Brushes.Gray : TextLight
        };

    private void BuildUI()
    {
        var frame = new Border
        {
            CornerRadius = new CornerRadius(12), Background = _frameBackground,
            BorderBrush = new SolidColorBrush(Color.FromRgb(90, 101, 121)), BorderThickness = new Thickness(1),
            Padding = new Thickness(10), Child = _root
        };
        Content = frame;
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        // Keep the entire date range legible even at the minimum 390-DIP width.
        // Controls are in their own row; a single crowded DockPanel truncated the range.
        var header = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var controls = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 5, 0, 0) };
        var prev = MakeButton("◀");
        prev.Click += (_, _) => { _visibleMonth = CalendarViewportPolicy.Navigate(_visibleMonth, _settings.CalendarViewMode, _settings.CalendarVisibleWeekRows, -1); RenderMonth(); AnnounceVisibleRange(); };
        DockPanel.SetDock(prev, Dock.Left); controls.Children.Add(prev);
        var next = MakeButton("▶");
        next.Click += (_, _) => { _visibleMonth = CalendarViewportPolicy.Navigate(_visibleMonth, _settings.CalendarViewMode, _settings.CalendarVisibleWeekRows, 1); RenderMonth(); AnnounceVisibleRange(); };
        DockPanel.SetDock(next, Dock.Left); controls.Children.Add(next);
        var settingsButton = MakeButton("설정");
        settingsButton.Click += (_, _) => TogglePreferences();
        DockPanel.SetDock(settingsButton, Dock.Right); controls.Children.Add(settingsButton);
        var sync = MakeButton("동기화");
        sync.Click += async (_, _) => await _sync();
        DockPanel.SetDock(sync, Dock.Right); controls.Children.Add(sync);
        var today = MakeButton("오늘");
        today.Click += (_, _) => GoToToday();
        DockPanel.SetDock(today, Dock.Right); controls.Children.Add(today);
        _monthCaption.FontSize = 13; // refreshed from saved font preferences in RenderMonth()
        _monthCaption.TextWrapping = TextWrapping.Wrap;
        _monthCaption.TextTrimming = TextTrimming.None;
        _monthCaption.FontWeight = FontWeights.SemiBold;
        _monthCaption.Foreground = Brushes.White;
        _monthCaption.VerticalAlignment = VerticalAlignment.Center;
        _monthCaption.HorizontalAlignment = HorizontalAlignment.Stretch;
        _monthCaption.TextAlignment = TextAlignment.Left;
        _monthCaption.ToolTip = "여기를 드래그하여 달력을 이동";
        _monthCaption.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 1 && !_editing)
            {
                try { DragMove(); }
                catch (InvalidOperationException) { }
                finally { EnsureWithinVisibleWorkArea("drag completed"); }
            }
        };
        Grid.SetRow(_monthCaption, 0); header.Children.Add(_monthCaption);
        Grid.SetRow(controls, 1); header.Children.Add(controls);
        Grid.SetRow(header, 0); _root.Children.Add(header);
        _body.Children.Add(_weekScroll);
        Grid.SetRow(_body, 1); _root.Children.Add(_body);
        var footer = new DockPanel { LastChildFill = true, Margin = new Thickness(2, 6, 2, 0) };
        var grip = new Thumb
        {
            Width = 20, Height = 20, Cursor = Cursors.SizeNWSE,
            ToolTip = "드래그하여 캘린더 크기 변경",
            // Keep the Calendar and icon-group grips visually identical.
            Template = (ControlTemplate)Application.Current.FindResource("DesktopResizeGripTemplate")
        };
        grip.DragDelta += (_, e) =>
        {
            Width = Math.Clamp(Width + e.HorizontalChange, MinWidth, MaxWidth);
            Height = Math.Clamp(Height + e.VerticalChange, MinHeight, MaxHeight);
        };
        grip.DragCompleted += (_, _) => EnsureWithinVisibleWorkArea("resize completed");
        DockPanel.SetDock(grip, Dock.Right); footer.Children.Add(grip);
        _status.Foreground = new SolidColorBrush(Color.FromRgb(168, 185, 205));
        _status.FontSize = 11;
        _status.Text = "빈 날짜 더블클릭: 추가 · 일정 더블클릭: 수정 · 일정 우클릭: 색상 · 일정 1초 누른 채 드래그: 이동";
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        footer.Children.Add(_status);
        Grid.SetRow(footer, 2); _root.Children.Add(footer);
    }

    private void AnnounceVisibleRange()
    {
        var viewport = CalendarViewportPolicy.Calculate(_visibleMonth, _settings.CalendarViewMode,
            _settings.CalendarVisibleWeekRows);
        var last = viewport.FirstSunday.AddDays(viewport.WeekCount * 7 - 1);
        SetStatus($"표시 기간: {viewport.FirstSunday:yyyy.MM.dd} ~ {last:yyyy.MM.dd}");
    }

    private static Button MakeButton(string label) => new()
    {
        Content = label, MinWidth = 30, Padding = new Thickness(7, 3, 7, 3),
        Margin = new Thickness(2, 0, 2, 0), Background = new SolidColorBrush(Color.FromRgb(45, 51, 65)),
        Foreground = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(76, 86, 105))
    };

    public bool IsEditing => _editing;

    // A purely geometric operation. Called before the first Show(), on monitor
    // changes, on explicit repair and after the resize grip is released.
    // Never tries to change the desktop band, force focus or alter Google data.
    public bool EnsureWithinVisibleWorkArea(string reason)
    {
        if (!MonitorWorkAreaResolver.TryGetWorkingArea(this, out var work)) return false;
        var current = new Rect(Left, Top, Width, Height);
        var corrected = VisibleWindowBoundsPolicy.Fit(current, work, MinWidth, MinHeight);
        if (corrected == current) return false;

        // A tiny display is allowed to reduce the practical minimum so WPF does
        // not silently expand the window beyond the available work area again.
        MinWidth = Math.Min(390, Math.Max(1, work.Width));
        MinHeight = Math.Min(310, Math.Max(1, work.Height));
        Width = corrected.Width;
        Height = corrected.Height;
        Left = corrected.Left;
        Top = corrected.Top;
        DiagnosticsLog.Info($"Calendar bounds corrected ({reason}): "
            + $"({current.Left:F0},{current.Top:F0}) {current.Width:F0}x{current.Height:F0} "
            + $"=> ({Left:F0},{Top:F0}) {Width:F0}x{Height:F0}, "
            + $"work=({work.Left:F0},{work.Top:F0}) {work.Width:F0}x{work.Height:F0}");
        return true;
    }

    // Explicit recovery command only: do not unexpectedly change saved positions on startup.
    public void ResetPositionToPrimaryWorkArea()
    {
        var work = SystemParameters.WorkArea;
        Width = Math.Max(_settings.CalendarWidth, MinWidth);
        Height = Math.Min(Math.Clamp(_settings.CalendarHeight, MinHeight, MaxHeight), Math.Max(MinHeight, work.Height));
        Left = work.Left + Math.Max(0, (work.Width - Width) / 2);
        Top = work.Top + Math.Max(0, (work.Height - Height) / 2);
        EnsureWithinVisibleWorkArea("explicit position reset");
    }

    public void ApplyViewAndAppearanceFromSettings()
    {
        ApplySizeFromSettings();
        ApplyCalendarAppearance();
        if (!_editing && !_showingPreferences) RenderMonth();
    }

    public void ApplySizeFromSettings()
    {
        if (_editing) return;
        Width = Math.Max(_settings.CalendarWidth, MinWidth);
        Height = Math.Clamp(_settings.CalendarHeight, MinHeight, MaxHeight);
        EnsureWithinVisibleWorkArea("settings size changed");
    }
    public void GoToToday() { _visibleMonth = DateTime.Today; RenderMonth(); }
    public void SetStatus(string text) => _status.Text = text;

    public void RenderMonth()
    {
        if (_editing) return;
        CancelEventMove(); // Do not destroy a live editor on timer-driven synchronization.
        _showingPreferences = false;
        if (_editorSizeHandler is not null)
        {
            _body.SizeChanged -= _editorSizeHandler;
            _editorSizeHandler = null;
        }
        _editorOverlay = null;
        _body.Children.Clear(); _body.Children.Add(_weekScroll);
        var viewport = CalendarViewportPolicy.Calculate(_visibleMonth, _settings.CalendarViewMode,
            _settings.CalendarVisibleWeekRows);
        var lastDate = viewport.FirstSunday.AddDays(viewport.WeekCount * 7 - 1);
        // Show the ACTUAL rendered range and the real local "today" separately.
        // Browsing another month never changes today's date in the heading.
        var todayCaption = CalendarHeaderPolicy.FormatToday(DateTime.Today);
        _monthCaption.Text = CalendarHeaderPolicy.FormatVisibleRangeAndToday(
            viewport.FirstSunday, lastDate, todayCaption);
        _monthCaption.ToolTip = CalendarHeaderPolicy.FormatViewportHint(
            _visibleMonth, _settings.CalendarViewMode, viewport.FirstSunday, lastDate);
        var dateFontSize = Math.Clamp(_settings.CalendarDateFontSize, 9, 24);
        var eventFontSize = Math.Clamp(_settings.CalendarEventFontSize, 9, 24);
        _monthCaption.FontSize = dateFontSize + 1;
        _weeks.Children.Clear();
        _renderedWeekRows.Clear();
        // A previously scrolled tall month must not open the next month below
        // its first week (especially now that scrollbars remain invisible).
        _weekScroll.ScrollToTop();
        var heading = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        for (var i = 0; i < 7; i++) heading.ColumnDefinitions.Add(new ColumnDefinition());
        var names = new[] { "일", "월", "화", "수", "목", "금", "토" };
        for (var c = 0; c < 7; c++)
        {
            var title = new TextBlock { Text = names[c], TextAlignment = TextAlignment.Center, FontSize = dateFontSize,
                Foreground = DateForeground((DayOfWeek)c, outsideMonth: false) };
            Grid.SetColumn(title, c); heading.Children.Add(title);
        }
        _weeks.Children.Add(heading);
        heading.SizeChanged += (_, _) =>
        {
            _dayHeadingHeight = heading.ActualHeight;
            StretchWeekRowsToViewport();
        };
        var first = viewport.FirstSunday;
        var events = _store.Events.Where(e => !e.Deleted && (e.LocalOnly || _preferences.IsVisible(e.CalendarId)))
            .Concat(_holidayFeeds.Events.Where(e => _preferences.IsVisible(e.CalendarId))).ToArray();
        for (var w = 0; w < viewport.WeekCount; w++)
        {
            var weekStart = first.AddDays(w * 7);
            var bars = WeekBarLayout.Build(weekStart, events);
            var lanes = bars.Count == 0 ? 0 : bars.Max(x => x.Lane) + 1;
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 3) };
            for (var c = 0; c < 7; c++) grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(Math.Max(27, dateFontSize + 14)) });
            for (var lane = 0; lane < lanes; lane++) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(Math.Max(21, eventFontSize + 10)) });
            grid.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(1, GridUnitType.Star), MinHeight = 13
            });
            for (var c = 0; c < 7; c++)
            {
                var date = weekStart.AddDays(c);
                var isToday = date.Date == DateTime.Today;
                var dayCell = new Border { Margin = new Thickness(1, 0, 1, 0), CornerRadius = new CornerRadius(4),
                    Background = isToday ? _todayBackground : _dayBackground,
                    ToolTip = EmptyDateInteractionHelp,
                    BorderThickness = isToday ? new Thickness(1.5) : new Thickness(0.5),
                    BorderBrush = isToday ? TodayOutline : new SolidColorBrush(Color.FromRgb(55, 64, 80)) };
                dayCell.MouseLeftButtonDown += (_, e) =>
                {
                    if (e.ClickCount != 2 || e.Handled) return;
                    e.Handled = true; BeginEditing(date, null);
                };
                Grid.SetColumn(dayCell, c); Grid.SetRowSpan(dayCell, grid.RowDefinitions.Count);
                grid.Children.Add(dayCell);
                var number = new TextBlock
                {
                    Text = date.Day.ToString(CultureInfo.CurrentCulture), Margin = new Thickness(5, 3, 0, 0),
                    Foreground = DateForeground(date.DayOfWeek,
                        _settings.CalendarViewMode == "Month" && date.Month != _visibleMonth.Month),
                    FontWeight = date.Date == DateTime.Today ? FontWeights.Bold : FontWeights.Normal,
                    FontSize = dateFontSize,
                    Cursor = Cursors.Hand,
                    ToolTip = EmptyDateInteractionHelp
                };
                number.MouseLeftButtonDown += (_, e) =>
                {
                    if (e.ClickCount != 2) return;
                    e.Handled = true; BeginEditing(date, null);
                };
                Grid.SetColumn(number, c); Grid.SetRow(number, 0); grid.Children.Add(number);
            }
            foreach (var bar in bars)
            {
                var appearance = _eventAppearance.Get(bar.Event.Id);
                var brush = ParseColor(appearance.BackgroundColor ??
                    _preferences.ColorFor(bar.Event.CalendarId));
                var fontColor = appearance.TextColor is { } customText
                    ? ParseColor(customText) : Colors.White;
                var line = new Border
                {
                    CornerRadius = new CornerRadius(3), Margin = new Thickness(2, 1, 2, 1),
                    Background = new SolidColorBrush(brush), Cursor = Cursors.Hand,
                    ToolTip = $"{bar.Event.Title}\n{bar.Event.Date:yyyy-MM-dd} ~ {bar.Event.EffectiveEndDate:yyyy-MM-dd}"
                };
                var name = (bar.Event.Conflict ? "⚠ " : "") + bar.Event.Title;
                if (bar.Event.StartAt is { } time) name = time.ToLocalTime().ToString("HH:mm") + " " + name;
                line.Child = new TextBlock { Text = name, Foreground = new SolidColorBrush(fontColor), FontSize = eventFontSize,
                    TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 0, 3, 0) };
                line.MouseLeftButtonDown += (_, e) =>
                {
                    if (e.ClickCount == 2)
                    {
                        CancelEventMove();
                        e.Handled = true; BeginEditing(bar.Event.Date, bar.Event);
                    }
                    else if (e.ClickCount == 1 && e.ChangedButton == MouseButton.Left)
                        BeginEventMoveHold(bar.Event, line, e.GetPosition(_body));
                };
                // Every calendar event supports visual-only appearance settings,
                // including read-only Google and deterministic holiday feed events.
                // These never mark Google events Dirty or send calendar API calls.
                var menu = CreateEventAppearanceMenu(bar.Event);
                line.MouseRightButtonDown += (_, _) => CancelEventMove();
                if (bar.Event.Conflict)
                {
                    menu.Items.Add(new Separator());
                    if (bar.Event.RemoteId is not null && !bar.Event.RemoteCreatePending)
                    {
                        var restore = new MenuItem { Header = "Google 원격 버전으로 복원 (로컬 변경 취소)" };
                        restore.Click += (_, _) => RestoreRemoteEvent(bar.Event);
                        menu.Items.Add(restore);
                    }
                    else
                    {
                        var retry = new MenuItem { Header = "업로드 다시 시도 (권한 확인 후)" };
                        retry.Click += (_, _) =>
                        {
                            try
                            {
                                if (IsSynchronizing) { SetStatus("동기화가 완료된 뒤 다시 시도하세요."); return; }
                                _store.CommitEventChange(bar.Event.Id, staged =>
                                {
                                    staged.Conflict = false;
                                    return true;
                                });
                                SetStatus("동기화 버튼으로 다시 업로드하세요."); RenderMonth();
                            }
                            catch (Exception ex) { SetStatus("재시도 오류: " + ex.Message); }
                        };
                        menu.Items.Add(retry);
                    }
                }
                line.ContextMenu = menu;
                Grid.SetColumn(line, bar.StartColumn); Grid.SetColumnSpan(line, bar.ColumnSpan);
                Grid.SetRow(line, bar.Lane + 1); grid.Children.Add(line);
            }
            _weeks.Children.Add(grid);
            _renderedWeekRows.Add(grid);
        }
        StretchWeekRowsToViewport();
        ApplyCalendarAppearance();
    }

    private ContextMenu CreateEventAppearanceMenu(CalendarEvent ev)
    {
        var menu = new ContextMenu();
        var baseStyle = _eventAppearance.Get(ev.Id);
        var canEdit = !_eventAppearance.IsReadOnly;
        AddColorSubmenu(menu, ev, "배경색 변경", background: true,
            baseStyle.BackgroundColor, canEdit);
        AddColorSubmenu(menu, ev, "글자색 변경", background: false,
            baseStyle.TextColor, canEdit);
        var reset = new MenuItem { Header = "색상 모두 기본값으로", IsEnabled = canEdit };
        reset.Click += (_, _) => SaveEventAppearance(ev, null, null);
        menu.Items.Add(reset);
        if (!canEdit)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem
            {
                Header = "색상 설정 저장소가 읽기 전용입니다. 진단 로그를 확인하세요.",
                IsEnabled = false
            });
        }
        return menu;
    }

    private void AddColorSubmenu(ContextMenu menu, CalendarEvent ev, string title,
        bool background, string? selectedColor, bool canEdit)
    {
        var submenu = new MenuItem { Header = title, IsEnabled = canEdit };
        var palette = new (string Name, string Hex)[]
        {
            ("빨강", "#F83A22"), ("주황", "#FFB347"), ("노랑", "#FFD166"),
            ("초록", "#78D390"), ("청록", "#26B7A9"), ("파랑", "#5D91DD"),
            ("보라", "#A48FFF"), ("분홍", "#E58CCB"), ("흰색", "#FFFFFF"),
            ("검정", "#101318"), ("회색", "#708090"), ("짙은 남색", "#273B62")
        };
        foreach (var (name, hex) in palette)
        {
            var swatch = new System.Windows.Shapes.Rectangle
            {
                Width = 13, Height = 13,
                Fill = new SolidColorBrush(ParseColor(hex)),
                Stroke = Brushes.Gray, StrokeThickness = 0.5,
                Margin = new Thickness(0, 0, 5, 0)
            };
            var item = new MenuItem
            {
                Header = name + " (" + hex + ")",
                Icon = swatch,
                IsChecked = string.Equals(selectedColor, hex, StringComparison.OrdinalIgnoreCase)
            };
            var chosenColor = hex;
            item.Click += (_, _) => ChangeEventColor(ev, background, chosenColor);
            submenu.Items.Add(item);
        }
        submenu.Items.Add(new Separator());
        var custom = new MenuItem { Header = "사용자 지정 색상…" };
        custom.Click += (_, _) =>
        {
            try
            {
                var style = _eventAppearance.Get(ev.Id);
                var defaultColor = background ? _preferences.ColorFor(ev.CalendarId) : "#FFFFFF";
                var initial = background ? style.BackgroundColor : style.TextColor;
                using var dialog = new Forms.ColorDialog
                {
                    FullOpen = true, AnyColor = true,
                    Color = System.Drawing.ColorTranslator.FromHtml(initial ?? defaultColor)
                };
                if (dialog.ShowDialog() == Forms.DialogResult.OK)
                    ChangeEventColor(ev, background,
                        $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}");
            }
            catch (Exception ex) { SetStatus("사용자 지정 색상 오류: " + ex.Message); }
        };
        submenu.Items.Add(custom);
        var clear = new MenuItem { Header = "이 색상만 기본값으로 복원" };
        clear.Click += (_, _) => ChangeEventColor(ev, background, null);
        submenu.Items.Add(clear);
        menu.Items.Add(submenu);
    }

    private void ChangeEventColor(CalendarEvent ev, bool background, string? selected)
    {
        var style = _eventAppearance.Get(ev.Id);
        SaveEventAppearance(ev,
            background ? selected : style.BackgroundColor,
            background ? style.TextColor : selected);
    }

    private void SaveEventAppearance(CalendarEvent ev, string? background, string? textColor)
    {
        try
        {
            _eventAppearance.Set(ev.Id, background, textColor);
            SetStatus("일정 표시 색상 저장 완료 · 로컬 표시 설정만 변경 (Google 일정 원본 유지)");
            RenderMonth();
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("Calendar event-local appearance update failed", ex);
            SetStatus("일정 색상 저장 실패: " + ex.Message);
        }
    }

    private bool CanRelocateEvent(CalendarEvent ev, out string reason)
    {
        reason = "";
        if (_editing || _showingPreferences || IsSynchronizing || _store.IsReadOnly || _preferences.IsReadOnly)
        { reason = "편집·동기화 중이거나 캘린더 데이터가 읽기 전용입니다."; return false; }
        if (ev.Deleted || ev.RemoteReadOnly)
        { reason = "이 일정은 읽기 전용이므로 이동할 수 없습니다."; return false; }
        if (ev.LocalOnly) return true;
        var sources = _preferences.Data.Catalog;
        if (sources.Count == 0)
        {
            if (ev.RemoteId is null) return true; // Truly local-only event.
            reason = "Google 캘린더 정보를 불러온 후 이동하세요.";
            return false;
        }
        var target = sources.FirstOrDefault(x => x.Id == (ev.CalendarId ?? "primary"));
        if (target is null && (ev.CalendarId is null or "primary"))
            target = sources.FirstOrDefault(x => x.IsPrimary);
        if (target?.CanWrite == true) return true;
        reason = "해당 Google 캘린더에는 수정 권한이 없습니다.";
        return false;
    }

    private void BeginEventMoveHold(CalendarEvent ev, Border bar, Point press)
    {
        CancelEventMove();
        if (!CanRelocateEvent(ev, out var reason)) { SetStatus(reason); return; }
        _moveCandidate = ev;
        _sourceBar = bar;
        _pressPoint = press;
        _longPressTimer.Start();
    }

    private void ActivateLongPressMove()
    {
        _longPressTimer.Stop();
        if (_moveCandidate is not { } ev || _sourceBar is not { } bar) return;
        if (Mouse.LeftButton != MouseButtonState.Pressed || !bar.IsMouseOver
            || !CanRelocateEvent(ev, out _)) { CancelEventMove(); return; }
        // Only capture after the explicit 1-second hold, never for a click.
        if (!Mouse.Capture(_body, CaptureMode.SubTree)) { CancelEventMove(); return; }
        _isMoving = true;
        _savedBarBackground = bar.Background;
        _savedBarChild = bar.Child;
        _savedBarTooltip = bar.ToolTip;
        bar.ToolTip = null;
        bar.Background = Brushes.Transparent;
        bar.Child = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromRgb(208, 220, 237)),
            StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 3, 2 },
            Fill = Brushes.Transparent, IsHitTestVisible = false
        };
        var eventStyle = _eventAppearance.Get(ev.Id);
        var color = ParseColor(eventStyle.BackgroundColor ?? _preferences.ColorFor(ev.CalendarId));
        var ghostTextColor = eventStyle.TextColor is { } customText
            ? ParseColor(customText) : Colors.White;
        _moveGhost = new Border
        {
            Width = Math.Max(80, bar.ActualWidth), Height = Math.Max(21, bar.ActualHeight),
            Background = new SolidColorBrush(color), BorderBrush = Brushes.White,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
            Opacity = 0.85, IsHitTestVisible = false,
            Child = new TextBlock { Text = ev.Title, Foreground = new SolidColorBrush(ghostTextColor),
                FontSize = Math.Clamp(_settings.CalendarEventFontSize, 9, 24),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) }
        };
        _moveLayer = new Canvas { IsHitTestVisible = false };
        _moveLayer.Children.Add(_moveGhost);
        System.Windows.Controls.Panel.SetZIndex(_moveLayer, 15);
        _body.Children.Add(_moveLayer);
        UpdateMovePosition(Mouse.GetPosition(_body));
        SetStatus("일정 이동 중 · 이동할 날짜에 놓으세요 · Esc: 취소");
    }

    private void OnMoveMouseMotion(object sender, MouseEventArgs e)
    {
        if (_moveCandidate is null) return;
        if (e.LeftButton != MouseButtonState.Pressed) { CancelEventMove(); return; }
        var point = e.GetPosition(_body);
        if (!_isMoving)
        {
            if (Math.Abs(point.X - _pressPoint.X) > SystemParameters.MinimumHorizontalDragDistance
                || Math.Abs(point.Y - _pressPoint.Y) > SystemParameters.MinimumVerticalDragDistance)
                CancelEventMove(); // Moving before the one-second hold never changes dates.
            return;
        }
        UpdateMovePosition(point);
        e.Handled = true;
    }

    private void UpdateMovePosition(Point point)
    {
        if (_moveGhost is not null)
        {
            Canvas.SetLeft(_moveGhost, point.X - _moveGhost.Width / 2);
            Canvas.SetTop(_moveGhost, point.Y - _moveGhost.Height / 2);
        }
        _moveDropDate = GetDateAtPosition(point);
        if (_moveGhost is not null)
            _moveGhost.BorderBrush = _moveDropDate.HasValue ? Brushes.White : Brushes.OrangeRed;
    }

    private DateTime? GetDateAtPosition(Point point)
    {
        // Use actual rendered cell coordinates after scrolling; a captured
        // mouse cannot rely on ordinary target hit testing.
        if (point.X < 0 || point.Y < 0 || point.X >= _weekScroll.ActualWidth
            || point.Y >= _weekScroll.ActualHeight) return null;
        var viewport = CalendarViewportPolicy.Calculate(_visibleMonth,
            _settings.CalendarViewMode, _settings.CalendarVisibleWeekRows);
        for (var week = 0; week < _renderedWeekRows.Count; week++)
        {
            var row = _renderedWeekRows[week];
            var origin = row.TranslatePoint(new Point(0, 0), _body);
            var x = point.X - origin.X;
            var y = point.Y - origin.Y;
            if (x < 0 || y < 0 || y >= row.ActualHeight || x >= row.ActualWidth) continue;
            double right = 0;
            for (var day = 0; day < 7; day++)
            {
                right += row.ColumnDefinitions[day].ActualWidth;
                if (x < right) return viewport.FirstSunday.AddDays(week * 7 + day);
            }
        }
        return null;
    }

    private void OnMoveMouseRelease(object sender, MouseButtonEventArgs e)
    {
        if (_moveCandidate is null) return;
        if (!_isMoving) { CancelEventMove(); return; }
        UpdateMovePosition(e.GetPosition(_body));
        var ev = _moveCandidate;
        var date = _moveDropDate;
        CancelEventMove();
        e.Handled = true;
        if (ev is null || !date.HasValue)
        { SetStatus("달력 밖에 놓아 이동을 취소했습니다."); return; }
        if (ev.Date.Date == date.Value.Date)
        { SetStatus("같은 날짜에 놓아 일정이 변경되지 않았습니다."); return; }
        if (!CanRelocateEvent(ev, out var reason)) { SetStatus(reason); return; }
        try
        {
            // Store writes a detached snapshot; failure cannot mutate live events.
            _store.UpsertBatch(CalendarEventMovePolicy.CreateDraft(ev, date.Value), ev);
            ProblemReportService.Reset("calendar-save");
            RenderMonth();
            SetStatus($"일정 이동 완료: {date.Value:yyyy.MM.dd} · Google 동기화 대기");
        }
        catch (Exception ex)
        {
            SetStatus("일정 이동 실패: " + ex.Message);
            if (ex is IOException or UnauthorizedAccessException)
            {
                var path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CyanDeck", "calendar-events.json");
                ProblemReportService.ShowOnce("calendar-save", "캘린더 일정 이동", "calendar-events.json", ex,
                    "날짜 이동 중 일정의 로컬 저장에 실패했습니다. 원래 일정과 파일을 유지합니다.", path, path + ".tmp");
            }
        }
    }

    private void CancelEventMove()
    {
        _longPressTimer.Stop();
        if (_isMoving && _sourceBar is not null)
        {
            _sourceBar.Background = _savedBarBackground;
            _sourceBar.Child = _savedBarChild;
            _sourceBar.ToolTip = _savedBarTooltip;
        }
        if (_moveLayer is not null) _body.Children.Remove(_moveLayer);
        _moveLayer = null;
        _moveGhost = null;
        _isMoving = false;
        _moveCandidate = null;
        _sourceBar = null;
        _savedBarBackground = null;
        _savedBarChild = null;
        _savedBarTooltip = null;
        _moveDropDate = null;
        if (Mouse.Captured == _body) Mouse.Capture(null);
    }

    // Keep text opaque while changing only the calendar background brushes.
    // This cannot disturb Explorer's desktop Z-order or other applications' focus.
    public void ApplyCalendarAppearance()
    {
        var baseColor = ParseColor(_settings.CalendarBackgroundColor);
        _frameBackground.Color = baseColor;
        _dayBackground.Color = Color.FromRgb(
            (byte)Math.Min(255, baseColor.R + 9),
            (byte)Math.Min(255, baseColor.G + 11),
            (byte)Math.Min(255, baseColor.B + 13));
        _todayBackground.Color = Color.FromRgb(
            (byte)Math.Min(255, baseColor.R + 15),
            (byte)Math.Min(255, baseColor.G + 39),
            (byte)Math.Min(255, baseColor.B + 74));
        // Avoid Window.Opacity entirely: the native placement pipeline controls
        // initial visibility, and changing Window.Opacity on focus can flicker.
        double active = double.IsFinite(_settings.CalendarOpacity)
            ? Math.Clamp(_settings.CalendarOpacity, 0.35, 1) : 0.86;
        double idle = double.IsFinite(_settings.CalendarIdleOpacity)
            ? Math.Clamp(_settings.CalendarIdleOpacity, 0.15, 1) : 0.38;
        bool full = !_settings.CalendarChameleonEnabled || _editing || _showingPreferences
            || IsActive || IsMouseOver;
        double activeBackground = double.IsFinite(_settings.CalendarActiveBackgroundOpacity)
            ? Math.Clamp(_settings.CalendarActiveBackgroundOpacity, 0.15, 1) : 0.86;
        double opacity = full ? Math.Clamp(activeBackground / active, 0.15, 1)
            : Math.Clamp(idle / active, 0.15, 1);
        _frameBackground.Opacity = opacity;
        _dayBackground.Opacity = opacity;
        _todayBackground.Opacity = Math.Max(opacity, 0.7);
    }

    private void StretchWeekRowsToViewport()
    {
        if (_renderedWeekRows.Count == 0) return;
        // Six weeks occupy the whole viewport when there is free height.
        // Dense event lanes remain wheel/touch-scrollable without a visible scrollbar.
        var viewportHeight = _weekScroll.ViewportHeight;
        if (!double.IsFinite(viewportHeight) || viewportHeight <= 0)
            viewportHeight = _weekScroll.ActualHeight;
        if (viewportHeight <= 0) return;
        var minimum = CalendarWeekSizingPolicy.MinHeightPerWeek(
            viewportHeight, _dayHeadingHeight, _renderedWeekRows.Count);
        foreach (var week in _renderedWeekRows)
            week.MinHeight = minimum;
    }

    // The incremental token may already have advanced past the conflicting
    // Google update. Clear it BEFORE discarding the local edits, so that the
    // next sync necessarily downloads a complete authoritative remote snapshot.
    private void RestoreRemoteEvent(CalendarEvent ev)
    {
        if (_editing || IsSynchronizing) { SetStatus("동기화 또는 편집 종료 후 복원하세요."); return; }
        if (_store.IsReadOnly || _preferences.IsReadOnly || ev.RemoteId is null || ev.RemoteCreatePending)
        {
            SetStatus("이 일정은 Google 원격 버전으로 복원할 수 없습니다."); return;
        }
        var calendarId = ev.CalendarId is null or "primary"
            ? _preferences.Data.Catalog.FirstOrDefault(c => c.IsPrimary)?.Id ?? "primary"
            : ev.CalendarId;
        try
        {
            _preferences.UpdateSyncToken(calendarId, null);
            _store.CommitEventChange(ev.Id, staged =>
            {
                staged.Dirty = false;
                staged.Conflict = false;
                return true;
            });
            SetStatus("복원 대기: 동기화를 실행하면 Google 버전을 다시 가져옵니다.");
            RenderMonth();
        }
        catch (Exception ex) { SetStatus("복원 준비 오류: " + ex.Message); }
    }

    private static Color ParseColor(string value)
    {
        try { return (Color)ColorConverter.ConvertFromString(value); }
        catch { return Color.FromRgb(93, 145, 221); }
    }

    private static DateTimeOffset AtLocal(DateTime date, TimeSpan time)
    {
        var local = DateTime.SpecifyKind(date.Date.Add(time), DateTimeKind.Unspecified);
        if (TimeZoneInfo.Local.IsInvalidTime(local)) throw new ArgumentException("존재하지 않는 현지 시각입니다(DST 전환). 다른 시간을 선택하세요.");
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private void BeginEditing(DateTime clickedDate, CalendarEvent? existing)
    {
        if (_store.IsReadOnly || _preferences.IsReadOnly) { SetStatus("캘린더 데이터가 읽기 전용 상태입니다."); return; }
        if (existing?.RemoteReadOnly == true)
        {
            SetStatus(KoreanHolidayCalendars.Contains(existing.CalendarId)
                ? "기본 제공 공휴일 캘린더는 읽기 전용입니다. 설정에서 표시 여부를 변경할 수 있습니다."
                : "Google에서 편집이 제한된 일정입니다.");
            return;
        }
        if (_editing) return;
        if (IsSynchronizing) { SetStatus("동기화가 완료된 후 일정을 수정할 수 있습니다."); return; }
        _editing = true;
        _showingPreferences = false;
        // Float a compact editor above only the right side; leave the date grid
        // visible and unchanged underneath so users can reference other days.
        var panel = new StackPanel { Margin = new Thickness(8), Background = Panel };
        var scroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var overlay = new Border
        {
            Background = Panel, BorderBrush = new SolidColorBrush(Color.FromRgb(100, 117, 141)),
            BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(9),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(3), Child = scroll
        };
        _editorOverlay = overlay;
        Grid.SetZIndex(overlay, 20);
        _body.Children.Add(overlay);
        void SizeEditor(object? sender, SizeChangedEventArgs args)
        {
            if (_editorOverlay != overlay) { _body.SizeChanged -= SizeEditor; return; }
            overlay.Width = Math.Clamp(_body.ActualWidth * 0.58, 305, 395);
            overlay.MaxHeight = Math.Max(110, _body.ActualHeight - 8);
        }
        _editorSizeHandler = SizeEditor;
        _body.SizeChanged += _editorSizeHandler;
        SizeEditor(_body, null!);
        ApplyCalendarAppearance();
        panel.Children.Add(new TextBlock { Text = existing is null ? "새 일정 (한 줄당 하나의 일정)" : "일정 수정 (추가 줄은 별도 일정)",
            Foreground = TextLight, FontSize = 14, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 8) });
        var input = new TextBox { Text = existing?.Title ?? "", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 70, MaxHeight = 140,
            ToolTip = "여러 줄을 입력하면 한 줄마다 독립적인 일정이 생성됩니다. Ctrl+Enter: 저장" };
        panel.Children.Add(input);
        var startDate = existing?.Date ?? clickedDate.Date;
        // Timed end is exclusive: preserve its actual end date while editing
        // HH:mm, even if the calendar bar ends on the previous day.
        var endDate = existing?.EndAt?.ToLocalTime().Date ?? existing?.EffectiveEndDate ?? startDate;
        var useDate = new CheckBox { Content = "날짜 직접 지정 (선택 해제 시 클릭한 날짜 적용)", Foreground = TextLight,
            IsChecked = existing is not null, Margin = new Thickness(0, 9, 0, 4) };
        panel.Children.Add(useDate);
        var dateRow = new StackPanel { Orientation = Orientation.Horizontal };
        // Explicit ko-KR is needed even if Windows' system/display language is English.
        // WPF's DatePicker popup Calendar inherits Language from its picker.
        var koreanCalendarLanguage = XmlLanguage.GetLanguage("ko-KR");
        var startPicker = new DatePicker { SelectedDate = startDate, Width = 137,
            Language = koreanCalendarLanguage, FirstDayOfWeek = DayOfWeek.Sunday,
            SelectedDateFormat = DatePickerFormat.Short, IsEnabled = useDate.IsChecked == true };
        var endPicker = new DatePicker { SelectedDate = endDate, Width = 137,
            Language = koreanCalendarLanguage, FirstDayOfWeek = DayOfWeek.Sunday,
            SelectedDateFormat = DatePickerFormat.Short, IsEnabled = useDate.IsChecked == true, Margin = new Thickness(8, 0, 0, 0) };
        var fromDateColumn = new StackPanel();
        fromDateColumn.Children.Add(new TextBlock { Text = "시작 날짜", Foreground = TextLight });
        fromDateColumn.Children.Add(startPicker);
        var toDateColumn = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
        toDateColumn.Children.Add(new TextBlock { Text = "종료 날짜", Foreground = TextLight });
        // Margin belongs to the column so the picker does not overflow the compact editor.
        endPicker.Margin = new Thickness(0);
        toDateColumn.Children.Add(endPicker);
        dateRow.Children.Add(fromDateColumn); dateRow.Children.Add(toDateColumn); panel.Children.Add(dateRow);
        useDate.Checked += (_, _) => { startPicker.IsEnabled = true; endPicker.IsEnabled = true; };
        useDate.Unchecked += (_, _) => { startPicker.IsEnabled = false; endPicker.IsEnabled = false; };
        var useTime = new CheckBox { Content = "시간 지정 (선택 해제 시 종일 일정)", Foreground = TextLight,
            IsChecked = existing?.StartAt.HasValue == true, Margin = new Thickness(0, 9, 0, 4) };
        panel.Children.Add(useTime);
        var timeRow = new StackPanel { Orientation = Orientation.Horizontal };
        var startTime = new CalendarTimePicker(existing?.StartAt?.ToLocalTime().TimeOfDay ?? new TimeSpan(9, 0, 0))
        { IsEnabled = useTime.IsChecked == true };
        var endTime = new CalendarTimePicker(existing?.EndAt?.ToLocalTime().TimeOfDay ?? new TimeSpan(10, 0, 0))
        { IsEnabled = useTime.IsChecked == true };
        var fromTimeColumn = new StackPanel();
        fromTimeColumn.Children.Add(new TextBlock { Text = "시작 시간", Foreground = TextLight });
        fromTimeColumn.Children.Add(startTime);
        var toTimeColumn = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
        toTimeColumn.Children.Add(new TextBlock { Text = "종료 시간", Foreground = TextLight });
        toTimeColumn.Children.Add(endTime);
        timeRow.Children.Add(fromTimeColumn); timeRow.Children.Add(toTimeColumn); panel.Children.Add(timeRow);
        var timedEndDate = existing?.EndAt?.ToLocalTime().Date;
        var allDayEndDate = existing?.EffectiveEndDate;
        useTime.Checked += (_, _) =>
        {
            startTime.IsEnabled = true; endTime.IsEnabled = true;
            // Restore the precise exclusive end when toggling back to timed.
            if (timedEndDate.HasValue && endPicker.SelectedDate == allDayEndDate)
                endPicker.SelectedDate = timedEndDate;
        };
        useTime.Unchecked += (_, _) =>
        {
            startTime.IsEnabled = false; endTime.IsEnabled = false;
            // Midnight is the *next* day's start. When converting a timed
            // event to all-day, don't accidentally create an extra all-day day.
            if (timedEndDate.HasValue && endPicker.SelectedDate == timedEndDate)
                endPicker.SelectedDate = allDayEndDate;
        };
        panel.Children.Add(new TextBlock { Text = "저장할 Google 캘린더", Foreground = TextLight, Margin = new Thickness(0, 9, 0, 4) });
        var writable = _preferences.Data.Catalog.Where(x => x.CanWrite).ToList();
        // Offline editing remains possible before the first Google connection.
        // A connected account with no writable calendars must NEVER fabricate
        // a writable 'primary' destination, since that can misroute an upload.
        var noWritableGoogleCalendar = _preferences.Data.Catalog.Count > 0 && writable.Count == 0;
        if (_preferences.Data.Catalog.Count == 0)
            writable.Add(new GoogleCalendarSource { Id = "primary", Name = "기본 캘린더 (연결 전)", AccessRole = "writer", IsPrimary = true });
        if (noWritableGoogleCalendar)
            panel.Children.Add(new TextBlock { Text = "이 계정에서 일정 작성 권한이 있는 Google 캘린더가 없습니다. 로컬에만 저장할 수 있습니다.",
                Foreground = Brushes.Khaki, TextWrapping = TextWrapping.Wrap });
        var targets = new ComboBox { ItemsSource = writable, DisplayMemberPath = nameof(GoogleCalendarSource.Name),
            SelectedValuePath = nameof(GoogleCalendarSource.Id), Width = 280, HorizontalAlignment = HorizontalAlignment.Left };
        var calendarId = existing is not null && !existing.LocalOnly
            ? existing.CalendarId ?? "primary"
            : _preferences.Data.TargetCalendarId ?? writable.FirstOrDefault()?.Id ?? "primary";
        if (calendarId == "primary") calendarId = writable.FirstOrDefault(x => x.IsPrimary)?.Id ?? calendarId;
        targets.SelectedValue = calendarId;
        if (targets.SelectedIndex < 0) targets.SelectedIndex = 0;
        targets.IsEnabled = existing is null || existing.RemoteId is null;
        panel.Children.Add(targets);
        var localOnly = new CheckBox
        {
            Content = "로컬에만 저장 (Google 캘린더에 업로드하지 않음)",
            Foreground = TextLight,
            IsChecked = existing?.LocalOnly == true ||
                (existing is null && (noWritableGoogleCalendar || _preferences.Data.DefaultNewEventsLocalOnly)),
            IsEnabled = existing?.RemoteId is null,
            Margin = new Thickness(0, 8, 0, 3),
            ToolTip = "CyanDeck의 로컬 일정 파일에만 저장합니다. Google 계정이 없어도 사용 가능합니다."
        };
        panel.Children.Add(localOnly);
        if (existing?.RemoteId is not null)
            panel.Children.Add(new TextBlock { Text = "Google에 연결된 기존 일정은 로컬 전용으로 전환할 수 없습니다.",
                Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap });
        void UpdateDestinationState() => targets.IsEnabled = localOnly.IsChecked != true
            && (existing is null || existing.RemoteId is null) && !noWritableGoogleCalendar;
        localOnly.Checked += (_, _) => UpdateDestinationState();
        localOnly.Unchecked += (_, _) => UpdateDestinationState();
        UpdateDestinationState();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var save = MakeButton("저장 (Ctrl+Enter)");
        save.IsEnabled = true;
        var cancel = MakeButton("취소");
        actions.Children.Add(save); actions.Children.Add(cancel);
        panel.Children.Add(actions);
        void Cancel() { _editing = false; RenderMonth(); }
        void Save()
        {
            try
            {
                var saveLocalOnly = localOnly.IsChecked == true;
                if (!saveLocalOnly && noWritableGoogleCalendar)
                    throw new InvalidOperationException("쓰기 권한이 있는 Google 캘린더를 연결하거나 '로컬에만 저장'을 선택하세요.");
                var selectedTarget = saveLocalOnly ? CalendarEventStore.LocalCalendarId
                    : targets.SelectedValue as string ?? throw new ArgumentException("저장 대상 캘린더를 선택하세요.");
                if (!saveLocalOnly && !writable.Any(x => x.Id == selectedTarget))
                    throw new ArgumentException("선택한 Google 캘린더에 쓰기 권한이 없습니다.");
                var from = useDate.IsChecked == true ? startPicker.SelectedDate ?? throw new ArgumentException("시작 날짜를 선택하세요.") : clickedDate.Date;
                var to = useDate.IsChecked == true ? endPicker.SelectedDate ?? throw new ArgumentException("종료 날짜를 선택하세요.") : from;
                DateTimeOffset? startAt = null, endAt = null;
                if (useTime.IsChecked == true)
                {
                    // The picker supplies a validated 24-hour TimeSpan, never free-form text.
                    startAt = AtLocal(from, startTime.SelectedTime);
                    endAt = AtLocal(to, endTime.SelectedTime);
                }
                var count = _store.UpsertBatch(new CalendarEventDraft
                {
                    StartDate = from, EndDate = to, StartAt = startAt, EndAt = endAt,
                    CalendarId = existing is not null && existing.RemoteId is not null
                        && (existing.CalendarId is null or "primary")
                        ? "primary" : selectedTarget, LocalOnly = saveLocalOnly, Text = input.Text
                }, existing);
                ProblemReportService.Reset("calendar-save");
                // The event is already durable. Failure to remember the last
                // target must not incorrectly report that the event was lost.
                string? preferenceWarning = null;
                if (existing is null && !saveLocalOnly)
                {
                    try { _preferences.UpdateTargetCalendar(targets.SelectedValue as string); }
                    catch (Exception ex) { preferenceWarning = ex.Message; }
                }
                _editing = false;
                RenderMonth();
                SetStatus(saveLocalOnly ? $"로컬 전용 일정 {count}건 저장됨 · Google에 업로드하지 않음"
                    : preferenceWarning is null
                        ? $"로컬 일정 {count}건 저장 · {writable.First(x => x.Id == selectedTarget).Name} · 동기화를 눌러 Google에 반영"
                        : $"로컬 일정 {count}건 저장됨 · 기본 캘린더 설정은 저장 실패: {preferenceWarning}");
            }
            catch (Exception ex)
            {
                SetStatus("저장 오류: " + ex.Message);
                if (ex is IOException or UnauthorizedAccessException)
                {
                    var path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CyanDeck", "calendar-events.json");
                    ProblemReportService.ShowOnce("calendar-save", "캘린더 일정 저장", "calendar-events.json", ex,
                        "사용자가 작성한 일정의 로컬 저장에 실패했습니다. 저장 실패한 변경 내용은 완료 처리되지 않았습니다.", path, path + ".tmp");
                }
            }
        }
        save.Click += (_, _) => Save(); cancel.Click += (_, _) => Cancel();
        if (existing is not null)
        {
            var delete = MakeButton("일정 삭제");
            delete.Click += (_, _) =>
            {
                try { _store.Delete(existing); ProblemReportService.Reset("calendar-save"); _editing = false; SetStatus("일정 삭제 완료"); RenderMonth(); }
                catch (Exception ex)
                {
                    SetStatus("삭제 오류: " + ex.Message);
                    if (ex is IOException or UnauthorizedAccessException)
                    {
                        var path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CyanDeck", "calendar-events.json");
                        ProblemReportService.ShowOnce("calendar-save", "캘린더 일정 삭제", "calendar-events.json", ex,
                            "사용자가 요청한 일정 삭제의 로컬 저장에 실패했습니다. 기존 일정은 보존됩니다.", path, path + ".tmp");
                    }
                }
            };
            actions.Children.Add(delete);
        }
        input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Cancel(); }
            else if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { e.Handled = true; Save(); }
        };
        // Schedule focus after the overlay enters the visual tree and is laid out.
        // A double-click is the user's own gesture; never call SetForegroundWindow.
        void FocusInput()
        {
            if (!_editing || _editorOverlay != overlay || !input.IsVisible) return;
            FocusManager.SetFocusedElement(overlay, input);
            input.Focus();
            Keyboard.Focus(input);
            input.SelectAll();
        }
        _ = Dispatcher.BeginInvoke((Action)FocusInput, DispatcherPriority.Input);
    }

    private void TogglePreferences()
    {
        if (_editing) { SetStatus("일정을 저장하거나 취소한 후 설정을 여세요."); return; }
        if (_showingPreferences) { RenderMonth(); return; }
        _showingPreferences = true;
        ApplyCalendarAppearance();
        _body.Children.Clear();
        var panel = new StackPanel { Margin = new Thickness(6) };
        _body.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        panel.Children.Add(new TextBlock { Text = "표시할 Google 캘린더와 색상", Foreground = TextLight,
            FontSize = 14, FontWeight = FontWeights.Bold });
        panel.Children.Add(new TextBlock { Text = "체크: 가져오기/표시 · 대상: 새 일정 저장 · 색상: 이 프로그램에서만 변경",
            Foreground = Brushes.LightGray, Margin = new Thickness(0, 3, 0, 7) });
        panel.Children.Add(new TextBlock { Text = "보기 방식 · 표시 행 수", Foreground = TextLight,
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 3) });
        var viewMode = new ComboBox { Width = 230, HorizontalAlignment = HorizontalAlignment.Left };
        viewMode.Items.Add(new ComboBoxItem { Content = "오늘 중심 주 단위", Tag = "Rolling" });
        viewMode.Items.Add(new ComboBoxItem { Content = "월 단위", Tag = "Month" });
        viewMode.SelectedIndex = _settings.CalendarViewMode == "Month" ? 1 : 0;
        panel.Children.Add(viewMode);
        var rows = new ComboBox { Width = 230, HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 5, 0, 4) };
        for (var n = 3; n <= 8; n++) rows.Items.Add(n);
        rows.SelectedItem = Math.Clamp(_settings.CalendarVisibleWeekRows, 3, 8);
        panel.Children.Add(rows);
        panel.Children.Add(new TextBlock { Text = "월 단위는 한 달을 모두 표시하도록 필요한 행을 추가합니다.",
            Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = "날짜 · 요일 · 월 제목 글자 크기 (9~24)", Foreground = TextLight,
            Margin = new Thickness(0, 10, 0, 3) });
        var dateFont = new Slider { Minimum = 9, Maximum = 24, TickFrequency = 1,
            IsSnapToTickEnabled = true, Value = Math.Clamp(_settings.CalendarDateFontSize, 9, 24),
            Width = 240, HorizontalAlignment = HorizontalAlignment.Left };
        var dateFontValue = new TextBlock { Foreground = Brushes.LightGray, Text = $"{dateFont.Value:0} px" };
        dateFont.ValueChanged += (_, _) => dateFontValue.Text = $"{dateFont.Value:0} px";
        panel.Children.Add(dateFont); panel.Children.Add(dateFontValue);
        panel.Children.Add(new TextBlock { Text = "일정 제목 글자 크기 (9~24)", Foreground = TextLight,
            Margin = new Thickness(0, 8, 0, 3) });
        var eventFont = new Slider { Minimum = 9, Maximum = 24, TickFrequency = 1,
            IsSnapToTickEnabled = true, Value = Math.Clamp(_settings.CalendarEventFontSize, 9, 24),
            Width = 240, HorizontalAlignment = HorizontalAlignment.Left };
        var eventFontValue = new TextBlock { Foreground = Brushes.LightGray, Text = $"{eventFont.Value:0} px" };
        eventFont.ValueChanged += (_, _) => eventFontValue.Text = $"{eventFont.Value:0} px";
        panel.Children.Add(eventFont); panel.Children.Add(eventFontValue);
        var chameleon = new CheckBox { Content = "캘린더 카멜레온 (비활성 시 배경만 흐리게)",
            IsChecked = _settings.CalendarChameleonEnabled, Foreground = TextLight,
            Margin = new Thickness(0, 10, 0, 4) };
        panel.Children.Add(chameleon);
        panel.Children.Add(new TextBlock { Text = "배경색 (#RRGGBB)", Foreground = TextLight });
        var baseColor = new TextBox { Text = _settings.CalendarBackgroundColor, Width = 115,
            HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(baseColor);
        panel.Children.Add(new TextBlock { Text = "비활성 배경 투명도", Foreground = TextLight,
            Margin = new Thickness(0, 5, 0, 0) });
        var idleOpacity = new Slider { Minimum = 0.15, Maximum = 1, TickFrequency = 0.05,
            Value = Math.Clamp(_settings.CalendarIdleOpacity, 0.15, 1), Width = 240,
            HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(idleOpacity);
        panel.Children.Add(new TextBlock { Text = "활성 배경 불투명도", Foreground = TextLight,
            Margin = new Thickness(0, 5, 0, 0) });
        var activeOpacity = new Slider { Minimum = 0.15, Maximum = 1, TickFrequency = 0.05,
            Value = Math.Clamp(_settings.CalendarActiveBackgroundOpacity, 0.15, 1), Width = 240,
            HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(activeOpacity);
        var appearanceSave = MakeButton("보기 / 글자 크기 / 색상 / 투명도 적용");
        appearanceSave.Click += (_, _) =>
        {
            if (!Regex.IsMatch(baseColor.Text.Trim(), "^#[0-9A-Fa-f]{6}$"))
            { SetStatus("캘린더 배경색은 #RRGGBB 형식입니다."); return; }
            _settings.CalendarViewMode = (viewMode.SelectedItem as ComboBoxItem)?.Tag as string ?? "Rolling";
            _settings.CalendarVisibleWeekRows = rows.SelectedItem is int rowCount ? rowCount : 6;
            _settings.CalendarDateFontSize = Math.Clamp(dateFont.Value, 9, 24);
            _settings.CalendarEventFontSize = Math.Clamp(eventFont.Value, 9, 24);
            _settings.CalendarChameleonEnabled = chameleon.IsChecked == true;
            _settings.CalendarBackgroundColor = baseColor.Text.Trim().ToUpperInvariant();
            _settings.CalendarIdleOpacity = idleOpacity.Value;
            _settings.CalendarActiveBackgroundOpacity = activeOpacity.Value;
            _onPositionChanged(); // same settings persistence checkpoint as calendar resizing
            _showingPreferences = false;
            RenderMonth();
            SetStatus("캘린더 보기·배경색·비활성 투명도 설정을 적용했습니다.");
        };
        panel.Children.Add(appearanceSave);
        if (_store.IsRecoveredFromBackup || _preferences.IsRecoveredFromBackup)
        {
            var restore = MakeButton("백업에서 실제 데이터 복원 (확인 필요)");
            restore.Click += (_, _) => ConfirmAndRestoreBackups();
            panel.Children.Add(restore);
        }
        panel.Children.Add(new TextBlock
        {
            Text = "Google 자동 동기화 간격 (분)",
            Foreground = TextLight, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 14, 0, 4)
        });
        var autoSyncMinutes = new TextBox
        {
            Text = _preferences.Data.AutoSyncIntervalMinutes.ToString(CultureInfo.InvariantCulture),
            Width = 95, HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip = "1~1440분 간격 · 0은 자동 동기화 끄기 (수동 동기화는 유지)"
        };
        panel.Children.Add(autoSyncMinutes);
        panel.Children.Add(new TextBlock
        {
            Text = "1~1440분 (새 사용자 기본 15분) · 0은 자동 동기화 끄기",
            Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap
        });
        var saveSyncInterval = MakeButton("자동 동기화 간격 저장");
        saveSyncInterval.Margin = new Thickness(2, 5, 2, 0);
        saveSyncInterval.Click += (_, _) =>
        {
            if (!int.TryParse(autoSyncMinutes.Text.Trim(), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var minutes)
                || minutes < CalendarSyncIntervalPolicy.MinMinutes
                || minutes > CalendarSyncIntervalPolicy.MaxMinutes)
            {
                SetStatus("자동 동기화 간격은 0~1440 사이의 정수(분)여야 합니다.");
                return;
            }
            if (_preferences.IsReadOnly)
            {
                SetStatus("읽기 전용 복구 상태에서는 동기화 설정을 저장할 수 없습니다.");
                return;
            }
            try
            {
                _preferences.UpdateAutoSyncInterval(minutes); // persist BEFORE changing live timer
                _onAutoSyncIntervalChanged();
                SetStatus(minutes == 0 ? "Google 자동 동기화를 껐습니다. 수동 동기화는 계속 사용 가능합니다."
                    : $"Google 자동 동기화 간격을 {minutes}분으로 설정했습니다.");
            }
            catch (Exception ex) { SetStatus("자동 동기화 간격 저장 오류: " + ex.Message); }
        };
        panel.Children.Add(saveSyncInterval);
        panel.Children.Add(new TextBlock
        {
            Text = "Google 계정 연결 · 캘린더 목록 자동 가져오기",
            Foreground = TextLight, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 14, 0, 5)
        });
        panel.Children.Add(new TextBlock
        {
            Text = "첫 연결 때 기본 브라우저에서 Google 로그인과 권한 승인을 진행합니다. "
                + "승인 후 계정 확인, 캘린더 목록·색상·쓰기 권한을 자동으로 가져옵니다. "
                + "일정의 업로드·다운로드는 별도 '동기화' 버튼에서 실행합니다.",
            Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 5)
        });
        if (!GoogleOAuthClientConfiguration.HasCompleteClient())
            panel.Children.Add(new TextBlock
            {
                Text = "Google 데스크톱 OAuth 설정이 이 배포본에 포함되지 않았습니다. 앱 제작자에게 인증 정보가 포함된 CyanDeck 빌드를 요청하세요. 일반 사용자의 JSON 설정은 필요하지 않습니다.",
                Foreground = Brushes.Khaki, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 5)
            });
        var connect = MakeButton(_connectAttempt is not null ? "연결 취소" : _connectHadFailure
            ? "Google 계정 다시 연결 / 캘린더 재시도" : "Google 계정 연결 / 캘린더 불러오기");
        connect.ToolTip = "실패 시 다시 클릭하면 브라우저에서 Google OAuth 로그인을 새로 시도합니다.";
        panel.Children.Add(connect);
        var connectionHint = new TextBlock
        {
            Text = _connectAttempt is not null ? "연결을 진행 중입니다. 취소 버튼을 누를 수 있습니다."
                : _connectHadFailure ? "이전 연결에 실패했습니다. 다시 연결을 누르면 브라우저 인증을 다시 시작합니다."
                : "연결이 실패하면 '다시 연결'을 누르세요. 진행 중에는 취소할 수 있습니다.",
            Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 7)
        };
        panel.Children.Add(connectionHint);
        connect.Click += async (_, _) =>
        {
            if (_connectAttempt is not null)
            {
                connect.IsEnabled = false;
                connectionHint.Text = "Google 계정 연결을 취소하는 중입니다...";
                _connectAttempt.Cancel();
                return;
            }
            if (string.IsNullOrWhiteSpace(GoogleOAuthClientConfiguration.Resolve(_settings.GoogleCalendarClientId)))
            {
                SetStatus("Google OAuth 데스크톱 클라이언트 ID가 없습니다. 배포본 설정을 확인하세요.");
                connectionHint.Text = "연결 설정이 없습니다. 앱 제작자가 OAuth 설정을 포함해 빌드해야 합니다.";
                return;
            }
            using var attempt = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            _connectAttempt = attempt;
            var forceReauthorize = _connectHadFailure;
            connect.Content = "연결 취소";
            connectionHint.Text = "Google 로그인 또는 캘린더 목록을 불러오는 중입니다. 3분이 지나면 종료됩니다.";
            SetStatus("Google 계정 연결 중... 실패하면 '다시 연결' 버튼이 표시됩니다.");
            var panelRebuilt = false;
            try
            {
                var sources = await _loadSources(attempt.Token, forceReauthorize);
                _connectHadFailure = false;
                if (_showingPreferences)
                {
                    _showingPreferences = false;
                    TogglePreferences(); // Rebuild source/permission selectors with the verified catalog.
                    panelRebuilt = true;
                }
                SetStatus($"Google 연결 확인 · 캘린더 {sources.Count}개를 불러왔습니다.");
            }
            catch (OperationCanceledException)
            {
                _connectHadFailure = true;
                var message = attempt.IsCancellationRequested
                    ? "Google 연결이 취소되었거나 시간이 초과되었습니다. 다시 연결을 눌러 새로 로그인하세요."
                    : "Google 연결이 중단되었습니다. 다시 연결을 눌러 새로 로그인하세요.";
                connectionHint.Text = message;
                SetStatus(message);
            }
            catch (Exception ex)
            {
                _connectHadFailure = true;
                connectionHint.Text = "Google 연결에 실패했습니다. 아래 '다시 연결'을 누르면 브라우저 인증이 다시 시작됩니다.";
                SetStatus("Google 계정 연결 실패: " + ex.Message);
            }
            finally
            {
                if (ReferenceEquals(_connectAttempt, attempt)) _connectAttempt = null;
                connect.IsEnabled = true;
                connect.Content = _connectHadFailure
                    ? "Google 계정 다시 연결 / 캘린더 재시도"
                    : "Google 계정 연결 / 캘린더 불러오기";
                // If the settings view was closed and reopened while awaiting OAuth,
                // refresh its button as well; don't leave a detached "Cancel" button.
                if (!panelRebuilt && _showingPreferences && !_body.IsAncestorOf(connect))
                {
                    _showingPreferences = false;
                    TogglePreferences();
                }
            }
        };
        var targets = new ComboBox { Width = 270, HorizontalAlignment = HorizontalAlignment.Left,
            ItemsSource = _preferences.Data.Catalog.Where(x => x.CanWrite).ToList(),
            DisplayMemberPath = nameof(GoogleCalendarSource.Name), SelectedValuePath = nameof(GoogleCalendarSource.Id),
            Margin = new Thickness(0, 6, 0, 10) };
        panel.Children.Add(new TextBlock { Text = "새 일정의 기본 저장 대상 (쓰기 가능한 캘린더만)",
            Foreground = TextLight, Margin = new Thickness(0, 8, 0, 0) });
        panel.Children.Add(targets);
        targets.SelectedValue = _preferences.Data.TargetCalendarId;
        targets.SelectionChanged += (_, _) =>
        {
            if (_preferences.IsReadOnly) return;
            var target = targets.SelectedValue as string;
            if (target == _preferences.Data.TargetCalendarId) return;
            try { _preferences.UpdateTargetCalendar(target); }
            catch (Exception ex)
            {
                SetStatus("저장 대상 설정 오류: " + ex.Message);
                targets.SelectedValue = _preferences.Data.TargetCalendarId;
            }
        };
        panel.Children.Add(new TextBlock { Text = "화면에 표시할 캘린더 (여러 개 선택 가능)",
            Foreground = TextLight, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 9, 0, 3) });
        // Built-in read-only ICS calendars are intentionally separate from the
        // Google account list. This section works without an OAuth login.
        panel.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(47, 58, 74)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(86, 112, 137)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7),
            Margin = new Thickness(0, 7, 0, 9), Padding = new Thickness(9),
            Child = CreateHolidaySettingsSection()
        });
        panel.Children.Add(new TextBlock { Text = "Google 계정의 캘린더", Foreground = TextLight,
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 5, 0, 2) });
        panel.Children.Add(new TextBlock { Text = "여기서 숨겨도 Google 캘린더의 원본 일정은 삭제되지 않습니다. "
                + "변경 후 '동기화'를 누르면 선택한 캘린더의 일정을 가져옵니다.",
            Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap });
        var bulk = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 5) };
        void ApplyBulkSelection(bool showAll)
        {
            try
            {
                _preferences.SetVisibleCalendars(showAll ? _preferences.Data.Catalog.Select(x => x.Id) : Enumerable.Empty<string>());
                _showingPreferences = false;
                TogglePreferences(); // Show the actual persisted checkbox state.
                SetStatus(showAll ? "모든 Google 캘린더를 표시하도록 선택했습니다. 동기화로 일정을 가져오세요."
                    : "모든 Google 캘린더를 달력 화면에서 숨겼습니다. 원본 일정은 보존됩니다.");
            }
            catch (Exception ex) { SetStatus("표시 캘린더 일괄 변경 실패: " + ex.Message); }
        }
        var showAllButton = MakeButton("전체 표시");
        showAllButton.Click += (_, _) => ApplyBulkSelection(true);
        bulk.Children.Add(showAllButton);
        var hideAllButton = MakeButton("전체 숨김");
        hideAllButton.Click += (_, _) => ApplyBulkSelection(false);
        bulk.Children.Add(hideAllButton);
        panel.Children.Add(bulk);
        foreach (var source in _preferences.Data.Catalog)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
            var label = source.Name + (source.IsPrimary ? " (기본)" : "")
                + (!source.CanWrite ? " · 읽기 전용" : "");
            var check = new CheckBox { Content = label, Foreground = TextLight,
                Width = 205, IsChecked = _preferences.IsVisible(source.Id), VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(check);
            check.Checked += (_, _) => ChangeVisible(source.Id, true);
            check.Unchecked += (_, _) => ChangeVisible(source.Id, false);
            var colorPicker = new CalendarColorPicker(_preferences.ColorFor(source.Id),
                !_preferences.IsReadOnly,
                selected =>
                {
                    _preferences.SetColorOverride(source.Id, selected);
                    SetStatus(source.Name + " 색상이 즉시 저장되었습니다.");
                }, SetStatus);
            row.Children.Add(colorPicker);
            var reset = MakeButton("초기화");
            reset.IsEnabled = !_preferences.IsReadOnly;
            reset.Click += (_, _) =>
            {
                try
                {
                    _preferences.SetColorOverride(source.Id, null);
                    colorPicker.SetDisplayedColor(_preferences.ColorFor(source.Id));
                    SetStatus(source.Name + " 기본 색상을 복원했습니다.");
                }
                catch (Exception ex) { SetStatus("색상 초기화 오류: " + ex.Message); }
            };
            row.Children.Add(reset);
            panel.Children.Add(row);
        }
        if (_preferences.Data.Catalog.Count == 0)
            panel.Children.Add(new TextBlock { Text = "아직 가져온 Google 캘린더가 없습니다. 위의 'Google 계정 연결 / 캘린더 불러오기'를 누르세요.", Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap });
        var back = MakeButton("달력으로 돌아가기"); back.Click += (_, _) => RenderMonth(); panel.Children.Add(back);
    }

    private void ConfirmAndRestoreBackups()
    {
        if (IsSynchronizing || _editing)
        {
            SetStatus("편집·동기화가 끝난 뒤 백업을 복원하세요.");
            return;
        }
        if ((_store.IsReadOnly && !_store.IsRecoveredFromBackup)
            || (_preferences.IsReadOnly && !_preferences.IsRecoveredFromBackup))
        {
            SetStatus("유효한 백업이 없는 저장소가 있습니다. 원본을 수동으로 확인하세요.");
            return;
        }
        var confirmation = MessageBox.Show(this,
            "읽기 전용 백업은 최신 일정이 아닐 수 있습니다. 손상된 원본은 별도 파일로 보존하고 "
            + "기존 .bak은 유지합니다. Google 동기화 토큰을 초기화하여 전체 재동기화합니다. "
            + "CyanDeck의 기존 데이터 폴더(%APPDATA%\\CyanDeck)를 별도로 백업한 후 복원을 진행하시겠습니까?",
            "캘린더 백업 복원 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes) return;
        try
        {
            // Order is deliberate: if recovery is interrupted, either the
            // calendar remains read-only or no stale sync tokens remain.
            if (_preferences.IsRecoveredFromBackup) _preferences.RestoreFromBackup();
            _preferences.InvalidateAllSyncTokens();
            if (_store.IsRecoveredFromBackup) _store.RestoreFromBackup();
            _showingPreferences = false;
            RenderMonth();
            SetStatus("백업 복원 완료. 기존 손상 파일은 .damaged-*로 보존했습니다(존재 시). Google 전체 재동기화가 필요합니다.");
        }
        catch (Exception ex)
        {
            SetStatus("복원 중단: " + ex.Message + " · 원본 파일과 .bak을 확인하세요.");
        }
    }

    private UIElement CreateHolidaySettingsSection()
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "기본 제공 캘린더 (계정 연결 불필요)",
            Foreground = TextLight, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "외부 ICS 구독 · 읽기 전용 · 공휴일 일정은 Google로 업로드되지 않습니다.",
            Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 7) });
        foreach (var source in KoreanHolidayCalendars.All)
        {
            var calendarId = source.Id;
            var checkRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 3) };
            var checkbox = new CheckBox { Content = source.Name,
                Foreground = TextLight, Width = 220, VerticalAlignment = VerticalAlignment.Center,
                IsChecked = _preferences.IsVisible(calendarId),
                IsEnabled = !_preferences.IsReadOnly,
                ToolTip = source.Url + " (읽기 전용)" };
            checkbox.Checked += (_, _) => ChangeHolidayVisible(calendarId, true);
            checkbox.Unchecked += (_, _) => ChangeHolidayVisible(calendarId, false);
            checkRow.Children.Add(checkbox);
            panel.Children.Add(checkRow);
            var colorRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 0, 5) };
            colorRow.Children.Add(new TextBlock { Text = "표시 색상", Foreground = TextLight,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            var colorPicker = new CalendarColorPicker(_preferences.ColorFor(calendarId),
                !_preferences.IsReadOnly,
                selected =>
                {
                    _preferences.SetColorOverride(calendarId, selected);
                    SetStatus(source.Name + " 색상이 즉시 저장되었습니다.");
                }, SetStatus);
            colorRow.Children.Add(colorPicker);
            var reset = MakeButton("기본색");
            reset.IsEnabled = !_preferences.IsReadOnly;
            reset.Click += (_, _) =>
            {
                try
                {
                    _preferences.SetColorOverride(calendarId, null);
                    colorPicker.SetDisplayedColor(_preferences.ColorFor(calendarId));
                    SetStatus(source.Name + " 기본색 " + source.Color + "을(를) 적용했습니다.");
                }
                catch (Exception ex) { SetStatus("공휴일 색상 초기화 실패: " + ex.Message); }
            };
            colorRow.Children.Add(reset);
            panel.Children.Add(colorRow);
        }
        var reload = MakeButton("공휴일 데이터 다시 불러오기");
        reload.ToolTip = "ICS를 다시 가져옵니다. Apple과 Google의 기본 주소 실패 시 대체 주소를 시도하고 결과를 표시합니다.";
        var detail = new TextBlock { Text = _holidayFeeds.LastResults.Count == 0
                ? "새로 구독한 공휴일은 첫 다운로드가 끝난 뒤 표시됩니다."
                : KoreanHolidayFeedService.DescribeResults(_holidayFeeds.LastResults),
            Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 3) };
        reload.Click += async (_, _) =>
        {
            reload.IsEnabled = false;
            detail.Text = "선택한 공휴일 캘린더를 가져오는 중… (연결 실패 시 결과가 표시됩니다.)";
            SetStatus("선택한 공휴일 캘린더의 새 데이터를 확인하고 있습니다.");
            try
            {
                var result = await _onHolidaySourcesChanged();
                detail.Text = result;
                SetStatus(result);
            }
            catch (Exception ex)
            {
                detail.Text = "공휴일 데이터 갱신 실패: " + ex.Message;
                SetStatus(detail.Text);
            }
            finally { reload.IsEnabled = true; }
        };
        panel.Children.Add(reload);
        panel.Children.Add(detail);
        return panel;
    }

    private async void ChangeHolidayVisible(string id, bool visible)
    {
        if (_preferences.IsReadOnly) return;
        try
        {
            _preferences.SetHolidayVisibility(id, visible);
            if (visible)
            {
                SetStatus("기본 제공 공휴일 캘린더를 표시합니다. 데이터 확인 중…");
                SetStatus(await _onHolidaySourcesChanged());
            }
            else SetStatus("기본 제공 공휴일 캘린더를 숨겼습니다. 저장된 ICS는 삭제하지 않습니다.");
        }
        catch (Exception ex) { SetStatus("공휴일 캘린더 설정 오류: " + ex.Message); }
    }

    private void ChangeVisible(string id, bool visible)
    {
        if (_preferences.IsReadOnly) return;
        try { _preferences.SetCalendarVisibility(id, visible); }
        catch (Exception ex)
        {
            SetStatus("표시 캘린더 설정 오류: " + ex.Message);
            // The persisted selection is authoritative; re-render the form so
            // a failed checkbox write cannot masquerade as a saved choice.
            _showingPreferences = false;
            TogglePreferences();
        }
    }
}
