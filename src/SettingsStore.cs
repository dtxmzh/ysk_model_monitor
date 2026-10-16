using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ModelMonitor;

/// <summary>
/// 单一数据源设置存储：内存 JsonObject + 同目录 model_monitor.ini（INI 格式）。
/// 启动：exe 目录（或当前工作目录）找到带 [model_monitor] 标识段的 ini → 按之启动；
/// 找不到/无标识 → 默认设置。任何设置变更即整份重写 ini（原子写）。
/// </summary>
public sealed class SettingsStore
{
    public static SettingsStore Instance { get; } = new();

    public const string IniName = "ysk_model_monitor.ini";
    public const string Section = "ysk_model_monitor";

    public event Action<string, object?>? Changed;

    private JsonObject _data = new();
    private string _iniPath = Path.Combine(AppContext.BaseDirectory, IniName);

    /// <summary>ini 是否为本软件配置（找到且含 [model_monitor] 段）。</summary>
    public bool IniLoaded { get; private set; }
    public string IniPath => _iniPath;

    // 默认值表（[model_monitor] 段的标量键）
    private static readonly Dictionary<string, JsonNode> Defaults = new()
    {
        ["left"] = 0.0,                      // 0 = 未设置，首次落主屏右上角
        ["top"] = 0.0,
        ["width"] = 460.0,
        ["height"] = 780.0,
        ["zoom"] = 1.0,
        ["opacity"] = 0.72,
        ["theme"] = "light",
        ["topmost"] = true,
        ["interval"] = 2,
        ["paused"] = false,
        ["hiddenBehavior"] = "slow10",       // keep | slow10 | pause
        ["minimizeToTaskbar"] = false,
        ["autostart"] = false,
        ["clickThrough"] = false,
        ["autoHide"] = false,
        ["snap"] = true,
        ["showGpuTable"] = true,
        ["showTopology"] = false,
        ["nvidiaSmi"] = true,
        ["autoDiscover"] = true,
        ["services"] = new JsonObject(),     // id -> bool（[services] 段）
        ["order"] = new JsonArray(),         // L2 卡片排序（order=a,b,c）
        ["scroll"] = 0.0,
    };

    // ================= 启动加载 =================

    public void Load()
    {
        // 1) exe 目录 → 2) 当前工作目录，找 model_monitor.ini
        string? found = null;
        foreach (var dir in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            try
            {
                var p = Path.Combine(dir, IniName);
                if (File.Exists(p)) { found = p; break; }
            }
            catch { }
        }
        if (found != null) _iniPath = found;

        try
        {
            if (found != null)
            {
                var ini = ParseIni(File.ReadAllText(found, Encoding.UTF8));
                // 标识校验：必须含 [model_monitor] 段，否则不认这份文件、按默认启动
                if (ini.ContainsKey(Section))
                {
                    _data = FromIni(ini);
                    IniLoaded = true;
                    Logger.Info("已加载配置 " + found);
                }
                else
                {
                    Logger.Warn(found + " 缺少 [" + Section + "] 标识段，按默认设置启动");
                    _iniPath = Path.Combine(AppContext.BaseDirectory, IniName); // 写回位置回 exe 目录
                }
            }
        }
        catch (Exception e)
        {
            try { if (found != null) File.Copy(found, found + ".bak", true); } catch { }
            _data = new JsonObject();
            Logger.Warn("ini 解析失败，已用默认值兜底并备份为 .bak: " + e.Message);
        }

        // 一次性迁移：没有可用 ini 但存在旧版 _data\settings.json → 继承旧设置（随后落 ini）
        if (!IniLoaded)
        {
            try
            {
                var legacy = AppPaths.File_("settings.json");
                if (File.Exists(legacy))
                {
                    var old = JsonNode.Parse(File.ReadAllText(legacy)) as JsonObject;
                    if (old != null)
                    {
                        _data = old;
                        Save();
                        Logger.Info("已从旧 settings.json 迁移设置到 model_monitor.ini");
                    }
                }
            }
            catch (Exception e) { Logger.Warn("旧 settings.json 迁移失败: " + e.Message); }
        }
    }

    // ================= 泛型读写（对外接口不变） =================

    public T Get<T>(string key)
    {
        if (_data.TryGetPropertyValue(key, out var v) && v != null)
        {
            try { return v.Deserialize<T>()!; } catch { }
        }
        if (Defaults.TryGetValue(key, out var d))
        {
            try { return d.Deserialize<T>()!; } catch { return (T)Convert.ChangeType(d.GetValue<object>(), typeof(T))!; }
        }
        return default!;
    }

    public JsonNode? GetNode(string key) =>
        _data.TryGetPropertyValue(key, out var v) ? v : (Defaults.TryGetValue(key, out var d) ? d : null);

    public void Set(string key, object? value)
    {
        _data[key] = value is null ? null : JsonNode.Parse(JsonSerializer.Serialize(value));
        Save();
        Changed?.Invoke(key, value);
    }

    /// <summary>改值但不发 Changed（窗口几何回写等内部场景，防循环）。</summary>
    public void SetSilent(string key, object? value)
    {
        _data[key] = value is null ? null : JsonNode.Parse(JsonSerializer.Serialize(value));
        Save();
    }

    // ================= INI 落盘 =================

    public void Save()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("; ============================================================");
            sb.AppendLine(";  ysk_model_monitor 配置文件（ysk_model_monitor.ini）");
            sb.AppendLine(";  由「本机模型监控悬浮窗」自动生成与维护：设置一变更即整份重写。");
            sb.AppendLine(";  [ysk_model_monitor] 段是本软件识别此配置文件的标识，请勿删除；");
            sb.AppendLine(";  删除整个文件 = 下次启动恢复默认设置。");
            sb.AppendLine("; ============================================================");
            sb.AppendLine("[" + Section + "]");

            foreach (var (k, dv) in Defaults)
            {
                if (k == "services" || k == "order") continue;
                var v = GetNode(k);
                if (v == null) continue;
                sb.AppendLine(k + "=" + Fmt(v, dv));
            }
            // order 数组 → 逗号串
            var order = GetNode("order") as JsonArray;
            sb.AppendLine("order=" + (order == null ? "" : string.Join(",", order.Select(n => n?.GetValue<string>() ?? ""))));

            sb.AppendLine();
            sb.AppendLine("[services]");
            var svcs = GetNode("services") as JsonObject;
            if (svcs != null)
                foreach (var kv in svcs)
                    if (kv.Value != null)
                        sb.AppendLine(kv.Key + "=" + Fmt(kv.Value, null));

            var tmp = _iniPath + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), Encoding.UTF8);
            if (File.Exists(_iniPath)) File.Replace(tmp, _iniPath, null);
            else File.Move(tmp, _iniPath);
        }
        catch (Exception e) { Logger.Warn(IniName + " 落盘失败: " + e.Message); }
    }

    public void ResetToDefaults()
    {
        _data = new JsonObject();
        foreach (var (k, v) in Defaults) _data[k] = v.DeepClone();
        Save();
        Changed?.Invoke("__reset", null);
    }

    // ================= INI 解析/格式化 =================

    private static Dictionary<string, Dictionary<string, string>> ParseIni(string text)
    {
        var all = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var cur = "";
        all[cur] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))   // net48 有 Split(char) 重载，OK
        {
            var line = raw.Trim('\r', ' ', '\t');
            if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
            if (line[0] == '[' && line.EndsWith("]"))
            {
                cur = line.Substring(1, line.Length - 2).Trim();   // 原 line[1..^1]：net48 编译器对 ^1 lowering 挑剔，改 Substring 两版通吃
                if (!all.ContainsKey(cur)) all[cur] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            all[cur][line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return all;
    }

    private static JsonObject FromIni(Dictionary<string, Dictionary<string, string>> ini)
    {
        var d = new JsonObject();
        var sec = ini[Section];
        foreach (var (k, dv) in Defaults)
        {
            if (k == "services" || k == "order") continue;
            if (!sec.TryGetValue(k, out var s)) continue;
            try
            {
                var kind = dv.GetValueKind();
                if (kind == JsonValueKind.True || kind == JsonValueKind.False)
                    d[k] = ParseBool(s);
                else if (kind == JsonValueKind.Number)
                {
                    double val = ParseDouble(s);
                    bool intDefault = GetD(dv) % 1 == 0;
                    d[k] = (intDefault && val % 1 == 0) ? (JsonNode)(long)val : (JsonNode)val;
                }
                else
                    d[k] = s;   // string
            }
            catch { /* 坏值 → 用默认 */ }
        }
        // order=a,b,c
        if (sec.TryGetValue("order", out var ord) && ord.Length > 0)
        {
            var arr = new JsonArray();
            // TrimEntries 是 .NET Core 独有：net48 手动 Trim
            foreach (var itemRaw in ord.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var item = itemRaw.Trim();
                if (item.Length > 0) arr.Add(item);
            }
            d["order"] = arr;
        }
        // [services] 段
        if (ini.TryGetValue("services", out var svc))
        {
            var o = new JsonObject();
            foreach (var (k, v) in svc)
                o[k] = ParseBool(v);
            d["services"] = o;
        }
        return d;
    }

    private static JsonNode ParseBool(string s) =>
        (s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1" || s.Equals("yes", StringComparison.OrdinalIgnoreCase) || s.Equals("on", StringComparison.OrdinalIgnoreCase));

    private static double ParseDouble(string s) =>
        double.Parse(s, NumberStyles.Any, CultureInfo.InvariantCulture);

    private static double GetD(JsonNode n)
    {
        // 节点可能源自 CLR 装箱（double/long/int）也可能源自 JSON 解析，逐级安全取数
        try { return n.GetValue<double>(); } catch { }
        try { return n.GetValue<long>(); } catch { }
        try { return n.GetValue<int>(); } catch { }
        return double.TryParse(n.ToJsonString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0;
    }

    private static string Fmt(JsonNode v, JsonNode? dv)
    {
        var kind = v.GetValueKind();
        if (kind == JsonValueKind.True) return "true";
        if (kind == JsonValueKind.False) return "false";
        if (kind == JsonValueKind.Number)
        {
            double d = GetD(v);
            // 默认值是整数的键（interval）写回也取整，防 ini 里出现 2.0 读回混乱
            if (dv != null && dv.GetValueKind() == JsonValueKind.Number && GetD(dv) % 1 == 0 && d % 1 == 0)
                return ((long)d).ToString(CultureInfo.InvariantCulture);
            return d.ToString("R", CultureInfo.InvariantCulture);
        }
        if (kind == JsonValueKind.String) return v.GetValue<string>();
        return v.ToJsonString();
    }

    // ---- 便捷属性 ----
    public double Left { get => Get<double>("left"); set => SetSilent("left", value); }
    public double Top { get => Get<double>("top"); set => SetSilent("top", value); }
    public double Width { get => Get<double>("width"); set => SetSilent("width", value); }
    public double Height { get => Get<double>("height"); set => SetSilent("height", value); }
    public double Zoom { get => Get<double>("zoom"); set => Set("zoom", value); }
    public double Opacity { get => Get<double>("opacity"); set => Set("opacity", value); }
    public string Theme { get => Get<string>("theme"); set => Set("theme", value); }
    public bool Topmost { get => Get<bool>("topmost"); set => Set("topmost", value); }
    public int Interval { get => Compat.Clamp(Get<int>("interval"), 1, 60); set => Set("interval", Compat.Clamp(value, 1, 60)); }
    public bool Paused { get => Get<bool>("paused"); set => Set("paused", value); }
    public string HiddenBehavior { get => Get<string>("hiddenBehavior"); set => Set("hiddenBehavior", value); }
    public bool MinToTaskbar { get => Get<bool>("minimizeToTaskbar"); set => Set("minimizeToTaskbar", value); }
    public bool ClickThrough { get => Get<bool>("clickThrough"); set => Set("clickThrough", value); }
    public bool AutoHideDock { get => Get<bool>("autoHide"); set => Set("autoHide", value); }
    public bool Snap { get => Get<bool>("snap"); set => Set("snap", value); }
    public bool Autostart { get => Get<bool>("autostart"); set => Set("autostart", value); }
    public bool ShowGpuTable { get => Get<bool>("showGpuTable"); set => Set("showGpuTable", value); }
    public bool ShowTopology { get => Get<bool>("showTopology"); set => Set("showTopology", value); }
    public bool NvidiaSmi { get => Get<bool>("nvidiaSmi"); set => Set("nvidiaSmi", value); }
    public bool AutoDiscover { get => Get<bool>("autoDiscover"); set => Set("autoDiscover", value); }
}
