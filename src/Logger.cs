using System.IO;

namespace ModelMonitor;

/// <summary>极简滚动日志：exe 同目录 _data\monitor.log，超 1MB 截半。</summary>
public static class Logger
{
    private static readonly object Gate = new();
    private static string Path_ = "";

    public static void Init(string dataDir)
    {
        Path_ = Path.Combine(dataDir, "monitor.log");
    }

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Err(string msg) => Write("ERR ", msg);

    private static void Write(string level, string msg)
    {
        try
        {
            lock (Gate)
            {
                if (string.IsNullOrEmpty(Path_)) return;
                var fi = new FileInfo(Path_);
                if (fi.Exists && fi.Length > 1_000_000)
                {
                    // 保留后半段
                    var all = File.ReadAllText(Path_);
                    File.WriteAllText(Path_, all.Substring(all.Length / 2));
                }
                File.AppendAllText(Path_, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {msg}\n");
            }
        }
        catch { /* 日志绝不引发崩溃 */ }
    }
}
