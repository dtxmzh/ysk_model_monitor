// 1.0.2 兼容层：同一套源码同时编译 net10（本机主力）与 net48（Win7SP1~Win11 轻量版）。
// 统一入口 Compat.* / CompatExt.*：net10 走原生 BCL，net48 走手写实现。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ModelMonitor;

internal static class Compat
{
#if NETFRAMEWORK
    public static int ProcessId() => Process.GetCurrentProcess().Id;
    public static string ExePath() => Process.GetCurrentProcess().MainModule.FileName;

    private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    public static long Tick64() => (long)((DateTime.UtcNow - Epoch).TotalMilliseconds);

    public static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);
    public static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

    /// <summary>近似 Kill(entireProcessTree:true)：WMI 递归找子进程逐个杀，再杀本体。</summary>
    public static void KillTree(Process p)
    {
        foreach (var c in ChildProcs(p.Id))
        {
            try { KillTree(c); } catch { }
            finally { try { c.Dispose(); } catch { } }
        }
        try { p.Kill(); } catch { }
    }

    private static List<Process> ChildProcs(int pid)
    {
        var list = new List<Process>();
        try
        {
            using (var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT ProcessId FROM Win32_Process WHERE ParentProcessId=" + pid))
                foreach (var o in searcher.Get())
                {
                    int childPid = Convert.ToInt32(o["ProcessId"]);
                    try { list.Add(Process.GetProcessById(childPid)); } catch { }
                }
        }
        catch { }
        return list;
    }

    public static async Task<string> ReadStr(HttpContent content, CancellationToken ct)
    {
        var t = content.ReadAsStringAsync();
        var done = await Task.WhenAny(t, Task.Delay(Timeout.Infinite, ct));
        if (done != t) throw new OperationCanceledException(ct);
        return await t;
    }

    public static async Task<string> ReadToEndTok(StreamReader reader, CancellationToken ct)
    {
        var t = reader.ReadToEndAsync();
        var done = await Task.WhenAny(t, Task.Delay(Timeout.Infinite, ct));
        if (done != t) throw new OperationCanceledException(ct);
        return await t;
    }

    public static async Task WaitExitTok(Process p, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>();
        EventHandler h = (_, __) => tcs.TrySetResult(true);
        try { p.Exited += h; } catch { }
        try
        {
            p.EnableRaisingEvents = true;
            if (p.HasExited) return;
            bool exited;
            using (ct.Register(() => tcs.TrySetResult(false)))
                exited = await tcs.Task;
            if (!exited) throw new OperationCanceledException(ct);
        }
        finally { try { p.Exited -= h; } catch { } }
    }

    public static async Task<string> GetStr(HttpClient http, string url, CancellationToken ct)
    {
        using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            return await ReadStr(resp.Content, ct);
        }
    }
#else
    public static int ProcessId() => Environment.ProcessId;
    public static string ExePath() => Environment.ProcessPath!;
    public static long Tick64() => Environment.TickCount64;
    public static double Clamp(double v, double lo, double hi) => Math.Clamp(v, lo, hi);
    public static int Clamp(int v, int lo, int hi) => Math.Clamp(v, lo, hi);
    public static void KillTree(Process p) => p.Kill(entireProcessTree: true);
    public static Task<string> ReadStr(HttpContent c, CancellationToken ct) => c.ReadAsStringAsync(ct);
    public static Task<string> ReadToEndTok(StreamReader r, CancellationToken ct) => r.ReadToEndAsync(ct);
    public static Task WaitExitTok(Process p, CancellationToken ct) => p.WaitForExitAsync(ct);
    public static Task<string> GetStr(HttpClient h, string url, CancellationToken ct) => h.GetStringAsync(url, ct);
#endif
}

internal static class CompatExt
{
#if NETFRAMEWORK
    public static HashSet<T> ToHashSetOf<T>(this IEnumerable<T> src) => new HashSet<T>(src);

    // net48 无 ConcurrentDictionary.TryAdd：ContainsKey+索引器近似（busy 标志竞态窗口极小，多跑一轮无害）
    public static bool TryAddEx<T>(this System.Collections.Concurrent.ConcurrentDictionary<T, byte> d, T k)
    {
        if (d.ContainsKey(k)) return false;
        d[k] = 0;
        return true;
    }

    public static V ValOrDef<K, V>(this Dictionary<K, V> d, K k)
    {
        V v;
        return d.TryGetValue(k, out v) ? v : default(V);
    }
#else
    public static HashSet<T> ToHashSetOf<T>(this IEnumerable<T> src) => src.ToHashSet();
    public static bool TryAddEx<T>(this System.Collections.Concurrent.ConcurrentDictionary<T, byte> d, T k) => d.TryAdd(k, (byte)0);
    public static V ValOrDef<K, V>(this Dictionary<K, V> d, K k) => d.GetValueOrDefault(k);
#endif
}
