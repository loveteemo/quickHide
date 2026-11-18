// Program.cs —— quickHide 工程拆分文件（v1.3.0 起由单文件拆分而来）
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
static class Program
{
    public const string Version = "0.1.35";
    public const string ApiBase = "https://loveteemo.com";
    // 反馈入口：跳到软件发布详情页
    public const string FeedbackUrl = "https://loveteemo.com/software/QuickHide";
    // 数据目录（ini/日志/状态文件统一放这里）
    public const string ReleasePageUrl = ApiBase + "/software";
    public const string UpdateApiUrl = ApiBase + "/web/software/latest?app=QuickHide";
    internal static string DataDir;
    [STAThread]
    static void Main(string[] args)
    {
        // 数据目录固定在 %APPDATA%\quickHide（与 exe 位置无关，dist 版本目录下配置/状态不丢失）
        DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "quickHide");
        try { if (!Directory.Exists(DataDir)) Directory.CreateDirectory(DataDir); } catch { }
        // 旧版配置迁移：exe 目录里有 ini 而 AppData 还没有 → 拷贝过去
        try
        {
            string exeIni = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "quickHide.ini");
            if (!File.Exists(Path.Combine(DataDir, "quickHide.ini")) && File.Exists(exeIni))
                File.Copy(exeIni, Path.Combine(DataDir, "quickHide.ini"));
        }
        catch { }
        string configPath = Path.Combine(DataDir, "quickHide.ini");
        string mode = "gui";
        foreach (string a in args)
        {
            string t = a.ToLowerInvariant();
            if (t.StartsWith("--config=") || t.StartsWith("/config=")) configPath = a.Substring(a.IndexOf('=') + 1).Trim('"');
            else if (t == "--hide" || t == "/hide") mode = "hide";
            else if (t == "--show" || t == "/show") mode = "show";
            else if (t == "--toggle" || t == "/toggle") mode = "toggle";
            else if (t == "--status" || t == "/status") mode = "status";
        }

        Config cfg = Config.Load(configPath);
        HideEngine engine = new HideEngine(cfg);

        if (mode != "gui")
        {
            RunCli(engine, mode);
            return;
        }

        // 升级即接管：先结束正在运行的旧实例（quickHide* 前缀匹配，含版本后缀命名），
        // WaitForExit 确保旧实例的热键/Mutex 已释放，再由本实例拿锁接管。
        try
        {
            int selfPid = Process.GetCurrentProcess().Id;
            foreach (Process p in Process.GetProcesses())
            {
                if (!p.ProcessName.StartsWith("quickHide", StringComparison.OrdinalIgnoreCase) || p.Id == selfPid) continue;
                try
                {
                    p.Kill();
                    p.WaitForExit(1500);
                }
                catch { }
                p.Dispose();
            }
        }
        catch { }

        // 单实例锁（旧实例已退出，正常必然拿到；拿不到说明有杀不掉的实例，直接退出不重复运行）
        bool mutexOwner;
        var instMutex = new Mutex(true, "Local\\quickHide_SingleInstance", out mutexOwner);
        if (!mutexOwner) return; // 已有实例在运行

        AudioMuter.ResumePendingSuspended();

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (s, e) => LogUiError(e.Exception);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        AppDomain.CurrentDomain.UnhandledException += (s, e) => LogUiError(e.ExceptionObject as Exception);

        Application.Run(new TrayContext(cfg, engine));
        GC.KeepAlive(instMutex); // 保持 Mutex 到进程退出
    }

    internal static void LogUiError(Exception ex)
    {
        LogText(ex == null ? "(unknown)" : ex.ToString());
    }

    public static void LogText(string msg)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(DataDir, "ui_error.log"),
                DateTime.Now + "  " + msg + "\r\n\r\n");
        }
        catch { }
    }

    internal static string latestSha; // 最近一次检查更新拿到的官方安装包 SHA256

    // 拉取发布服务上的最新版本；失败返回 null（error 为失败原因）
    internal static string FetchLatestVersion(out string date, out string notes, out string error)
    {
        date = null;
        notes = null;
        error = null;
        latestSha = null;
        try
        {
            System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
            using (var wc = new System.Net.WebClient())
            {
                wc.Encoding = Encoding.UTF8;
                wc.Headers[System.Net.HttpRequestHeader.UserAgent] = "quickHide-Client";
                string json = wc.DownloadString(UpdateApiUrl);
                var resp = new System.Web.Script.Serialization.JavaScriptSerializer()
                    .Deserialize<Dictionary<string, object>>(json);
                if (Convert.ToInt32(resp["code"]) == 0)
                {
                    var data = resp["data"] as Dictionary<string, object>;
                    if (data != null)
                    {
                        date = Convert.ToString(data["create_time"]);
                        notes = Convert.ToString(data["release_notes"]);
                        latestSha = Convert.ToString(data.ContainsKey("sha256") ? data["sha256"] : "");
                        return Convert.ToString(data["version"]);
                    }
                }
                else
                {
                    error = Convert.ToString(resp.ContainsKey("msg") ? resp["msg"] : resp["message"]);
                }
            }
        }
        catch
        {
            error = "检查失败：网络不可用或发布服务未部署";
        }
        return null;
    }

    internal static int CompareVersion(string a, string b)
    {
        string[] pa = (a ?? "").Split('.');
        string[] pb = (b ?? "").Split('.');
        for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            int x = 0, y = 0;
            if (i < pa.Length) int.TryParse(pa[i], out x);
            if (i < pb.Length) int.TryParse(pb[i], out y);
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    static void RunCli(HideEngine engine, string mode)
    {
        switch (mode)
        {
            case "hide":
                {
                    int n = engine.HideAll();
                    Console.WriteLine("OK hidden=" + n);
                    break;
                }
            case "show":
                {
                    int n = engine.RestoreAll();
                    Console.WriteLine("OK restored=" + n);
                    break;
                }
            case "toggle":
                {
                    if (engine.HiddenCount > 0)
                    {
                        int n = engine.RestoreAll();
                        Console.WriteLine("OK restored=" + n);
                    }
                    else
                    {
                        int n = engine.HideAll();
                        Console.WriteLine("OK hidden=" + n);
                    }
                    break;
                }
            case "status":
                {
                    Console.WriteLine("hidden_count=" + engine.HiddenCount);
                    foreach (var e in engine.Entries) Console.WriteLine("hidden hwnd=0x{0:X} pid={1} class={2}", e.Hwnd.ToInt64(), e.Pid, e.Class);
                    break;
                }
        }
        // ShowWindowAsync 是异步的，给目标窗口线程一点时间
        Thread.Sleep(engine.LastActionWindowCount > 0 ? 600 : 0);
    }
}
}
