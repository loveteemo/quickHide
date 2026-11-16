// HotkeyWindow.cs —— quickHide 工程拆分文件（v1.3.0 起由单文件拆分而来）
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
    class HotkeyWindow : Form
    {
        public event Action<int> HotkeyPressed;
        const int WM_HOTKEY = 0x0312;

        public HotkeyWindow()
        {
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = new System.Drawing.Rectangle(-32000, -32000, 1, 1);
            Opacity = 0;
            ShowWithoutActivationGuard();
        }

        // 不抢占焦点地创建并显示
        void ShowWithoutActivationGuard()
        {
            int ex = GetWindowLong(Handle, GWL_EXSTYLE);
            SetWindowLong(Handle, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                var d = HotkeyPressed;
                if (d != null) d(m.WParam.ToInt32());
            }
            base.WndProc(ref m);
        }

        const int GWL_EXSTYLE = -20;
        const int WS_EX_NOACTIVATE = 0x08000000;
        const int WS_EX_TOOLWINDOW = 0x00000080;

        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
    }

    // ---------- 热键输入框（点击后按组合键录入）----------
}
