using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CyanDeck.Models;

namespace CyanDeck.Views;

public partial class SnapshotGalleryWindow : Window
{
    private readonly IList<DeckLayoutSnapshot> _snapshots;
    private readonly Action<DeckLayoutSnapshot> _restore;
    private readonly Action<DeckLayoutSnapshot> _delete;
    private readonly Action _changed;
    private readonly DeckLayoutSnapshot _current;
    private readonly Func<DeckLayoutSnapshot, ImageSource?>? _screenPreviewLoader;

    public SnapshotGalleryWindow(IList<DeckLayoutSnapshot> snapshots, DeckLayoutSnapshot current, Action<DeckLayoutSnapshot> restore, Action<DeckLayoutSnapshot> delete, Action changed, Func<DeckLayoutSnapshot, ImageSource?>? screenPreviewLoader = null)
    {
        _snapshots = snapshots;
        _current = current;
        _restore = restore;
        _delete = delete;
        _changed = changed;
        _screenPreviewLoader = screenPreviewLoader;
        InitializeComponent();
        // The gallery has no Cancel button; Esc closes it like the other CyanDeck dialogs.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Escape) return;
            e.Handled = true;
            Close();
        };
        Rebuild();
    }

    private void Rebuild()
    {
        CardsPanel.Children.Clear();
        if (_snapshots.Count == 0)
        {
            CardsPanel.Children.Add(new TextBlock
            {
                Text = "저장된 스냅샷이 없습니다.", Foreground = new SolidColorBrush(Color.FromRgb(150, 160, 174)),
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 80, 0, 0)
            });
            return;
        }

        foreach (var snapshot in _snapshots.ToArray()) CardsPanel.Children.Add(BuildCard(snapshot));
    }

    private UIElement BuildCard(DeckLayoutSnapshot snapshot)
    {
        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(31, 34, 40)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(60, 65, 76)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Margin = new Thickness(0, 0, 0, 12), Padding = new Thickness(14)
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var preview = BuildPreview(snapshot, _screenPreviewLoader?.Invoke(snapshot));
        Grid.SetColumn(preview, 0);
        grid.Children.Add(preview);

        var info = new StackPanel { Margin = new Thickness(18, 2, 0, 0) };
        info.Children.Add(new TextBlock { Text = snapshot.Name, FontSize = 16, FontWeight = FontWeights.SemiBold });
        info.Children.Add(new TextBlock
        {
            Text = $"{snapshot.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}  ·  {(snapshot.IsAutomatic ? "자동 백업  ·  " : "")}{Math.Max(1, snapshot.Monitors.Count)}개 모니터  ·  {snapshot.Fences.Count}개 그룹",
            Foreground = new SolidColorBrush(Color.FromRgb(159, 171, 185)), Margin = new Thickness(0, 5, 0, 10)
        });
        if (!string.IsNullOrWhiteSpace(snapshot.MonitorSignature))
            info.Children.Add(new TextBlock { Text = $"모니터 프로필 {snapshot.MonitorSignature}", Foreground = new SolidColorBrush(Color.FromRgb(117, 137, 164)), FontSize = 11 });

        var comparison = Compare(snapshot, _current);
        info.Children.Add(new TextBlock
        {
            Text = comparison.Summary, Foreground = new SolidColorBrush(comparison.HasChanges ? Color.FromRgb(235, 190, 105) : Color.FromRgb(114, 201, 154)),
            Margin = new Thickness(0, 7, 0, 0), TextWrapping = TextWrapping.Wrap
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        var restore = new Button { Content = "복원" };
        restore.Click += (_, _) =>
        {
            if (comparison.HasChanges)
            {
                var answer = MessageBox.Show(this, $"이 스냅샷을 복원하시겠습니까?\n\n{comparison.Summary}\n\n먼저 되돌리기용 스냅샷을 저장합니다.", "레이아웃 스냅샷 복원", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes) return;
            }
            _restore(snapshot);
            Close();
        };
        var rename = new Button { Content = "이름 바꾸기" };
        rename.Click += (_, _) =>
        {
            var value = TextPrompt.Show(this, "스냅샷 이름 바꾸기", "스냅샷 이름", snapshot.Name);
            if (string.IsNullOrWhiteSpace(value)) return;
            snapshot.Name = value.Trim();
            _changed();
            Rebuild();
        };
        var delete = new Button { Content = "삭제" };
        delete.Click += (_, _) =>
        {
            var answer = MessageBox.Show(this,
                $"'{snapshot.Name}' 스냅샷을 삭제하시겠습니까?\n\n삭제한 스냅샷은 되돌릴 수 없습니다. 현재 그룹 배치는 바뀌지 않습니다.",
                "레이아웃 스냅샷 삭제", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
            _delete(snapshot);
            Rebuild();
        };
        // Deleting a snapshot cannot be undone: red like the other destructive buttons.
        if (TryFindResource("DeckDangerButtonStyle") is Style danger)
        {
            delete.Style = danger;
            delete.Margin = new Thickness(4, 0, 0, 0);
        }
        buttons.Children.Add(restore);
        buttons.Children.Add(rename);
        buttons.Children.Add(delete);
        info.Children.Add(buttons);
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);
        border.Child = grid;
        return border;
    }

    private static SnapshotComparison Compare(DeckLayoutSnapshot snapshot, DeckLayoutSnapshot current)
    {
        var currentById = current.Fences.ToDictionary(x => x.FenceId);
        var snapshotById = snapshot.Fences.ToDictionary(x => x.FenceId);
        var moved = 0;
        var resized = 0;
        var stateChanged = 0;
        foreach (var p in snapshot.Fences)
        {
            if (!currentById.TryGetValue(p.FenceId, out var c)) continue;
            if (Math.Abs(p.X - c.X) > 1 || Math.Abs(p.Y - c.Y) > 1) moved++;
            if (Math.Abs(p.Width - c.Width) > 1 || Math.Abs(p.Height - c.Height) > 1) resized++;
            if (p.PageIndex != c.PageIndex || p.RolledUp != c.RolledUp || p.SelectedTabIndex != c.SelectedTabIndex) stateChanged++;
        }
        var groupSetChanged = snapshotById.Keys.Except(currentById.Keys).Count() + currentById.Keys.Except(snapshotById.Keys).Count();

        var currentItems = current.Items.GroupBy(x => (x.TabId, x.ItemId)).ToDictionary(g => g.Key, g => g.First());
        var iconMoved = snapshot.Items.Count(p => currentItems.TryGetValue((p.TabId, p.ItemId), out var c) &&
            ((p.X.HasValue != c.X.HasValue) || (p.Y.HasValue != c.Y.HasValue) ||
             (p.X.HasValue && c.X.HasValue && Math.Abs(p.X.Value - c.X.Value) > 1) ||
             (p.Y.HasValue && c.Y.HasValue && Math.Abs(p.Y.Value - c.Y.Value) > 1)));
        var pageChanged = snapshot.CurrentPage != current.CurrentPage ? 1 : 0;
        var has = moved + resized + stateChanged + groupSetChanged + iconMoved + pageChanged > 0;
        if (!has) return new SnapshotComparison(false, "현재 레이아웃과 같습니다.");

        var parts = new List<string>();
        if (moved > 0) parts.Add($"그룹 {moved}개 이동");
        if (resized > 0) parts.Add($"그룹 {resized}개 크기 변경");
        if (stateChanged > 0) parts.Add($"페이지/접기/탭 상태 {stateChanged}개 변경");
        if (iconMoved > 0) parts.Add($"자유 배치 아이콘 위치 {iconMoved}개 변경");
        if (groupSetChanged > 0) parts.Add($"일치하지 않는 그룹 {groupSetChanged}개");
        if (pageChanged > 0) parts.Add("활성 바탕화면 페이지가 다름");
        return new SnapshotComparison(true, "현재와 비교: " + string.Join(" · ", parts));
    }

    private sealed record SnapshotComparison(bool HasChanges, string Summary);

    private static Border BuildPreview(DeckLayoutSnapshot snapshot, ImageSource? screenPreview)
    {
        const double width = 286;
        const double height = 154;
        var canvas = new Canvas { Width = width, Height = height, ClipToBounds = true };
        var outer = new Border
        {
            Width = width, Height = height, CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(Color.FromRgb(20, 22, 27)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(67, 73, 86)), BorderThickness = new Thickness(1),
            Child = screenPreview is null ? canvas : new Image { Source = screenPreview, Stretch = Stretch.UniformToFill }
        };

        if (screenPreview is not null) return outer;

        var placements = snapshot.Fences;
        if (placements.Count == 0) return outer;
        var minX = placements.Min(p => p.X);
        var minY = placements.Min(p => p.Y);
        var maxX = placements.Max(p => p.X + p.Width);
        var maxY = placements.Max(p => p.Y + (p.RolledUp ? 42 : p.Height));
        var spanX = Math.Max(1, maxX - minX);
        var spanY = Math.Max(1, maxY - minY);
        var scale = Math.Min((width - 24) / spanX, (height - 24) / spanY);

        var pageColors = new[]
        {
            Color.FromRgb(107, 153, 238), Color.FromRgb(155, 140, 255), Color.FromRgb(103, 201, 164),
            Color.FromRgb(229, 172, 92), Color.FromRgb(225, 126, 159)
        };
        foreach (var p in placements)
        {
            var rect = new Border
            {
                Width = Math.Max(12, p.Width * scale),
                Height = Math.Max(8, (p.RolledUp ? 42 : p.Height) * scale),
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(Color.FromArgb(150, pageColors[Math.Abs(p.PageIndex) % pageColors.Length].R,
                    pageColors[Math.Abs(p.PageIndex) % pageColors.Length].G, pageColors[Math.Abs(p.PageIndex) % pageColors.Length].B)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(180, 235, 240, 248)), BorderThickness = new Thickness(.6),
                ToolTip = $"페이지 {p.PageIndex + 1}"
            };
            Canvas.SetLeft(rect, 12 + (p.X - minX) * scale);
            Canvas.SetTop(rect, 12 + (p.Y - minY) * scale);
            canvas.Children.Add(rect);
        }
        return outer;
    }
}
