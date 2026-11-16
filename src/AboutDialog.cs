// AboutDialog.cs —— quickHide 工程拆分文件（v1.3.0 起由单文件拆分而来）
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
    class AboutDialog : Form
    {
        readonly Label lblUpdate;

        public AboutDialog(TrayContext owner)
        {
            Text = "关于 quickHide";
            Icon = Theme.AppIcon;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Bg;
            ForeColor = Theme.Fg;
            Font = new Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.Dpi;

            // 自适应纵向布局：内容自动撑开，底部按钮区固定留白，绝不贴边
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 8,
                BackColor = Theme.Bg,
                Padding = new Padding(26, 22, 26, 22),
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));  // 标题
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));  // 副标题
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));  // 功能列表
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F)); // 弹性空白 + 更新信息
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));  // 权限说明
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));  // 作者
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));  // 版本哈希
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62)); // 按钮行（固定安全区）

            // ---- 标题行（图标 + 名称）----
            var head = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0, 0, 0, 6),
                BackColor = Theme.Bg,
            };
            var pic = new PictureBox
            {
                Image = Theme.AppIcon.ToBitmap(),
                SizeMode = PictureBoxSizeMode.Normal,
                Size = new Size(32, 32),
                Margin = new Padding(0, 2, 12, 0),
            };
            var title = new Label
            {
                Text = "quickHide " + Program.Version,
                AutoSize = true,
                ForeColor = Color.White,
                Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold),
                Margin = new Padding(0, 4, 0, 0),
            };
            head.Controls.Add(pic);
            head.Controls.Add(title);

            // ---- 副标题 ----
            var intro = new Label
            {
                Text = "摸鱼 / 游戏必备：一键隐藏、一键还原任何窗口",
                AutoSize = true,
                ForeColor = Theme.FgDim,
                Margin = new Padding(2, 0, 0, 14),
            };

            // ---- 功能列表 ----
            var features = new Label
            {
                Text =
                    "· 一键隐藏指定进程的全部窗口：DNF、抖音、音乐播放器……随便填\r\n" +
                    "· 隐藏时自动静音目标进程（还原时恢复原音量），防外放暴露\r\n" +
                    "· 隐藏期间自动补藏新弹出的窗口、静音新出现的音频\r\n" +
                    "· 双击进程行即可按 25%～100% 缩放窗口，输入 100% 或点还原恢复原大小\r\n" +
                    "· 独立缩放热键：隐藏键只隐藏、缩放键只缩放，互不干扰\r\n" +
                    "· 退出或崩溃后重启，自动还原窗口，绝不\"丢\"窗口",
                AutoSize = true,
                ForeColor = Theme.Fg,
                Margin = new Padding(2, 0, 0, 10),
            };

            // ---- 更新信息（平时隐藏，占用中间弹性区域）----
            lblUpdate = new Label
            {
                Text = "",
                AutoSize = false,
                Dock = DockStyle.Fill,
                ForeColor = Theme.Warn,
                Visible = false,
                Margin = new Padding(2, 6, 0, 6),
            };

            // ---- 权限说明 ----
            var note = new Label
            {
                Text = "需要管理员权限运行：游戏反作弊会屏蔽普通权限进程的窗口操作。",
                AutoSize = true,
                ForeColor = Theme.FgDim,
                Margin = new Padding(2, 8, 0, 0),
            };

            // ---- 作者（与博客/发布详情对齐）----
            var author = new Label
            {
                Text = "作者：teemo",
                AutoSize = true,
                ForeColor = Theme.FgDim,
                Margin = new Padding(2, 4, 0, 0),
            };

            // ---- 版本哈希（与发布页公布的 SHA256 比对校验，防冒牌包）----
            var hash = new Label
            {
                Text = "SHA256 " + ComputeSelfSha256(),
                AutoSize = true,
                ForeColor = Theme.FgDim,
                Font = new Font("Consolas", 8F),
                Margin = new Padding(2, 2, 0, 0),
            };

            // ---- 按钮行（右对齐，与底边保持安全距离）----
            var btnRow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Dock = DockStyle.Fill,
                BackColor = Theme.Bg,
                Margin = new Padding(0, 10, 0, 0),
                Padding = new Padding(0),
            };
            var btnClose = Theme.MakeButton("关闭", false);
            btnClose.Click += (s, e) => Close();
            var btnOpenPage = Theme.MakeButton("打开发布页", false);
            btnOpenPage.Visible = false;
            btnOpenPage.Click += (s, e) =>
            {
                try { Process.Start(Program.ReleasePageUrl); } catch { }
            };
            var btnCheck = Theme.MakeButton("检查更新", false);
            btnCheck.Click += (s, e) => CheckUpdate(btnCheck, btnOpenPage, owner);
            var btnFeedback = Theme.MakeButton("反馈", false);
            btnFeedback.Click += (s, e) =>
            {
                try { Process.Start(Program.FeedbackUrl); }
                catch { MessageBox.Show(this, "请手动打开反馈地址：" + Program.FeedbackUrl, "quickHide", MessageBoxButtons.OK, MessageBoxIcon.Information); }
            };
            var btnDonate = Theme.MakeButton("支持", true);
            btnDonate.Click += (s, e) =>
            {
                using (var dlg = new SponsorDialog())
                {
                    dlg.StartPosition = FormStartPosition.CenterParent;
                    dlg.ShowDialog(this);
                }
            };
            btnRow.Controls.Add(btnClose);
            btnRow.Controls.Add(btnOpenPage);
            btnRow.Controls.Add(btnCheck);
            btnRow.Controls.Add(btnDonate);
            btnRow.Controls.Add(btnFeedback);

            root.Controls.Add(head, 0, 0);
            root.Controls.Add(intro, 0, 1);
            root.Controls.Add(features, 0, 2);
            root.Controls.Add(lblUpdate, 0, 3);
            root.Controls.Add(note, 0, 4);
            root.Controls.Add(author, 0, 5);
            root.Controls.Add(hash, 0, 6);
            root.Controls.Add(btnRow, 0, 7);

            Controls.Add(root);

            // 先按内容算一个合适的初始高度（宽度固定 520，高度自适应 + 底部留白）
            int contentH = head.PreferredSize.Height + 6
                         + intro.PreferredSize.Height + 14
                         + features.PreferredSize.Height + 10
                         + note.PreferredSize.Height + 8
                         + author.PreferredSize.Height + 4
                         + hash.PreferredSize.Height + 2
                         + 62 + 44; // 按钮区 + 上下 padding
            ClientSize = new Size(520, Math.Max(300, contentH));
        }

        // 本程序 exe 的 SHA256（与发布页公布的哈希比对，校验是否官方原包）
        // 注意用程序集自身路径而非 Application.ExecutablePath：后者在某些宿主（如反射调试）下会指向宿主进程
        static string ComputeSelfSha256()
        {
            try
            {
                string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                if (string.IsNullOrEmpty(exePath)) return "（无法计算）";
                using (var sha = System.Security.Cryptography.SHA256.Create())
                using (var fs = File.OpenRead(exePath))
                {
                    var sb = new StringBuilder();
                    foreach (byte b in sha.ComputeHash(fs)) sb.Append(b.ToString("x2"));
                    return sb.ToString();
                }
            }
            catch { return "（无法计算）"; }
        }

        // 检查更新（异步，不阻塞界面）
        void CheckUpdate(Button btnCheck, Button btnOpenPage, TrayContext owner)
        {
            btnCheck.Enabled = false;
            btnCheck.Text = "检查中...";
            lblUpdate.Visible = false;
            btnOpenPage.Visible = false;

            System.Threading.Tasks.Task.Factory.StartNew(() =>
            {
                string latestDate, latestNotes, failMsg;
                string latestVer = Program.FetchLatestVersion(out latestDate, out latestNotes, out failMsg);
                string officialSha = (Program.latestSha ?? "").Trim().ToLowerInvariant();
                string localSha = (ComputeSelfSha256() ?? "").ToLowerInvariant();

                try
                {
                    Invoke((Action)(() =>
                    {
                        btnCheck.Enabled = true;
                        btnCheck.Text = "检查更新";
                        if (latestVer == null)
                        {
                            lblUpdate.ForeColor = Theme.Warn;
                            lblUpdate.Text = failMsg ?? "后台暂无发布版本";
                            lblUpdate.Visible = true;
                            return;
                        }
                        if (Program.CompareVersion(latestVer, Program.Version) > 0)
                        {
                            lblUpdate.ForeColor = Theme.Warn;
                            string notesText = (latestNotes ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n");
                            string shaText = officialSha.Length == 64
                                ? "\r\n官方安装包 SHA256：" + officialSha
                                : "";
                            lblUpdate.Text = "发现新版本 v" + latestVer + "（" + latestDate + "）\r\n" + notesText + shaText;
                            lblUpdate.Visible = true;
                            btnOpenPage.Visible = true;
                            // 内容变多时把窗口撑高，避免文字被裁切（只增不减）
                            int need = lblUpdate.GetPreferredSize(new Size(lblUpdate.Width, 0)).Height + 12;
                            if (need > lblUpdate.Height)
                                ClientSize = new Size(ClientSize.Width, ClientSize.Height + (need - lblUpdate.Height));
                        }
                        else
                        {
                            lblUpdate.ForeColor = Theme.Ok;
                            string shaText = "";
                            if (officialSha.Length == 64)
                            {
                                if (localSha == officialSha)
                                    shaText = "\r\n官方 SHA256 校验：✓ 与当前程序一致（官方原包）";
                                else
                                    shaText = "\r\n⚠ 官方 SHA256 校验：与当前程序不一致，请从官方渠道重新下载！";
                            }
                            lblUpdate.Text = "当前已是最新版本（v" + Program.Version + "）" + shaText;
                            lblUpdate.Visible = true;
                            btnOpenPage.Visible = false;
                        }
                    }));
                }
                catch { }
            });
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

    // ---------- 支持弹窗 ----------
}
