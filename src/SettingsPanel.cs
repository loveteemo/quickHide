// SettingsPanel.cs —— quickHide 工程拆分文件（v1.3.0 起由单文件拆分而来）
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
    class SettingsPanel : UserControl
    {
        readonly TrayContext owner;
        readonly HotkeyBox hkHide, hkShow, hkScale;
        readonly NumericUpDown numWatch;
        readonly ToggleSwitch swRestore, swAutorun, swHideTaskbar, swShowBg, swMute, swSuspend, swListMode;
        readonly ToolTip tipHelp = new ToolTip();
        // 列表模式切换并保存后触发（主页刷新列表用）
        public Action ModeChanged;
        readonly System.Windows.Forms.Timer autoSave = new System.Windows.Forms.Timer { Interval = 700 };
        // ReloadFromCfg 回填控件时抑制自动保存
        bool loading;

        public SettingsPanel(TrayContext owner)
        {
            this.owner = owner;
            BackColor = Theme.Bg;
            ForeColor = Theme.Fg;
            Font = new Font("Microsoft YaHei UI", 9F);

            Controls.Add(MkLabel("隐藏热键", 28, 38));
            hkHide = MkHotkey(128, 34);
            Controls.Add(hkHide);
            Controls.Add(MkLabel("还原热键", 288, 38));
            hkShow = MkHotkey(388, 34);
            Controls.Add(hkShow);
            Controls.Add(MkLabel("缩放热键", 548, 38));
            hkScale = MkHotkey(638, 34);
            Controls.Add(hkScale);

            Controls.Add(MkLabel("监视间隔(ms)", 28, 78));
            numWatch = new NumericUpDown
            {
                Location = new Point(128, 74),
                Minimum = 100,
                Maximum = 5000,
                Increment = 100,
                Width = 100,
                BackColor = Theme.Panel,
                ForeColor = Theme.Fg,
                BorderStyle = BorderStyle.FixedSingle,
            };
            Controls.Add(numWatch);

            // 开关区（单列布局：每行 文字 + 开关 + ? 帮助）
            swRestore = AddSwitchRow("退出时自动还原被隐藏的窗口",
                "退出 quickHide 时自动还原所有被隐藏的窗口，防止忘记还原", 106);
            swAutorun = AddSwitchRow("开机自启",
                "登录 Windows 后自动以管理员身份启动 quickHide（计划任务实现，不弹 UAC）", 136);
            swHideTaskbar = AddSwitchRow("缩放时隐藏任务栏按钮",
                "窗口缩放的同时隐藏它的任务栏按钮，还原时恢复", 166);
            swMute = AddSwitchRow("隐藏时自动静音目标进程",
                "隐藏目标进程时自动将其静音，还原时恢复原音量（个别系统音频组件异常时自动降级为暂停）", 196);
            swSuspend = AddSwitchRow("隐藏时暂停目标进程",
                "隐藏目标进程时挂起它：声音视频彻底停止、不占 CPU，还原时恢复运行（优先级高于静音）", 226);
            swShowBg = AddSwitchRow("进程列表同时显示后台/托盘进程",
                "列表同时显示没有可见窗口的后台/托盘程序（默认只显示有窗口的软件）", 256);
            swListMode = AddSwitchRow("窗口模式（可分别设置每个窗口）",
                "开启后列表按窗口逐个显示（如 Chrome 的多个窗口可分别隐藏/缩放）；关闭则按进程合并显示", 286);

            // 修改后自动保存（防抖）：开关/热键/监视间隔任一变化，0.7 秒后写盘并立即生效
            swRestore.CheckedChanged += (s, e) => QueueAutoSave();
            swAutorun.CheckedChanged += (s, e) => QueueAutoSave();
            swHideTaskbar.CheckedChanged += (s, e) => QueueAutoSave();
            swShowBg.CheckedChanged += (s, e) => QueueAutoSave();
            swListMode.CheckedChanged += (s, e) =>
            {
                // 立即写入内存配置（列表刷新需要读到新模式），持久化由防抖自动保存完成
                owner.cfg.ListMode = swListMode.Checked ? "window" : "process";
                QueueAutoSave();
                if (ModeChanged != null) ModeChanged();
            };
            swMute.CheckedChanged += (s, e) => QueueAutoSave();
            swSuspend.CheckedChanged += (s, e) => QueueAutoSave();
            numWatch.ValueChanged += (s, e) => QueueAutoSave();
            hkHide.ComboChanged += (s, e) => QueueAutoSave();
            hkShow.ComboChanged += (s, e) => QueueAutoSave();
            hkScale.ComboChanged += (s, e) => QueueAutoSave();
            // 点击热键框录入期间挂起全局热键：按旧热键不再触发隐藏/还原，而是正常录入
            hkHide.RecordingChanged += (s, e) => OnRecordingChanged();
            hkShow.RecordingChanged += (s, e) => OnRecordingChanged();
            hkScale.RecordingChanged += (s, e) => OnRecordingChanged();
            autoSave.Tick += (s, e) => { autoSave.Stop(); SaveNow(); };

            ReloadFromCfg();
        }

        // 任意热键框获得焦点 → 挂起；全部失焦 → 恢复旧热键并立即保存换绑新热键
        void OnRecordingChanged()
        {
            if (AnyRecording()) { owner.SuspendHotkeys(); return; }
            // LostFocus 先于新框 GotFocus 触发：延迟到焦点尘埃落定再处理，避免“恢复又立刻挂起”抖动
            BeginInvoke((Action)(delegate
            {
                if (IsDisposed || AnyRecording()) return;
                autoSave.Stop();       // 取消防抖：离开输入框即刻生效，不再等 0.7 秒
                owner.ResumeHotkeys(); // 先恢复旧热键兜底，保存失败（校验/占用）时仍有热键可用
                SaveNow();
            }));
        }

        bool AnyRecording()
        {
            return hkHide.Recording || hkShow.Recording || hkScale.Recording;
        }

        // 主窗体失活（点击本程序以外的任何窗口）时统一结束录入：
        // LostFocus 在个别失活路径上不可靠，Deactivate 兜底保证取消高亮并提交
        public void EndAllRecording()
        {
            hkHide.EndRecording();
            hkShow.EndRecording();
            hkScale.EndRecording();
        }

        // 关窗前补交仍未失焦提交的热键改动（点 X 关窗不触发 LostFocus），并立即保存
        public void FlushPendingEdits()
        {
            if (IsDisposed) return;
            bool c1 = hkHide.CommitPending();
            bool c2 = hkShow.CommitPending();
            bool c3 = hkScale.CommitPending();
            if (c1 || c2 || c3) SaveNow();
        }

        // 关窗/销毁时若仍在录入态，确保全局热键恢复
        protected override void Dispose(bool disposing)
        {
            if (disposing) owner.ResumeHotkeys();
            base.Dispose(disposing);
        }

        void QueueAutoSave()
        {
            if (loading) return;
            autoSave.Stop();
            autoSave.Start();
        }

        // 自动保存：写盘 + 重载生效；热键非法时弹窗并回滚显示
        void SaveNow()
        {
            if (loading) return;
            // 正在录入热键时不落盘：交换隐藏/还原热键的中间态（两框相同）不应触发校验回滚，
            // 推迟到录入结束（失焦提交）后再保存
            if (AnyRecording()) { QueueAutoSave(); return; }
            string err = ValidateHotkeys();
            if (err != null)
            {
                MessageBox.Show(this, err, "quickHide",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                ReloadFromCfg(); // 恢复为实际生效的配置显示
                return;
            }
            WriteToCfg();
            try
            {
                Config.WriteCurrent(owner.cfg, owner.cfg.Path);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "保存失败：" + ex.Message, "quickHide",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            owner.SyncCfgMTime();
            bool ok = owner.ApplyReload(null);
            if (!ok) ReloadFromCfg(); // 热键注册失败已回退，控件对齐实际生效值
        }

        Label MkLabel(string text, int x, int y)
        {
            return new Label { Text = text, AutoSize = true, ForeColor = Theme.FgDim, Location = new Point(x, y + 4) };
        }

        HotkeyBox MkHotkey(int x, int y)
        {
            var hk = new HotkeyBox { Location = new Point(x, y) };
            return hk;
        }

        // 开关行：文字标签 + 滑动开关 + ? 帮助图标（悬浮显示详情）
        ToggleSwitch AddSwitchRow(string text, string tip, int y)
        {
            Controls.Add(MkLabel(text, 28, y));
            var sw = new ToggleSwitch { Location = new Point(280, y + 3) };
            Controls.Add(sw);
            var q = new Label
            {
                Text = "?",
                AutoSize = true,
                ForeColor = Theme.Accent,
                Cursor = Cursors.Hand,
                Location = new Point(322, y + 6),
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            };
            tipHelp.SetToolTip(q, tip);
            Controls.Add(q);
            return sw;
        }

        public void ReloadFromCfg()
        {
            loading = true;
            try
            {
                hkHide.Combo = owner.cfg.HideHotkey;
                hkShow.Combo = owner.cfg.ShowHotkey;
                hkScale.Combo = owner.cfg.ScaleHotkey;
                numWatch.Value = owner.cfg.WatchIntervalMs;
                swRestore.Checked = owner.cfg.RestoreOnExit;
                swAutorun.Checked = TrayContext.AutorunEnabled();
                swHideTaskbar.Checked = owner.cfg.HideTaskbarWhenScaled;
                swShowBg.Checked = owner.cfg.ShowBackgroundProcs;
            swListMode.Checked = (owner.cfg.ListMode == "window");
                swMute.Checked = owner.cfg.MuteOnHide;
                swSuspend.Checked = owner.cfg.SuspendOnHide;
            }
            finally
            {
                loading = false;
            }
        }

        // 校验；返回错误信息或 null
        public string ValidateHotkeys()
        {
            string h = hkHide.Combo, s = hkShow.Combo, c = hkScale.Combo;
            if (string.IsNullOrEmpty(h) || string.IsNullOrEmpty(s) || string.IsNullOrEmpty(c))
                return "隐藏、还原、缩放热键都不能为空";
            if (h == s) return "隐藏和还原的热键不能相同";
            if (c == h || c == s) return "缩放热键不能和隐藏/还原热键相同";
            return null;
        }

        public void WriteToCfg()
        {
            owner.cfg.HideHotkey = hkHide.Combo;
            owner.cfg.ShowHotkey = hkShow.Combo;
            owner.cfg.ScaleHotkey = hkScale.Combo;
            owner.cfg.WatchIntervalMs = (int)numWatch.Value;
            owner.cfg.RestoreOnExit = swRestore.Checked;
            owner.cfg.HideTaskbarWhenScaled = swHideTaskbar.Checked;
            owner.cfg.MuteOnHide = swMute.Checked;
            owner.cfg.SuspendOnHide = swSuspend.Checked;
            owner.cfg.ShowBackgroundProcs = swShowBg.Checked;
            owner.cfg.ListMode = swListMode.Checked ? "window" : "process";
            TrayContext.SetAutorunRegistry(swAutorun.Checked);
        }
    }
}
