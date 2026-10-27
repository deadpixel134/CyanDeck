using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace CyanDeck.Calendar;

// "반복" section of the in-calendar event editor: frequency, interval, weekly
// days and an optional end (date or count). Produces the stored RRULE text.
internal sealed class CalendarRecurrenceEditor
{
    private static readonly string[] DayNames = ["일", "월", "화", "수", "목", "금", "토"];
    private readonly ComboBox _frequency;
    private readonly ComboBox _interval;
    private readonly TextBlock _unit;
    private readonly StackPanel _intervalRow;
    private readonly WrapPanel _weekdayRow;
    private readonly CheckBox[] _days = new CheckBox[7];
    private readonly StackPanel _endRow;
    private readonly ComboBox _endMode;
    private readonly DatePicker _until;
    private readonly StackPanel _countRow;
    private readonly TextBox _count;
    private readonly TextBlock _summary;
    private DateTime _startDate;

    public StackPanel Root { get; } = new() { Margin = new Thickness(0, 9, 0, 0) };
    public event EventHandler? Changed;

    public CalendarRecurrenceEditor(CalendarRecurrenceRule? initial, DateTime startDate, Brush text)
    {
        _startDate = startDate.Date;
        Root.Children.Add(new TextBlock { Text = "반복", Foreground = text, Margin = new Thickness(0, 0, 0, 3) });
        var firstRow = new StackPanel { Orientation = Orientation.Horizontal };
        _frequency = DarkCombo(130);
        _frequency.Items.Add(new ComboBoxItem { Content = "반복 안 함", Tag = null });
        _frequency.Items.Add(new ComboBoxItem { Content = "매일", Tag = CalendarRecurrenceFrequency.Daily });
        _frequency.Items.Add(new ComboBoxItem { Content = "매주", Tag = CalendarRecurrenceFrequency.Weekly });
        _frequency.Items.Add(new ComboBoxItem { Content = "매월", Tag = CalendarRecurrenceFrequency.Monthly });
        _frequency.Items.Add(new ComboBoxItem { Content = "매년", Tag = CalendarRecurrenceFrequency.Yearly });
        _frequency.SelectedIndex = initial is null ? 0 : (int)initial.Frequency + 1;
        firstRow.Children.Add(_frequency);
        _intervalRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0) };
        _interval = DarkCombo(62);
        for (var n = 1; n <= 30; n++) _interval.Items.Add(n);
        if (initial is not null && initial.Interval > 30) _interval.Items.Add(initial.Interval);
        _interval.SelectedItem = initial?.Interval ?? 1;
        _unit = new TextBlock { Foreground = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0) };
        _intervalRow.Children.Add(_interval);
        _intervalRow.Children.Add(_unit);
        firstRow.Children.Add(_intervalRow);
        Root.Children.Add(firstRow);

        _weekdayRow = new WrapPanel { Margin = new Thickness(0, 5, 0, 0) };
        for (var d = 0; d < 7; d++)
        {
            var day = (DayOfWeek)d;
            var check = new CheckBox
            {
                Content = DayNames[d], Foreground = text, Margin = new Thickness(0, 0, 9, 0),
                IsChecked = initial?.Frequency == CalendarRecurrenceFrequency.Weekly
                    ? initial.ByDays.Count > 0 ? initial.ByDays.Contains(day) : day == _startDate.DayOfWeek
                    : day == _startDate.DayOfWeek
            };
            check.Checked += (_, _) => UpdateState();
            check.Unchecked += (_, _) => UpdateState();
            _days[d] = check;
        }
        // Monday-first like the RRULE week; Sunday last.
        for (var i = 1; i <= 7; i++) _weekdayRow.Children.Add(_days[i % 7]);
        Root.Children.Add(_weekdayRow);

        _endRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        _endRow.Children.Add(new TextBlock { Text = "반복 종료", Foreground = text, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0) });
        _endMode = DarkCombo(100);
        _endMode.Items.Add(new ComboBoxItem { Content = "없음", Tag = "none" });
        _endMode.Items.Add(new ComboBoxItem { Content = "날짜까지", Tag = "until" });
        _endMode.Items.Add(new ComboBoxItem { Content = "횟수", Tag = "count" });
        _endMode.SelectedIndex = initial?.Until is not null ? 1 : initial?.Count is not null ? 2 : 0;
        _endRow.Children.Add(_endMode);
        _until = new DatePicker
        {
            SelectedDate = initial?.Until ?? _startDate.AddMonths(1), Width = 130, Margin = new Thickness(6, 0, 0, 0),
            Language = XmlLanguage.GetLanguage("ko-KR"), FirstDayOfWeek = DayOfWeek.Sunday,
            SelectedDateFormat = DatePickerFormat.Short
        };
        if (CyanDeck.Views.DeckThemeResources.ControlStyle("DeckDatePickerStyle") is { } datePickerStyle)
            _until.Style = datePickerStyle;
        _endRow.Children.Add(_until);
        _countRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 0) };
        _count = new TextBox
        {
            Text = (initial?.Count ?? 10).ToString(CultureInfo.InvariantCulture), Width = 52,
            VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "1~999"
        };
        if (CyanDeck.Views.DeckThemeResources.ControlStyle("DarkTextBoxStyle") is { } textBoxStyle)
            _count.Style = textBoxStyle;
        _countRow.Children.Add(_count);
        _countRow.Children.Add(new TextBlock { Text = "회", Foreground = text, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0) });
        _endRow.Children.Add(_countRow);
        Root.Children.Add(_endRow);

        _summary = new TextBlock { Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        Root.Children.Add(_summary);

        _frequency.SelectionChanged += (_, _) => UpdateState();
        _interval.SelectionChanged += (_, _) => UpdateState();
        _endMode.SelectionChanged += (_, _) => UpdateState();
        _until.SelectedDateChanged += (_, _) => UpdateState();
        _count.TextChanged += (_, _) => UpdateState();
        UpdateState();
    }

    private static ComboBox DarkCombo(double width)
    {
        var combo = new ComboBox { Width = width, HorizontalAlignment = HorizontalAlignment.Left };
        if (CyanDeck.Views.DeckThemeResources.ControlStyle("DarkComboBoxStyle") is { } style) combo.Style = style;
        return combo;
    }

    private CalendarRecurrenceFrequency? SelectedFrequency
        => (_frequency.SelectedItem as ComboBoxItem)?.Tag is CalendarRecurrenceFrequency frequency ? frequency : null;

    private string EndMode => (_endMode.SelectedItem as ComboBoxItem)?.Tag as string ?? "none";

    public bool IsRepeating => SelectedFrequency is not null;

    // A changed start date moves a single selected weekday along with it.
    public void SyncStartDate(DateTime startDate)
    {
        var previous = _startDate;
        _startDate = startDate.Date;
        if (previous == _startDate) return;
        var checkedDays = _days.Where(c => c.IsChecked == true).ToArray();
        if (checkedDays.Length == 1 && ReferenceEquals(checkedDays[0], _days[(int)previous.DayOfWeek]))
            checkedDays[0].IsChecked = false;
        _days[(int)_startDate.DayOfWeek].IsChecked = true;
        UpdateState();
    }

    // Stored rule text, or null for "반복 안 함". Invalid input throws with a
    // message the editor shows in its status line.
    public string? BuildRule()
    {
        if (SelectedFrequency is not { } frequency) return null;
        var interval = _interval.SelectedItem is int value ? value : 1;
        var days = Array.Empty<DayOfWeek>();
        if (frequency == CalendarRecurrenceFrequency.Weekly)
        {
            days = Enumerable.Range(0, 7).Where(d => _days[d].IsChecked == true).Select(d => (DayOfWeek)d).ToArray();
            if (days.Length == 0) days = [_startDate.DayOfWeek];
            if (!days.Contains(_startDate.DayOfWeek))
                throw new ArgumentException("매주 반복 요일에 시작 날짜의 요일을 포함하세요.");
        }
        DateTime? until = null;
        int? count = null;
        if (EndMode == "until")
        {
            until = _until.SelectedDate?.Date ?? throw new ArgumentException("반복 종료 날짜를 선택하세요.");
            if (until < _startDate) throw new ArgumentException("반복 종료 날짜는 시작 날짜 이후여야 합니다.");
        }
        else if (EndMode == "count")
        {
            if (!int.TryParse(_count.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                || parsed < 1 || parsed > CalendarRecurrenceRule.MaxCount)
                throw new ArgumentException("반복 횟수는 1~999 사이의 숫자로 입력하세요.");
            count = parsed;
        }
        return new CalendarRecurrenceRule
        {
            Frequency = frequency, Interval = interval, ByDays = days, Until = until, Count = count
        }.ToString();
    }

    public string Signature()
        => string.Join("|", _frequency.SelectedIndex, _interval.SelectedItem,
            string.Concat(_days.Select(c => c.IsChecked == true ? "1" : "0")), EndMode,
            _until.SelectedDate, _count.Text);

    private void UpdateState()
    {
        var frequency = SelectedFrequency;
        _intervalRow.Visibility = frequency is null ? Visibility.Collapsed : Visibility.Visible;
        _weekdayRow.Visibility = frequency == CalendarRecurrenceFrequency.Weekly ? Visibility.Visible : Visibility.Collapsed;
        _endRow.Visibility = frequency is null ? Visibility.Collapsed : Visibility.Visible;
        _until.Visibility = EndMode == "until" ? Visibility.Visible : Visibility.Collapsed;
        _countRow.Visibility = EndMode == "count" ? Visibility.Visible : Visibility.Collapsed;
        _unit.Text = frequency switch
        {
            CalendarRecurrenceFrequency.Daily => "일마다",
            CalendarRecurrenceFrequency.Weekly => "주마다",
            CalendarRecurrenceFrequency.Monthly => "개월마다",
            CalendarRecurrenceFrequency.Yearly => "년마다",
            _ => ""
        };
        try
        {
            var rule = BuildRule();
            _summary.Text = rule is not null && CalendarRecurrenceRule.TryParse(rule, out var parsed) ? parsed.Describe() : "";
            _summary.Foreground = Brushes.LightGray;
        }
        catch (ArgumentException ex)
        {
            _summary.Text = ex.Message;
            _summary.Foreground = Brushes.Khaki;
        }
        _summary.Visibility = _summary.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

// "이 일정만 / 모든 반복 일정" choice shown when a repeating event is edited.
internal sealed class CalendarSeriesScopeSelector
{
    public StackPanel Root { get; } = new() { Margin = new Thickness(0, 0, 0, 8) };
    public RadioButton ThisOnly { get; }
    public RadioButton All { get; }
    private readonly TextBlock _hint;
    private readonly string _allHint;
    private readonly string? _allBlockedReason;

    public CalendarSeriesScopeSelector(Brush text, string allHint, bool forceAll, string? forcedReason,
        string? allBlockedReason = null)
    {
        _allHint = allHint;
        _allBlockedReason = forceAll ? null : allBlockedReason;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        ThisOnly = new RadioButton { Content = "이 일정만", Foreground = text, IsChecked = !forceAll,
            IsEnabled = !forceAll, Margin = new Thickness(0, 0, 14, 0) };
        All = new RadioButton { Content = "모든 반복 일정", Foreground = text, IsChecked = forceAll };
        row.Children.Add(ThisOnly);
        row.Children.Add(All);
        Root.Children.Add(row);
        _hint = new TextBlock { Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
        Root.Children.Add(_hint);
        if (forceAll && forcedReason is not null) All.ToolTip = forcedReason;
        if (_allBlockedReason is not null)
        {
            // e.g. an unsent "이 일정만" edit must reach Google before a series change.
            All.IsEnabled = false;
            All.ToolTip = _allBlockedReason;
            ToolTipService.SetShowOnDisabled(All, true);
        }
        ThisOnly.Checked += (_, _) => UpdateHint(forcedReason);
        All.Checked += (_, _) => UpdateHint(forcedReason);
        UpdateHint(forcedReason);
    }

    public bool AllSelected => All.IsChecked == true;

    private void UpdateHint(string? forcedReason)
        => _hint.Text = AllSelected
            ? (forcedReason is not null && !ThisOnly.IsEnabled ? forcedReason + "\n" : "") + _allHint
            : "반복 일정 중 이 일정만 변경됩니다." + (_allBlockedReason is null ? "" : "\n" + _allBlockedReason);
}

// C5: changing the start moves the end by the same amount (keeps the length)
// and an end earlier than the start on the same day is shown as the next day.
// Linking reacts only to user input made after the editor opened.
internal sealed class CalendarEditorTimeLink
{
    private readonly CheckBox _useDate;
    private readonly CheckBox _useTime;
    private readonly DatePicker _startPicker;
    private readonly DatePicker _endPicker;
    private readonly CalendarTimePicker _startTime;
    private readonly CalendarTimePicker _endTime;
    private readonly DateTime _defaultDate;
    private readonly TextBlock _hint;
    private readonly Action<string> _status;
    private DateTime _lastStart;
    private DateTime _lastEnd;
    private bool _linking;

    public event Action<DateTime>? StartDateChanged;

    public CalendarEditorTimeLink(CheckBox useDate, DatePicker startPicker, DatePicker endPicker, CheckBox useTime,
        CalendarTimePicker startTime, CalendarTimePicker endTime, DateTime defaultDate, TextBlock hint, Action<string> status)
    {
        _useDate = useDate; _startPicker = startPicker; _endPicker = endPicker; _useTime = useTime;
        _startTime = startTime; _endTime = endTime; _defaultDate = defaultDate.Date; _hint = hint; _status = status;
        _lastStart = CurrentStart();
        _lastEnd = CurrentEnd();
        _startTime.TimeChanged += (_, _) => OnStartChanged();
        _startPicker.SelectedDateChanged += (_, _) => OnStartChanged();
        _endTime.TimeChanged += (_, _) => OnEndChanged();
        _endPicker.SelectedDateChanged += (_, _) => OnEndDateChanged();
        _useDate.Checked += (_, _) => Refresh();
        _useDate.Unchecked += (_, _) => Refresh();
        _useTime.Checked += (_, _) => Refresh();
        _useTime.Unchecked += (_, _) => Refresh();
        UpdateHint();
    }

    public DateTime StartDate => _useDate.IsChecked == true ? _startPicker.SelectedDate?.Date ?? _defaultDate : _defaultDate;
    private DateTime EndDate => _useDate.IsChecked == true ? _endPicker.SelectedDate?.Date ?? StartDate : StartDate;
    private DateTime CurrentStart() => StartDate + _startTime.SelectedTime;
    private DateTime CurrentEnd() => CalendarEditorTimePolicy.ResolveTimedEnd(StartDate, _startTime.SelectedTime,
        EndDate, _endTime.SelectedTime);

    private void OnStartChanged()
    {
        if (_linking) return;
        var previousStart = _lastStart;
        var newStart = CurrentStart();
        var newEnd = CalendarEditorTimePolicy.ShiftEnd(_lastStart, _lastEnd, newStart);
        _linking = true;
        try
        {
            _endTime.SetTime(newEnd.TimeOfDay);
            if (_useDate.IsChecked == true) _endPicker.SelectedDate = newEnd.Date;
        }
        finally { _linking = false; }
        _lastStart = newStart;
        _lastEnd = CurrentEnd();
        if (previousStart.Date != newStart.Date) StartDateChanged?.Invoke(newStart.Date);
        UpdateHint();
    }

    private void OnEndChanged()
    {
        if (_linking) return;
        _lastEnd = CurrentEnd();
        UpdateHint();
    }

    private void OnEndDateChanged()
    {
        if (_linking) return;
        if (_useDate.IsChecked == true && _endPicker.SelectedDate is { } end
            && _startPicker.SelectedDate is { } start && end.Date < start.Date)
        {
            _linking = true;
            try { _endPicker.SelectedDate = start.Date; }
            finally { _linking = false; }
            _status("종료 날짜는 시작 날짜보다 빠를 수 없어 시작 날짜로 맞췄습니다.");
        }
        _lastEnd = CurrentEnd();
        UpdateHint();
    }

    private void Refresh()
    {
        var previousDate = _lastStart.Date;
        _lastStart = CurrentStart();
        _lastEnd = CurrentEnd();
        if (previousDate != _lastStart.Date) StartDateChanged?.Invoke(_lastStart.Date);
        UpdateHint();
    }

    private void UpdateHint()
    {
        var hint = _useTime.IsChecked == true
            ? CalendarEditorTimePolicy.EndHint(StartDate, _startTime.SelectedTime, EndDate, _endTime.SelectedTime)
            : null;
        _hint.Text = hint ?? "";
        _hint.Visibility = hint is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
