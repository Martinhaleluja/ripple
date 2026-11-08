using System;
using System.Drawing;
using System.Windows.Forms;
using Ripple.Core;

namespace Ripple.UI;

/// <summary>The tray icon: open, pause, quit.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _pause;
    private readonly Icon _picture;

    public TrayIcon(Action open, Action togglePause, Action quit)
    {
        _picture = LogoArt.TrayIcon(SystemInformation.SmallIconSize.Width >= 24 ? 32 : 24);
        _pause = new ToolStripMenuItem("Pause", null, (_, _) => togglePause());

        var menu = new ContextMenuStrip
        {
            Renderer = new ToolStripProfessionalRenderer(new DarkColours()),
            BackColor = Color.FromArgb(0x13, 0x13, 0x16),
            ForeColor = Color.FromArgb(0xF2, 0xF2, 0xF7),
            ShowImageMargin = false,
            Font = new Font("Segoe UI", 9.5f),
            Padding = new Padding(4),
        };
        menu.Items.Add(new ToolStripMenuItem("Open Ripple", null, (_, _) => open()) { Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) });
        menu.Items.Add(_pause);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Quit", null, (_, _) => quit()));
        foreach (ToolStripItem item in menu.Items)
        {
            item.ForeColor = menu.ForeColor;
            item.Padding = new Padding(6, 3, 18, 3);
        }

        _icon = new NotifyIcon { Icon = _picture, Text = "Ripple", ContextMenuStrip = menu, Visible = true };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                open();
            }
        };
    }

    public void ShowState(bool paused, string reason)
    {
        _pause.Text = paused ? "Resume" : "Pause";
        var text = reason.Length > 0 ? $"Ripple: {reason.ToLowerInvariant()}" : "Ripple";
        _icon.Text = text.Length > 63 ? text[..63] : text;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _picture.Dispose();
    }

    private sealed class DarkColours : ProfessionalColorTable
    {
        private static readonly Color Panel = Color.FromArgb(0x13, 0x13, 0x16);
        private static readonly Color Hover = Color.FromArgb(0x26, 0x26, 0x2C);
        private static readonly Color Line = Color.FromArgb(0x2A, 0x2A, 0x30);

        public override Color ToolStripDropDownBackground => Panel;
        public override Color MenuBorder => Line;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color SeparatorDark => Line;
        public override Color SeparatorLight => Panel;
        public override Color ImageMarginGradientBegin => Panel;
        public override Color ImageMarginGradientMiddle => Panel;
        public override Color ImageMarginGradientEnd => Panel;
    }
}
