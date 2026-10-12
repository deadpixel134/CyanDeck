using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace DesktopDeck.Views;

/// <summary>
/// An ordinary, non-topmost error report window. In recoverable situations it
/// must not activate itself or raise the desktop-band windows above other apps.
/// </summary>
internal sealed class ProblemReportWindow : Window
{
    private readonly TextBox _reportText;
    private readonly TextBlock _copyStatus;
    private bool _loaded;

    public ProblemReportWindow(string report, bool fatal)
    {
        Title = "DesktopDeck 문제 발생 — 개발자에게 오류 전달";
        Width = 620;
        Height = 500;
        MinWidth = 480;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = true;
        ShowActivated = fatal;
        Topmost = false;
        Background = new SolidColorBrush(Color.FromRgb(247, 249, 252));
        Foreground = new SolidColorBrush(Color.FromRgb(30, 40, 55));

        var grid = new Grid { Margin = new Thickness(22) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new TextBlock
        {
            Text = "DesktopDeck에서 문제가 발생했습니다.",
            FontSize = 19,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(heading, 0);
        grid.Children.Add(heading);

        var instruction = new TextBlock
        {
            Text = "오류 진단 내용을 클립보드에 복사합니다. 개발자와 대화하는 창에 돌아가서 붙여넣기(Ctrl+V) 후 전송해 주세요. 프로그램이 자동으로 전송하지는 않습니다.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(instruction, 1);
        grid.Children.Add(instruction);

        _copyStatus = new TextBlock
        {
            Text = "진단 내용 복사 준비 중…",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(_copyStatus, 2);
        grid.Children.Add(_copyStatus);

        _reportText = new TextBox
        {
            Text = report,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Padding = new Thickness(10)
        };
        Grid.SetRow(_reportText, 3);
        grid.Children.Add(_reportText);

        var privacy = new TextBlock
        {
            Text = "전송하기 전에 진단 내용에 공유하고 싶지 않은 정보가 없는지 확인해 주세요.",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(94, 102, 119)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 10)
        };
        Grid.SetRow(privacy, 4);
        grid.Children.Add(privacy);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var copy = new Button
        {
            Content = "진단 내용 다시 복사",
            MinWidth = 152,
            Padding = new Thickness(12, 7, 12, 7),
            Margin = new Thickness(0, 0, 10, 0)
        };
        copy.Click += (_, _) => CopyReport();
        buttons.Children.Add(copy);
        var close = new Button { Content = "닫기", MinWidth = 88, Padding = new Thickness(12, 7, 12, 7) };
        close.Click += (_, _) => Close();
        buttons.Children.Add(close);
        Grid.SetRow(buttons, 5);
        grid.Children.Add(buttons);

        Content = grid;
        Loaded += (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            CopyReport();
        };
    }

    public void UpdateLockOwners(string fullReport)
    {
        _reportText.Text = fullReport;
        // There is no automatic second clipboard write: it might replace text
        // the user already copied after the initial alert. Explicitly notify.
        _copyStatus.Text = "관련 파일 사용자 조회 결과가 추가되었습니다. 최신 내용을 보내려면 '진단 내용 다시 복사'를 눌러 주세요.";
    }

    private void CopyReport()
    {
        try
        {
            // WinForms' bounded retry overload handles brief clipboard contention
            // and preserves the report after the DesktopDeck process closes.
            Forms.Clipboard.SetDataObject(_reportText.Text, true, 3, 80);
            _copyStatus.Text = "진단 내용이 복사되었습니다. 개발자 대화창에 Ctrl+V로 붙여넣고 전송해 주세요.";
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or
            System.Threading.ThreadStateException or System.ComponentModel.Win32Exception)
        {
            _copyStatus.Text = "클립보드가 사용 중이라 자동 복사에 실패했습니다. '진단 내용 다시 복사'를 누르거나 아래 내용을 직접 선택하여 복사해 주세요.";
        }
    }
}
