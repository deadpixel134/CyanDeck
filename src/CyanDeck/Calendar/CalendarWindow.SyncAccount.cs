using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CyanDeck.Services;

namespace CyanDeck.Calendar;

// Sync status on event bars, deferred refresh after background sync, Google
// disconnect and local event backup. Hooks are set by CalendarModule after the
// window is created; every hook is optional so the window still works without them.
public sealed partial class CalendarWindow
{
    private const string IdleRenderNote = " · 달력은 작업을 마친 뒤 갱신";
    private bool _idleRenderPending;

    // Returns true when an automatic Google upload follows a local change shortly.
    internal Func<bool>? LocalChangeCommitted { get; set; }
    internal Func<bool>? GoogleNeedsReauthorization { get; set; }
    internal Func<bool>? HasGoogleAccount { get; set; }
    internal Func<Task<GoogleDisconnectResult>>? DisconnectGoogle { get; set; }
    internal Func<bool>? CanImportCalendarEvents { get; set; }

    // A press-and-hold or an actual event drag is in progress.
    public bool IsMovingEvent => _moveCandidate is not null;

    private bool CanRenderNow => !_editing && !_showingPreferences && _moveCandidate is null;

    // Read once per render: is any Google account tied to this PC? Without one,
    // events saved before the first connection are not shown as "upload pending".
    private bool _renderGoogleConnected = true;

    // Called by RenderMonth: the deferred refresh is now applied, so its status note goes.
    private void BeginSyncStateRender()
    {
        _idleRenderPending = false;
        if (_status.Text.EndsWith(IdleRenderNote, StringComparison.Ordinal))
            SetStatus(_status.Text[..^IdleRenderNote.Length]);
        try { _renderGoogleConnected = HasGoogleAccount?.Invoke() != false; }
        catch (Exception ex)
        {
            _renderGoogleConnected = true;
            DiagnosticsLog.Warn("Calendar Google connection check failed: " + ex.Message);
        }
    }

    // Background sync and holiday refresh: never close the settings panel, cancel an
    // event drag or jump the scroll position. The refresh waits until the user is done.
    public void RenderMonthWhenIdle()
    {
        if (!CanRenderNow)
        {
            _idleRenderPending = true;
            if (!_status.Text.EndsWith(IdleRenderNote, StringComparison.Ordinal))
                SetStatus(_status.Text + IdleRenderNote);
            return;
        }
        RenderPreservingScroll();
    }

    private void ScheduleIdleRender()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            if (_idleRenderPending && CanRenderNow) RenderPreservingScroll();
        }));
    }

    // Only the data changed: keep the same period and the same scroll position.
    private void RenderPreservingScroll()
    {
        var anchor = _visibleMonth;
        var mode = _settings.CalendarViewMode;
        var offset = _weekScroll.VerticalOffset;
        RenderMonth();
        if (offset > 0 && anchor == _visibleMonth && mode == _settings.CalendarViewMode)
        {
            _weekScroll.UpdateLayout();
            _weekScroll.ScrollToVerticalOffset(offset);
        }
    }

    private string DescribeEventForTooltip(CalendarEvent ev)
        => CalendarEventStatusText.Tooltip(ev, CalendarDisplayName(ev), _renderGoogleConnected);

    private string? CalendarDisplayName(CalendarEvent ev)
    {
        if (ev.LocalOnly) return null; // the state already says "로컬 전용"
        var holiday = KoreanHolidayCalendars.All.FirstOrDefault(source => source.Id == ev.CalendarId);
        if (holiday is not null) return holiday.Name;
        var catalog = _preferences.Data.Catalog;
        var calendar = ev.CalendarId is null or "primary"
            ? catalog.FirstOrDefault(source => source.IsPrimary)
            : catalog.FirstOrDefault(source => source.Id == ev.CalendarId);
        return calendar?.Name;
    }

    // After a successful local save/move/delete of a Google-linked event.
    private string DescribeUploadAfterLocalChange(bool linkedToGoogle, string manualHint)
    {
        if (!linkedToGoogle) return manualHint;
        try
        {
            return LocalChangeCommitted?.Invoke() == true ? "잠시 후 Google에 자동 반영" : manualHint;
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Warn("Calendar prompt sync scheduling failed: " + ex.Message);
            return manualHint;
        }
    }

    private void AddGoogleDisconnectControls(StackPanel panel)
    {
        if (DisconnectGoogle is null || HasGoogleAccount?.Invoke() != true) return;
        var disconnect = MakeButton("Google 연결 해제…");
        if (CyanDeck.Views.DeckThemeResources.ControlStyle("DeckDangerButtonStyle") is { } dangerStyle)
            disconnect.Style = dangerStyle;
        disconnect.HorizontalAlignment = HorizontalAlignment.Left;
        disconnect.ToolTip = "이 PC에서 Google 로그인과 계정 연결을 지웁니다. Google 서버의 일정은 지우지 않습니다.";
        disconnect.IsEnabled = !_store.IsReadOnly && !_preferences.IsReadOnly;
        disconnect.Click += async (_, _) => await ConfirmAndDisconnectGoogleAsync(disconnect);
        panel.Children.Add(disconnect);
        panel.Children.Add(new TextBlock
        {
            Text = "다른 Google 계정으로 바꾸려면 연결을 해제한 뒤 다시 연결하세요. 로컬 전용 일정은 그대로 남습니다.",
            Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 4)
        });
    }

    private async Task ConfirmAndDisconnectGoogleAsync(Button button)
    {
        if (DisconnectGoogle is not { } disconnectGoogle) return;
        if (_editing || IsSynchronizing || IsGoogleConnecting)
        {
            SetStatus("편집·동기화·Google 연결이 끝난 뒤 연결을 해제하세요.");
            return;
        }
        var downloaded = GoogleDisconnectPolicy.CountDownloadedCopies(_store.Events);
        var unsent = GoogleDisconnectPolicy.CountUnsentChanges(_store.Events);
        var message = "Google 계정 연결을 해제할까요?\n\n"
            + "이 PC에 저장된 Google 로그인 정보, 계정 연결과 캘린더 목록을 지웁니다. "
            + $"내려받은 Google 일정 {downloaded}개는 달력에서 사라지지만 Google 서버의 일정은 삭제되지 않습니다.\n"
            + "로컬 전용 일정은 그대로 남습니다.";
        if (unsent > 0)
            message += $"\n\n아직 Google에 올리지 못한 일정·변경 {unsent}건이 있습니다. "
                + "이 일정은 로컬 전용 일정으로 바꿔 이 PC에만 남기며, 나중에 연결하는 계정에는 올라가지 않습니다.";
        if (MessageBox.Show(this, message, "Google 연결 해제", MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        IsSynchronizing = true; // blocks editing, dragging and sync while files change
        button.IsEnabled = false;
        SetStatus("Google 연결을 해제하는 중…");
        try
        {
            var result = await disconnectGoogle();
            _connectHadFailure = false;
            var revokeNote = result.Revoked ? "" : " · Google 계정의 앱 권한은 Google 계정 보안 설정에서 직접 해제할 수 있습니다";
            SetStatus($"Google 연결을 해제했습니다 · 내려받은 일정 {result.Removed}개 정리 · 로컬 전용으로 보관 {result.ConvertedToLocal}개{revokeNote}");
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("Google Calendar disconnect failed", ex);
            SetStatus("Google 연결 해제 실패: " + ex.Message);
        }
        finally
        {
            IsSynchronizing = false;
            button.IsEnabled = true;
            if (_showingPreferences)
            {
                // Rebuild the Google section without the old account's calendars.
                var status = _status.Text;
                _showingPreferences = false;
                TogglePreferences();
                SetStatus(status);
            }
            else RenderMonthWhenIdle();
        }
    }

    private void AddLocalEventBackupSection(StackPanel panel)
    {
        AddPreferencesSectionTitle(panel, "일정 백업");
        panel.Children.Add(new TextBlock
        {
            Text = "Google에 올리지 않은 로컬 일정을 파일(.json)로 내보내거나 다시 가져옵니다. "
                + "일정 색상도 함께 저장됩니다. Google 일정은 Google에 보관되므로 포함하지 않습니다.",
            Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 5)
        });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 4) };
        var exportButton = MakeButton("로컬 일정 내보내기…");
        exportButton.Click += (_, _) => ExportLocalEvents();
        row.Children.Add(exportButton);
        var importAllowed = CanImportCalendarEvents?.Invoke() != false;
        var importButton = MakeButton("일정 가져오기…");
        importButton.IsEnabled = importAllowed && !_store.IsReadOnly;
        importButton.Click += (_, _) => ImportLocalEvents();
        row.Children.Add(importButton);
        panel.Children.Add(row);
        panel.Children.Add(new TextBlock
        {
            Text = importAllowed
                ? "가져온 일정은 로컬 전용으로 추가되고, 이미 있는 일정은 건너뜁니다. "
                    + "설정의 '레이아웃 및 설정 내보내기'를 하면 같은 폴더에 로컬 일정도 '….calendar-events.json'으로 함께 저장됩니다."
                : "일정 가져오기는 CyanDeck 관리 정책에 의해 비활성화되어 있습니다.",
            Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap
        });
    }

    private void ExportLocalEvents()
    {
        try
        {
            var count = CalendarEventBackup.CountLocalEvents(_store.Events);
            if (count == 0)
            {
                SetStatus("내보낼 로컬 일정이 없습니다. Google 일정은 Google에 보관됩니다.");
                return;
            }
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "CyanDeck 로컬 일정 내보내기",
                Filter = "CyanDeck 일정 백업 (*.json)|*.json|모든 파일 (*.*)|*.*",
                FileName = $"CyanDeck-calendar-{DateTime.Now:yyyyMMdd-HHmm}.json",
                AddExtension = true,
                DefaultExt = ".json"
            };
            if (dialog.ShowDialog(this) != true) return;
            var written = CalendarEventBackup.Export(_store.Events, _eventAppearance.Get, dialog.FileName);
            SetStatus($"로컬 일정 {written}개를 내보냈습니다: {Path.GetFileName(dialog.FileName)}");
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("Calendar local event export failed", ex);
            SetStatus("일정 내보내기 실패: " + ex.Message);
        }
    }

    private void ImportLocalEvents()
    {
        if (_editing || IsSynchronizing) { SetStatus("편집·동기화가 끝난 뒤 일정을 가져오세요."); return; }
        if (_store.IsReadOnly) { SetStatus("캘린더 데이터가 읽기 전용 상태라 가져올 수 없습니다."); return; }
        if (CanImportCalendarEvents?.Invoke() == false)
        {
            SetStatus("일정 가져오기는 CyanDeck 관리 정책에 의해 비활성화되어 있습니다.");
            return;
        }
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "CyanDeck 일정 가져오기",
                Filter = "CyanDeck 일정 백업 (*.json)|*.json|모든 파일 (*.*)|*.*",
                CheckFileExists = true
            };
            if (dialog.ShowDialog(this) != true) return;
            var backup = CalendarEventBackup.Read(dialog.FileName);
            var plan = CalendarEventBackup.PlanImport(_store.Events, backup);
            var skipped = DescribeSkippedImport(plan);
            if (plan.Added.Count == 0)
            {
                SetStatus("가져올 새 일정이 없습니다" + skipped);
                return;
            }
            if (MessageBox.Show(this,
                    $"로컬 일정 {plan.Added.Count}개를 가져올까요?\n\n"
                    + "가져온 일정은 로컬 전용으로 추가되며 Google에 올라가지 않습니다. 기존 일정은 바뀌지 않습니다.",
                    "일정 가져오기", MessageBoxButton.YesNo, MessageBoxImage.Question,
                    MessageBoxResult.Yes) != MessageBoxResult.Yes)
                return;
            // Durable checkpoint first; the live collection changes only after the save succeeds.
            _store.CommitSnapshot(_store.CreateSnapshot().Concat(plan.Added).ToList());
            var colorNote = "";
            if (plan.Appearance.Count > 0)
            {
                try
                {
                    if (_eventAppearance.IsReadOnly) colorNote = " · 색상 설정이 읽기 전용이라 색상은 가져오지 못함";
                    else _eventAppearance.SetMany(plan.Appearance);
                }
                catch (Exception ex) { colorNote = " · 색상 저장 실패: " + ex.Message; }
            }
            RenderMonth();
            SetStatus($"로컬 일정 {plan.Added.Count}개를 가져왔습니다{skipped}{colorNote}");
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("Calendar local event import failed", ex);
            SetStatus("일정 가져오기 실패: " + ex.Message);
        }
    }

    private static string DescribeSkippedImport(CalendarEventImportPlan plan)
    {
        var parts = new List<string>();
        if (plan.SkippedDuplicates > 0) parts.Add("이미 있는 일정 " + plan.SkippedDuplicates + "개");
        if (plan.SkippedLinked > 0) parts.Add("Google 일정 " + plan.SkippedLinked + "개");
        if (plan.SkippedInvalid > 0) parts.Add("읽을 수 없는 일정 " + plan.SkippedInvalid + "개");
        return parts.Count == 0 ? "" : " · 건너뜀: " + string.Join(", ", parts);
    }
}
