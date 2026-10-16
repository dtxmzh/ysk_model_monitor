using System.IO;

namespace ModelMonitor;

/// <summary>绿色性路径：exe 同目录 _data\；不可写时整体回退 %LOCALAPPDATA%\yskMonitor。</summary>
public static class AppPaths
{
    public static string DataDir { get; private set; } = "";
    public static bool FallbackUsed { get; private set; }

    public static void Init()
    {
        var exeDir = AppContext.BaseDirectory.TrimEnd('\\');
        var candidate = Path.Combine(exeDir, "_data");
        try
        {
            Directory.CreateDirectory(candidate);
            var probe = Path.Combine(candidate, ".writetest");
            File.WriteAllText(probe, "1");
            File.Delete(probe);
            DataDir = candidate;
            return;
        }
        catch { }
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "yskMonitor");
        Directory.CreateDirectory(local);
        DataDir = local;
        FallbackUsed = true;
    }

    public static string File_(string name) => Path.Combine(DataDir, name);
    public static string WebView2UserData => Path.Combine(DataDir, "webview2");
}
