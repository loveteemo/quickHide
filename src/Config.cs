// Config.cs —— quickHide 工程拆分文件（v1.3.0 起由单文件拆分而来）
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
// ---------- 配置 ----------
public class Config
{
    public List<string> Processes = new List<string>();
    public List<string> ExtraClasses = new List<string>();
    public string HideHotkey = "Ctrl+Alt+H";
    public string ShowHotkey = "Ctrl+Alt+J";
    public string ScaleHotkey = "Ctrl+Alt+S";
    public Dictionary<string, int> Scale = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // 规范化进程名 -> 窗口大小%（25-100，100=原始大小）
    public int WatchIntervalMs = 500;
    public bool RestoreOnExit = true;
    public bool HideTaskbarWhenScaled = false;
    public bool MuteOnHide = true; // 隐藏窗口时自动静音目标进程（还原时恢复原音量）
    public bool ShowBackgroundProcs = false; // 进程列表同时显示后台/托盘进程（默认关）
    public bool SuspendOnHide = false; // 隐藏时暂停目标进程（声音视频全停，还原恢复；比静音更彻底）
    public string ListMode = "process"; // 列表粒度：process=按进程 / window=按窗口（可分别设置每个窗口）
    public bool RemindOnRebind = true; // 热键换绑成功后弹窗提醒（关闭则在热键框后显示“设置完成”）
    public DateTime MTime; // 配置文件最后修改时间（自动重载检测用）
    public string Path;
    public int GetScale(string normProc)
    {
        int v;
        return Scale.TryGetValue(normProc, out v) ? v : 100;
    }
    public static Config Load(string path)
    {
        var c = new Config { Path = path };
        if (!File.Exists(path)) { WriteDefault(path); }
        try { c.MTime = File.GetLastWriteTimeUtc(path); } catch { }
        string section = "";
        foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
            if (line.StartsWith("[") && line.EndsWith("]")) { section = line.Substring(1, line.Length - 2).Trim().ToLowerInvariant(); continue; }
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line.Substring(0, eq).Trim().ToLowerInvariant();
            string val = line.Substring(eq + 1).Trim();
            switch (section)
            {
            case "targets":
                if (key == "processes") c.Processes = NormalizeProcs(ParseList(val));
                else if (key == "extraclasses") c.ExtraClasses = ParseList(val);
                break;
            case "processscale":
            {
                string pn = Config.NormProc(key);
                int v;
                if (pn.Length > 0 && int.TryParse(val, out v) && v >= 25 && v <= 100) c.Scale[pn] = v;
                break;
            }
            case "hotkeys":
                    if (key == "hide") c.HideHotkey = val;
                    else if (key == "show") c.ShowHotkey = val;
                    else if (key == "scale") c.ScaleHotkey = val;
                    break;
                case "options":
                    if (key == "watchintervalms") int.TryParse(val, out c.WatchIntervalMs);
                    else if (key == "restoreonexit") c.RestoreOnExit = val != "0";
                    else if (key == "hidetaskbarwhenscaled") c.HideTaskbarWhenScaled = val != "0";
                    else if (key == "muteonhide") c.MuteOnHide = val != "0";
                    else if (key == "showbackgroundprocs") c.ShowBackgroundProcs = val != "0";
                    else if (key == "suspendonhide") c.SuspendOnHide = val != "0";
                    else if (key == "listmode") c.ListMode = (val == "window") ? "window" : "process";
                    else if (key == "remidonrebind") c.RemindOnRebind = val != "0";
                    break;
            }
        }
        if (c.WatchIntervalMs < 100) c.WatchIntervalMs = 100;
        // 内置默认：始终自动附带 DNF 外置聊天窗口（无需在配置中单独列出）
        if (!c.ExtraClasses.Contains("dnf_wnd_chat")) c.ExtraClasses.Add("dnf_wnd_chat");
        return c;
    }
    public static List<string> ParseList(string val)
    {
        var list = new List<string>();
        foreach (string s in val.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            list.Add(s.Trim().ToLowerInvariant());
        return list;
    }
    // 统一进程名：小写、去掉 .exe 后缀（dnf.exe 与 dnf 视为同一进程，避免重复）
    public static string NormProc(string name)
    {
        string s = name.Trim().ToLowerInvariant();
        if (s.EndsWith(".exe")) s = s.Substring(0, s.Length - 4);
        return s;
    }
    // 规范化 + 去重（保持顺序）
    public static List<string> NormalizeProcs(List<string> list)
    {
        var result = new List<string>();
        foreach (string s in list)
        {
            string n = NormProc(s);
            if (n.Length > 0 && !result.Contains(n)) result.Add(n);
        }
        return result;
    }
    // 设置页保存：把当前配置写回 ini（文件仍是唯一事实来源）
    public static void WriteCurrent(Config c, string path)
    {
        string content =
"# quickHide 配置文件（保存后自动生效）\r\n" +
"[Targets]\r\n" +
"# 要一键隐藏的进程名，多个用逗号分隔（不区分大小写，可带可不带 .exe）\r\n" +
"# 例如: DNF.exe, douyin.exe\r\n" +
"Processes=" + string.Join(",", c.Processes.ToArray()) + "\r\n" +
"# 额外按窗口类名隐藏的窗口（用于跨进程的特殊窗口，如 DNF 外置聊天窗口）\r\n" +
"ExtraClasses=" + string.Join(",", c.ExtraClasses.ToArray()) + "\r\n" +
"\r\n[Hotkeys]\r\n" +
"# 自定义全局热键，可用修饰键: Ctrl Shift Alt Win，主键支持 A-Z 0-9 F1-F24\r\n" +
"# 隐藏/缩放热键各司其职：隐藏键只隐藏，缩放键只缩放\r\n" +
"Hide=" + c.HideHotkey + "\r\n" +
"Show=" + c.ShowHotkey + "\r\n" +
"Scale=" + c.ScaleHotkey + "\r\n" +
"\r\n[Options]\r\n" +
"# 隐藏期间自动补藏新出现窗口的轮询间隔（毫秒）\r\n" +
"WatchIntervalMs=" + c.WatchIntervalMs + "\r\n" +
"# 退出程序时是否自动还原被隐藏的窗口（1=是 0=否）\r\n" +
"RestoreOnExit=" + (c.RestoreOnExit ? "1" : "0") + "\r\n" +
"# 缩放窗口时是否同时隐藏任务栏按钮（1=是 0=否）\r\n" +
"HideTaskbarWhenScaled=" + (c.HideTaskbarWhenScaled ? "1" : "0") + "\r\n" +
            "MuteOnHide=" + (c.MuteOnHide ? "1" : "0") + "\r\n" +
            "ShowBackgroundProcs=" + (c.ShowBackgroundProcs ? "1" : "0") + "\r\n" +
            "SuspendOnHide=" + (c.SuspendOnHide ? "1" : "0") + "\r\n" +
            "ListMode=" + (c.ListMode == "window" ? "window" : "process") + "\r\n" +
            "RemindOnRebind=" + (c.RemindOnRebind ? "1" : "0") + "\r\n";
        var scaleLines = new StringBuilder();
        foreach (KeyValuePair<string, int> kv in c.Scale)
        {
            if (kv.Value == 100) continue;
            scaleLines.Append(kv.Key + "=" + kv.Value + "\r\n");
        }
        if (scaleLines.Length > 0)
            content += "\r\n[ProcessScale]\r\n" + scaleLines.ToString();
        File.WriteAllText(path, content, new UTF8Encoding(true));
    }
    static void WriteDefault(string path)
    {
        const string def =
@"# quickHide 配置文件（修改后重启程序或从托盘菜单选择“重载配置”生效）
[Targets]
# 要一键隐藏的进程名，多个用逗号分隔（不区分大小写，可带可不带 .exe）
# 例如: DNF.exe, douyin.exe
Processes=DNF.exe
# 额外按窗口类名隐藏的窗口（用于跨进程的特殊窗口，如 DNF 外置聊天窗口）
ExtraClasses=DNF_WND_CHAT
[Hotkeys]
# 自定义全局热键，可用修饰键: Ctrl Shift Alt Win，主键支持 A-Z 0-9 F1-F24
# Hide/Show: 按进程一键隐藏/还原；Scale: 只按比例缩放，不隐藏
Hide=Ctrl+Alt+H
Show=Ctrl+Alt+J
Scale=Ctrl+Alt+S
[Options]
# 隐藏期间自动补藏新出现窗口的轮询间隔（毫秒）
WatchIntervalMs=500
# 退出程序时是否自动还原被隐藏的窗口（1=是 0=否）
RestoreOnExit=1
# 缩放窗口时是否同时隐藏任务栏按钮（1=是 0=否）
HideTaskbarWhenScaled=0
# 隐藏窗口时自动静音目标进程（还原时恢复原音量；1=是 0=否）
MuteOnHide=1
# 进程列表同时显示后台/托盘进程（1=是 0=否）
ShowBackgroundProcs=0
# 列表粒度：process=按进程（默认）/ window=按窗口（可分别设置每个窗口）
# 隐藏时暂停目标进程（声音视频全停，还原恢复；1=是 0=否，默认 0=静音模式）
SuspendOnHide=0
# 列表粒度：process=按进程（默认）/ window=按窗口（可分别设置每个窗口）
ListMode=process
# 热键换绑成功后弹窗提醒（关闭则在热键框后显示“设置完成”；1=是 0=否）
RemindOnRebind=1
";
        File.WriteAllText(path, def, new UTF8Encoding(true));
    }
}
}