// HomeForm.cs —— quickHide 工程拆分文件（v1.3.0 起由单文件拆分而来）
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
    class HomeForm : Form
    {
        readonly TrayContext owner;
        readonly TabControl tabs;
        readonly ProcessPanel processPanel;
        readonly SettingsPanel settingsPanel;

        public HomeForm(TrayContext owner)
        {
            this.owner = owner;

            Text = "quickHide 主页";
            Icon = Theme.AppIcon;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = true;
            ClientSize = new Size(820, 560);
            BackColor = Theme.Bg;
            ForeColor = Theme.Fg;
            Font = new Font("Microsoft YaHei UI", 9F);

            // 标签页：进程 / 系统设置
            tabs = new DarkTabControl
            {
                Dock = DockStyle.Fill,
                ItemSize = new Size(110, 34),
            };

            processPanel = new ProcessPanel(owner) { Dock = DockStyle.Fill };
            settingsPanel = new SettingsPanel(owner) { Dock = DockStyle.Fill };
            settingsPanel.ModeChanged = () => processPanel.ReloadView();
            // 关窗时补交仍在录入框里未失焦的热键改动（点 X 关窗不会触发 LostFocus）
            FormClosing += (s, e) => settingsPanel.FlushPendingEdits();

            var tpProcs = new TabPage("进程");
            var tpSettings = new TabPage("设置");
            tpProcs.BackColor = Theme.Bg;
            tpSettings.BackColor = Theme.Bg;
            tpProcs.Controls.Add(processPanel);
            tpSettings.Controls.Add(settingsPanel);
            tabs.TabPages.AddRange(new[] { tpProcs, tpSettings });

            // 右下角版本号 + 关于入口
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 30, BackColor = Theme.Bg };
            var lblAbout = new LinkLabel
            {
                Text = "关于 quickHide v" + Program.Version,
                AutoSize = true,
                LinkColor = Theme.FgDim,
                ActiveLinkColor = Theme.Accent,
                LinkBehavior = LinkBehavior.HoverUnderline,
                Cursor = Cursors.Hand,
                Dock = DockStyle.Right,
                Padding = new Padding(0, 8, 16, 0),
            };
            lblAbout.LinkClicked += (s, e) =>
            {
                using (var d = new AboutDialog(owner)) d.ShowDialog(this);
            };
            bottom.Controls.Add(lblAbout);

            Controls.Add(tabs);
            Controls.Add(bottom);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Win32.EnableDarkTitleBar(Handle);
        }

        // 供托盘热键动作在执行前取当前勾选（勾选立即生效，不必先点保存）
        internal List<string> CollectCheckedProcesses()
        {
            return processPanel.GetCheckedProcesses();
        }

        // 窗口模式：取勾选的目标窗口句柄；非窗口模式返回 null（DoHide 走进程清单）
        internal List<IntPtr> CollectWindowTargets()
        {
            return processPanel.GetCheckedWindowHandles();
        }


    }
}
