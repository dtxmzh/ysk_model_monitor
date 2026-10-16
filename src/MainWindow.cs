using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;
using MessageBox = System.Windows.MessageBox;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace ModelMonitor;

public sealed class MainWindow : Window
{
    private readonly WebView2 Web;
    private WinForms.NotifyIcon? _tray;
    private WinForms.ContextMenuStrip? _trayMenu;
    private Drawing.Icon? _trayIcon;
    private HwndSource? _hwndSource;
    private Monitor _mon = null!;
    private bool _exiting;
    private bool _hidden;
    private int _lastTrayState = -1;
    private readonly DispatcherTimer _geoTimer;

    // ---- 鼠标穿透 / 贴边自动隐藏 / 边缘吸附 ----
    Window? _stripWin;          // 触发线 = 独立无边框小窗（主窗收起时整体隐藏，根治 WebView2 残影蓝条）
    enum DockState { None, Collapsed, Expanded }
    private DockState _dock = DockState.None;
    private string _dockEdge = "";
    private double _savedL, _savedT, _savedW, _savedH;
    private DispatcherTimer? _dockPoll;
    private DateTime? _hoverSince;

    public MainWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;   // 不能 null，null 不吃命中测试
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = true;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        Title = "本机模型监控";

        var cfg = SettingsStore.Instance;
        Width = Compat.Clamp(cfg.Width, 360, 4000);
        Height = Compat.Clamp(cfg.Height, 420, 4000);
        Topmost = cfg.Topmost;
        MinWidth = 360; MinHeight = 420;

        // 8px 隐形可命中内衬（alpha=1 肉眼不可见）：WebView2 是子 HWND，边缘像素留在 WPF 侧才能收 NCHITTEST
        var grid = new Grid();
        grid.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) });
        Web = new WebView2 { Margin = new Thickness(8) };
        grid.Children.Add(Web);
        Content = grid;

        _geoTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _geoTimer.Tick += (_, _) => { _geoTimer.Stop(); SaveGeometry(); };

        App.Mon = _mon = new Monitor(App.Reg);
        _mon.SnapshotReady += OnSnapshot;
        SettingsStore.Instance.Changed += OnSettingsChanged;
    }

    // ================= 初始化 =================

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource?.AddHook(WndProc);

        // 禁用 Windows 自带贴边吸附（Aero Snap 半屏/最大化）：去掉 WS_MAXIMIZEBOX 后，
        // 系统拖到边缘不再弹分屏预览；WM_GETMINMAXINFO 兜底锁尺寸
        int st = GetWindowLong(hwnd, GWL_STYLE);
        SetWindowLong(hwnd, GWL_STYLE, st & ~WS_MAXIMIZEBOX);

        RestoreOrPlaceWindow();
        InitTray();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        // 恢复上次退出时的穿透/贴边开关状态
        if (SettingsStore.Instance.ClickThrough) ApplyClickThrough();
        if (SettingsStore.Instance.AutoHideDock) ApplyDocking();

        _ = InitWebViewAsync();
        _mon.Start();
    }

    private void OnDisplayChanged(object? s, EventArgs e)
    {
        try { Dispatcher.Invoke(ClampToWorkArea); } catch { }
    }

    private void RestoreOrPlaceWindow()
    {
        var cfg = SettingsStore.Instance;
        var wa = SystemParameters.WorkArea;
        if (cfg.Left > 1 || cfg.Top > 1)
        {
            Left = cfg.Left; Top = cfg.Top;
            ClampToWorkArea();
        }
        else
        {
            Left = wa.Right - 24 - Width;
            Top = wa.Top + 24;
        }
    }

    private void ClampToWorkArea()
    {
        if (_dock != DockState.None) return;   // 收起/展开态位置由贴边逻辑管，别用 80px 容差钳坏
        var wa = SystemParameters.WorkArea;
        Width = Math.Min(Width, wa.Width);
        Height = Math.Min(Height, wa.Height);
        Left = Compat.Clamp(Left, wa.Left - Width + 80, wa.Right - 80);
        Top = Compat.Clamp(Top, wa.Top, Math.Max(wa.Top, wa.Bottom - 60));
    }

    private async System.Threading.Tasks.Task InitWebViewAsync()
    {
        try
        {
            // 透明根因坑：必须在 EnsureCoreWebView2Async 之前设 alpha=0
            Web.DefaultBackgroundColor = Drawing.Color.FromArgb(0, 0, 0, 0);
            Directory.CreateDirectory(AppPaths.WebView2UserData);
            // 1.0.2 资源优化：V8 老生代堆封顶 96MB（监控页极小，防长跑缓涨）+ 关后台限流豁免
            var envOpts = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = "--js-flags=--max-old-space-size=96 --renderer-process-limit=1 --disable-features=BackForwardCache",
            };
            var env = await CoreWebView2Environment.CreateAsync(null, AppPaths.WebView2UserData, envOpts);
            await Web.EnsureCoreWebView2Async(env);

            var s = Web.CoreWebView2.Settings;
            s.IsStatusBarEnabled = false;
            s.AreDefaultContextMenusEnabled = false;
            s.IsZoomControlEnabled = false;
            s.AreDevToolsEnabled = true;
            s.IsSwipeNavigationEnabled = false;

            Web.CoreWebView2.WebMessageReceived += (_, e) =>
                Dispatcher.BeginInvoke(() => HandleMessage(e.TryGetWebMessageAsString() ?? ""));

            string www = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            if (!Directory.Exists(www))
                www = Path.Combine(Path.GetDirectoryName(Compat.ExePath()) ?? ".", "wwwroot");
            Web.CoreWebView2.SetVirtualHostNameToFolderMapping("app.local", www, CoreWebView2HostResourceAccessKind.Allow);
            Web.CoreWebView2.Navigate("https://app.local/index.html");

            Web.CoreWebView2.NavigationCompleted += (_, e) =>
            {
                if (e.IsSuccess)
                {
                    Logger.Info("WebView2 页面就绪");
                    PushMenu();
                    PushSnapshotNow();
                }
                else Logger.Warn("页面加载失败: " + e.WebErrorStatus);
            };
            ApplyZoom();
        }
        catch (Exception e)
        {
            Logger.Err("WebView2 初始化失败: " + e);
            MessageBox.Show("WebView2 运行时初始化失败：\n" + e.Message, "本机模型监控",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ================= 消息桥 =================

    private void HandleMessage(string msg)
    {
        try
        {
            if (msg == "menu:query") { PushMenu(); return; }
            if (msg.StartsWith("menu:")) { MenuActions.Execute(msg[5..], this); return; }
            if (msg.StartsWith("act:")) { MenuActions.Execute(msg, this); return; }
            switch (msg)
            {
                case "win:drag":
                {
                    var hwnd = new WindowInteropHelper(this).Handle;
                    ReleaseCapture();
                    SendMessage(hwnd, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
                    break;
                }
                case "win:min": HideToTray(); break;
                default:
                    if (msg.StartsWith("order:"))
                        SettingsStore.Instance.SetSilent("order", System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonArray>(msg[6..]));
                    else if (msg.StartsWith("scroll:"))
                        SettingsStore.Instance.SetSilent("scroll", double.TryParse(msg[7..], out var sc) ? sc : 0);
                    break;
            }
        }
        catch (Exception e) { Logger.Warn("消息处理失败 [" + msg + "]: " + e.Message); }
    }

    private void OnSnapshot(string json)
    {
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                UpdateTrayFromSnapshot(json);   // 托盘 tooltip 常鲜（隐藏/收起时也更新）
                // 1.0.2：前端不可见（收托盘/贴边收起）时不投递——省掉整轮 DOM 重建与 SVG 重绘
                if (!_hidden && _dock != DockState.Collapsed)
                    Web.CoreWebView2?.PostWebMessageAsString(json);
            }
            catch { }
        });
    }

    private void PushSnapshotNow()
    {
        // 1.0.2：不可见时跳过投递（展开/唤回路径各自补推最新快照）
        if (_hidden || _dock == DockState.Collapsed) return;
        try { Web.CoreWebView2?.PostWebMessageAsString(_mon.BuildSnapshot().ToJsonString()); } catch { }
    }

    private void OnSettingsChanged(string key, object? _)
    {
        Dispatcher.BeginInvoke(() =>
        {
            switch (key)
            {
                case "topmost": ApplyTopmost(); break;
                case "zoom": ApplyZoom(); break;
                case "paused": UpdateTrayIcon(); break;
            }
            PushMenu();
            if (key != "topmost" && key != "zoom") PushSnapshotNow();
        });
    }

    public void ApplyAll()
    {
        // 恢复默认 = 连窗口尺寸/位置一并恢复（width/height 回到 460×780，位置回默认右上角）
        var cfg = SettingsStore.Instance;
        Width = Compat.Clamp(cfg.Width, 360, 4000);
        Height = Compat.Clamp(cfg.Height, 420, 4000);
        RestoreOrPlaceWindow();
        ApplyTopmost(); ApplyZoom(); ApplyClickThrough();
        if (SettingsStore.Instance.AutoHideDock) ApplyDocking(); else StopDocking();
        PushMenu(); PushSnapshotNow();
    }
    public void ApplyTopmost() { Topmost = SettingsStore.Instance.Topmost; }

    // ================= 鼠标穿透 =================

    public void ApplyClickThrough()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            if (SettingsStore.Instance.ClickThrough) ex |= WS_EX_TRANSPARENT | WS_EX_LAYERED;
            else ex &= ~WS_EX_TRANSPARENT;
            SetWindowLong(hwnd, GWL_EXSTYLE, ex);
            // 穿透时强制置顶，避免被踩到下面找不回来
            if (SettingsStore.Instance.ClickThrough) Topmost = true;
            Logger.Info("鼠标穿透 = " + SettingsStore.Instance.ClickThrough);
        }
        catch (Exception e) { Logger.Warn("穿透切换失败: " + e.Message); }
    }

    // ================= 贴边自动隐藏 =================

    public void ApplyDocking()
    {
        if (SettingsStore.Instance.AutoHideDock)
        {
            if (_dockPoll == null)
            {
                _dockPoll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                _dockPoll.Tick += (_, _) => DockTick();
            }
            _dockPoll.Start();
            Logger.Info("贴边自动隐藏已开启");
        }
        else StopDocking();
    }

    public void StopDocking()
    {
        _dockPoll?.Stop();
        if (_dock != DockState.None) RestoreFromDock();
        HideStrip();
        Logger.Info("贴边自动隐藏已关闭");
    }

    private void DockTick()
    {
        try
        {
            var cfg = SettingsStore.Instance;
            if (!cfg.AutoHideDock || _hidden || _exiting) return;
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return;
            if (!TryWorkArea(hwnd, out var wa)) return;
            GetCursorPos(out var p);
            bool lbtnDown = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;   // 拖拽中不做状态迁移
            const int TOUCH = 4, HOVERBAND = 14;

            switch (_dock)
            {
                case DockState.None:
                    if (lbtnDown) return;
                    // 优先级：左 > 右 > 上 > 下
                    string edge =
                        r.Left - wa.Left <= TOUCH ? "left" :
                        wa.Right - r.Right <= TOUCH ? "right" :
                        r.Top - wa.Top <= TOUCH ? "top" :
                        wa.Bottom - r.Bottom <= TOUCH ? "bottom" : "";
                    if (edge != "") CollapseTo(edge);
                    break;

                case DockState.Collapsed:
                    // 主窗已整体隐藏，触发线是独立小窗——命中判定用触发线自己的矩形
                    var sh = _stripWin != null ? new WindowInteropHelper(_stripWin).Handle : IntPtr.Zero;
                    if (sh == IntPtr.Zero || !GetWindowRect(sh, out var sr)) return;
                    bool onStrip =
                        _dockEdge == "left" ? p.X <= wa.Left + HOVERBAND && p.Y >= sr.Top - 8 && p.Y <= sr.Bottom + 8 :
                        _dockEdge == "right" ? p.X >= wa.Right - HOVERBAND && p.Y >= sr.Top - 8 && p.Y <= sr.Bottom + 8 :
                        _dockEdge == "top" ? p.Y <= wa.Top + HOVERBAND && p.X >= sr.Left - 8 && p.X <= sr.Right + 8 :
                        p.Y >= wa.Bottom - HOVERBAND && p.X >= sr.Left - 8 && p.X <= sr.Right + 8;
                    if (onStrip)
                    {
                        if (_hoverSince == null) _hoverSince = DateTime.UtcNow;
                        else if ((DateTime.UtcNow - _hoverSince.Value).TotalMilliseconds >= 500) ExpandFrom();
                    }
                    else _hoverSince = null;
                    break;

                case DockState.Expanded:
                    if (lbtnDown) return;
                    // 窗口被拖离边缘 → 解除贴边状态
                    string still =
                        r.Left - wa.Left <= TOUCH ? "left" :
                        wa.Right - r.Right <= TOUCH ? "right" :
                        r.Top - wa.Top <= TOUCH ? "top" :
                        wa.Bottom - r.Bottom <= TOUCH ? "bottom" : "";
                    if (still == "") { RestoreFromDock(); _dock = DockState.None; HideStrip(); return; }
                    _dockEdge = still;
                    // 鼠标离开窗口矩形（容差 6px）→ 立刻收起
                    bool inside = p.X >= r.Left - 6 && p.X <= r.Right + 6 && p.Y >= r.Top - 6 && p.Y <= r.Bottom + 6;
                    if (!inside) { CollapseTo(still); _hoverSince = null; }
                    break;
            }
        }
        catch (Exception e) { Logger.Warn("DockTick: " + e.Message); }
    }

    private void CollapseTo(string edge)
    {
        if (_dock == DockState.Collapsed && _dockEdge == edge) return;
        _dockEdge = edge;
        if (_dock != DockState.Collapsed) { _savedL = Left; _savedT = Top; _savedW = Width; _savedH = Height; }
        var hwnd = new WindowInteropHelper(this).Handle;
        if (!TryWorkArea(hwnd, out var wa)) return;
        double scale = ScaleOf(hwnd);
        double wl = wa.Left / scale, wt = wa.Top / scale, wr = wa.Right / scale, wb = wa.Bottom / scale;
        const double T = 2;   // 触发线线宽 2px（四边统一）
        // 主窗整体隐藏（不改尺寸！）：根治 WebView2 子 HWND 滞后残影（蓝条），高度天然保留
        Hide();
        var s = EnsureStrip();
        switch (edge)
        {
            // 左右：触发线高 = 工作区高（自动扣任务栏）；上下：触发线长 = 原窗口宽
            case "left": s.Left = wl; s.Top = wt; s.Width = T; s.Height = wb - wt; break;
            case "right": s.Left = wr - T; s.Top = wt; s.Width = T; s.Height = wb - wt; break;
            case "top": s.Left = Compat.Clamp(_savedL, wl, Math.Max(wl, wr - _savedW)); s.Top = wt; s.Width = _savedW; s.Height = T; break;
            case "bottom": s.Left = Compat.Clamp(_savedL, wl, Math.Max(wl, wr - _savedW)); s.Top = wb - T; s.Width = _savedW; s.Height = T; break;
        }
        if (!s.IsVisible) s.Show();
        s.Topmost = true;
        _dock = DockState.Collapsed;
        _hoverSince = null;
        SuspendWeb();   // 1.0.2：收起=不可见，挂起 WebView2 渲染（省 CPU/显存/V8 内存）
    }

    private bool _webSuspended;
    private void SuspendWeb()
    {
        // UI 线程调用；包装类只给 TrySuspendAsync（内部自 marshal），fire-and-forget，失败仅日志
        if (_webSuspended || _exiting) return;
        try
        {
            var wv = Web.CoreWebView2;
            if (wv == null) return;
            _webSuspended = true;
            _ = wv.TrySuspendAsync().ContinueWith(t =>
            {
                if (t.IsFaulted) { _webSuspended = false; Logger.Warn("挂起 WebView2 失败: " + t.Exception?.InnerException?.Message); }
            }, TaskScheduler.Default);
        }
        catch (Exception e) { _webSuspended = false; Logger.Warn("挂起 WebView2 失败: " + e.Message); }
    }
    private void ResumeWeb()
    {
        if (!_webSuspended) return;
        _webSuspended = false;
        try
        {
#if NETFRAMEWORK
            // net462 包装类无 Resume()：官方语义=挂起后任何 PostWebMessage/导航都会自动唤醒，发一条轻量消息即可
            Web.CoreWebView2?.PostWebMessageAsString("resume");
#else
            Web.CoreWebView2?.Resume();
#endif
        }
        catch (Exception e) { Logger.Warn("恢复 WebView2 失败: " + e.Message); }
    }

    private Window EnsureStrip()
    {
        if (_stripWin != null) return _stripWin;
        _stripWin = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = new SolidColorBrush(Color.FromArgb(150, 91, 108, 249)),
            ShowInTaskbar = false,
            ShowActivated = false,
            Focusable = false,
            IsHitTestVisible = true,
            ResizeMode = ResizeMode.NoResize,
            Topmost = true,
            Title = "yskDockStrip",
            Owner = null,
        };
        return _stripWin;
    }

    private void HideStrip()
    {
        try { _stripWin?.Hide(); } catch { }
    }

    private void ExpandFrom()
    {
        if (_dock != DockState.Collapsed) return;
        HideStrip();
        ResumeWeb();   // 1.0.2：先恢复渲染再显窗，避免展开瞬间白帧
        Show();
        Left = _savedL; Top = _savedT; Width = _savedW; Height = _savedH;
        ClampIntoWorkArea();   // 展开后整窗必须完整落在工作区内，不超屏幕边缘
        _dock = DockState.Expanded;
        _hoverSince = null;
        PushSnapshotNow();   // 1.0.2：收起期间未投递，展开立即补推最新一帧
    }

    private void RestoreFromDock()
    {
        if (_dock == DockState.Collapsed)
        {
            HideStrip();
            ResumeWeb();   // 1.0.2
            Show();
            Left = _savedL; Top = _savedT; Width = _savedW; Height = _savedH;
        }
        ClampIntoWorkArea();
        _dock = DockState.None;
    }

    /// <summary>把窗口整体钳进所在显示器工作区（DIP 换算，四边都不超出）。</summary>
    private void ClampIntoWorkArea()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !TryWorkArea(hwnd, out var wa)) return;
        double scale = ScaleOf(hwnd);
        double wl = wa.Left / scale, wt = wa.Top / scale, wr = wa.Right / scale, wb = wa.Bottom / scale;
        if (Width > wr - wl) Left = wl; else Left = Compat.Clamp(Left, wl, wr - Width);
        if (Height > wb - wt) Top = wt; else Top = Compat.Clamp(Top, wt, wb - Height);
        _savedL = Left; _savedT = Top;   // 钳过的才是下次收起该恢复的
    }

    private static double ScaleOf(IntPtr hwnd)
    {
        try
        {
            var src = PresentationSource.FromVisual(App.Main);
            if (src?.CompositionTarget != null) return src.CompositionTarget.TransformToDevice.M11;
        }
        catch { }
        _ = hwnd;
        return 1.0;
    }

    private static bool TryWorkArea(IntPtr hwnd, out RECT wa)
    {
        // 工作区（work）= 显示器区域按任务栏位置自动裁剪后的可用区，天然避让任务栏
        wa = default;
        IntPtr mon = MonitorFromWindow(hwnd, 2); // MONITOR_DEFAULTTONEAREST
        if (mon == IntPtr.Zero) return false;
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(mon, ref mi)) return false;
        wa = mi.rcWork;
        return true;
    }

    // ================= 屏幕边缘吸附（QQ 式，含任务栏） =================

    private void SnapToEdge()
    {
        try
        {
            if (!SettingsStore.Instance.Snap || _exiting) return;
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return;
            if (!TryWorkArea(hwnd, out var wa)) return;
            double scale = ScaleOf(hwnd);
            const int MAG = 64;   // 吸附阈值（物理像素），参考 Win 自带贴边吸附的宽松范围
            double wl = wa.Left / scale, wt = wa.Top / scale, wr = wa.Right / scale, wb = wa.Bottom / scale;
            double nl = Left, nt = Top;
            if (Math.Abs(r.Left - wa.Left) <= MAG) nl = wl;
            if (Math.Abs(wa.Right - r.Right) <= MAG) nl = wr - Width;    // DIP 精确贴齐，防取整超边
            if (Math.Abs(r.Top - wa.Top) <= MAG) nt = wt;
            if (Math.Abs(wa.Bottom - r.Bottom) <= MAG) nt = wb - Height;
            // 任务栏也当吸附边：窗口被拖进任务栏区域（贴近屏幕外沿）时，吸附回任务栏内沿（工作区边），不遮任务栏
            var tb = TaskbarEdge(out int tbEdge);
            if (tbEdge != 0)
            {
                double tl = tb.Left / scale, tt = tb.Top / scale, tr = tb.Right / scale, tbt = tb.Bottom / scale;
                if ((tbEdge & 1) != 0 && Math.Abs(r.Left - tl) <= MAG) nl = wl;                 // 任务栏在左：贴其内沿
                if ((tbEdge & 2) != 0 && Math.Abs(tr - r.Right) <= MAG) nl = wr - Width;         // 任务栏在右：贴其内沿
                if ((tbEdge & 4) != 0 && Math.Abs(r.Top - tt) <= MAG) nt = wt;                   // 任务栏在顶：贴其内沿
                if ((tbEdge & 8) != 0 && Math.Abs(tbt - r.Bottom) <= MAG) nt = wb - Height;      // 任务栏在下：贴其内沿
            }
            if (Math.Abs(nl - Left) > 0.01 || Math.Abs(nt - Top) > 0.01)
            {
                Left = nl; Top = nt;
                Logger.Info($"边缘吸附: ({r.Left},{r.Top}) → ({nl:F0},{nt:F0})");
            }
        }
        catch (Exception e) { Logger.Warn("边缘吸附失败: " + e.Message); }
    }

    // ---- 任务栏位置探测（SHAppBarMessage）----
    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public int lParam;
    }
    [DllImport("shell32.dll")] private static extern IntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);
    private const uint ABM_GETTASKBARPOS = 0x00000004;

    /// <summary>任务栏所在边：1=左 2=右 4=上 8=下；返回其屏幕矩形（取不到返回 default+0）。</summary>
    private static RECT TaskbarEdge(out int edge)
    {
        edge = 0;
        try
        {
            var abd = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>() };
            if (SHAppBarMessage(ABM_GETTASKBARPOS, ref abd) == IntPtr.Zero) return default;
            edge = abd.uEdge switch { 0 => 4, 1 => 1, 2 => 2, 3 => 8, _ => 0 };   // ABE_LEFT=0 TOP=1 RIGHT=2 BOTTOM=3
            return abd.rc;
        }
        catch { return default; }
    }

    public void ApplyZoom()
    {
        // 宽等比、高不缩放：有效缩放 = 用户缩放 × (窗口宽 ÷ 基准460)。
        // 缩放后 CSS 视口恒约 460px 宽 → 内容随宽度等比缩放；高度不参与 → 加高只显示更多内容。
        try
        {
            double w = Width > 50 ? Width : 460;
            double z = Compat.Clamp(cfgZoom() * (w / 460.0), 0.3, 3.0);
            Web.ZoomFactor = Math.Round(z, 4);
        }
        catch { }
    }
    private static double cfgZoom() { try { return SettingsStore.Instance.Zoom; } catch { return 1.0; } }
    public void FlashInterval() { Post("flash:interval"); }
    private void PushMenu()
    {
        try { Web.CoreWebView2?.PostWebMessageAsString("menu:" + MenuActions.MenuTreeJson()); } catch { }
    }
    public void ShowAbout()
    {
        var ver = GetType().Assembly.GetName().Version?.ToString() ?? "1.0";
        var build = File.GetLastWriteTime(Compat.ExePath()).ToString("yyyy-MM-dd HH:mm");
        Post("about:" + System.Text.Json.JsonSerializer.Serialize(new
        {
            ver, build, exe = Compat.ExePath(), data = AppPaths.DataDir
        }));
    }
    private void Post(string s) { try { Web.CoreWebView2?.PostWebMessageAsString(s); } catch { } }

    // ================= 托盘 =================

    private void InitTray()
    {
        _trayMenu = new WinForms.ContextMenuStrip();
        _trayMenu.Opening += (_, _) => RebuildTrayMenu();
        _tray = new WinForms.NotifyIcon
        {
            Text = "本机模型监控",
            ContextMenuStrip = _trayMenu,
        };
        UpdateTrayIcon();
        _tray.Visible = true;
        _tray.MouseUp += (_, e) =>
        {
            // 只用左键单击，不注册双击，避免双击=切换两次的经典 BUG
            if (e.Button == WinForms.MouseButtons.Left) ToggleVisible();
        };
    }

    private void RebuildTrayMenu()
    {
        if (_trayMenu == null) return;
        _trayMenu.Items.Clear();
        foreach (var item in MenuActions.Build())
            _trayMenu.Items.Add(BuildTrayItem(item));
    }

    private WinForms.ToolStripItem BuildTrayItem(MenuItem m)
    {
        if (m.type == "header")
        {
            var h = new WinForms.ToolStripMenuItem(m.label) { Enabled = false };
            h.Font = new Drawing.Font(h.Font, Drawing.FontStyle.Bold);
            return h;
        }
        if (m.type == "sub" && m.children != null)
        {
            var mi = new WinForms.ToolStripMenuItem(m.label);
            foreach (var c in m.children) mi.DropDownItems.Add(BuildTrayItem(c));
            mi.DropDownItemClicked += (_, e) =>
            {
                var tag = e.ClickedItem?.Tag as string;
                if (tag == null) return;
                MenuActions.Execute(tag, this);
                RebuildTrayMenu();
                // 间隔 ±：菜单保持打开便于连点
                if (tag.StartsWith("act:interval:") || tag.StartsWith("interval:"))
                    _trayMenu!.Show(_trayMenu.Bounds.Location);
            };
            return mi;
        }
        var it = new WinForms.ToolStripMenuItem(m.label)
        {
            Tag = m.id,
            Checked = m.@checked,
            CheckOnClick = false,
        };
        if (m.tooltip != null) it.ToolTipText = m.tooltip;
        it.Click += (_, _) =>
        {
            MenuActions.Execute((string)it.Tag!, this);
            RebuildTrayMenu();
        };
        return it;
    }

    private void UpdateTrayFromSnapshot(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            int online = root.GetProperty("summary").GetProperty("online").GetInt32();
            int total = root.GetProperty("summary").GetProperty("total").GetInt32();
            int interval = root.GetProperty("interval").GetInt32();
            bool paused = root.GetProperty("paused").GetBoolean();
            bool stale = root.TryGetProperty("smiStale", out var st) && st.GetBoolean();
            string summary = $"在线 {online}/{total} · 刷新 {(paused ? "暂停" : interval + "s")}";
            int state = paused ? 2 : stale ? 1 : 0;
            if (state != _lastTrayState) { _lastTrayState = state; UpdateTrayIcon(); }
            if (_tray != null)
                _tray.Text = "本机模型监控 — " + summary;
        }
        catch { }
    }

    private Drawing.Icon? _foxBase;   // 缘狐图标（内嵌 app.ico 解出）

    private Drawing.Icon FoxBase()
    {
        if (_foxBase != null) return _foxBase;
        try
        {
            var asm = GetType().Assembly;
            string? res = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("app.ico"));
            if (res != null)
            {
                using var st = asm.GetManifestResourceStream(res)!;
                _foxBase = new Drawing.Icon(st, 32, 32);   // 多尺寸 ico 里取 32 帧
                return _foxBase;
            }
        }
        catch (Exception e) { Logger.Warn("内嵌图标加载失败: " + e.Message); }
        _foxBase = Drawing.SystemIcons.Application;
        return _foxBase;
    }

    private void UpdateTrayIcon()
    {
        // 0=正常绿 1=陈旧琥珀 2=暂停灰 —— 缘狐小狐脸 + 右下状态点
        int state = _lastTrayState;
        var bmp = new Drawing.Bitmap(32, 32);
        IntPtr hIcon;
        try
        {
            using (var g = Drawing.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.InterpolationMode = Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                using (var foxBmp = FoxBase().ToBitmap())
                    g.DrawImage(foxBmp, 0, 0, 32, 32);
                Drawing.Color dot = state switch
                {
                    1 => Drawing.Color.FromArgb(245, 158, 11),
                    2 => Drawing.Color.FromArgb(150, 150, 150),
                    _ => Drawing.Color.FromArgb(34, 197, 94),
                };
                using var pen = new Drawing.Pen(Drawing.Color.White, 2f);
                using var db = new Drawing.SolidBrush(dot);
                g.FillEllipse(db, 20, 20, 11, 11);
                g.DrawEllipse(pen, 20, 20, 11, 11);
            }
            hIcon = bmp.GetHicon();   // 1.0.2：先取句柄，随后释放 Bitmap（旧代码 Bitmap 泄漏 GDI 位图句柄）
        }
        finally { bmp.Dispose(); }
        var old = _trayIcon;
        _trayIcon = Drawing.Icon.FromHandle(hIcon);
        if (_tray != null) _tray.Icon = _trayIcon;
        if (old != null) { DestroyIcon(old.Handle); old.Dispose(); }
    }

    // ================= 显示/隐藏/退出 =================

    public void ToggleVisible()
    {
        if (_hidden) ShowFromTray(); else HideToTray();
    }

    public void HideToTray()
    {
        if (_hidden) return;
        _hidden = true;
        if (_dock == DockState.Collapsed) HideStrip();   // 收起态收进托盘时触发线一并消失
        SaveGeometry();
        Hide();
        ShowInTaskbar = false;
        _mon.SetWindowVisible(false);
    }

    public void ShowFromTray()
    {
        if (!_hidden) return;
        _hidden = false;
        if (_dock == DockState.Collapsed) ExpandFrom();  // 收起态点托盘 → 直接完整展开
        ResumeWeb();   // 1.0.2：托盘唤回时恢复渲染
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
        // 短暂置顶确保浮上来，再恢复用户设置
        Topmost = true;
        Dispatcher.BeginInvoke(() => Topmost = SettingsStore.Instance.Topmost, DispatcherPriority.Input);
        _mon.SetWindowVisible(true);
        PushSnapshotNow();
    }

    public void ExitApp()
    {
        _exiting = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting)
        {
            e.Cancel = true;   // 没有关闭按钮：Alt+F4 收回托盘
            HideToTray();
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        _mon.Stop();
        _dockPoll?.Stop();
        try { _stripWin?.Close(); } catch { }
        try { Web.Dispose(); } catch { }
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        if (_trayIcon != null) { DestroyIcon(_trayIcon.Handle); }
        KillWebview2Children();
        Logger.Info("退出");
        base.OnClosed(e);
        System.Windows.Application.Current.Shutdown();
    }

    private void SaveGeometry()
    {
        if (_hidden || _dock != DockState.None) return;   // 收起态的 6px 尺寸不能写回设置
        var cfg = SettingsStore.Instance;
        cfg.Left = Left; cfg.Top = Top; cfg.Width = Width; cfg.Height = Height;
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        if (_dock == DockState.Expanded) { _savedL = Left; _savedT = Top; }   // 展开态拖动→更新恢复位
        _geoTimer.Stop(); _geoTimer.Start();
    }
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        ClampToWorkArea();
        if (_dock == DockState.Expanded) { _savedW = Width; _savedH = Height; }
        if (sizeInfo.WidthChanged) ApplyZoom();   // 宽度变更 → 内容等比缩放（高度变更不动缩放）
        _geoTimer.Stop(); _geoTimer.Start();
    }

    // ================= NCHITTEST 边缘拖拽 =================

    private const int WM_NCHITTEST = 0x0084;
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int WM_EXITSIZEMOVE = 0x0231;
    private const int HTCAPTION = 2;
    private const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14,
                      HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_GETMINMAXINFO)
        {
            // 兜底禁 Aero Snap：最大追踪尺寸 = 工作区，就算被系统 snap 也撑不满全屏
            try
            {
                if (lParam != IntPtr.Zero && TryWorkArea(hwnd, out var wa))
                {
                    var mmi = (MINMAXINFO)Marshal.PtrToStructure(lParam, typeof(MINMAXINFO))!;
                    mmi.ptMaxPosition.X = wa.Left; mmi.ptMaxPosition.Y = wa.Top;
                    mmi.ptMaxSize.X = wa.Right - wa.Left; mmi.ptMaxSize.Y = wa.Bottom - wa.Top;
                    mmi.ptMaxTrackSize.X = wa.Right - wa.Left; mmi.ptMaxTrackSize.Y = wa.Bottom - wa.Top;
                    Marshal.StructureToPtr(mmi, lParam, true);
                    handled = true; return IntPtr.Zero;
                }
            }
            catch { }
        }
        if (msg == WM_EXITSIZEMOVE)
        {
            // 拖拽/缩放结束 → QQ 式吸附（贴边隐藏开启时由 DockTick 接着收起）
            Dispatcher.BeginInvoke(SnapToEdge);
        }
        if (msg == WM_NCHITTEST)
        {
            if (_dock == DockState.Collapsed) { handled = true; return (IntPtr)1; }   // 收起态触发线不做 resize
            var p = lParam.ToInt64();
            int sx = (short)(p & 0xFFFF), sy = (short)((p >> 16) & 0xFFFF);
            if (GetWindowRect(hwnd, out var r))
            {
                int l = sx - r.Left, t = sy - r.Top, w = r.Right - r.Left, h = r.Bottom - r.Top;
                const int E = 6, C = 12;
                bool left = l >= 0 && l < E, right = l >= w - E && l < w;
                bool top = t >= 0 && t < C, bottom = t >= h - C && t < h;
                int? code = null;
                if (top && left) code = HTTOPLEFT;
                else if (top && right) code = HTTOPRIGHT;
                else if (bottom && left) code = HTBOTTOMLEFT;
                else if (bottom && right) code = HTBOTTOMRIGHT;
                else if (left) code = HTLEFT;
                else if (right) code = HTRIGHT;
                else if (t >= 0 && t < E) code = HTTOP;
                else if (t >= h - E && t < h) code = HTBOTTOM;
                if (code != null) { handled = true; return (IntPtr)code!.Value; }
            }
        }
        return IntPtr.Zero;
    }

    // ================= Win32 =================

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public int dwFlags; }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr hObject);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const int WS_MAXIMIZEBOX = 0x00010000;
    private const int WS_EX_LAYERED = 0x00080000, WS_EX_TRANSPARENT = 0x00000020;
    private const int VK_LBUTTON = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
    }

    private const int WM_GETMINMAXINFO = 0x0024;

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll")] private static extern int Process32First(IntPtr snapshot, ref PROCESSENTRY32 entry);
    [DllImport("kernel32.dll")] private static extern int Process32Next(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    /// <summary>退出时清掉本进程名下的 msedgewebview2.exe 子进程树，不留孤儿。</summary>
    private static void KillWebview2Children()
    {
        try
        {
            int self = Compat.ProcessId();
            var kids = new System.Collections.Generic.List<int>();
            IntPtr snap = CreateToolhelp32Snapshot(0x2, 0); // TH32CS_SNAPPROCESS
            if (snap == IntPtr.Zero || (long)snap == -1) return;
            try
            {
                var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
                for (int ok = Process32First(snap, ref entry); ok != 0; ok = Process32Next(snap, ref entry))
                    if (entry.th32ParentProcessID == self &&
                        entry.szExeFile.Equals("msedgewebview2.exe", StringComparison.OrdinalIgnoreCase))
                        kids.Add((int)entry.th32ProcessID);
            }
            finally { CloseHandle(snap); }   // 1.0.2：快照句柄必须关，防 GDI/内核对象泄漏
            foreach (var pid in kids)
                try { using var pr = System.Diagnostics.Process.GetProcessById(pid); Compat.KillTree(pr); } catch { }
        }
        catch (Exception e) { Logger.Warn("清理 WebView2 子进程: " + e.Message); }
    }
}
