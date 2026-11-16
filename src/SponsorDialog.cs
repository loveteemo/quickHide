// SponsorDialog.cs —— quickHide 工程拆分文件（v1.3.0 起由单文件拆分而来）
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
    class SponsorDialog : Form
    {
        public SponsorDialog()
        {
            Text = "支持 quickHide";
            Icon = Theme.AppIcon;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Bg;
            ForeColor = Theme.Fg;
            Font = new Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.Dpi;

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                BackColor = Theme.Bg,
                Padding = new Padding(14, 12, 14, 12),
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 顶部文案
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 两个收款码
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));  // 按钮行

            var tip = new Label
            {
                Text = "如果 quickHide 帮到了你，欢迎请作者喝杯咖啡 ~",
                AutoSize = true,
                ForeColor = Theme.FgDim,
                Margin = new Padding(2, 0, 2, 8),
            };

            var grid = new TableLayoutPanel
            {
                ColumnCount = 2,
                RowCount = 1,
                AutoSize = true,
                BackColor = Theme.Bg,
                Margin = new Padding(0),
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            grid.Controls.Add(MakeCard("微信支付", Color.FromArgb(0x2A, 0xAA, 0x5E), LoadEmbeddedImage("QuickHide.donate_wechat.png")), 0, 0);
            grid.Controls.Add(MakeCard("支付宝", Color.FromArgb(0x16, 0x77, 0xFF), LoadEmbeddedImage("QuickHide.donate_alipay.jpg")), 1, 0);

            var btnRow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Dock = DockStyle.Fill,
                BackColor = Theme.Bg,
                Margin = new Padding(0, 8, 0, 0),
            };
            var btnClose = Theme.MakeButton("关闭", false);
            btnClose.Click += (s, e) => Close();
            btnRow.Controls.Add(btnClose);

            root.Controls.Add(tip, 0, 0);
            root.Controls.Add(grid, 0, 1);
            root.Controls.Add(btnRow, 0, 2);
            Controls.Add(root);

            ClientSize = new Size(500, 465);
        }

        // 一列：标题 + 收款码
        static Control MakeCard(string title, Color color, Image img)
        {
            var col = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                AutoSize = true,
                BackColor = Theme.Bg,
                Margin = new Padding(4, 0, 4, 0),
            };
            col.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            col.RowStyles.Add(new RowStyle(SizeType.Absolute, 322));
            var cap = new Label
            {
                Text = title,
                AutoSize = true,
                ForeColor = color,
                Font = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold),
                Margin = new Padding(2, 2, 2, 4),
            };
            col.Controls.Add(cap, 0, 0);
            if (img != null)
            {
                var box = new PictureBox
                {
                    Size = new Size(228, 320),
                    SizeMode = PictureBoxSizeMode.Normal,
                    Image = FitToCanvas(img, 228, 320),
                    BackColor = Color.White,
                    Margin = new Padding(2, 0, 2, 0),
                };
                col.Controls.Add(box, 0, 1);
            }
            else
            {
                var miss = new Label
                {
                    Text = "未内置收款码图片",
                    Dock = DockStyle.Fill,
                    ForeColor = Theme.FgDim,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Margin = new Padding(2, 0, 2, 0),
                };
                col.Controls.Add(miss, 0, 1);
            }
            return col;
        }

        // 等比缩放到固定白底画布并居中：不依赖 Zoom 模式和布局换算，保证收款码完整显示
        static Image FitToCanvas(Image img, int cw, int ch)
        {
            double scale = Math.Min((double)cw / img.Width, (double)ch / img.Height);
            int w = Math.Max(1, (int)Math.Round(img.Width * scale));
            int h = Math.Max(1, (int)Math.Round(img.Height * scale));
            var canvas = new Bitmap(cw, ch);
            using (var g = Graphics.FromImage(canvas))
            {
                g.Clear(Color.White);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(img, (cw - w) / 2, (ch - h) / 2, w, h);
            }
            img.Dispose();
            return canvas;
        }

        // 从 exe 内嵌资源读图；GDI+ 要求流在图片生命周期内保持打开，先拷贝成独立副本再关流
        static Image LoadEmbeddedImage(string resName)
        {
            try
            {
                using (var s = typeof(Program).Assembly.GetManifestResourceStream(resName))
                {
                    if (s == null) return null;
                    using (var ms = new MemoryStream())
                    {
                        s.CopyTo(ms);
                        ms.Position = 0;
                        return new Bitmap(Image.FromStream(ms));
                    }
                }
            }
            catch { return null; }
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

    // ---------- 主页 ----------
}
