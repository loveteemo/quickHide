// Theme.cs —— quickHide 工程拆分文件（v1.3.0 起由单文件拆分而来）
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace quickHide
{
// ---------- UI 主题 ----------

static class Theme
{
    public static readonly Color Bg = Color.FromArgb(24, 24, 36);        // 窗体背景
    public static readonly Color Panel = Color.FromArgb(32, 32, 48);     // 面板/输入框
    public static readonly Color ListBg = Color.FromArgb(20, 20, 30);    // 列表背景
    public static readonly Color RowAlt = Color.FromArgb(27, 27, 41);    // 隔行
    public static readonly Color RowHidden = Color.FromArgb(43, 36, 27); // 已隐藏行
    public static readonly Color Fg = Color.FromArgb(226, 226, 234);     // 主文字
    public static readonly Color FgDim = Color.FromArgb(140, 140, 162);  // 次要文字
    public static readonly Color Accent = Color.FromArgb(122, 162, 247); // 强调蓝
    public static readonly Color Border = Color.FromArgb(64, 64, 88);
    public static readonly Color Ok = Color.FromArgb(152, 195, 121);     // 绿：显示中
    public static readonly Color Warn = Color.FromArgb(229, 192, 123);   // 琥珀：已隐藏
    public static readonly Color Btn = Color.FromArgb(44, 44, 64);

    public static Color Lighten(Color c, int d) { return Color.FromArgb(Math.Min(255, c.R + d), Math.Min(255, c.G + d), Math.Min(255, c.B + d)); }
    public static Color Darken(Color c, int d) { return Color.FromArgb(Math.Max(0, c.R - d), Math.Max(0, c.G - d), Math.Max(0, c.B - d)); }

    public static Button MakeButton(string text, bool primary)
    {
        var b = new Button();
        b.Text = text;
        b.AutoSize = true;
        b.FlatStyle = FlatStyle.Flat;
        b.Font = new Font("Microsoft YaHei UI", 9F);
        b.Padding = new Padding(10, 4, 10, 4);
        b.Margin = new Padding(3, 6, 3, 3);
        b.Cursor = Cursors.Hand;
        if (primary)
        {
            b.BackColor = Accent;
            b.ForeColor = Color.FromArgb(18, 24, 40);
            b.FlatAppearance.BorderColor = Accent;
        }
        else
        {
            b.BackColor = Btn;
            b.ForeColor = Fg;
            b.FlatAppearance.BorderColor = Border;
        }
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.MouseOverBackColor = Lighten(b.BackColor, 14);
        b.FlatAppearance.MouseDownBackColor = Darken(b.BackColor, 12);
        return b;
    }

    static Icon icon;
    public static Icon AppIcon
    {
        get
        {
            // 通用盾牌：低调不显眼，与系统安全类图标一致，方便伪装
            if (icon == null)
                icon = (Icon)SystemIcons.Shield.Clone();
            return icon;
        }
    }
}

// 深色托盘菜单配色
class DarkMenuColors : ProfessionalColorTable
{
    public override Color MenuItemSelected { get { return Theme.Lighten(Theme.Btn, 6); } }
    public override Color MenuItemSelectedGradientBegin { get { return Theme.Lighten(Theme.Btn, 6); } }
    public override Color MenuItemSelectedGradientEnd { get { return Theme.Lighten(Theme.Btn, 6); } }
    public override Color MenuItemPressedGradientBegin { get { return Theme.Btn; } }
    public override Color MenuItemPressedGradientEnd { get { return Theme.Btn; } }
    public override Color MenuItemBorder { get { return Theme.Border; } }
    public override Color MenuBorder { get { return Theme.Border; } }
    public override Color ToolStripDropDownBackground { get { return Theme.Panel; } }
    public override Color ImageMarginGradientBegin { get { return Theme.Panel; } }
    public override Color ImageMarginGradientMiddle { get { return Theme.Panel; } }
    public override Color ImageMarginGradientEnd { get { return Theme.Panel; } }
    public override Color ToolStripBorder { get { return Theme.Border; } }
    public override Color SeparatorDark { get { return Theme.Border; } }
    public override Color SeparatorLight { get { return Theme.Border; } }
}

// 双缓冲列表，避免频繁刷新闪烁
class BufferedListView : ListView
{
    public BufferedListView() { DoubleBuffered = true; }
}

// 全自绘深色 TabControl（默认控件标签条是浅灰色，与深色主题不搭）
class DarkTabControl : TabControl
{
    public DarkTabControl()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.Opaque |
            ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Theme.Bg);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Theme.Bg);
        for (int i = 0; i < TabCount; i++)
        {
            Rectangle r = GetTabRect(i);
            bool sel = i == SelectedIndex;
            using (var b = new SolidBrush(sel ? Theme.Panel : Theme.Bg))
                e.Graphics.FillRectangle(b, r);
            if (sel)
                using (var b = new SolidBrush(Theme.Accent))
                    e.Graphics.FillRectangle(b, r.X, r.Bottom - 3, r.Width, 3);
            TextRenderer.DrawText(
                e.Graphics,
                TabPages[i].Text,
                new Font("Microsoft YaHei UI", 9.5F, sel ? FontStyle.Bold : FontStyle.Regular),
                r,
                sel ? Color.White : Theme.FgDim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }
}
}
