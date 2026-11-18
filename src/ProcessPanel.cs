// ProcessPanel.cs —— quickHide 工程拆分文件（v1.3.0 起由单文件拆分而来）
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
    class ProcessPanel : UserControl
    {
        readonly TrayContext owner;
        readonly BufferedListView lvProcs;
        readonly Dictionary<string, int> procScale = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        readonly ImageList iconList = new ImageList { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };
        readonly Dictionary<string, int> iconCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        readonly System.Windows.Forms.Timer autoSave;
        // LoadWindowList 程序化设置勾选时抑制即时动作（防止 no-op 调用风暴）
        bool suppressItemAction;
        // 窗口模式：勾选的目标窗口句柄（按隐藏热键时执行）
        readonly HashSet<long> windowTargets = new HashSet<long>();
        // 窗口模式：窗口句柄 -> 所属进程名（切回进程模式时勾选按进程名合并）
        readonly Dictionary<long, string> hwndProc = new Dictionary<long, string>();
        System.Windows.Forms.Label lblTip;
        // ReloadFromCfg 程序化设置勾选时抑制自动保存：未运行进程不勾选 ≠ 从清单删除
        bool suppressAutoSave;
        // 异步扫描状态：防止重复点击刷新；扫描期间 lblTip 显示进度
        bool scanning;
        int scanSeq;
        // 清单里未运行的进程：列表不显示行，但保留在底层清单（进程启动后自动纳入隐藏）
        readonly HashSet<string> preservedProcs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);


        public ProcessPanel(TrayContext owner)
        {
            this.owner = owner;
            BackColor = Theme.Bg;
            ForeColor = Theme.Fg;
            Font = new Font("Microsoft YaHei UI", 9F);

            lblTip = MkLabel("", 20, 20);
            Controls.Add(lblTip);
            var lnkRefresh = new LinkLabel
            {
                AutoSize = true,
                Text = "刷新",
                LinkColor = Theme.Accent,
                ActiveLinkColor = Theme.Fg,
                LinkBehavior = LinkBehavior.HoverUnderline,
                Location = new Point(730, 24),
                Cursor = Cursors.Hand,
            };
            lnkRefresh.LinkClicked += (s, e) => ReloadView();
            Controls.Add(lnkRefresh);
            lvProcs = new BufferedListView
            {
                View = View.Details,
                CheckBoxes = true,
                FullRowSelect = true,
                Location = new Point(28, 54),
                Size = new Size(744, 362),
                BackColor = Theme.ListBg,
                ForeColor = Theme.Fg,
                BorderStyle = BorderStyle.FixedSingle,
            };
            lvProcs.SmallImageList = iconList;
            lvProcs.Columns.Add("软件名称", 175); // 图标显示在名称文本左侧；勾选目标仍按内部进程名
            var colTitle = lvProcs.Columns.Add("窗口标题", 290);
            colTitle.Width = -2; // 自动填充剩余宽度，杜绝横向滚动条
            lvProcs.Columns.Add("软件地址", 220);
            lvProcs.Columns.Add("大小%", 60);
            Controls.Add(lvProcs);
            lvProcs.DoubleClick += (s, e) =>
            {
                if (lvProcs.SelectedItems.Count == 0) return;
                OpenScaleDialog(lvProcs.SelectedItems[0]);
            };
            lvProcs.ItemCheck += (s, e) =>
            {
                if (suppressItemAction) return;
                if (owner.cfg.ListMode == "window")
                {
                    // 窗口模式：勾选只选择目标（记录窗口句柄），按隐藏热键才执行
                    var hwndKey = ((IntPtr)lvProcs.Items[e.Index].Tag).ToInt64();
                    if (e.NewValue == CheckState.Checked)
                        windowTargets.Add(hwndKey);
                    else
                        windowTargets.Remove(hwndKey);
                    return;
                }
                // 进程模式：勾选只选择目标（清单），按隐藏热键才执行
                if (suppressAutoSave) return;
                autoSave.Stop();
                autoSave.Start();
            };

            autoSave = new System.Windows.Forms.Timer { Interval = 700 };
            autoSave.Tick += (s, e) =>
            {
                autoSave.Stop();
                PersistChecks();
            };

            ReloadView();
        }

        // 按配置的列表粒度刷新面板，并同步顶部提示文案
        public void ReloadView()
        {
            bool windowMode = (owner.cfg.ListMode == "window");
            lblTip.Text = windowMode
                ? "窗口模式：勾选要隐藏的窗口（热键执行），双击某行设置该窗口缩放"
                : "进程模式：勾选要隐藏的软件（热键执行），双击某行设置缩放";
            if (windowMode) LoadWindowList();
            else ReloadFromCfg();
        }


        public int CheckedCount { get { return lvProcs.CheckedItems.Count; } }

        // 窗口模式：勾选行的窗口句柄；非窗口模式返回 null
        public List<IntPtr> GetCheckedWindowHandles()
        {
            if (owner.cfg.ListMode != "window") return null;
            var list = new List<IntPtr>();
            foreach (ListViewItem it in lvProcs.CheckedItems)
                list.Add((IntPtr)it.Tag);
            return list;
        }

        Label MkLabel(string text, int x, int y)
        {
            return new Label { Text = text, AutoSize = true, ForeColor = Theme.FgDim, Location = new Point(x, y + 4) };
        }

        public Dictionary<string, int> GetScaleMap()
        {
            // 保留配置里已有、但当前未出现在列表中的进程比例
            var map = new Dictionary<string, int>(owner.cfg.Scale, StringComparer.OrdinalIgnoreCase);
            foreach (ListViewItem it in lvProcs.Items)
            {
                string n = Config.NormProc((string)it.Tag);
                int v;
                if (!int.TryParse(it.SubItems[3].Text, out v)) continue;
                if (v >= 25 && v < 100) map[n] = v;
                else map.Remove(n);
            }
            return map;
        }

        void OpenScaleDialog(ListViewItem it)
        {
            if (owner.cfg.ListMode == "window")
            {
                IntPtr hwnd = (IntPtr)it.Tag;
                int curW = owner.engine.GetWindowScalePct(hwnd);
                using (var dlg = new ScaleDialog(it.Text, curW))
                {
                    if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                    int pct = dlg.Percent;
                    owner.engine.ScaleWindowEntry(hwnd, pct);
                    it.SubItems[3].Text = pct.ToString();
                    owner.ShowTip(pct >= 100
                        ? string.Format("已恢复 \"{0}\" 的原始大小", it.Text)
                        : string.Format("已把 \"{0}\" 缩放为 {1}%", it.Text, pct), false);
                }
                return;
            }
            string name = (string)it.Tag;
            int cur;
            if (!int.TryParse(it.SubItems[3].Text, out cur)) cur = 100;
            using (var dlg = new ScaleDialog(name, cur))
            {
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                int pct = dlg.Percent;
                int n = owner.ApplyScaleImmediate(name, pct);
                procScale[name] = pct;
                it.SubItems[3].Text = pct.ToString();
                if (pct >= 100)
                    owner.ShowTip(n > 0
                        ? string.Format("已恢复 {0} 的 {1} 个窗口原始大小", name, n)
                        : string.Format("{0} 当前没有可见窗口，已清除缩放设置", name), false);
                else
                    owner.ShowTip(n > 0
                        ? string.Format("已把 {0} 的 {1} 个窗口缩放为 {2}%", name, n, pct)
                        : string.Format("{0} 当前没有可见窗口；隐藏的窗口会在还原时按 {1}% 显示", name, pct), false);
            }
        }

        public List<string> GetCheckedProcesses()
        {
            var list = new List<string>();
            foreach (ListViewItem it in lvProcs.CheckedItems)
            {
                string n = Config.NormProc((string)it.Tag);
                if (n.Length > 0 && !list.Contains(n)) list.Add(n);
            }
            // 未运行不显示行的清单进程必须跟着保存，否则勾选别的软件时会把它们从清单挤掉
            foreach (string n in preservedProcs)
                if (!list.Contains(n)) list.Add(n);
            return list;
        }

        // 进程列表 = 当前勾选且正在运行的进程 + 当前正在运行的有窗口程序（未勾选）；
        // 清单里未运行的进程不显示行（底层清单保留，进程启动后自动纳入隐藏）
        public void ReloadFromCfg()
        {
            if (scanning) return;
            scanning = true;
            int seq = ++scanSeq;
            lblTip.Text = "正在扫描进程，请稍候...";

            // 快照当前配置（工作线程读取）
            var cfgProcs = new List<string>(owner.cfg.Processes);
            var scaleMap = new Dictionary<string, int>(procScale);
            var checkedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in cfgProcs) checkedSet.Add(n);
            foreach (var n in GetCheckedProcesses()) checkedSet.Add(n);

            string winDir = Path.GetDirectoryName(Environment.SystemDirectory) ?? "C:\\WINDOWS";

            System.Threading.Tasks.Task.Factory.StartNew(() =>
            {
                var rows = new List<ProcRow>();
                try
                {
                    uint self = (uint)Process.GetCurrentProcess().Id;
                    foreach (var p in Process.GetProcesses())
                    {
                        if (p.Id == self) continue;
                        string name = p.ProcessName.ToLowerInvariant();
                        if (checkedSet.Contains(name)) continue;
                        string title = null;
                        string path = null;
                        bool hasWindow = false;
                        try
                        {
                            title = p.MainWindowTitle;
                            hasWindow = p.MainWindowHandle != IntPtr.Zero;
                            var mm = p.MainModule;
                            if (mm != null) path = mm.FileName;
                        }
                        catch { }
                        if (string.IsNullOrEmpty(title) && !hasWindow) continue;
                        if (string.IsNullOrEmpty(title)) title = "(无标题窗口)";
                        if (!string.IsNullOrEmpty(path) && HideEngine.IsSystemProcessPath(path, winDir)) continue;
                        if (HideEngine.KnownNonSoftwareNames.Contains(name)) continue;
                        rows.Add(new ProcRow(name, HideEngine.GetDisplayName(name, title, path), title, path, ExtractIconCopy(path)));
                    }
                }
                catch (Exception ex) { Program.LogUiError(ex); }
                rows.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));

                BeginInvoke((Action)(() =>
                {
                    if (seq != scanSeq) return;
                    RenderProcList(cfgProcs, rows, scaleMap);
                    scanning = false;
                }));
            });
        }

        // UI 线程：渲染进程模式列表
        void RenderProcList(List<string> checkedNow, List<ProcRow> rows, Dictionary<string, int> scaleMap)
        {
            suppressAutoSave = true;
            suppressItemAction = true;
            try
            {
                preservedProcs.Clear();
                lvProcs.BeginUpdate();
                lvProcs.Items.Clear();
                foreach (var n in checkedNow)
                {
                    string title, path;
                    TryGetRunningInfo(n, out title, out path);
                    if (title == null)
                    {
                        // 未运行：不显示行，但保留在底层清单（进程启动后自动纳入隐藏）
                        preservedProcs.Add(n);
                        continue;
                    }
                    string displayName = GetDisplayName2(n, title, path);
                    int sc;
                    if (!scaleMap.TryGetValue(n, out sc)) sc = 100;
                    lvProcs.Items.Add(MkProcItem(n, displayName, title, path, sc, true));
                }
                foreach (var r in rows)
                {
                    int sc;
                    if (!scaleMap.TryGetValue(r.Name, out sc)) sc = 100;
                    lvProcs.Items.Add(MkProcItem(r.Name, r.DisplayName, r.Title, r.Path, sc, false));
                }
                lvProcs.EndUpdate();
                lblTip.Text = string.Format("已发现 {0} 个软件", lvProcs.Items.Count);
            }
            finally
            {
                suppressAutoSave = false;
                suppressItemAction = false;
            }
        }

        // 进程面板用的友好名（与 HideEngine.GetDisplayName 同逻辑）
        static string GetDisplayName2(string procName, string title, string path)
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

        // 扫描结果行（工作线程产出 → UI 线程渲染）
        class WindowItem
        {
            public IntPtr Hwnd;
            public string ProcName;
            public string DisplayName, Title, Path;
            public int Scale = 100;
            public bool Hidden;
            public Bitmap Icon; // 工作线程预提取的图标副本
        }

        // 窗口模式数据源（异步）：枚举/MainModule/图标提取都可能被杀软或受保护进程拖慢，绝不能在 UI 线程做
        public void LoadWindowList()
        {
            if (scanning) return; // 防重复点击刷新
            scanning = true;
            int seq = ++scanSeq;
            lblTip.Text = "正在扫描窗口，请稍候...";

            System.Threading.Tasks.Task.Factory.StartNew(() =>
            {
                var items = new List<WindowItem>();
                hwndProc.Clear();
                try
                {
                    foreach (var w in owner.engine.EnumWindowRows())
                    {
                        items.Add(new WindowItem
                        {
                            Hwnd = w.Hwnd,
                            ProcName = w.ProcName,
                            DisplayName = w.DisplayName,
                            Title = w.Title,
                            Path = w.Path,
                            Hidden = w.Hidden,
                            Scale = owner.engine.GetWindowScalePct(w.Hwnd),
                            Icon = ExtractIconCopy(w.Path),
                        });
                    }
                }
                catch (Exception ex) { Program.LogUiError(ex); }

                // 回 UI 线程渲染（BeginInvoke 不会死锁 UI）
                if (IsHandleCreated && !IsDisposed)
                    BeginInvoke((Action)(() => RenderWindowList(seq, items)));
                else
                    scanning = false;
            });
        }

        // UI 线程：渲染扫描结果
        void RenderWindowList(int seq, List<WindowItem> items)
        {
            scanning = false;
            if (seq != scanSeq) return; // 过期扫描结果（用户又点了刷新）
            suppressItemAction = true;
            try
            {
                lvProcs.BeginUpdate();
                lvProcs.Items.Clear();
                if (items.Count == 0)
                {
                    lblTip.Text = "暂未发现可操作的软件窗口，请先启动目标软件后点击刷新";
                }
                else
                {
                    lblTip.Text = owner.cfg.ListMode == "window"
                        ? "窗口模式：勾选 = 隐藏该窗口，取消勾选 = 显示；双击某行设置该窗口缩放"
                        : string.Format("已发现 {0} 个软件", items.Count);
                }
                foreach (var w in items)
                {
                    hwndProc[w.Hwnd.ToInt64()] = w.ProcName;
                    var it = new ListViewItem(w.DisplayName); // 图标显示在软件名文本左侧
                    it.ImageIndex = IconIndexFor(w.Path, w.Icon);
                    it.SubItems.Add(w.Title);
                    it.SubItems.Add(w.Path ?? "");
                    it.SubItems.Add(w.Scale.ToString());
                    it.Tag = w.Hwnd;
                    it.Checked = windowTargets.Contains(w.Hwnd.ToInt64()) || w.Hidden; // 已隐藏的保持勾选（还原热键仍可还原）
                    lvProcs.Items.Add(it);
                }
                lvProcs.EndUpdate();
            }
            finally
            {
                suppressItemAction = false;
            }
        }

        class ProcRow
        {
            public readonly string Name, Title, Path;
            public readonly string DisplayName;
            public readonly Bitmap Icon;
            public ProcRow(string name, string displayName, string title, string path, Bitmap icon = null)
            {
                Name = name; DisplayName = displayName; Title = title; Path = path; Icon = icon;
            }
        }

        // 已知"非独立软件"进程：Windows 系统组件 + 常见框架/辅助进程（无独立主窗口、藏在后台）
        // MainModule 不可读时按名兜底；发现漏网之鱼往这个集合里补名字即可
        static readonly HashSet<string> KnownNonSoftwareNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
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
        static bool IsSystemProcessPath(string path, string winDir)
        {
            try
            {
                string full = Path.GetFullPath(path);
                return full.StartsWith(winDir + "\\", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // 软件友好名：exe 的文件描述（FileDescription）优先，其次窗口标题，最后内部进程名
        static string GetDisplayName(string procName, string title, string path)
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

        // 查勾选进程的当前标题与 exe 路径（未运行返回 null）
        static void TryGetRunningInfo(string procName, out string title, out string path)
        {
            title = null; path = null;
            foreach (var p in Process.GetProcesses())
                if (string.Equals(p.ProcessName, procName, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(p.MainWindowTitle))
                        {
                            title = p.MainWindowTitle;
                            var mm = p.MainModule;
                            if (mm != null) path = mm.FileName;
                            return;
                        }
                    }
                    catch { }
                }
        }

        ListViewItem MkProcItem(string name, string displayName, string title, string path, int scale, bool hidden)
        {
            var it = new ListViewItem(displayName); // 图标 + 软件名称（勾选目标仍按内部进程名）
            it.ImageIndex = IconIndexFor(path);
            it.SubItems.Add(title);
            it.SubItems.Add(path ?? "");
            it.SubItems.Add(scale.ToString());
            it.Tag = name;
            it.Checked = hidden;
            return it;
        }

        // 工作线程：提取图标副本（ExtractAssociatedIcon 可能对网络路径/被占文件挂起，绝不能在 UI 线程调）
        Bitmap ExtractIconCopy(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return SystemIcons.Application.ToBitmap();
                using (var ic = Icon.ExtractAssociatedIcon(path))
                {
                    if (ic != null) return ic.ToBitmap();
                }
                return SystemIcons.Application.ToBitmap();
            }
            catch { return SystemIcons.Application.ToBitmap(); }
        }

        // 取进程图标索引（按 exe 路径缓存；读不到的用通用图标）
        int IconIndexFor(string path, Bitmap preExtracted = null)
        {
            string key = string.IsNullOrEmpty(path) ? "" : path.ToLowerInvariant();
            int idx;
            if (iconCache.TryGetValue(key, out idx)) return idx;
            Image img = preExtracted;
            if (img == null)
            {
                try
                {
                    if (string.IsNullOrEmpty(path)) img = SystemIcons.Application.ToBitmap();
                    else img = Icon.ExtractAssociatedIcon(path).ToBitmap();
                }
                catch { img = SystemIcons.Application.ToBitmap(); }
            }
            iconList.Images.Add(key, img);
            idx = iconList.Images.Count - 1;
            iconCache[key] = idx;
            return idx;
        }

        // 勾选变化（防抖后）：写回内存配置并持久化到 ini（勾选立即生效）
        void PersistChecks()
        {
            try
            {
                owner.cfg.Processes = GetCheckedProcesses();
                owner.cfg.Scale = GetScaleMap();
                Config.WriteCurrent(owner.cfg, owner.cfg.Path);
                owner.SyncCfgMTime();
            }
            catch { }
        }
    }

    // ---------- 窗口缩放弹窗 ----------
}
