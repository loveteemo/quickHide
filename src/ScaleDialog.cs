// ScaleDialog.cs —— quickHide 工程拆分文件（v1.3.0 起由单文件拆分而来）
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
    class ScaleDialog : Form
    {
        readonly NumericUpDown num;
        public int Percent { get; private set; }

        public ScaleDialog(string procName, int current)
        {
            Text = "窗口缩放 - " + procName;
            Icon = Theme.AppIcon;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(390, 196);
            BackColor = Theme.Bg;
            ForeColor = Theme.Fg;
            Font = new Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.Dpi;

            Controls.Add(new Label
            {
                Text = "缩放比例（25%～100%）",
                AutoSize = true,
                ForeColor = Theme.Fg,
                Location = new Point(20, 18),
            });
            num = new NumericUpDown
            {
                Location = new Point(20, 46),
                Width = 100,
                Minimum = 25,
                Maximum = 100,
                Increment = 5,
                BackColor = Theme.Panel,
                ForeColor = Theme.Fg,
                BorderStyle = BorderStyle.FixedSingle,
            };
            Percent = HideEngine.ClampScale(current);
            num.Value = Percent;
            Controls.Add(num);
            Controls.Add(new Label
            {
                Text = "100% = 原始大小",
                AutoSize = true,
                ForeColor = Theme.FgDim,
                Location = new Point(130, 50),
            });
            Controls.Add(new Label
            {
                Text = "确认后立即缩放该进程的所有窗口。\r\n隐藏中的窗口会在还原时按该比例显示。",
                AutoSize = false,
                Size = new Size(350, 40),
                ForeColor = Theme.FgDim,
                Location = new Point(20, 82),
            });

            var btnRow = new FlowLayoutPanel
            {
                Location = new Point(20, 136),
                Size = new Size(350, 38),
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = Theme.Bg,
            };
            var btnOk = Theme.MakeButton("确定", true);
            btnOk.Margin = new Padding(3, 4, 10, 0);
            btnOk.Click += (s, e) => { Percent = (int)num.Value; DialogResult = DialogResult.OK; Close(); };
            var btnRestore = Theme.MakeButton("还原", false);
            btnRestore.Margin = new Padding(3, 4, 10, 0);
            btnRestore.Click += (s, e) => { num.Value = 100; Percent = 100; DialogResult = DialogResult.OK; Close(); };
            var btnCancel = Theme.MakeButton("取消", false);
            btnCancel.Margin = new Padding(3, 4, 3, 0);
            btnCancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            btnRow.Controls.AddRange(new Control[] { btnOk, btnRestore, btnCancel });
            Controls.Add(btnRow);
            AcceptButton = btnOk;
            CancelButton = btnCancel;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Win32.EnableDarkTitleBar(Handle);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Win32.EnableDarkTitleBar(Handle);
        }
    }

    // ---------- 关于弹窗 ----------
}
