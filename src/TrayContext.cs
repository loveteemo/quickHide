// TrayContext.cs —— quickHide 工程拆分文件（v1.3.0 起由单文件拆分而来）
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
// ---------- 托盘宿主 ----------

public class TrayContext : ApplicationContext
{
    internal readonly Config cfg;
    internal readonly HideEngine engine;
    readonly HotkeyWindow hotwin;
    readonly NotifyIcon tray;
    readonly System.Windows.Forms.Timer watcher;
    ToolStripMenuItem miHide, miShow, miAutorun;
    HomeForm home;
    // 热键槽位当前实际生效的组合（注册失败回退时用）
    readonly Dictionary<int, string> activeHotkeys = new Dictionary<int, string>();
    bool hotkeysSuspended; // 设置页录入热键期间挂起全部全局热键
    DateTime cfgMTime;
    string pendingUpdateVersion; // 气泡提示的版本号，点击气泡直接打开关于

    const int HOTKEY_ID_HIDE = 1;
    const int HOTKEY_ID_SHOW = 2;
    const int HOTKEY_ID_SCALE = 3;

    public TrayContext(Config cfg, HideEngine engine)
    {
        this.cfg = cfg;
        this.engine = engine;
        cfgMTime = cfg.MTime;

        hotwin = new HotkeyWindow();
        hotwin.Show();
        hotwin.Hide();
        hotwin.HotkeyPressed += id =>
        {
            if (hotkeysSuspended) return; // 录入态保险（热键已注销，正常不会走到这里）
            if (id == HOTKEY_ID_HIDE) DoHide();
            else if (id == HOTKEY_ID_SHOW) DoShow();
            else if (id == HOTKEY_ID_SCALE) DoScale();
        };
        RegisterHotkeys();

        engine.ActionDone += (count, isHide) =>
        {
            string msg = isHide
                ? string.Format("已隐藏 {0} 个窗口（{1}）", count, string.Join(", ", cfg.Processes))
                : string.Format("已还原 {0} 个窗口", count);
            ShowTip(msg, isHide);
            UpdateMenuState();
        };

        tray = new NotifyIcon
        {
            Icon = Theme.AppIcon,
            Text = "quickHide - 未隐藏",
            ContextMenuStrip = BuildMenu(),
            Visible = true,
        };
        // 托盘左键 = 打开主页（Click 事件左右键都会触发，必须用 MouseClick 过滤，否则右键会抢掉菜单）
        tray.MouseClick += (s, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowHome();
        };
        tray.BalloonTipClicked += (s, e) =>
        {
            if (string.IsNullOrEmpty(pendingUpdateVersion)) return;
            pendingUpdateVersion = null;
            ShowAbout();
        };
        engine.StateChanged += UpdateMenuState;

        // 上次异常退出留下的隐藏状态 → 先还原
        if (engine.HiddenCount > 0) DoShow();

        watcher = new System.Windows.Forms.Timer { Interval = Math.Max(100, cfg.WatchIntervalMs) };
        watcher.Tick += (s, e) =>
        {
            engine.WatchStep();
            // 配置文件被修改后自动重载（无需手动点“重载配置”）
            try
            {
                if (File.GetLastWriteTimeUtc(cfg.Path) != cfgMTime)
                    ApplyReload("检测到配置修改，已自动重载");
            }
            catch { }
        };
        watcher.Start();

        StartUpdateCheck();

        // 启动时直接显示主页（而不是只在托盘）
        ShowHome();
    }

    // 启动后静默检查更新：旧版本才提示，网络失败不打扰
    void StartUpdateCheck()
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            Thread.Sleep(8000);
            string date, notes, error;
            string latest = Program.FetchLatestVersion(out date, out notes, out error);
            if (latest == null || Program.CompareVersion(latest, Program.Version) <= 0) return;
            try
            {
                hotwin.BeginInvoke((Action)(() => NotifyUpdate(latest)));
            }
            catch { }
        });
    }

    void NotifyUpdate(string ver)
    {
        ShowTip("发现新版本 v" + ver + "，单击气泡查看更新内容", true);
        pendingUpdateVersion = ver;
    }

    void ShowAbout()
    {
        ShowHome();
        using (var d = new AboutDialog(this)) d.ShowDialog(home);
    }

    ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors());
        menu.ShowImageMargin = false;
        menu.Font = new Font("Microsoft YaHei UI", 9F);

        AddMenuItem(menu, "主页... (&M)", (s, e) => ShowHome());
        miHide = AddMenuItem(menu, "一键隐藏 (&H)", (s, e) => DoHide());
        miShow = AddMenuItem(menu, "一键还原 (&S)", (s, e) => DoShow());
        miShow.Enabled = engine.HiddenCount > 0;
        AddMenuItem(menu, "-", null);
        miAutorun = AddMenuItem(menu, "开机自启", AutorunToggled);
        miAutorun.CheckOnClick = true;
        miAutorun.Checked = AutorunEnabled();
        AddMenuItem(menu, "关于 quickHide", (s, e) => ShowAbout());
        AddMenuItem(menu, "-", null);
        AddMenuItem(menu, "退出", (s, e) => ExitApp());
        return menu;
    }

    ToolStripMenuItem AddMenuItem(ContextMenuStrip menu, string text, EventHandler onClick)
    {
        if (text == "-") { menu.Items.Add(new ToolStripSeparator()); return null; }
        var it = new ToolStripMenuItem(text);
        if (onClick != null) it.Click += onClick;
        it.ForeColor = Theme.Fg;
        menu.Items.Add(it);
        return it;
    }

    void UpdateMenuState()
    {
        try
        {
            bool has = engine.HiddenCount > 0;
            miShow.Enabled = has;
            tray.Text = has
                ? string.Format("quickHide - 已隐藏 {0} 个窗口", engine.HiddenCount)
                : "quickHide - 未隐藏";
        }
        catch { } // 工作线程跨线程更新控件时静默（balloon 提示已在 ShowTip 中完成）
    }

    void DoHide()
    {
        SyncCheckedProcesses();
        // 窗口模式：隐藏勾选的目标窗口（Chrome 多开可只藏其一）；进程模式：按清单 HideAll
        var winTargets = (home != null && !home.IsDisposed) ? home.CollectWindowTargets() : null;
        System.Threading.Tasks.Task.Factory.StartNew(() =>
        {
            try
            {
                int n;
                if (winTargets != null && winTargets.Count > 0)
                {
                    n = 0;
                    foreach (IntPtr h in winTargets) n += engine.HideWindow(h);
                }
                else
                {
                    n = engine.HideAll();
                }
                if (n == 0 && engine.HiddenCount == 0)
                    ShowTip("未找到目标进程的可见窗口", false);
            }
            catch (Exception ex) { Program.LogUiError(ex); }
        });
    }

    void DoShow()
    {
        SyncCheckedProcesses();
        System.Threading.Tasks.Task.Factory.StartNew(() =>
        {
            try { engine.RestoreAll(); }
            catch (Exception ex) { Program.LogUiError(ex); }
        });
    }

    void DoScale()
    {
        SyncCheckedProcesses();
        int n = engine.ApplyConfiguredScales();
        if (n > 0) ShowTip(string.Format("已缩放 {0} 个窗口", n), false);
        else if (cfg.Scale.Count == 0) ShowTip("还没有配置缩放比例（双击进程行设置）", false);
        else ShowTip("没有找到可缩放的可见窗口", false);
    }

    // 主页开着时，把进程面板的当前勾选同步进内存配置：勾选立即生效，无需先点"保存并应用"
    // （是否持久化仍由"保存并应用"决定）
    void SyncCheckedProcesses()
    {
        try
        {
            if (home == null || home.IsDisposed) return;
            var list = home.CollectCheckedProcesses();
            if (list != null && list.Count >= 0) cfg.Processes = list;
        }
        catch { }
    }

    void ShowHome()
    {
        if (home == null || home.IsDisposed) home = new HomeForm(this);
        home.Show();
        home.Activate();
    }

    // 让 mtime 监视器忽略刚才那次程序自己写的 ini（避免无意义的重载气泡）
    internal void SyncCfgMTime()
    {
        try { cfgMTime = File.GetLastWriteTimeUtc(cfg.Path); } catch { }
    }

    internal void ShowTip(string text, bool info)
    {
        pendingUpdateVersion = null; // 普通提示会顶掉气泡，点击不再算作更新提醒
        if (tray == null) return; // 启动早期托盘尚未创建
        try
        {
            tray.BalloonTipTitle = "quickHide";
            tray.BalloonTipText = text;
            tray.ShowBalloonTip(1500);
        }
        catch { }
    }

    // 设置页进入热键录入时调用：注销全部全局热键（RegisterHotKey 会全局吞键，
    // 不注销的话按旧热键会直接触发隐藏/还原，输入框根本收不到按键）
    internal void SuspendHotkeys()
    {
        if (hotkeysSuspended || hotwin == null || hotwin.Handle == IntPtr.Zero) return;
        hotkeysSuspended = true;
        Win32.UnregisterHotKey(hotwin.Handle, HOTKEY_ID_HIDE);
        Win32.UnregisterHotKey(hotwin.Handle, HOTKEY_ID_SHOW);
        Win32.UnregisterHotKey(hotwin.Handle, HOTKEY_ID_SCALE);
    }

    // 录入结束（失焦/关窗）后恢复注册当前生效的组合；
    // 录入后提交的新热键由 SaveNow → ApplyReload 再换绑
    internal void ResumeHotkeys()
    {
        if (!hotkeysSuspended) return;
        hotkeysSuspended = false;
        try
        {
            if (hotwin.IsDisposed || hotwin.Handle == IntPtr.Zero) return; // 退出中，进程结束系统自动回收热键
        }
        catch { return; }
        RegisterHotkeys(true); // 挂起时已全部注销，组合没变也要重新注册回来
    }

    // 三个槽位的目标组合与当前生效组合是否完全一致（一致则无需重注册）
    bool AllSlotsMatch()
    {
        lock (activeHotkeys)
        {
            string a1, a2, a3;
            return activeHotkeys.TryGetValue(HOTKEY_ID_HIDE, out a1) && a1 == cfg.HideHotkey
                && activeHotkeys.TryGetValue(HOTKEY_ID_SHOW, out a2) && a2 == cfg.ShowHotkey
                && activeHotkeys.TryGetValue(HOTKEY_ID_SCALE, out a3) && a3 == cfg.ScaleHotkey;
        }
    }

    bool RegisterHotkeys(bool force = false)
    {
        try
        {
        // 挂起期间保持注销（别在录入到一半时把热键注册回去吞按键），恢复时统一重注册
        if (!force && hotkeysSuspended) return true;
        // 目标组合与当前生效组合完全一致 → 无需动注册
        if (!force && AllSlotsMatch()) return true;

        // 先整体注销再统一注册：交换两个热键（如 F6↔F7）时，同一组合不能同时挂在两个槽位上，
        // 逐槽位“只腾自己”会让新键撞上另一槽位还占着的旧组合 → 注册失败触发回退，
        // 把整个交换打回原形（ini 显示新键、实际生效的还是旧键）
        var prevActive = new Dictionary<int, string>();
        lock (activeHotkeys)
        {
            foreach (var kv in activeHotkeys) prevActive[kv.Key] = kv.Value;
            activeHotkeys.Clear();
        }
        if (hotwin.Handle != IntPtr.Zero)
        {
            Win32.UnregisterHotKey(hotwin.Handle, HOTKEY_ID_HIDE);
            Win32.UnregisterHotKey(hotwin.Handle, HOTKEY_ID_SHOW);
            Win32.UnregisterHotKey(hotwin.Handle, HOTKEY_ID_SCALE);
        }

        // 先把三个槽位都尝试一遍再统一提示，避免一个冲突弹窗中断后续热键注册
        var fails = new List<string>();
        RegisterOne(HOTKEY_ID_HIDE, "隐藏热键", cfg.HideHotkey, v => cfg.HideHotkey = v, fails, prevActive);
        RegisterOne(HOTKEY_ID_SHOW, "还原热键", cfg.ShowHotkey, v => cfg.ShowHotkey = v, fails, prevActive);
        RegisterOne(HOTKEY_ID_SCALE, "缩放热键", cfg.ScaleHotkey, v => cfg.ScaleHotkey = v, fails, prevActive);
        if (fails.Count == 0) return true;

        string msg = string.Join("\r\n\r\n", fails.ToArray());
        Program.LogText(msg);
        MessageBox.Show(msg, "quickHide 热键冲突", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false; // 让调用方把回退后的 cfg 写回盘/刷新控件，保持“显示=实际生效”一致
        }
        catch (Exception ex)
        {
            Program.LogText("RegisterHotkeys 异常：" + ex.Message);
            return false;
        }
    }

    // 注册热键；被占用时保留上一次生效的组合，绝不把旧键也弄丢
    bool RegisterOne(int id, string slot, string combo, Action<string> keepCfg, List<string> fails, Dictionary<int, string> prevActive)
    {
        string active;
        prevActive.TryGetValue(id, out active);

        uint mods, vk;
        if (!ParseHotkey(combo, out mods, out vk))
        {
            HotkeyFail(id, slot, combo, false, active, keepCfg, fails);
            return false;
        }
        if (Win32.RegisterHotKey(hotwin.Handle, id, mods | 0x4000u /*MOD_NOREPEAT*/, vk))
        {
            lock (activeHotkeys) activeHotkeys[id] = combo;
            return true;
        }
        HotkeyFail(id, slot, combo, true, active, keepCfg, fails);
        return false;
    }

    void HotkeyFail(int id, string slot, string combo, bool occupied, string prev, Action<string> keepCfg, List<string> fails)
    {
        bool restored = false;
        if (!string.IsNullOrEmpty(prev))
        {
            uint pm, pv;
            if (ParseHotkey(prev, out pm, out pv) && Win32.RegisterHotKey(hotwin.Handle, id, pm | 0x4000u, pv))
            {
                lock (activeHotkeys) activeHotkeys[id] = prev;
                keepCfg(prev); // 配置同步回仍生效的旧热键
                restored = true;
            }
        }
        if (!restored) lock (activeHotkeys) activeHotkeys.Remove(id);

        string msg;
        if (occupied)
            msg = "新热键已被占用，请重新设置。\r\n\r\n" + slot + "：" + combo + "\r\n占用程序：" + DetectHotkeyOwner();
        else
            msg = "热键格式无效，请重新设置。\r\n\r\n" + slot + "：" + combo + "\r\n请使用 Ctrl/Alt/Shift/Win + A-Z、0-9 或 F1-F24。";

        if (restored)
            msg += prev == combo ? "\r\n\r\n原热键仍可用：" + prev : "\r\n\r\n已保留原热键：" + prev;
        else if (!string.IsNullOrEmpty(prev))
            msg += "\r\n\r\n原热键 " + prev + " 也已无法恢复，请重新设置。";

        fails.Add(msg);
    }

    // Windows 公共 API 查不到外部热键的真正拥有者，只能用已知程序进程名做启发式识别；
    // 识别不到时明确回报“其他程序”，不臆测具体软件。
    static string DetectHotkeyOwner()
    {
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                string n;
                try { n = p.ProcessName.ToLowerInvariant().Replace(" ", "").Replace("-", "").Replace("_", ""); }
                catch { continue; }
                if (n.Contains("taskbarhide") || n == "taskbar") return "Taskbar Hide";
                if (n.Contains("autohotkey")) return "AutoHotkey";
                if (n.Contains("powertoys")) return "PowerToys";
            }
        }
        catch { }
        return "其他程序（Windows 无法查询具体软件）";
    }

    void UnregisterHotkeys()
    {
        if (hotwin.Handle != IntPtr.Zero)
        {
            Win32.UnregisterHotKey(hotwin.Handle, HOTKEY_ID_HIDE);
            Win32.UnregisterHotKey(hotwin.Handle, HOTKEY_ID_SHOW);
            Win32.UnregisterHotKey(hotwin.Handle, HOTKEY_ID_SCALE);
        }
    }

    void ReloadConfig()
    {
        ApplyReload("配置已重载");
    }

    // 返回热键是否全部注册成功；失败时旧热键已回退，弹窗也已提示
    internal bool ApplyReload(string tip)
    {
        bool prevHideTaskbar = cfg.HideTaskbarWhenScaled;
        Config newCfg = Config.Load(cfg.Path);
        cfg.Processes = newCfg.Processes;
        cfg.ExtraClasses = newCfg.ExtraClasses;
        cfg.HideHotkey = newCfg.HideHotkey;
        cfg.ShowHotkey = newCfg.ShowHotkey;
        cfg.ScaleHotkey = newCfg.ScaleHotkey;
        cfg.Scale = newCfg.Scale;
        cfg.WatchIntervalMs = newCfg.WatchIntervalMs;
        cfg.RestoreOnExit = newCfg.RestoreOnExit;
        cfg.HideTaskbarWhenScaled = newCfg.HideTaskbarWhenScaled;
        watcher.Interval = Math.Max(100, cfg.WatchIntervalMs);
        cfgMTime = newCfg.MTime;

        bool hotkeysOk = RegisterHotkeys();
        if (!hotkeysOk)
        {
            // 被占用的新热键已回退成旧键，把旧键写回文件，避免下次启动继续撞墙
            try
            {
                Config.WriteCurrent(cfg, cfg.Path);
                cfgMTime = File.GetLastWriteTimeUtc(cfg.Path);
            }
            catch { }
        }
        if (prevHideTaskbar != cfg.HideTaskbarWhenScaled)
            engine.ApplyTaskbarOption();
        if (hotkeysOk && tip != null) ShowTip(tip, false); // 失败提示已单独弹窗
        UpdateMenuState();
        return hotkeysOk;
    }

    // 双击进程行确认后立即缩放，并把比例写回配置
    internal int ApplyScaleImmediate(string procName, int pct)
    {
        string n = Config.NormProc(procName);
        pct = HideEngine.ClampScale(pct);
        if (pct >= 100) cfg.Scale.Remove(n);
        else cfg.Scale[n] = pct;
        try
        {
            Config.WriteCurrent(cfg, cfg.Path);
            cfgMTime = File.GetLastWriteTimeUtc(cfg.Path);
        }
        catch { }
        return engine.ScaleProcess(n, pct);
    }

    static bool ParseHotkey(string combo, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        if (string.IsNullOrEmpty(combo)) return false;
        foreach (string raw in combo.Split('+'))
        {
            string t = raw.Trim().ToLowerInvariant();
            if (t.Length == 0) continue;
            switch (t)
            {
                case "ctrl": case "control": mods |= 0x2; continue;
                case "alt": mods |= 0x1; continue;
                case "shift": mods |= 0x4; continue;
                case "win": case "windows": mods |= 0x8; continue;
            }
            if (t.Length == 1 && char.IsLetterOrDigit(t[0]))
            {
                // 'A'-'Z'、'0'-'9' 的 VK 码与 ASCII 码相同
                vk = (uint)char.ToUpperInvariant(t[0]);
                continue;
            }
            if (t[0] == 'f' && t.Length <= 3)
            {
                int n;
                if (int.TryParse(t.Substring(1), out n) && n >= 1 && n <= 24) { vk = (uint)(0x70 + n - 1); continue; }
            }
            return false; // 未识别的键
        }
        return vk != 0;
    }

    internal const string AutorunTaskName = "QuickHide";

    // requireAdministrator 程序放进注册表 Run 键会被登录时静默跳过，
    // 计划任务（ONLOGON + HIGHEST）才是提权程序开机自启的可靠方案
    internal static bool AutorunEnabled()
    {
        string output;
        if (RunSchtasks("/Query /TN \"" + AutorunTaskName + "\"", out output)) return true;
        // 兼容旧版注册表方式
        using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            return k != null && k.GetValue("QuickHide") != null;
    }

    internal static void SetAutorunRegistry(bool on)
    {
        if (on)
        {
            string output;
            RunSchtasks("/Create /F /TN \"" + AutorunTaskName + "\" /TR \"" + Application.ExecutablePath + "\" /SC ONLOGON /RL HIGHEST", out output);
            RemoveLegacyRunKey(); // 迁移：清理旧版注册表自启，避免重复启动
        }
        else
        {
            string output;
            RunSchtasks("/Delete /F /TN \"" + AutorunTaskName + "\"", out output);
            RemoveLegacyRunKey();
        }
    }

    static bool RunSchtasks(string args, out string output)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using (var p = Process.Start(psi))
            {
                output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit(10000);
                return p.ExitCode == 0;
            }
        }
        catch { output = ""; return false; }
    }

    static void RemoveLegacyRunKey()
    {
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                k.DeleteValue("QuickHide", false);
        }
        catch { }
    }

    void AutorunToggled(object s, EventArgs e)
    {
        SetAutorunRegistry(miAutorun.Checked);
        ShowTip(miAutorun.Checked ? "已开启开机自启" : "已关闭开机自启", false);
    }

    void ExitApp()
    {
        if (cfg.RestoreOnExit && engine.HiddenCount > 0) engine.RestoreAll();
        engine.RestoreTaskbarButtons();
        watcher.Stop();
        UnregisterHotkeys();
        tray.Visible = false;
        tray.Dispose();
        hotwin.Close();
        Application.Exit();
    }

    // 承载全局热键的隐藏窗口
    }
}
