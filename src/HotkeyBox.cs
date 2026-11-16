// HotkeyBox.cs —— quickHide 工程拆分文件（v1.3.0 起由单文件拆分而来）
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
    class HotkeyBox : TextBox
    {
        public event EventHandler ComboChanged;

        public string Combo
        {
            get { return Text; }
            set
            {
                if (Text == value) return;
                Text = value;
                if (ComboChanged != null) ComboChanged(this, EventArgs.Empty);
            }
        }

        public HotkeyBox()
        {
            ReadOnly = true;
            BackColor = Theme.Panel;
            ForeColor = Theme.Fg;
            BorderStyle = BorderStyle.FixedSingle;
            Width = 130;
            TextAlign = HorizontalAlignment.Center;
            Font = new Font("Microsoft YaHei UI", 9.5F);
            Cursor = Cursors.Hand;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            Keys k = e.KeyCode;
            if (k == Keys.ControlKey || k == Keys.ShiftKey || k == Keys.Menu || k == Keys.LWin || k == Keys.RWin)
                return; // 只按了修饰键，等待主键
            string key = null;
            if (k >= Keys.A && k <= Keys.Z) key = ((char)('A' + (k - Keys.A))).ToString();
            else if (k >= Keys.D0 && k <= Keys.D9) key = ((char)('0' + (k - Keys.D0))).ToString();
            else if (k >= Keys.F1 && k <= Keys.F24) key = "F" + (k - Keys.F1 + 1);
            else return; // 不支持的键，保持原值
            string combo = "";
            if (e.Control) combo += "Ctrl+";
            if (e.Alt) combo += "Alt+";
            if (e.Shift) combo += "Shift+";
            if ((GetKeyState(0xA4) | GetKeyState(0xA5)) < 0) combo += "Win+"; // VK_LWIN/VK_RWIN
            Text = combo + key;
        }

        // WinForms 的 KeyEventArgs 不暴露 Win 键状态，用 API 查
        [DllImport("user32.dll")]
        static extern short GetKeyState(int vk);
    }

    // ---------- 系统设置面板 ----------
}
