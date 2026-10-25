using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ModelMonitor;

public sealed class ServiceDef
{
    public string id = "";
    public string name = "";
    public string kind = "generic";      // strata|ninfer|llama|ollama|tts|asr|comfyui|generic
    public string base_url = "";
    public string health_path = "/health";
    public string? props_path;
    public string? slots_path;
    public string? metrics_path;
    public string? stats_path;           // ninfer: /stats 原生吞吐与上下文统计
    public string? log_path;             // ninfer: 引擎日志尾随解析（5s throughput + req done 行）
    public string pid_match = "";
    public bool enabled = true;
    public string note = "";
    public bool discovered;              // 自动发现且未固化
}

/// <summary>服务注册表：内置清单首启写 _data\services.json，可编辑重载；含端口自动发现。</summary>
public sealed class ServiceRegistry
{
    private static readonly string Path_ = AppPaths.File_("services.json");
    public List<ServiceDef> Services { get; private set; } = new();
    public List<ServiceDef> Discovered { get; private set; } = new();

    // 通用内置清单：常见运行时默认端口；本机专属清单由 _data\services.json 首写后自行维护
    // （普通字符串拼接——net48/C#7.3 无裸字符串字面量）
    private static readonly string Builtin = string.Join("\n", new[]
    {
        "[",
        "  {\"id\":\"ollama\",\"name\":\"Ollama\",\"kind\":\"ollama\",\"base_url\":\"http://127.0.0.1:11434\",\"health_path\":\"/api/version\",\"pid_match\":\"ollama\",\"enabled\":true,\"note\":\"\"},",
        "  {\"id\":\"comfyui\",\"name\":\"ComfyUI\",\"kind\":\"comfyui\",\"base_url\":\"http://127.0.0.1:8188\",\"health_path\":\"/system_stats\",\"pid_match\":\"python\",\"enabled\":true,\"note\":\"\"},",
        "  {\"id\":\"llama8080\",\"name\":\"llama-server :8080\",\"kind\":\"llama\",\"base_url\":\"http://127.0.0.1:8080\",\"health_path\":\"/health\",\"props_path\":\"/props\",\"slots_path\":\"/slots\",\"pid_match\":\"llama-server\",\"enabled\":true,\"note\":\"默认端口，可改\"}",
        "]",
    });

    public void Load()
    {
        JsonArray arr;
        try
        {
            if (!File.Exists(Path_))
            {
                File.WriteAllText(Path_, Builtin);
                Logger.Info("services.json 首次写入内置清单");
            }
            arr = JsonNode.Parse(File.ReadAllText(Path_)) as JsonArray ?? JsonNode.Parse(Builtin)!.AsArray();
        }
        catch (Exception e)
        {
            Logger.Warn("services.json 读取失败，用内置清单: " + e.Message);
            arr = JsonNode.Parse(Builtin)!.AsArray();
        }

        Services = new List<ServiceDef>();
        foreach (var n in arr.OfType<JsonObject>())
        {
            var d = new ServiceDef
            {
                id = Str(n, "id") ?? MakeId(Str(n, "base_url") ?? Guid.NewGuid().ToString("N")),
                name = Str(n, "name") ?? "未命名服务",
                kind = Str(n, "kind") ?? "generic",
                base_url = (Str(n, "base_url") ?? "").TrimEnd('/'),
                health_path = Str(n, "health_path") ?? "/health",
                props_path = Str(n, "props_path"),
                slots_path = Str(n, "slots_path"),
                metrics_path = Str(n, "metrics_path"),
                stats_path = Str(n, "stats_path"),
                log_path = Str(n, "log_path"),
                pid_match = Str(n, "pid_match") ?? "",
                enabled = n["enabled"]?.GetValue<bool>() ?? true,
                note = Str(n, "note") ?? "",
            };
            if (string.IsNullOrEmpty(d.id)) d.id = MakeId(d.base_url);
            Services.Add(d);
        }

        // 应用 settings 里的启用开关
        var svcFlags = SettingsStore.Instance.GetNode("services")?.AsObject();
        if (svcFlags != null)
            foreach (var s in Services)
                if (svcFlags.TryGetPropertyValue(s.id, out var v) && v != null)
                {
                    try { s.enabled = v.GetValue<bool>(); } catch { }
                }
    }

    public void SaveEnabledFlags()
    {
        var o = new JsonObject();
        foreach (var s in Services) o[s.id] = s.enabled;
        foreach (var s in Discovered) o["disc:" + s.id] = s.enabled;
        SettingsStore.Instance.SetSilent("services", o);
    }

    public ServiceDef? Find(string id) =>
        Services.FirstOrDefault(s => s.id == id) ?? Discovered.FirstOrDefault(s => s.id == id);

    private static string? Str(JsonObject o, string k) =>
        o.TryGetPropertyValue(k, out var v) && v != null ? v.GetValue<string>() : null;

    private static string MakeId(string s)
    {
        var port = System.Text.RegularExpressions.Regex.Match(s, @":(\d+)");
        return port.Success ? "p" + port.Groups[1].Value : "svc" + Math.Abs(s.GetHashCode()).ToString("x4");
    }

    /// <summary>扫描 LISTEN 端口，试 llama-server 指纹，命中则追加「发现的服务」。</summary>
    public async Task ScanAsync(HttpClient http)
    {
        long startMs = Compat.Tick64();
        var registered = new HashSet<int>(
            Services.Concat(Discovered).Select(s => Port(s.base_url)).Where(p => p > 0));
        HashSet<int> listening;
        try
        {
            listening = IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Where(e => e.AddressFamily == AddressFamily.InterNetwork)
                .Select(e => e.Port)
                .Where(p => p >= 1024 && !registered.Contains(p))
                .ToHashSetOf();
        }
        catch (Exception e) { Logger.Warn("端口扫描失败: " + e.Message); return; }

        foreach (var port in listening)
        {
            if (Discovered.Any(d => Port(d.base_url) == port)) continue;
            if (Compat.Tick64() - startMs > 10_000) { Logger.Warn("端口扫描超预算，本轮截断"); break; }
            try
            {
                using var cts = new CancellationTokenSource(1500);   // 关键：无超时的话一个挂起端口会卡死整个扫描
                var h = await Compat.GetStr(http, $"http://127.0.0.1:{port}/health", cts.Token);
                if (!h.Contains("\"ok\"")) continue;
                using var cts2 = new CancellationTokenSource(1500);
                var p = await Compat.GetStr(http, $"http://127.0.0.1:{port}/props", cts2.Token);
                var pj = JsonNode.Parse(p)?.AsObject();
                var alias = pj?["model_alias"]?.GetValue<string>();
                if (string.IsNullOrEmpty(alias)) continue;
                Discovered.Add(new ServiceDef
                {
                    id = "p" + port,
                    name = "发现的服务 :" + port,
                    kind = "llama",
                    base_url = $"http://127.0.0.1:{port}",
                    health_path = "/health", props_path = "/props", slots_path = "/slots",
                    pid_match = "llama-server",
                    enabled = true,
                    discovered = true,
                    note = alias,
                });
                Logger.Info($"自动发现 llama-server 指纹: 127.0.0.1:{port} ({alias})");
            }
            catch { /* 端口不通/非 llama，静默 */ }
        }
    }

    public void Promote(string id)
    {
        var d = Discovered.FirstOrDefault(x => x.id == id);
        if (d == null) return;
        Discovered.Remove(d);
        d.name = d.note + " :" + d.base_url.Split(':').Last();
        d.discovered = false;
        Services.Add(d);
        File.WriteAllText(Path_, JsonSerializer.Serialize(Services.Select(s => new
        {
            s.id, s.name, s.kind, s.base_url, s.health_path, s.props_path, s.slots_path, s.metrics_path, s.stats_path, s.log_path, s.pid_match, s.enabled, s.note
        }).ToList(), new JsonSerializerOptions { WriteIndented = true }));
        Logger.Info("已固化发现服务: " + d.base_url);
    }

    public static int Port(string url)
    {
        try { return new Uri(url).Port; } catch { return -1; }
    }
}
