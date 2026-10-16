using System;
using System.IO;
using System.Windows;

namespace ModelMonitor;

/// <summary>纯代码 WPF 应用（无 XAML，避免 WPF/WinForms 双 Main 生成器冲突）。</summary>
public sealed class App : System.Windows.Application
{
    public static ServiceRegistry Reg = null!;
    public static Monitor? Mon;
    public static MainWindow? Main;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        AppPaths.Init();
        Logger.Init(AppPaths.DataDir);
        if (AppPaths.FallbackUsed) Logger.Warn("exe 目录不可写，数据回退到 " + AppPaths.DataDir);

        SettingsStore.Instance.Load();
        Reg = new ServiceRegistry();
        Reg.Load();

        Main = new MainWindow();
        Main.Show();
        Logger.Info($"启动完成 pid={Compat.ProcessId()} data={AppPaths.DataDir}");
    }
}

public static class Program
{
    [STAThread]
    public static void Main()
    {
        var app = new App();
        app.Run();
    }
}
