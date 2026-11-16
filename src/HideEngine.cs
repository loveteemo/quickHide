// HideEngine.cs —— quickHide 工程拆分文件（v1.3.0 起由单文件拆分而来）
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
// ---------- 隐藏引擎 ----------

public class HideEntry
{
    public IntPtr Hwnd;
    public uint Pid;
    public string Class;
    public string ProcName; // 规范化进程名（还原时按比例缩放窗口用）
    public WINDOWPLACEMENT Placement;
    // 缩放前的基准尺寸（未缩放时 == Placement.rcNormalPosition）
    // 关键：还原时按基准计算，绝不从"已被缩放过的当前尺寸"再乘一次，否则会越缩越小
    public RECT BaseRect;
}

    // 窗口尺寸记忆：记录"缩放前的基准"和"我们上次还原成的实际尺寸"
    // 全部用 GetWindowRect 的真实像素矩形，避开 rcNormalPosition 的换算误差
    class SizeMemo
    {
        public RECT Base;    // 基准（缩放前）尺寸
        public RECT Applied; // 上次由我们还原成的（缩放后）尺寸
    }

public class HideEngine
{
    readonly Config cfg;
    readonly List<HideEntry> hidden = new List<HideEntry>();
    // 窗口模式隐藏的进程集合（底层绑定：这些 pid 新弹出的窗口自动补藏，聊天窗/弹窗不漏）
    readonly HashSet<uint> winModePids = new HashSet<uint>();
    readonly string stateFile;
    // 按 "pid|类名" 记住窗口基准尺寸，防止缩放比例被反复累积
    readonly Dictionary<string, SizeMemo> sizeMemo = new Dictionary<string, SizeMemo>(StringComparer.OrdinalIgnoreCase);
    readonly string memoFile;
    // 被我们隐藏了任务栏按钮的窗口（缩放时）
    readonly HashSet<long> taskbarHidden = new HashSet<long>();

    public int HiddenCount { get { lock (hidden) return hidden.Count; } }
    public int LastActionWindowCount { get; private set; }

    public List<HideEntry> Entries { get { lock (hidden) return new List<HideEntry>(hidden); } }

    public event Action<int, bool> ActionDone; // (count, isHide)
    public event Action StateChanged;          // 隐藏集合发生变化（托盘状态刷新用）

    // 进程级隐藏会话进行中：监视器会自动补藏目标进程的新窗口
    bool processSession;

    public HideEngine(Config cfg)
    {
        this.cfg = cfg;
        stateFile = Path.Combine(Path.GetDirectoryName(cfg.Path), "state.txt");
        memoFile = Path.Combine(Path.GetDirectoryName(cfg.Path), "size_memo.txt");
        LoadMemo();
        LoadState();
    }

    // 计算"基准尺寸"（缩放前应有的原始大小）。
    // 数据源统一用 GetWindowRect 的真实像素尺寸：窗口可见时永远准确，不受 DPI/非客户区换算影响。
    // 规则：有 memo 且当前宽度接近"上次我们还原成的宽度"（±15%）→ 用户没动过，沿用原基准；
    //       否则视为用户手动调整过窗口，以当前尺寸为新基准。
    RECT ResolveBaseRect(uint pid, string cls, RECT current)
    {
        string key = pid + "|" + cls;
        SizeMemo memo;
        if (sizeMemo.TryGetValue(key, out memo))
        {
            int curW = current.right - current.left;
            int appW = memo.Applied.right - memo.Applied.left;
            if (appW > 6 && Math.Abs(curW - appW) <= Math.Max(6, appW * 15 / 100))
                return memo.Base;
        }
        return current;
    }

    // 记录本次由我们设置到窗口上的尺寸，供下次判断"是否被用户改过"
    void RememberApplied(uint pid, string cls, RECT baseRect, RECT applied)
    {
        sizeMemo[pid + "|" + cls] = new SizeMemo { Base = baseRect, Applied = applied };
        SaveMemo();
    }

    // 基准尺寸落盘：CLI 模式每次都是新进程，靠文件在进程间传递"上次缩放后的尺寸"
    void SaveMemo()
    {
        try
        {
            var sb = new StringBuilder();
            foreach (var kv in sizeMemo)
            {
                var m = kv.Value;
                // 分隔符用 '@'：key 本身含 '|'（pid|类名），若用 '|' 会导致解析错位，
                // 基准判断永远失败 → 缩放比例反复累积 → 窗口越缩越小
                sb.AppendFormat("{0}@{1},{2},{3},{4}@{5},{6},{7},{8}\r\n",
                    kv.Key,
                    m.Base.left, m.Base.top, m.Base.right, m.Base.bottom,
                    m.Applied.left, m.Applied.top, m.Applied.right, m.Applied.bottom);
            }
            File.WriteAllText(memoFile, sb.ToString(), Encoding.ASCII);
            Program.LogText("[MEMO SAVE] " + sizeMemo.Count + " entries -> " + memoFile);
        }
        catch (Exception ex) { Program.LogText("[MEMO ERR] " + ex.Message); }
    }

    void LoadMemo()
    {
        if (!File.Exists(memoFile)) return;
        try
        {
            foreach (string line in File.ReadAllLines(memoFile))
            {
                string[] f = line.Split('@');
                if (f.Length != 3) continue;
                var b = f[1].Split(',');
                var a = f[2].Split(',');
                int v1, v2, v3, v4, v5, v6, v7, v8;
                if (b.Length != 4 || a.Length != 4) continue;
                if (!int.TryParse(b[0], out v1) || !int.TryParse(b[1], out v2)
                    || !int.TryParse(b[2], out v3) || !int.TryParse(b[3], out v4)) continue;
                if (!int.TryParse(a[0], out v5) || !int.TryParse(a[1], out v6)
                    || !int.TryParse(a[2], out v7) || !int.TryParse(a[3], out v8)) continue;
                sizeMemo[f[0]] = new SizeMemo
                {
                    Base = new RECT { left = v1, top = v2, right = v3, bottom = v4 },
                    Applied = new RECT { left = v5, top = v6, right = v7, bottom = v8 },
                };
            }
        }
        catch { }
    }

    // ----- 核心: 一键隐藏 -----

    public int HideAll()
    {
        processSession = true;
        var targets = GetTargetPids();
        int count = HideProcessTargets(targets);
        LastActionWindowCount = count;
        if (count > 0)
        {
            SaveState();
            ApplyAudioTargets(targets);
        }
        RaiseDone(count, true);
        RaiseStateChanged();
        return count;
    }

    // 隐藏一组目标 PID 的全部可见窗口（HideAll 与单进程隐藏共用）
    int HideProcessTargets(HashSet<uint> targets)
    {
        int count = 0;
        foreach (IntPtr h in EnumTopWindows())
        {
            uint pid; string cls;
            if (!Identify(h, out pid, out cls)) continue;
            if (!IsTarget(h, pid, cls, targets)) continue;
            if (!Win32.IsWindowVisible(h)) continue;
            if (Win32.IsCloaked(h)) continue;
            if (FindEntry(h) != null) { // 已在隐藏集合中但被游戏重新显示了 → 再补一刀
                Win32.ShowWindowAsync(h, Win32.SW_HIDE);
                continue;
            }
            var entry = CreateEntry(h, pid, cls);
            Win32.ShowWindowAsync(h, Win32.SW_HIDE);
            lock (hidden) hidden.Add(entry);
            count++;
        }
        return count;
    }

    // 计算静音/暂停目标分组（软件级判定）：
    // 多进程软件（Chrome 等）的窗口、渲染、GPU、音频进程同属一个进程族——
    // 只要该软件还有任何一个可见窗口，其全部进程（含无窗口的渲染/GPU/音频进程）都不可挂起，
    // 否则会冻结仍在使用的可见窗口（页面无法响应鼠标）。
    // full = 软件的全部窗口都已隐藏（可安全挂起，声音必停）
    // partial = 软件仍有可见窗口（只做会话级静音，绝不挂起）
    void ComputeAudioTargets(HashSet<uint> basePids, out HashSet<uint> fullPids, out HashSet<uint> partialPids)
    {
        fullPids = new HashSet<uint>();
        partialPids = new HashSet<uint>();
        if (basePids.Count == 0) return;

        var visibleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        uint self = (uint)Process.GetCurrentProcess().Id;
        foreach (IntPtr h in EnumTopWindows())
        {
            uint p; string cls;
            if (!Identify(h, out p, out cls)) continue;
            if (p == self) continue;
            if (FindEntry(h) != null) continue; // 已隐藏的不算可见
            if (!Win32.IsWindowVisible(h) || Win32.IsCloaked(h)) continue;
            visibleNames.Add(GetProcName(p));
        }

        foreach (var pid in basePids)
        {
            var name = GetProcName(pid);
            if (visibleNames.Contains(name)) partialPids.Add(pid);
            else fullPids.Add(pid);
        }
    }

    // 统一分发：暂停模式挂起 full、会话静音 partial；静音模式会话静音全部（full 允许挂起兜底）
    void ApplyAudioTargets(HashSet<uint> basePids)
    {
        HashSet<uint> fullPids; HashSet<uint> partialPids;
        ComputeAudioTargets(basePids, out fullPids, out partialPids);
        if (fullPids.Count > 0)
        {
            if (cfg.SuspendOnHide) AudioMuter.SuspendTargets(fullPids);
            else if (cfg.MuteOnHide) AudioMuter.MuteProcesses(fullPids);
        }
        if (partialPids.Count > 0)
        {
            // 仍有可见窗口：只尝试会话级静音，绝不挂起（否则可见窗口假死）
            AudioMuter.MuteProcesses(partialPids, false);
        }
    }

    internal class WindowRow
    {
        public IntPtr Hwnd;
        public uint Pid;
        public string ProcName, Title, Path, DisplayName;
        public bool Hidden;
    }

    internal static readonly HashSet<string> KnownNonSoftwareNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Windows 系统组件
        "ctfmon", "dwm", "tabtip", "conhost", "svchost", "lsass", "services", "smss",
        "csrss", "wininit", "winlogon", "spoolsv", "taskhostw", "sihost", "wudfhost",
        "runtimebroker", "searchhost", "startmenuexperiencehost", "shellexperiencehost",
        "widgets", "textinputhost", "applicationframehost", "explorer", "audiodg",
        // 常见框架 / 辅助进程
        "crashpad_handler", "bash", "client", "downloader", "upgrader",
        "sguard64", "taslogin64", "nvdisplay.container", "msedgewebview2",
        "wechatappex", "crossdeviceservice", "oneupingame",
        "wetype_server", "wetype_update", "wetype_renderer", "wetype",
    };

    // 系统框架进程：exe 位于 Windows 目录（System32/SystemApps/WinSxS 等）——重启电脑也在，不是用户的"软件"
    internal static bool IsSystemProcessPath(string path, string winDir)
    {
        try
        {
            string full = Path.GetFullPath(path);
            return full.StartsWith(winDir + "\\\\", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // 软件友好名：exe 的文件描述（FileDescription）优先，其次窗口标题，最后内部进程名
    internal static string GetDisplayName(string procName, string title, string path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path))
            {
                var fi = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
                if (!string.IsNullOrEmpty(fi.FileDescription)) return fi.FileDescription;
            }
        }
        catch { }
        if (!string.IsNullOrEmpty(title) && title != "(无标题窗口)") return title;
        return procName;
    }

    // 枚举"软件窗口行"：可见的顶层用户窗口 + 已隐藏会话中的窗口；系统/框架进程已过滤
    internal List<WindowRow> EnumWindowRows()
    {
        var rows = new List<WindowRow>();
        string winDir = Path.GetDirectoryName(Environment.SystemDirectory) ?? "C:\\WINDOWS";
        string selfName = Process.GetCurrentProcess().ProcessName.ToLowerInvariant();

        foreach (IntPtr h in EnumTopWindows())
        {
            uint pid; string cls;
            if (!Identify(h, out pid, out cls)) continue;
            long key = h.ToInt64();
            bool isHidden = IsWindowHidden(h);
            bool visible = Win32.IsWindowVisible(h) && !Win32.IsCloaked(h);
            if (!visible && !isHidden) continue;

            var sb = new StringBuilder(256);
            Win32.GetWindowText(h, sb, sb.Capacity);
            string title = sb.ToString();
            if (string.IsNullOrEmpty(title) && !isHidden) continue; // 无标题的不可见消息窗口跳过

            string procName = GetProcName(pid).ToLowerInvariant();
            if (procName == selfName) continue; // 自己

            string path = null;
            try
            {
                var proc = Process.GetProcessById((int)pid);
                var mm = proc.MainModule;
                if (mm != null) path = mm.FileName;
            }
            catch { }
            if (!isHidden)
            {
                if (!string.IsNullOrEmpty(path) && IsSystemProcessPath(path, winDir)) continue;
                if (KnownNonSoftwareNames.Contains(procName)) continue;
            }

            string display = GetDisplayName(procName, title, path);
            rows.Add(new WindowRow { Hwnd = h, Pid = pid, ProcName = procName, Title = title, Path = path, DisplayName = display, Hidden = isHidden });
        }
        rows.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        return rows;
    }

    // ----- 窗口级操作（进程面板逐窗口勾选/缩放）-----

    HideEntry CreateEntry(IntPtr h, uint pid, string cls)
    {
        var entry = new HideEntry { Hwnd = h, Pid = pid, Class = cls, ProcName = GetProcName(pid) };
        entry.Placement.length = Marshal.SizeOf(typeof(WINDOWPLACEMENT));
        if (!Win32.GetWindowPlacement(h, ref entry.Placement)) entry.Placement.showCmd = Win32.SW_SHOW;
        // 基准用真实窗口矩形（此时窗口可见，值准确）
        RECT realRect;
        if (Win32.GetWindowRect(h, out realRect)) {
            entry.BaseRect = ResolveBaseRect(pid, cls, realRect);
        }
        else entry.BaseRect = entry.Placement.rcNormalPosition;
        return entry;
    }

    // 该窗口是否处于隐藏集合中
    internal bool IsWindowHidden(IntPtr hwnd)
    {
        lock (hidden) return FindEntry(hwnd) != null;
    }

    // 单窗口隐藏（进程面板勾选）
    public int HideWindow(IntPtr hwnd)
    {
        uint pid; string cls;
        if (!Identify(hwnd, out pid, out cls)) return 0;
        if (FindEntry(hwnd) != null) return 0;
        if (!Win32.IsWindowVisible(hwnd) || Win32.IsCloaked(hwnd)) return 0;
        winModePids.Add(pid);
        var entry = CreateEntry(hwnd, pid, cls);
        Win32.ShowWindowAsync(hwnd, Win32.SW_HIDE);
        lock (hidden) hidden.Add(entry);
        // 附带隐藏同进程的 ExtraClasses 窗口（DNF_WND_CHAT 聊天窗口）
        int extra = HideExtraOfPid(pid);
        // 静音/暂停：按软件名覆盖其全部进程，再按"是否仍有可见窗口"分流（可见窗口绝不挂起）
        var pids = GetPidsForProc(Config.NormProc(GetProcName(pid)));
        ApplyAudioTargets(pids);
        SaveState();
        RaiseStateChanged();
        return 1 + extra;
    }

    // 附带隐藏同 pid 的 ExtraClasses 窗口（如 DNF 的外置聊天窗口）
    int HideExtraOfPid(uint pid)
    {
        int n = 0;
        foreach (IntPtr h in EnumTopWindows())
        {
            uint p; string cls;
            if (!Identify(h, out p, out cls)) continue;
            if (p != pid) continue;
            if (FindEntry(h) != null) continue;
            if (!Win32.IsWindowVisible(h) || Win32.IsCloaked(h)) continue;
            if (!IsExtraClass(cls)) continue;
            var entry = CreateEntry(h, p, cls);
            Win32.ShowWindowAsync(h, Win32.SW_HIDE);
            lock (hidden) hidden.Add(entry);
            n++;
        }
        return n;
    }

    bool IsExtraClass(string cls)
    {
        foreach (string c in cfg.ExtraClasses)
            if (string.Equals(cls, c, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // 单窗口显示（进程面板取消勾选）
    public int ShowWindowEntry(IntPtr hwnd)
    {
        HideEntry e;
        lock (hidden)
        {
            e = FindEntry(hwnd);
            if (e == null) return 0;
            hidden.Remove(e);
        }
        RestoreEntry(e);
        // 同 pid 的 ExtraClasses 窗口一并还原（DNF_WND_CHAT 聊天窗口跟主窗口回来）
        var extras = new List<HideEntry>();
        lock (hidden)
        {
            foreach (var x in hidden)
                if (x.Pid == e.Pid && IsExtraClass(x.Class)) extras.Add(x);
            foreach (var x in extras) hidden.Remove(x);
        }
        foreach (var x in extras) RestoreEntry(x);
        // 该进程已无隐藏窗口 → 移除窗口模式绑定
        bool stillHidden = false;
        lock (hidden)
        {
            foreach (var x in hidden)
                if (x.Pid == e.Pid) { stillHidden = true; break; }
        }
        if (!stillHidden) winModePids.Remove(e.Pid);
        // 该窗口重新可见 → 若其进程处于挂起状态，恢复运行（否则可见窗口假死）
        AudioMuter.ResumePid(e.Pid);
        // 所有窗口都已还原 → 解除静音/恢复挂起的进程
        if (HiddenCount == 0) AudioMuter.RestoreAllMutes();
        SaveState();
        RaiseStateChanged();
        return 1 + extras.Count;
    }

    // 窗口级缩放记忆（临时：进程生命周期内有效，防"越缩越小"用固定基准）
    readonly Dictionary<long, RECT> windowScaleBase = new Dictionary<long, RECT>();
    readonly Dictionary<long, int> windowScalePct = new Dictionary<long, int>();

    internal int GetWindowScalePct(IntPtr hwnd)
    {
        int v;
        return windowScalePct.TryGetValue(hwnd.ToInt64(), out v) ? v : 100;
    }

    // 缩放单个窗口（进程面板双击行）；100 = 恢复原始大小
    public int ScaleWindowEntry(IntPtr hwnd, int pct)
    {
        pct = ClampScale(pct);
        uint pid; string cls;
        if (!Identify(hwnd, out pid, out cls)) return 0;
        RECT cur;
        if (!Win32.GetWindowRect(hwnd, out cur)) return 0;
        if (cur.right - cur.left < 20 || cur.bottom - cur.top < 20) return 0;
        long key = hwnd.ToInt64();

        RECT baseRect;
        if (pct >= 100)
        {
            if (!windowScaleBase.TryGetValue(key, out baseRect)) baseRect = cur;
            windowScaleBase.Remove(key);
            windowScalePct.Remove(key);
        }
        else if (!windowScaleBase.TryGetValue(key, out baseRect))
        {
            baseRect = cur; // 首次缩放：以当前尺寸为基准，重复缩放不累积
            windowScaleBase[key] = baseRect;
        }
        windowScalePct[key] = pct;

        int baseW = baseRect.right - baseRect.left;
        int baseH = baseRect.bottom - baseRect.top;
        if (baseW <= 0 || baseH <= 0) return 0;
        int targetW = pct >= 100 ? baseW : (int)Math.Round(baseW * pct / 100.0);
        int targetH = pct >= 100 ? baseH : (int)Math.Round(baseH * pct / 100.0);

        if (Win32.IsWindowVisible(hwnd) && Win32.IsZoomed(hwnd))
            Win32.ShowWindowAsync(hwnd, Win32.SW_RESTORE);
        Win32.SetWindowPos(hwnd, IntPtr.Zero, baseRect.left, baseRect.top, targetW, targetH,
            Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_ASYNCWINDOWPOS);
        Thread.Sleep(120);
        ApplyTaskbarVisibility(hwnd, pct);
        LastActionWindowCount = 1;
        return 1;
    }

    // ----- 核心: 一键还原 -----

    public int RestoreAll()
    {
        AudioMuter.ResumeAllSuspended(); // 先恢复挂起的目标进程，再显示其窗口
        processSession = false;
        int count = 0;
        List<HideEntry> snapshot;
        lock (hidden) { snapshot = new List<HideEntry>(hidden); hidden.Clear(); }

        foreach (var e in snapshot)
        {
            if (RestoreEntry(e)) count++;
        }
        LastActionWindowCount = count;
        try { if (File.Exists(stateFile)) File.Delete(stateFile); } catch { }
        AudioMuter.RestoreAllMutes(); // 还原目标进程的原始音量
        winModePids.Clear();
        RaiseDone(count, false);
        RaiseStateChanged();
        return count;
    }

    // 还原单条记录；句柄被系统复用（pid/类名对不上）时放弃
    bool RestoreEntry(HideEntry e)
    {
        if (!Win32.IsWindow(e.Hwnd)) return false;
        uint pid; string cls;
        if (!Identify(e.Hwnd, out pid, out cls) || pid != e.Pid || cls != e.Class) return false;
        if (Win32.IsWindowVisible(e.Hwnd)) return true; // 目标程序自己已恢复
        var pl = e.Placement;
        pl.length = Marshal.SizeOf(typeof(WINDOWPLACEMENT));

        // 目标矩形 = 基准尺寸 × 缩放比例（基准是真实像素矩形，不参与任何换算）
        RECT baseRect = e.BaseRect;
        if (baseRect.right <= baseRect.left) baseRect = pl.rcNormalPosition;
        int pct = cfg.GetScale(Config.NormProc(e.ProcName ?? ""));
        int targetW = baseRect.right - baseRect.left;
        int targetH = baseRect.bottom - baseRect.top;
        if (pct != 100)
        {
            targetW = (int)Math.Round(targetW * pct / 100.0);
            targetH = (int)Math.Round(targetH * pct / 100.0);
        }

        // 1) 先用 WINDOWPLACEMENT 恢复"正常位置"标志（避免最大化状态干扰）
        pl.rcNormalPosition = new RECT
        {
            left = baseRect.left,
            top = baseRect.top,
            right = baseRect.left + targetW,
            bottom = baseRect.top + targetH,
        };
        bool ok = Win32.SetWindowPlacement(e.Hwnd, ref pl);
        if (!ok || pl.showCmd == Win32.SW_HIDE)
            Win32.ShowWindowAsync(e.Hwnd, pl.showCmd == Win32.SW_SHOWMAXIMIZED ? Win32.SW_SHOWMAXIMIZED : Win32.SW_SHOW);

        // 2) 再用 SetWindowPos 精确设置真实像素矩形（SetWindowPlacement 的 rcNormalPosition
        //    会受非客户区/DPI 换算影响，导致尺寸漂移）
        if (pct != 100)
            Win32.SetWindowPos(e.Hwnd, IntPtr.Zero, baseRect.left, baseRect.top, targetW, targetH,
                Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_ASYNCWINDOWPOS);

        // 3) 回读真实生效的尺寸，作为"我们上次设置的值"（下次判断用户是否手动改过）
        RECT realApplied;
        if (Win32.GetWindowRect(e.Hwnd, out realApplied)) {
            Program.LogText("[SHOW] " + e.Class + " base=" + (baseRect.right-baseRect.left) + " target=" + targetW + " real=" + (realApplied.right-realApplied.left) + " pct=" + pct);
            RememberApplied(e.Pid, e.Class, baseRect, realApplied);
        }
        else
            RememberApplied(e.Pid, e.Class, baseRect,
                new RECT { left = baseRect.left, top = baseRect.top, right = baseRect.left + targetW, bottom = baseRect.top + targetH });
        ApplyTaskbarVisibility(e.Hwnd, pct);
        return true;
    }

    // ----- 即时缩放（双击进程行 / 缩放热键）-----

    public static int ClampScale(int pct)
    {
        if (pct < 25) return 25;
        if (pct > 100) return 100;
        return pct;
    }

    // pct=100 时恢复基准尺寸；其他值按基准矩形缩放，避免反复缩放后越来越小
    public int ScaleProcess(string procName, int pct)
    {
        pct = ClampScale(pct);
        string norm = Config.NormProc(procName ?? "");
        var pids = GetPidsForProc(norm);
        LastActionWindowCount = 0;
        if (pids.Count == 0) return 0;

        // SetWindowPos 是异步的，立刻回读会拿到旧尺寸；先全部下发，稍后统一回读真实生效尺寸
        var pending = new List<ScaleApply>();
        foreach (IntPtr h in EnumTopWindows())
        {
            uint pid; string cls;
            if (!Identify(h, out pid, out cls)) continue;
            if (!pids.Contains(pid)) continue;
            if (!Win32.IsWindowVisible(h) || Win32.IsCloaked(h)) continue;
            RECT cur;
            if (!Win32.GetWindowRect(h, out cur)) continue;
            if (cur.right - cur.left < 20 || cur.bottom - cur.top < 20) continue;

            RECT baseRect = ResolveBaseRect(pid, cls, cur);
            if (Win32.IsZoomed(h))
                Win32.ShowWindowAsync(h, Win32.SW_RESTORE);
            int baseW = baseRect.right - baseRect.left;
            int baseH = baseRect.bottom - baseRect.top;
            if (baseW <= 0 || baseH <= 0) continue;
            int targetW = pct >= 100 ? baseW : (int)Math.Round(baseW * pct / 100.0);
            int targetH = pct >= 100 ? baseH : (int)Math.Round(baseH * pct / 100.0);

            Win32.SetWindowPos(h, IntPtr.Zero, baseRect.left, baseRect.top, targetW, targetH,
                Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_ASYNCWINDOWPOS);
            pending.Add(new ScaleApply
            {
                Hwnd = h,
                Pid = pid,
                Class = cls,
                Base = baseRect,
                TargetW = targetW,
                TargetH = targetH,
                Pct = pct,
            });
        }
        if (pending.Count == 0) return 0;

        Thread.Sleep(120);
        foreach (var p in pending)
        {
            RECT applied;
            if (!Win32.GetWindowRect(p.Hwnd, out applied))
                applied = new RECT
                {
                    left = p.Base.left,
                    top = p.Base.top,
                    right = p.Base.left + p.TargetW,
                    bottom = p.Base.top + p.TargetH,
                };
            Program.LogText("[SCALE] " + p.Class
                + " base=" + (p.Base.right - p.Base.left) + "X" + (p.Base.bottom - p.Base.top)
                + " target=" + p.TargetW + "X" + p.TargetH
                + " real=" + (applied.right - applied.left) + "X" + (applied.bottom - applied.top)
                + " pct=" + p.Pct);
            RememberApplied(p.Pid, p.Class, p.Base, applied);
            ApplyTaskbarVisibility(p.Hwnd, p.Pct);
        }
        LastActionWindowCount = pending.Count;
        return pending.Count;
    }

    class ScaleApply
    {
        public IntPtr Hwnd;
        public uint Pid;
        public string Class;
        public RECT Base;
        public int TargetW;
        public int TargetH;
        public int Pct;
    }

    // 缩放热键：把所有已配置比例的进程窗口按各自比例缩放（只缩放，不隐藏）
    public int ApplyConfiguredScales()
    {
        int total = 0;
        var snapshot = new List<KeyValuePair<string, int>>(cfg.Scale);
        foreach (var kv in snapshot)
        {
            if (kv.Value >= 25 && kv.Value < 100)
                total += ScaleProcess(kv.Key, kv.Value);
        }
        LastActionWindowCount = total;
        return total;
    }

    HashSet<uint> GetPidsForProc(string norm)
    {
        var set = new HashSet<uint>();
        if (norm.Length == 0) return set;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (Config.NormProc(p.ProcessName) == norm) set.Add((uint)p.Id);
            }
            catch { }
        }
        return set;
    }

    // 缩放时隐藏任务栏按钮；回到 100% 时恢复
    void ApplyTaskbarVisibility(IntPtr h, int pct)
    {
        long key = h.ToInt64();
        if (!cfg.HideTaskbarWhenScaled || pct >= 100)
        {
            if (taskbarHidden.Remove(key)) TaskbarList.ShowTab(h);
            return;
        }
        if (TaskbarList.HideTab(h)) taskbarHidden.Add(key);
    }

    // 关闭“缩放时隐藏任务栏按钮”或退出程序时，把按钮还回去
    public void RestoreTaskbarButtons()
    {
        foreach (long v in new List<long>(taskbarHidden))
            TaskbarList.ShowTab(new IntPtr(v));
        taskbarHidden.Clear();
    }

    public void ApplyTaskbarOption()
    {
        if (!cfg.HideTaskbarWhenScaled)
        {
            RestoreTaskbarButtons();
            return;
        }
        foreach (IntPtr h in EnumTopWindows())
        {
            uint pid; string cls;
            if (!Identify(h, out pid, out cls)) continue;
            if (!Win32.IsWindowVisible(h) || Win32.IsCloaked(h)) continue;
            int pct = cfg.GetScale(GetProcName(pid));
            if (pct >= 25 && pct < 100 && TaskbarList.HideTab(h))
                taskbarHidden.Add(h.ToInt64());
        }
    }

    static string GetProcName(uint pid)
    {
        try { return Config.NormProc(Process.GetProcessById((int)pid).ProcessName); }
        catch { return ""; }
    }

    // ----- 监视: 隐藏期间补藏新窗口（外置聊天窗口是动态创建的）-----

    public int WatchStep()
    {
        if (HiddenCount == 0) return 0;
        int count = 0;
        // 补藏目标 = 清单进程 ∪ 窗口模式已隐藏的进程（底层统一：聊天窗/弹窗不漏）
        var targets = GetTargetPids();
        lock (hidden)
        {
            foreach (var e in hidden) targets.Add(e.Pid);
        }
        lock (winModePids)
        {
            foreach (var pid in winModePids) targets.Add(pid);
        }
        foreach (IntPtr h in EnumTopWindows())
        {
            uint pid; string cls;
            if (!Identify(h, out pid, out cls)) continue;
            var e = FindEntry(h);
            if (e == null)
            {
                // 仅进程模式补藏新窗口（窗口模式勾选即精确目标，不自动扩大范围）
                if (!processSession) continue;
                if (!IsTarget(h, pid, cls, targets)) continue;
                if (!Win32.IsWindowVisible(h) || Win32.IsCloaked(h)) continue;
                var ne = new HideEntry { Hwnd = h, Pid = pid, Class = cls, ProcName = GetProcName(pid) };
                ne.Placement.length = Marshal.SizeOf(typeof(WINDOWPLACEMENT));
                if (!Win32.GetWindowPlacement(h, ref ne.Placement)) ne.Placement.showCmd = Win32.SW_SHOW;
                RECT nr;
                if (Win32.GetWindowRect(h, out nr)) ne.BaseRect = ResolveBaseRect(pid, cls, nr);
                else ne.BaseRect = ne.Placement.rcNormalPosition;
                Win32.ShowWindowAsync(h, Win32.SW_HIDE);
                lock (hidden) hidden.Add(ne);
                count++;
            }
            else if (Win32.IsWindowVisible(h))
            {
                // 已记录但又被目标程序重新显示了 → 保持隐藏状态
                Win32.ShowWindowAsync(h, Win32.SW_HIDE);
            }
        }
        if (count > 0)
        {
            SaveState();
            RaiseStateChanged();
        }
        ApplyAudioTargets(targets); // 新出现的音频会话（如藏后才开始的播放）也静音/暂停
        return count;
    }

    HideEntry FindEntry(IntPtr h)
    {
        lock (hidden)
        {
            foreach (var e in hidden) if (e.Hwnd == h) return e;
        }
        return null;
    }

    static IntPtr[] EnumTopWindows()
    {
        var list = new List<IntPtr>();
        Win32.EnumWindows((h, l) => { list.Add(h); return true; }, IntPtr.Zero);
        return list.ToArray();
    }

    void RaiseDone(int count, bool isHide)
    {
        var d = ActionDone;
        if (d != null) d(count, isHide);
    }

    void RaiseStateChanged()
    {
        var d = StateChanged;
        if (d != null) d();
    }

    // ----- 目标匹配 -----

    HashSet<uint> GetTargetPids()
    {
        var set = new HashSet<uint>();
        foreach (var p in Process.GetProcesses())
        {
            string n = p.ProcessName.ToLowerInvariant();
            foreach (string t in cfg.Processes)
            {
                if (n == t || n == StripExe(t)) { try { set.Add((uint)p.Id); } catch { } break; }
            }
        }
        return set;
    }

    static string StripExe(string s)
    {
        return s.EndsWith(".exe") ? s.Substring(0, s.Length - 4) : s;
    }

    bool IsTarget(IntPtr hwnd, uint pid, string cls, HashSet<uint> pids)
    {
        if (pid == (uint)Process.GetCurrentProcess().Id) return false;
        if (pids.Contains(pid)) return true;
        foreach (string c in cfg.ExtraClasses)
            if (string.Equals(cls, c, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static bool Identify(IntPtr hwnd, out uint pid, out string cls)
    {
        pid = 0;
        cls = null;
        uint p;
        Win32.GetWindowThreadProcessId(hwnd, out p);
        if (p == 0) return false;
        pid = p;
        var sb = new StringBuilder(256);
        if (Win32.GetClassName(hwnd, sb, 256) <= 0) return false;
        cls = sb.ToString();
        return cls.Length > 0;
    }

    // ----- 崩溃保护: 隐藏状态落盘，重启后可还原 -----

    void SaveState()
    {
        try
        {
            if (HiddenCount == 0)
            {
                if (File.Exists(stateFile)) File.Delete(stateFile);
                return;
            }
            var sb = new StringBuilder();
            foreach (var e in Entries)
            {
                var p = e.Placement;
                var br = e.BaseRect;
                sb.AppendFormat("{0}|{1}|{2}|{3}|{4}|{5},{6}|{7},{8}|{9},{10},{11},{12}|{13}|{14},{15},{16},{17}\r\n",
                    e.Hwnd.ToInt64(), e.Pid, Uri.EscapeDataString(e.Class), p.flags, p.showCmd,
                    p.ptMinPosition.x, p.ptMinPosition.y, p.ptMaxPosition.x, p.ptMaxPosition.y,
                    p.rcNormalPosition.left, p.rcNormalPosition.top, p.rcNormalPosition.right, p.rcNormalPosition.bottom,
                    Uri.EscapeDataString(e.ProcName ?? ""),
                    br.left, br.top, br.right, br.bottom);
            }
            File.WriteAllText(stateFile, sb.ToString(), Encoding.ASCII);
        }
        catch { }
    }

    void LoadState()
    {
        Program.LogText("[LOADSTATE] path=" + stateFile + " exists=" + File.Exists(stateFile));
        if (!File.Exists(stateFile)) return;
        try
        {
            foreach (string line in File.ReadAllLines(stateFile))
            {
                string[] f = line.Split('|');
                if (f.Length < 8) continue;
                long h; uint pid;
                if (!long.TryParse(f[0], out h) || !uint.TryParse(f[1], out pid)) continue;
                var e = new HideEntry
                {
                    Hwnd = new IntPtr(h),
                    Pid = pid,
                    Class = Uri.UnescapeDataString(f[2]),
                    ProcName = f.Length >= 9 ? Uri.UnescapeDataString(f[8]) : "",
                };
                e.Placement.length = Marshal.SizeOf(typeof(WINDOWPLACEMENT));
                int.TryParse(f[3], out e.Placement.flags);
                int.TryParse(f[4], out e.Placement.showCmd);
                int a, b, c2, d;
                var mn = f[5].Split(','); var mx = f[6].Split(','); var rc = f[7].Split(',');
                if (mn.Length == 2 && int.TryParse(mn[0], out a) && int.TryParse(mn[1], out b)) { e.Placement.ptMinPosition.x = a; e.Placement.ptMinPosition.y = b; }
                if (mx.Length == 2 && int.TryParse(mx[0], out a) && int.TryParse(mx[1], out b)) { e.Placement.ptMaxPosition.x = a; e.Placement.ptMaxPosition.y = b; }
                if (rc.Length == 4 && int.TryParse(rc[0], out a) && int.TryParse(rc[1], out b) && int.TryParse(rc[2], out c2) && int.TryParse(rc[3], out d))
                {
                    e.Placement.rcNormalPosition.left = a; e.Placement.rcNormalPosition.top = b;
                    e.Placement.rcNormalPosition.right = c2; e.Placement.rcNormalPosition.bottom = d;
                }
                // 基准尺寸：f[9] 是逗号串 "left,top,right,bottom"
                if (f.Length >= 10)
                {
                    var br = f[9].Split(',');
                    int bl, bt, br2, bb;
                    if (br.Length == 4
                        && int.TryParse(br[0], out bl) && int.TryParse(br[1], out bt)
                        && int.TryParse(br[2], out br2) && int.TryParse(br[3], out bb) && br2 > bl)
                    {
                        e.BaseRect.left = bl; e.BaseRect.top = bt;
                        e.BaseRect.right = br2; e.BaseRect.bottom = bb;
                    }
                }
                if (e.BaseRect.right <= e.BaseRect.left) {
                    e.BaseRect = e.Placement.rcNormalPosition;
                    Program.LogText("[LOAD] " + e.Class + " base fallback to rcNormal=" + (e.BaseRect.right-e.BaseRect.left));
                } else {
                    Program.LogText("[LOAD] " + e.Class + " base=" + (e.BaseRect.right-e.BaseRect.left) + " fields=" + f.Length);
                }
                lock (hidden) hidden.Add(e);
            }
        }
        catch { }
    }
}
}
