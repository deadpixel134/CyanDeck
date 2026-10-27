using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CyanDeck.Views;

/// <summary>
/// Explicit, fail-closed choice for a user-initiated Delete in a Collection.
/// Never shows a default destructive action; dismissing it means Cancel.
/// </summary>
internal enum CollectionDesktopDeleteChoice
{
    Cancel,
    Recycle,
    RemoveFromGroup
}

internal sealed class CollectionDesktopDeleteDialog : Window
{
    private CollectionDesktopDeleteChoice _choice = CollectionDesktopDeleteChoice.Cancel;

    private CollectionDesktopDeleteDialog(int count)
    {
        Title = "CyanDeck — 바탕화면 항목 삭제";
        Width = 570;
        SizeToContent = SizeToContent.Height;
        MinHeight = 230;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Topmost = false;
        Background = new SolidColorBrush(Color.FromRgb(29, 32, 39));
        Foreground = Brushes.White;

        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock
        {
            Text = count == 1 ? "선택한 바탕화면 항목을 어떻게 처리할까요?"
                : $"선택한 바탕화면 항목 {count}개를 어떻게 처리할까요?",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
        });
        panel.Children.Add(new TextBlock
        {
            Text = "휴지통으로 이동: 실제 바탕화면의 원본 파일·폴더를 휴지통에 넣습니다.\n" +
                   "그룹에서만 제거: CyanDeck에서만 숨기고 원본은 그대로 유지합니다.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(221, 226, 236)),
            Margin = new Thickness(0, 0, 0, 20)
        });

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var recycle = CreateButton("휴지통으로 이동", 132);
        recycle.Click += (_, _) => Finish(CollectionDesktopDeleteChoice.Recycle);
        actions.Children.Add(recycle);
        var groupOnly = CreateButton("그룹에서만 제거", 132);
        groupOnly.Margin = new Thickness(9, 0, 0, 0);
        groupOnly.Click += (_, _) => Finish(CollectionDesktopDeleteChoice.RemoveFromGroup);
        actions.Children.Add(groupOnly);
        var cancel = CreateButton("취소", 76);
        cancel.Margin = new Thickness(9, 0, 0, 0);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => Finish(CollectionDesktopDeleteChoice.Cancel);
        actions.Children.Add(cancel);
        panel.Children.Add(actions);
        Content = panel;
        // Dark buttons like the rest of CyanDeck; recycling real files is shown as destructive.
        if (DeckThemeResources.MergeDarkControls(Resources)
            && DeckThemeResources.ControlStyle("DeckDangerButtonStyle") is { } danger)
            recycle.Style = danger;
    }

    private static Button CreateButton(string title, double minWidth) => new()
    {
        Content = title,
        MinWidth = minWidth,
        Padding = new Thickness(12, 9, 12, 9),
        FontSize = 13,
        IsDefault = false
    };

    private void Finish(CollectionDesktopDeleteChoice choice)
    {
        _choice = choice;
        DialogResult = true;
    }

    internal static CollectionDesktopDeleteChoice Show(Window owner, int count)
    {
        if (count <= 0) return CollectionDesktopDeleteChoice.Cancel;
        var dialog = new CollectionDesktopDeleteDialog(count) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog._choice : CollectionDesktopDeleteChoice.Cancel;
    }
}
