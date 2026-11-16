// AudioMuter.cs —— quickHide 工程拆分文件（v1.3.0 起由单文件拆分而来）
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
// ---------- 进程音频静音（WASAPI，按会话精确控制）----------

static class AudioMuter
{
    // 被我们静音的会话 -> 静音前的音量（还原时恢复）
    static readonly Dictionary<string, float> saved = new Dictionary<string, float>();
    static bool comInitDone;
    // COM 会话级静音不可用时的兜底：挂起目标进程（NtSuspendProcess，不依赖 MMDevApi）
    // 挂起 = 目标进程完全暂停（声音、视频、后台活动全部停止），还原时 NtResumeProcess 恢复
    static readonly List<uint> suspendedPids = new List<uint>();
    static bool suspendStateDirty;

    [DllImport("ole32.dll")]
    static extern int CoInitializeEx(IntPtr reserved, int coInit);

    [DllImport("ntdll.dll")]
    static extern int NtSuspendProcess(IntPtr processHandle);
    [DllImport("ntdll.dll")]
    static extern int NtResumeProcess(IntPtr processHandle);

    const uint PROCESS_SUSPEND_RESUME = 0x0800;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);

    static string SuspendStateFile()
    {
        return Path.Combine(quickHide.Program.DataDir, "suspend_state.txt");
    }

    // 挂起状态落盘：本进程被下一个实例强杀时，由新实例恢复这些进程的运行
    static void SaveSuspendState()
    {
        try { File.WriteAllText(SuspendStateFile(), string.Join(",", suspendedPids)); } catch { }
    }

    static void ClearSuspendState()
    {
        try { if (File.Exists(SuspendStateFile())) File.Delete(SuspendStateFile()); } catch { }
    }

    // 新实例启动时调用：上一个实例若被强杀且还有挂起中的进程，恢复它们的运行
    public static void ResumePendingSuspended()
    {
        try
        {
            if (!File.Exists(SuspendStateFile())) return;
            string raw = File.ReadAllText(SuspendStateFile()).Trim();
            File.Delete(SuspendStateFile());
            if (raw.Length == 0) return;
            foreach (string s in raw.Split(','))
            {
                uint pid;
                if (!uint.TryParse(s, out pid)) continue;
                IntPtr h = OpenProcess(PROCESS_SUSPEND_RESUME, false, pid);
                if (h != IntPtr.Zero)
                {
                    NtResumeProcess(h);
                    CloseHandle(h);
                    Program.LogText("[AUDIO] 已恢复上一个实例挂起的进程 pid=" + pid);
                }
            }
        }
        catch { }
        suspendedPids.Clear();
    }

    // 挂起目标进程集合（COM 会话级静音失败时兜底）
    internal static void SuspendTargets(HashSet<uint> pids)
    {
        foreach (uint pid in pids)
        {
            if (suspendedPids.Contains(pid)) continue;
            IntPtr h = OpenProcess(PROCESS_SUSPEND_RESUME, false, pid);
            if (h == IntPtr.Zero) continue;
            if (NtSuspendProcess(h) == 0)
            {
                suspendedPids.Add(pid);
                // 保持句柄打开：挂起计数与句柄相关，恢复时统一 NtResumeProcess + CloseHandle
                lock (_suspendHandles) _suspendHandles[pid] = h;
                suspendStateDirty = true;
            }
            else CloseHandle(h);
        }
        if (suspendStateDirty) SaveSuspendState();
    }

    // 恢复单个被挂起的进程（取消勾选显示窗口时调用）
    internal static void ResumePid(uint pid)
    {
        IntPtr h;
        bool was = false;
        if (_suspendHandles.TryGetValue(pid, out h))
        {
            NtResumeProcess(h);
            CloseHandle(h);
            _suspendHandles.Remove(pid);
            was = true;
        }
        else if (suspendedPids.Contains(pid))
        {
            h = OpenProcess(PROCESS_SUSPEND_RESUME, false, pid);
            if (h != IntPtr.Zero)
            {
                NtResumeProcess(h);
                CloseHandle(h);
                was = true;
            }
        }
        if (was)
        {
            suspendedPids.Remove(pid);
            SaveSuspendState();
            Program.LogText("[AUDIO] 已恢复进程运行 pid=" + pid);
        }
    }

    // 恢复所有挂起的目标进程
    internal static void ResumeTargets()
    {
        foreach (uint pid in suspendedPids)
        {
            IntPtr h;
            if (_suspendHandles.TryGetValue(pid, out h))
            {
                NtResumeProcess(h);
                CloseHandle(h);
            }
            else
            {
                IntPtr h2 = OpenProcess(PROCESS_SUSPEND_RESUME, false, pid);
                if (h2 != IntPtr.Zero)
                {
                    NtResumeProcess(h2);
                    CloseHandle(h2);
                }
            }
        }
        if (suspendedPids.Count > 0)
            Program.LogText("[AUDIO] 已恢复挂起的目标进程：" + string.Join(",", suspendedPids));
        _suspendHandles.Clear();
        suspendedPids.Clear();
        ClearSuspendState();
    }

    static readonly Dictionary<uint, IntPtr> _suspendHandles = new Dictionary<uint, IntPtr>();

    public static void MuteProcesses(HashSet<uint> pids, bool allowSuspendFallback = true)
    {
        if (pids == null || pids.Count == 0) return;
        int hit = 0;
        bool comOk = ForEachSession((c2, vol) =>
        {
            uint spid;
            if (c2.GetProcessId(out spid) == 0 && pids.Contains(spid))
            {
                IntPtr idPtr;
                if (c2.GetSessionInstanceIdentifier(out idPtr) == 0)
                {
                    string key = Marshal.PtrToStringUni(idPtr);
                    Marshal.FreeCoTaskMem(idPtr);
                    if (!string.IsNullOrEmpty(key) && !saved.ContainsKey(key))
                    {
                        float v;
                        if (vol.GetMasterVolume(out v) == 0)
                        {
                            saved[key] = v;
                            vol.SetMasterVolume(0f, IntPtr.Zero);
                            hit++;
                        }
                    }
                }
            }
            return true;
        });
        if (comOk && (hit > 0 || saved.Count > 0))
        {
            Program.LogText("[AUDIO] 会话级静音命中 " + hit + " 个会话");
            return;
        }
        // COM 会话枚举失败 → 兜底挂起；但"仍有可见窗口"的调用方禁止挂起（避免可见窗口假死）
        if (!allowSuspendFallback) return;
        SuspendTargets(pids);
    }

    public static void RestoreAllMutes()
    {
        if (saved.Count > 0)
        {
            ForEachSession((c2, vol) =>
            {
                IntPtr idPtr;
                if (c2.GetSessionInstanceIdentifier(out idPtr) == 0)
                {
                    string key = Marshal.PtrToStringUni(idPtr);
                    Marshal.FreeCoTaskMem(idPtr);
                    if (!string.IsNullOrEmpty(key) && saved.ContainsKey(key))
                    {
                        vol.SetMasterVolume(saved[key], IntPtr.Zero);
                        saved.Remove(key);
                    }
                }
                return true;
            });
        }
        saved.Clear(); // 已消失的会话（进程退出）音量随进程消亡，无需还原
        ResumeTargets();
    }

    // 还原窗口前调用：先把挂起的目标进程恢复运行（再显示窗口，避免展示冻结画面）
    public static void ResumeAllSuspended()
    {
        ResumeTargets();
    }

    static bool ForEachSession(Func<IAudioSessionControl2, ISimpleAudioVolume, bool> action)
    {
        try
        {
            if (!comInitDone)
            {
                CoInitializeEx(IntPtr.Zero, 0);
                comInitDone = true;
            }
            var enumeratorCom = new MMDeviceEnumerator();
            IMMDevice device;
            ((IMMDeviceEnumerator)enumeratorCom).GetDefaultAudioEndpoint(0, 1, out device);
            object mgrObj;
            var iid = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A7B7F");
            device.Activate(ref iid, 0x17, IntPtr.Zero, out mgrObj);
            var mgr = (IAudioSessionManager2)mgrObj;

            IAudioSessionEnumerator sessions;
            mgr.GetSessionEnumerator(out sessions);
            int count;
            sessions.GetCount(out count);
            Program.LogText("[AUDIO] sessions=" + count);
            for (int i = 0; i < count; i++)
            {
                IAudioSessionControl control;
                sessions.GetSession(i, out control);
                var c2 = (IAudioSessionControl2)control;
                var vol = (ISimpleAudioVolume)control;
                bool cont = action(c2, vol);
                Marshal.ReleaseComObject(control);
                if (!cont) break;
            }
            Marshal.ReleaseComObject(sessions);
            Marshal.ReleaseComObject(mgr);
            Marshal.ReleaseComObject(device);
            Marshal.ReleaseComObject(enumeratorCom);
            return true;
        }
        catch (Exception ex)
        {
            Program.LogText("[AUDIO] 会话枚举失败：" + ex.Message);
            return false; // COM 会话级静音不可用，走设备级兜底
        }
    }

    // 以下 COM 接口声明严格按 vtable 顺序（未用的方法也要占位）
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumerator { }

    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        int GetDevice(string id, out IMMDevice device);
        int RegisterEndpointNotificationCallback(IntPtr cb);
        int UnregisterEndpointNotificationCallback(IntPtr cb);
    }

    [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        int OpenPropertyStore(int access, out IntPtr store);
        int GetId(out IntPtr id);
        int GetState(out int state);
    }

    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A7B7F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        // IAudioSessionManager
        int GetAudioSessionControl(IntPtr audioSessionGuid, int flags, out IntPtr control);
        int GetSimpleAudioVolume(IntPtr audioSessionGuid, int flags, out IntPtr volume);
        // IAudioSessionManager2
        int GetSessionEnumerator(out IAudioSessionEnumerator enumerator);
        int RegisterSessionNotification(IntPtr notification);
        int UnregisterSessionNotification(IntPtr notification);
        int RegisterDuckNotification(IntPtr systemSound, IntPtr duckNotification);
        int UnregisterDuckNotification(IntPtr duckNotification);
    }

    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        int GetCount(out int count);
        int GetSession(int index, out IAudioSessionControl session);
    }

    [Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl
    {
        int GetState(out int state);
        int GetDisplayName(out IntPtr displayName);
        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string displayName);
        int GetIconPath(out IntPtr iconPath);
        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string iconPath);
        int GetGroupingParam(out Guid groupingParam);
        int SetGroupingParam(ref Guid groupingParam, IntPtr ctx);
        int RegisterAudioSessionNotification(IntPtr notification);
        int UnregisterAudioSessionNotification(IntPtr notification);
    }

    [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        // IAudioSessionControl
        int GetState(out int state);
        int GetDisplayName(out IntPtr displayName);
        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string displayName);
        int GetIconPath(out IntPtr iconPath);
        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string iconPath);
        int GetGroupingParam(out Guid groupingParam);
        int SetGroupingParam(ref Guid groupingParam, IntPtr ctx);
        int RegisterAudioSessionNotification(IntPtr notification);
        int UnregisterAudioSessionNotification(IntPtr notification);
        // IAudioSessionControl2
        int GetSessionIdentifier(out IntPtr identifier);
        int GetSessionInstanceIdentifier(out IntPtr identifier);
        int GetProcessId(out uint processId);
        int IsSystemSoundsSession();
        int SetDuckingPreference(bool optOut);
    }

    [Guid("87CE5498-68D6-44E5-921F-39BDF43204AF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISimpleAudioVolume
    {
        int SetMasterVolume(float level, IntPtr ctx);
        int GetMasterVolume(out float level);
        int SetMute(bool mute, IntPtr ctx);
        int GetMute(out bool mute);
    }
}
}
