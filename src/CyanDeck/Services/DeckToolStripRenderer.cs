using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace CyanDeck.Services;

/// <summary>
/// Dark look for the WinForms menus (tray icon, blank-desktop creation menu), matching the WPF
/// group menus in Themes/DeckMenu.xaml. Windows high contrast keeps the system renderer.
/// </summary>
internal sealed class DeckToolStripRenderer : Forms.ToolStripProfessionalRenderer
{
    private static readonly Color MenuBackground = Color.FromArgb(0x1F, 0x23, 0x29);
    private static readonly Color MenuBorder = Color.FromArgb(0x3A, 0x42, 0x4E);
    private static readonly Color MenuHighlight = Color.FromArgb(0x2C, 0x37, 0x45);
    private static readonly Color MenuText = Color.FromArgb(0xEE, 0xF2, 0xF7);
    private static readonly Color MenuMuted = Color.FromArgb(0x8D, 0x98, 0xA7);
    private static readonly Color MenuDisabled = Color.FromArgb(0x5D, 0x67, 0x74);
    private static readonly Color MenuCheck = Color.FromArgb(0x7D, 0xB8, 0xEE);
    private static readonly Color MenuSeparator = Color.FromArgb(0x33, 0x3A, 0x45);

    private DeckToolStripRenderer() : base(new DeckColorTable())
    {
        RoundedEdges = false;
    }

    /// <summary>Applies the dark look to a menu and all of its submenus.</summary>
    internal static void Apply(Forms.ToolStripDropDown menu)
    {
        if (Forms.SystemInformation.HighContrast) return;
        ApplyRecursive(menu, new DeckToolStripRenderer());
    }

    private static void ApplyRecursive(Forms.ToolStripDropDown dropDown, Forms.ToolStripRenderer renderer)
    {
        dropDown.Renderer = renderer;
        dropDown.Opened -= RoundCorners;
        dropDown.Opened += RoundCorners;
        foreach (Forms.ToolStripItem item in dropDown.Items)
        {
            if (item is Forms.ToolStripMenuItem { HasDropDownItems: true } menuItem)
                ApplyRecursive(menuItem.DropDown, renderer);
        }
    }

    // Windows 11 rounds popup corners on request; earlier versions ignore the attribute.
    private static void RoundCorners(object? sender, EventArgs e)
    {
        if (sender is not Forms.Control { IsHandleCreated: true } control) return;
        var preference = DwmCornerRoundSmall;
        _ = DwmSetWindowAttribute(control.Handle, DwmWindowCornerPreference, ref preference, sizeof(int));
    }

    protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? MenuText : MenuDisabled;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(Forms.ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = e.Item is { Enabled: false } ? MenuDisabled : MenuMuted;
        base.OnRenderArrow(e);
    }

    // A light check mark; the system glyph is black and disappears on the dark background.
    protected override void OnRenderItemCheck(Forms.ToolStripItemImageRenderEventArgs e)
    {
        var area = e.ImageRectangle;
        if (area.Width <= 0 || area.Height <= 0) return;
        var graphics = e.Graphics;
        var previous = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var pen = new Pen(e.Item.Enabled ? MenuCheck : MenuDisabled, 1.8f)
               { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            graphics.DrawLines(pen, new[]
            {
                new PointF(area.Left + area.Width * 0.22f, area.Top + area.Height * 0.52f),
                new PointF(area.Left + area.Width * 0.42f, area.Top + area.Height * 0.72f),
                new PointF(area.Left + area.Width * 0.80f, area.Top + area.Height * 0.30f)
            });
        }
        graphics.SmoothingMode = previous;
    }

    protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
    {
        var y = e.Item.Height / 2;
        using var pen = new Pen(MenuSeparator);
        e.Graphics.DrawLine(pen, 28, y, Math.Max(28, e.Item.Width - 8), y);
    }

    private sealed class DeckColorTable : Forms.ProfessionalColorTable
    {
        public DeckColorTable() { UseSystemColors = false; }
        public override Color ToolStripDropDownBackground => MenuBackground;
        public override Color ImageMarginGradientBegin => MenuBackground;
        public override Color ImageMarginGradientMiddle => MenuBackground;
        public override Color ImageMarginGradientEnd => MenuBackground;
        public override Color MenuBorder => DeckToolStripRenderer.MenuBorder;
        public override Color MenuItemBorder => MenuHighlight;
        public override Color MenuItemSelected => MenuHighlight;
        public override Color MenuItemSelectedGradientBegin => MenuHighlight;
        public override Color MenuItemSelectedGradientEnd => MenuHighlight;
        public override Color MenuItemPressedGradientBegin => MenuHighlight;
        public override Color MenuItemPressedGradientMiddle => MenuHighlight;
        public override Color MenuItemPressedGradientEnd => MenuHighlight;
        public override Color SeparatorDark => MenuSeparator;
        public override Color SeparatorLight => MenuSeparator;
        public override Color CheckBackground => MenuBackground;
        public override Color CheckSelectedBackground => MenuHighlight;
        public override Color CheckPressedBackground => MenuHighlight;
        public override Color ButtonSelectedBorder => MenuHighlight;
    }

    private const int DwmWindowCornerPreference = 33;
    private const int DwmCornerRoundSmall = 3;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
