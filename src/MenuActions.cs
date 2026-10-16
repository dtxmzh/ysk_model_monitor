using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace ModelMonitor;

public sealed class MenuItem
{
    public string id = "";
    public string label = "";
    public string type = "action";     // action | check | sub | header
    [System.Text.Json.Serialization.JsonPropertyName("checked")]
    public bool @checked;
    public string? tooltip;
    public List<MenuItem>? children;
}

/// <summary>设置菜单单一数据源：托盘 ContextMenuStrip 与悬浮窗 HTML 菜单都从这里构建。</summary>
public static class MenuActions
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "ysk_model_monitor";
    private const string RunValueOld = "yskModelMonitor";   // 改名前旧值，迁移时清掉

    public static List<MenuItem> Build()
    {
        var cfg = SettingsStore.Instance;
        var items = new List<MenuItem>();

        void H(string label) => items.Add(new MenuItem { type = "header", label = label });
        MenuItem A(string id, string label, string? tip = null) => new() { id = id, label = label, tooltip = tip };
        MenuItem C(string id, string label, bool on, string? tip = null) => new() { id = id, label = label, @checked = on, type = "check", tooltip = tip };

        H("显示");
        items.Add(A("act:toggle", "显示 / 隐藏主界面"));
        items.Add(C("act:topmost", "始终置于顶层", cfg.Topmost));
        items.Add(C("act:clickthrough", "鼠标穿透", cfg.ClickThrough, "点击穿过窗口直达桌面；与贴边自动隐藏互斥"));
        items.Add(C("act:autohide", "贴边自动隐藏", cfg.AutoHideDock, "贴住屏幕边缘后收起，悬停触发线 0.5s 展开；与鼠标穿透互斥"));
        items.Add(C("act:snap", "屏幕边缘吸附", cfg.Snap, "拖动松手时自动吸附最近的屏幕边缘"));
        items.Add(new MenuItem
        {
            id = "sub:theme", label = "主题", type = "sub",
            children = new List<MenuItem>
            {
                C("act:theme:light", "浅色", cfg.Theme == "light"),
                C("act:theme:dark", "深色", cfg.Theme == "dark"),
            }
        });
        items.Add(new MenuItem
        {
            id = "sub:zoom", label = $"界面缩放 {Math.Round(cfg.Zoom * 100)}%", type = "sub",
            children = new List<MenuItem>
            {
                A("act:zoom:-", "− 缩小 10%"),
                A("act:zoom:+", "＋ 放大 10%"),
            }
        });
        items.Add(new MenuItem
        {
            id = "sub:opacity", label = $"背景不透明度 {Math.Round(cfg.Opacity * 100)}%", type = "sub",
            children = new List<MenuItem>
            {
                A("act:opacity:-", "− 更透明 5%"),
                A("act:opacity:+", "＋ 更不透明 5%"),
            }
        });

        H("刷新");
        int iv = cfg.Interval;
        items.Add(new MenuItem
        {
            id = "sub:interval", label = $"刷新间隔（当前：{iv}s）", type = "sub",
            tooltip = "1–60 秒，步进 1 秒",
            children = new List<MenuItem>
            {
                C("act:interval:1", "1 秒（最快）", iv == 1),
                C("act:interval:2", "2 秒（默认）", iv == 2),
                C("act:interval:5", "5 秒", iv == 5),
                C("act:interval:10", "10 秒", iv == 10),
                C("act:interval:15", "15 秒", iv == 15),
                C("act:interval:30", "30 秒", iv == 30),
                C("act:interval:60", "60 秒（最慢）", iv == 60),
                new MenuItem { type = "header", label = "自定义（逐秒）" },
                A("act:interval:-", $"−1s（当前 {iv}s）"),
                A("act:interval:+", $"+1s（当前 {iv}s）"),
            }
        });
        items.Add(C("act:pause", "暂停刷新", cfg.Paused, "快捷键 Space"));
        string hb = cfg.HiddenBehavior;
        items.Add(new MenuItem
        {
            id = "sub:hidden", label = "窗口隐藏时", type = "sub",
            children = new List<MenuItem>
            {
                C("act:hidden:keep", "保持间隔", hb == "keep"),
                C("act:hidden:slow10", "降为 10s", hb == "slow10"),
                C("act:hidden:pause", "暂停", hb == "pause"),
            }
        });

        H("监控对象");
        foreach (var s in App.Reg.Services.Concat(App.Reg.Discovered))
        {
            var st = (App.Mon != null && App.Mon.SvcStates.TryGetValue(s.id, out var v)) ? v : null;
            string mark = st?.Online == true ? " · 在线" : " · 离线";
            items.Add(C("svc:" + s.id, s.name + mark, s.enabled));
        }
        items.Add(C("act:autodiscover", "自动发现新端口", cfg.AutoDiscover));
        items.Add(A("act:open-services", "打开 services.json"));
        items.Add(A("act:reload-services", "重载 services.json"));

        H("GPU");
        items.Add(C("act:gpu-table", "显示 GPU 进程归属表", cfg.ShowGpuTable));
        items.Add(C("act:topology", "显示服务↔GPU 拓扑图", cfg.ShowTopology));
        items.Add(C("act:nvidia-smi", "nvidia-smi 采集", cfg.NvidiaSmi, "关掉可完全停止进程调用"));

        H("其他");
        items.Add(C("act:autostart", "开机自启", cfg.Autostart, "写/删 HKCU\\...\\Run 一项"));
        items.Add(A("act:reset", "恢复默认设置"));
        items.Add(A("act:about", "关于 / 版本"));
        items.Add(A("act:quit", "退出"));

        return items;
    }

    public static void Execute(string rawId, MainWindow? win)
    {
        var cfg = SettingsStore.Instance;
        var id = rawId;
        while (id.StartsWith("act:")) id = id[4..];   // 托盘与 HTML 两路统一剥前缀（HTML 会带 act: 前缀发来）
        try
        {
            switch (id)
            {
                case "toggle": win?.ToggleVisible(); break;
                case "topmost": cfg.Topmost = !cfg.Topmost; win?.ApplyTopmost(); break;
                case "clickthrough":
                    cfg.ClickThrough = !cfg.ClickThrough;
                    if (cfg.ClickThrough && cfg.AutoHideDock) { cfg.AutoHideDock = false; win?.StopDocking(); }
                    win?.ApplyClickThrough();
                    break;
                case "autohide":
                    cfg.AutoHideDock = !cfg.AutoHideDock;
                    if (cfg.AutoHideDock && cfg.ClickThrough) { cfg.ClickThrough = false; win?.ApplyClickThrough(); }
                    win?.ApplyDocking();
                    break;
                case "snap": cfg.Snap = !cfg.Snap; break;
                case "theme:light": cfg.Theme = "light"; break;
                case "theme:dark": cfg.Theme = "dark"; break;
                case "zoom:-": cfg.Zoom = Math.Round(Compat.Clamp(cfg.Zoom - 0.1, 0.7, 1.5), 2); win?.ApplyZoom(); break;
                case "zoom:+": cfg.Zoom = Math.Round(Compat.Clamp(cfg.Zoom + 0.1, 0.7, 1.5), 2); win?.ApplyZoom(); break;
                case "opacity:-": cfg.Opacity = Math.Round(Compat.Clamp(cfg.Opacity - 0.05, 0.35, 1.0), 2); break;
                case "opacity:+": cfg.Opacity = Math.Round(Compat.Clamp(cfg.Opacity + 0.05, 0.35, 1.0), 2); break;
                case "pause": cfg.Paused = !cfg.Paused; break;
                case "hidden:keep": cfg.HiddenBehavior = "keep"; break;
                case "hidden:slow10": cfg.HiddenBehavior = "slow10"; break;
                case "hidden:pause": cfg.HiddenBehavior = "pause"; break;
                case "autodiscover": cfg.AutoDiscover = !cfg.AutoDiscover; break;
                case "gpu-table": cfg.ShowGpuTable = !cfg.ShowGpuTable; break;
                case "topology": cfg.ShowTopology = !cfg.ShowTopology; break;
                case "nvidia-smi": cfg.NvidiaSmi = !cfg.NvidiaSmi; break;
                case "autostart": SetAutostart(!cfg.Autostart); cfg.Autostart = !cfg.Autostart; break;
                case "reset": cfg.ResetToDefaults(); win?.ApplyAll(); break;
                case "about": win?.ShowAbout(); break;
                case "quit": win?.ExitApp(); break;
                case "open-services":
                    Process.Start(new ProcessStartInfo(AppPaths.File_("services.json")) { UseShellExecute = true });
                    break;
                case "reload-services":
                    App.Reg.Load(); App.Mon?.ReloadServices(); break;
                case "esc": win?.HideToTray(); break;
                case "ctrlq": win?.ExitApp(); break;
                case "t": cfg.Theme = cfg.Theme == "light" ? "dark" : "light"; break;
                case "p": cfg.Topmost = !cfg.Topmost; win?.ApplyTopmost(); break;
                case "space": cfg.Paused = !cfg.Paused; break;
                case "plus": cfg.Interval = Compat.Clamp(cfg.Interval + 1, 1, 60); win?.FlashInterval(); break;
                case "minus": cfg.Interval = Compat.Clamp(cfg.Interval - 1, 1, 60); win?.FlashInterval(); break;
                default:
                    if (id.StartsWith("interval:"))
                    {
                        var arg = id["interval:".Length..];
                        if (int.TryParse(arg, out int v)) cfg.Interval = v;
                        else if (arg == "+") cfg.Interval = Compat.Clamp(cfg.Interval + 1, 1, 60);
                        else if (arg == "-") cfg.Interval = Compat.Clamp(cfg.Interval - 1, 1, 60);
                        win?.FlashInterval();
                    }
                    else if (id.StartsWith("svc:"))
                    {
                        var svc = App.Reg.Find(id[4..]);
                        if (svc != null) { svc.enabled = !svc.enabled; App.Reg.SaveEnabledFlags(); }
                    }
                    else if (id.StartsWith("promote:"))
                    {
                        App.Reg.Promote(id[8..]); App.Mon?.ReloadServices();
                    }
                    break;
            }
        }
        catch (Exception e) { Logger.Warn("菜单动作失败 " + rawId + ": " + e.Message); }
    }

    public static void SetAutostart(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key == null) return;
            if (key.GetValue(RunValueOld) != null) key.DeleteValue(RunValueOld);
            if (on) key.SetValue(RunValue, "\"" + Compat.ExePath() + "\"");
            else if (key.GetValue(RunValue) != null) key.DeleteValue(RunValue);
        }
        catch (Exception e) { Logger.Warn("自启注册表写入失败: " + e.Message); }
    }

    public static string MenuTreeJson()
    {
        return JsonSerializer.Serialize(Build(), new JsonSerializerOptions
        {
            IncludeFields = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });
    }
}
