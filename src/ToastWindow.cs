// ToastWindow.cs —— 应用内 toast 浮层（右下角、不抢焦点、自动淡出）
// 不用托盘气泡：Win11 勿扰/通知设置会静默吞掉 NotifyIcon 气泡，自绘浮层保证可见
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace quickHide
{
    class ToastWindow : Form
    {
        const int LifeMs = 2200;   // 完整显示时长
        const int FadeStepMs = 70; // 淡出步进间隔

        static ToastWindow current; // 连续提示时替换前一个

        readonly Action onClick;
        readonly Timer life = new Timer { Interval = LifeMs };
        readonly Timer fade = new Timer { Interval = FadeStepMs };
        readonly Font textFont;

        // UI 线程调用（TrayContext.ShowToast 已做 marshaling）
        public static void Show(string text, Action click)
        {
            if (current != null) { try { current.Close(); } catch { } current = null; }
            if (string.IsNullOrEmpty(text)) return;
            current = new ToastWindow(text, click);
            current.Show();
        }

        ToastWindow(string text, Action click)
        {
            onClick = click;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Theme.Panel;
            ForeColor = Theme.Fg;
            ShowWithoutActivationGuard(); // 不抢焦点，热键录入/游戏操作不被打断

            // 字体挂在字段上，OnFormClosed 才释放——Label 绘制时不能拿着已释放的 Font
            // （曾因 using 提前 dispose 导致首次绘制抛 GDI+ 异常，整窗变 DWM 红叉占位图）
            textFont = new Font("Microsoft YaHei UI", 9.5F);
            Size constrained = TextRenderer.MeasureText(text, textFont, new Size(430, int.MaxValue), TextFormatFlags.WordBreak);
            int w = Math.Min(constrained.Width + 30, 460);
            int h = constrained.Height + 20;
            var lbl = new Label
            {
                Text = text,
                Font = textFont,
                ForeColor = Theme.Fg,
                BackColor = Theme.Panel,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
                Bounds = new Rectangle(14, 0, w - 20, h),
            };
            Controls.Add(lbl);
            Size = new Size(w, h);

            // 工作区（排除任务栏）右下角
            Rectangle wa = SystemInformation.WorkingArea;
            Location = new Point(wa.Right - Width - 18, wa.Bottom - Height - 18);

            life.Tick += delegate { life.Stop(); fade.Start(); };
            fade.Tick += delegate
            {
                alpha -= 22;
                if (alpha <= 15) { Close(); return; }
                SetAlpha();
            };
        }

        // 不激活窗口地显示
        protected override bool ShowWithoutActivation { get { return true; } }

        // 分层透明度直接走 SetLayeredWindowAttributes：WinForms 的 Opacity 在
        // “句柄被提前强制创建”的场景下不会应用分层属性，窗口会变成 DWM 红叉占位图
        int alpha = 245;
        void SetAlpha()
        {
            SetLayeredWindowAttributes(Handle, 0, (byte)alpha, LWA_ALPHA);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            SetAlpha();
            life.Start();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var p = new Pen(Theme.Accent))
                e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            Close();
            if (onClick != null) onClick();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            if (current == this) current = null;
            life.Dispose();
            fade.Dispose();
            textFont.Dispose();
        }

        const int GWL_EXSTYLE = -20;
        const int WS_EX_NOACTIVATE = 0x08000000;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        const int WS_EX_LAYERED = 0x00080000;
        const uint LWA_ALPHA = 2;

        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
        [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h, uint crKey, byte alpha, uint flags);

        void ShowWithoutActivationGuard()
        {
            int ex = GetWindowLong(Handle, GWL_EXSTYLE);
            SetWindowLong(Handle, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_LAYERED);
        }
    }
}
