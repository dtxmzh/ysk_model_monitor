using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ModelMonitor;

/// <summary>端口→PID 映射（GetExtendedTcpTable），用于 GPU 进程归属到已登记服务。</summary>
internal static class TcpMap
{
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int dwOutBufLen, bool sort,
        int ipVersion, int tblClass, int reserved);

    private const int AF_INET = 2;
    private const int TCP_TABLE_OWNER_PID_LISTENER = 3;

    /// <summary>返回 本地监听端口 -> PID。</summary>
    public static Dictionary<int, int> ListenerPids()
    {
        var result = new Dictionary<int, int>();
        int len = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref len, true, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0);
        if (len <= 0) return result;
        IntPtr buf = Marshal.AllocHGlobal(len);
        try
        {
            if (GetExtendedTcpTable(buf, ref len, true, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0) != 0) return result;
            int rows = Marshal.ReadInt32(buf);
            IntPtr rowPtr = buf + 4;
            for (int i = 0; i < rows; i++)
            {
                uint portBe = (uint)Marshal.ReadInt32(rowPtr + 8);   // dwLocalPort
                int port = (int)(((portBe & 0xFF) << 8) | ((portBe >> 8) & 0xFF));
                int pid = Marshal.ReadInt32(rowPtr + 20);             // dwOwningPid
                if (port > 0 && pid > 0) result[port] = pid;
                rowPtr += 24; // MIB_TCPROW_OWNER_PID = 6 DWORD
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return result;
    }
}

/// <summary>轮询引擎：按基准节拍派生各数据源间隔，并发探测，环形缓冲，产出快照 JSON。</summary>
public sealed class Monitor
{
    public event Action<string>? SnapshotReady;   // 快照 JSON 字符串

    private readonly HttpClient _http;
    private readonly ServiceRegistry _registry;
    private readonly SettingsStore _cfg = SettingsStore.Instance;

    // ---- 服务状态 ----
    public sealed class SvcState
    {
        public ServiceDef Def = new();
        public bool Online;
        public int Fails;
        public long LastOkMs, LatencyMs;
        public JsonObject? Data;
        public bool Busy;
        public JsonObject? PropsCache, TagsCache;
        public long PropsDue, TagsDue;
        public LogTail.Bag? LogBag;      // ninfer: 日志尾随状态
    }
    public Dictionary<string, SvcState> SvcStates { get; } = new();

    // ---- GPU 状态 ----
    public sealed class GpuRow
    {
        public int Index; public string Name = "", Uuid = "";
        public double Util, MemUsed, MemTotal, Temp, Power, PowerLimit, ClkSm, ClkMax;
        public readonly List<double> UtilHist = new(), MemHist = new(), TempHist = new(), PowerHist = new();
    }
    public List<GpuRow> Gpus = new();
    public JsonArray ProcsRaw = new();           // 显存归属表
    public int SmiFails; public long SmiLastOk, SmiUnavailableUntil;

    // ---- 系统 ----
    private ulong _prevIdle, _prevKernel, _prevUser, _prevTotal;
    public double CpuUtil; public (ulong total, ulong avail) Ram = (0, 0);
    public readonly List<double> CpuHist = new();

    // ---- 调度 ----
    private readonly Dictionary<string, long> _taskDue = new();
    private readonly ConcurrentDictionary<string, byte> _busy = new();
    private CancellationTokenSource? _cts;
    private bool _windowVisible = true;
    private long _lastScan, _portPidDue;
    private Dictionary<int, int> _portPid = new();

    public Monitor(ServiceRegistry registry)
    {
        _registry = registry;
#if NETFRAMEWORK
        // net48：WebRequestHandler 显式绕系统代理（sing-box 会劫持 127.0.0.1 探测）
        _http = new HttpClient(new System.Net.Http.WebRequestHandler
        {
            UseProxy = false,
            AllowAutoRedirect = true,
        }) { Timeout = Timeout.InfiniteTimeSpan };
#else
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,                       // 关键：本机 sing-box 系统代理会劫持 127.0.0.1
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            MaxConnectionsPerServer = 8,
        };
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
#endif
        foreach (var s in _registry.Services.Concat(_registry.Discovered))
            SvcStates[s.id] = new SvcState { Def = s };
    }

    public void SetWindowVisible(bool visible) { _windowVisible = visible; }
    public void Start() { _cts = new CancellationTokenSource(); _ = RunLoop(_cts.Token); }
    public void Stop() { _cts?.Cancel(); }

    /// <summary>services.json 重载后补建状态。</summary>
    public void ReloadServices()
    {
        foreach (var s in _registry.Services.Concat(_registry.Discovered).ToList())
            GetOrCreate(s);
    }

    /// <summary>当前生效基准（考虑暂停/隐藏降频）。0 = 暂停。</summary>
    public int EffectiveBase()
    {
        if (_cfg.Paused) return 0;
        if (!_windowVisible)
        {
            switch (_cfg.HiddenBehavior)
            {
                case "keep": break;
                case "pause": return 0;
                default: return Math.Max(_cfg.Interval, 10);
            }
        }
        return _cfg.Interval;
    }

    private static int Derived(int baseS, double mult) => Compat.Clamp((int)Math.Round(baseS * mult), 1, 120);
    private static int ProbeTimeout(int baseS) => baseS <= 2 ? 1500 : 3000;

    private async Task RunLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            int baseS = EffectiveBase();
            if (baseS == 0)
            {
                PushSnapshot();
                try { await Task.Delay(1000, ct); } catch { }
                continue;
            }
            long now = NowMs();
            var due = new List<Task>();

            foreach (var svc in _registry.Services.Concat(_registry.Discovered).Where(s => s.enabled).ToList())
            {
                var st = GetOrCreate(svc);
                due.Add(TryRun("svc:" + svc.id, now, IntervalFor(svc, baseS), st, () => ProbeService(st, baseS)));
            }

            if (_cfg.NvidiaSmi && now >= SmiUnavailableUntil)
            {
                due.Add(TryRun("smi", now, Derived(baseS, 1), null, () => RunNvidiaMain()));
                due.Add(TryRun("smi_procs", now, Derived(baseS, 3), null, () => RunNvidiaProcs()));
            }

            if (_cfg.AutoDiscover && now - _lastScan >= Derived(baseS, 30) * 1000L)
            {
                _lastScan = now;
                // 扫描 fire-and-forget：绝不能挂进 WhenAll，否则一个不回 HTTP 的端口会卡死整个刷新循环
                _ = TryRun("scan", now, 1, null, async () =>
                {
                    await _registry.ScanAsync(_http);
                    foreach (var d in _registry.Discovered)
                        if (!SvcStates.ContainsKey(d.id)) SvcStates[d.id] = new SvcState { Def = d };
                });
            }

            if (now >= _portPidDue) { _portPidDue = now + Derived(baseS, 5) * 1000L; _portPid = SafeTcpMap(); }

            SampleCpu();

            try { await Task.WhenAll(due); } catch { }
            PushSnapshot();

            try { await Task.Delay(TimeSpan.FromSeconds(baseS), ct); } catch { }
        }
    }

    private static Dictionary<int, int> SafeTcpMap()
    {
        try { return TcpMap.ListenerPids(); } catch { return new Dictionary<int, int>(); }
    }

    private SvcState GetOrCreate(ServiceDef def)
    {
        if (!SvcStates.TryGetValue(def.id, out var st))
        {
            st = new SvcState { Def = def };
            SvcStates[def.id] = st;
        }
        return st;
    }

    private static int IntervalFor(ServiceDef def, int baseS) => def.kind switch
    {
        "strata" or "ninfer" or "llama" or "ollama" => Derived(baseS, 1),
        _ => Derived(baseS, 2),
    };

    private Task TryRun(string key, long now, int intervalS, SvcState? st, Func<Task> body)
    {
        long dueAt = _taskDue.TryGetValue(key, out var d) ? d : 0;
        if (now < dueAt) return Task.CompletedTask;
        _taskDue[key] = now + intervalS * 1000L;
        if (st != null && st.Busy) return Task.CompletedTask;   // 上一轮未回，跳过本轮防堆积
        if (st == null && !_busy.TryAddEx(key)) return Task.CompletedTask;
        return Task.Run(async () =>
        {
            try { await body(); }
            catch (Exception e) { Logger.Warn(key + " 异常: " + e.Message); }
            finally
            {
                if (st != null) st.Busy = false;
                else _busy.TryRemove(key, out _);
            }
        });
    }

    // ================= 服务探测 =================

    private async Task ProbeService(SvcState st, int baseS)
    {
        var def = st.Def;
        int to = ProbeTimeout(baseS);
        var sw = Stopwatch.StartNew();
        try
        {
            JsonObject data = new();
            long now = NowMs();
            switch (def.kind)
            {
                case "strata":
                    data["health"] = await GetJson(def.base_url + def.health_path, to);
                    if (def.metrics_path != null)
                        data["metrics"] = await GetJson(def.base_url + def.metrics_path, to);
                    break;
                case "ninfer":
                    // KVMem(NInfer)：HTTP 三样 + 日志尾随（throughput/req done 行）
                    data["health"] = await GetJson(def.base_url + def.health_path, to);
                    if (def.stats_path != null)
                        data["stats"] = TrimStats(await GetJson(def.base_url + def.stats_path, to));
                    if (def.props_path != null && (st.PropsDue == 0 || now >= st.PropsDue))
                    {
                        st.PropsCache = TrimStats(await GetJson(def.base_url + def.props_path, to));
                        st.PropsDue = now + Derived(baseS, 5) * 1000L;
                    }
                    if (st.PropsCache != null) data["props"] = st.PropsCache.DeepClone();
                    if (!string.IsNullOrEmpty(def.log_path))
                    {
                        st.LogBag ??= new LogTail.Bag();
                        LogTail.Update(st.LogBag, def.log_path!);
                        data["logtail"] = LogTail.Snapshot(st.LogBag);
                    }
                    break;
                case "llama":
                    await GetJson(def.base_url + def.health_path, to);
                    if (def.props_path != null && (st.PropsDue == 0 || now >= st.PropsDue))
                    {
                        st.PropsCache = TrimProps(await GetJson(def.base_url + def.props_path, to));
                        st.PropsDue = now + Derived(baseS, 5) * 1000L;
                    }
                    if (st.PropsCache != null) data["props"] = st.PropsCache.DeepClone();
                    if (def.slots_path != null)
                        data["slots"] = TrimSlots(await GetJson(def.base_url + def.slots_path, to));
                    break;
                case "ollama":
                    data["version"] = await GetJson(def.base_url + def.health_path, to);
                    data["ps"] = await GetJson(def.base_url + "/api/ps", to);
                    if (st.TagsCache == null || now >= st.TagsDue)
                    {
                        st.TagsCache = TrimTags(await GetJson(def.base_url + "/api/tags", to));
                        st.TagsDue = now + Derived(baseS, 15) * 1000L;
                    }
                    data["tags"] = st.TagsCache!.DeepClone();
                    break;
                case "comfyui":
                    data["health"] = TrimComfy(await GetJson(def.base_url + def.health_path, to));
                    break;
                default: // tts / asr / generic
                    data["health"] = await GetJson(def.base_url + def.health_path, to);
                    break;
            }
            sw.Stop();
            st.Data = data;
            st.LatencyMs = sw.ElapsedMilliseconds;
            st.LastOkMs = now;
            st.Online = true;
            st.Fails = 0;
        }
        catch
        {
            sw.Stop();
            st.Fails++;
            if (st.Fails >= 3) st.Online = false;   // 连续失败 3 次才判离线
        }
    }

    private async Task<JsonNode?> GetJson(string url, int timeoutMs)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseContentRead, cts.Token);
        resp.EnsureSuccessStatusCode();
        var text = await Compat.ReadStr(resp.Content, cts.Token);
        return JsonNode.Parse(text);
    }

    private static JsonObject TrimProps(JsonNode? p)
    {
        var o = new JsonObject();
        if (p is JsonObject po)
        {
            Copy(po, o, "model_alias", "model_ftype", "model_path", "total_slots", "build_info", "is_sleeping", "modalities");
            if (po["default_generation_settings"]?["n_ctx"] is JsonNode n) o["n_ctx"] = n.DeepClone();
        }
        return o;
    }

    private static JsonArray TrimSlots(JsonNode? s)
    {
        var arr = new JsonArray();
        if (s is JsonArray sa)
            foreach (var sl in sa.OfType<JsonObject>())
            {
                var o = new JsonObject();
                Copy(sl, o, "id", "n_ctx", "is_processing", "n_prompt_tokens", "n_prompt_tokens_processed", "n_prompt_tokens_cache", "speculative");
                arr.Add(o);
            }
        return arr;
    }

    private static JsonObject TrimTags(JsonNode? t)
    {
        var o = new JsonObject(); var arr = new JsonArray();
        if (t?["models"] is JsonArray ma)
            foreach (var m in ma.OfType<JsonObject>())
            {
                var mo = new JsonObject { ["name"] = m["name"]?.DeepClone(), ["size"] = m["size"]?.DeepClone(), ["modified_at"] = m["modified_at"]?.DeepClone() };
                if (m["details"] is JsonObject d) Copy(d, mo, "parameter_size", "quantization_level", "context_length", "family");
                arr.Add(mo);
            }
        o["models"] = arr;
        return o;
    }

    private static JsonObject TrimComfy(JsonNode? ss)
    {
        var o = new JsonObject();
        if (ss?["system"] is JsonObject sys) Copy(sys, o, "os", "python_version", "comfyui_version");
        if (ss?["devices"] is JsonArray dev)
        {
            var arr = new JsonArray();
            foreach (var dv in dev.OfType<JsonObject>())
            {
                var mo = new JsonObject();
                Copy(dv, mo, "name", "type", "vram_total", "vram_free", "torch_vram_total", "torch_vram_free");
                arr.Add(mo);
            }
            o["devices"] = arr;
        }
        return o;
    }

    /// <summary>ninfer /stats /props 结构未定型：只留两层内的标量，防快照爆炸。</summary>
    private static JsonObject TrimStats(JsonNode? s)
    {
        var o = new JsonObject();
        if (s is not JsonObject so) return o;
        foreach (var (k, v) in so)
        {
            if (v is JsonObject vo)
            {
                var inner = new JsonObject();
                int cnt = 0;
                foreach (var (k2, v2) in vo)
                {
                    if (v2 is JsonObject || v2 is JsonArray) continue;
                    inner[k2] = v2?.DeepClone();
                    if (++cnt >= 30) break;
                }
                if (cnt > 0) o[k] = inner;
            }
            else if (v is not JsonArray)
                o[k] = v?.DeepClone();
        }
        return o;
    }

    private static void Copy(JsonObject src, JsonObject dst, params string[] keys)
    {
        foreach (var k in keys)
            if (src.TryGetPropertyValue(k, out var v) && v != null) dst[k] = v.DeepClone();
    }

    // ================= nvidia-smi =================

    private async Task RunNvidiaMain()
    {
        var outp = await Smi(
            "--query-gpu=index,name,uuid,utilization.gpu,utilization.memory,memory.total,memory.used,temperature.gpu,power.draw,power.limit,clocks.sm,clocks.max.sm",
            "csv,noheader,nounits", 3000);
        var lines = outp.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) throw new Exception("nvidia-smi empty");
        var rows = new List<GpuRow>();
        foreach (var line in lines)
        {
            var c = line.Split(',');
            if (c.Length < 12) continue;
            rows.Add(new GpuRow
            {
                Index = (int)D(c[0]),
                Name = c[1].Trim(),
                Uuid = c[2].Trim(),
                Util = D(c[3]), MemTotal = D(c[5]), MemUsed = D(c[6]),
                Temp = D(c[7]), Power = D(c[8]), PowerLimit = D(c[9]),
                ClkSm = D(c[10]), ClkMax = D(c[11]),
            });
        }
        foreach (var g in rows)
        {
            var old = Gpus.FirstOrDefault(o => o.Index == g.Index);
            if (old != null)
            {
                g.UtilHist.AddRange(old.UtilHist); g.MemHist.AddRange(old.MemHist);
                g.TempHist.AddRange(old.TempHist); g.PowerHist.AddRange(old.PowerHist);
            }
            g.UtilHist.Add(g.Util); Cap(g.UtilHist);
            g.MemHist.Add(g.MemUsed); Cap(g.MemHist);
            g.TempHist.Add(g.Temp); Cap(g.TempHist);
            g.PowerHist.Add(g.Power); Cap(g.PowerHist);
        }
        Gpus = rows;
        SmiFails = 0; SmiLastOk = NowMs();
    }

    private async Task RunNvidiaProcs()
    {
        var outp = await Smi("--query-compute-apps=gpu_uuid,pid,process_name,used_memory", "csv,noheader,nounits", 3000);
        var merged = new Dictionary<int, (string name, Dictionary<int, double> per)>();
        foreach (var line in outp.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var c = line.Split(',');
            if (c.Length < 4) continue;
            if (!int.TryParse(c[1].Trim(), out int pid)) continue;
            string uuid = c[0].Trim();
            string pname = c[2].Trim();
            double mib = D(c[3]);
            int gpuIdx = Gpus.FirstOrDefault(g => g.Uuid.Equals(uuid, StringComparison.OrdinalIgnoreCase))?.Index ?? -1;
            if (!merged.TryGetValue(pid, out var ent))
            {
                ent = (Path.GetFileName(pname), new Dictionary<int, double>());
                merged[pid] = ent;
            }
            ent.per[gpuIdx] = ent.per.ValOrDef(gpuIdx) + mib;
        }
        ProcsRaw = BuildProcTable(merged);
    }

    private JsonArray BuildProcTable(Dictionary<int, (string name, Dictionary<int, double> per)> merged)
    {
        var arr = new JsonArray();
        foreach (var (pid, (pname, per)) in merged.OrderByDescending(m => m.Value.per.Values.Sum()))
        {
            string? svcId = null;
            foreach (var svc in _registry.Services.Concat(_registry.Discovered))
            {
                int port = ServiceRegistry.Port(svc.base_url);
                if (port > 0 && _portPid.TryGetValue(port, out int spid) && spid == pid) { svcId = svc.id; break; }
            }
            arr.Add(new JsonObject
            {
                ["pid"] = pid,
                ["name"] = pname,
                ["per"] = PerNode(per),
                ["total"] = Math.Round(per.Values.Sum(), 1),
                ["service"] = svcId,
            });
        }
        return arr;
    }

    private static JsonNode PerNode(Dictionary<int, double> per)
    {
        var o = new JsonObject();
        foreach (var (k, v) in per) o["g" + k] = Math.Round(v, 1);
        return o;
    }

    private static string? _smiExe;   // nvidia-smi 实际可执行路径（通用化：PATH 找不到时探常见位置）

    private static string FindSmi()
    {
        if (_smiExe != null) return _smiExe;
        try
        {
            var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            foreach (var p in new[] { Path.Combine(win, "System32", "nvidia-smi.exe"), Path.Combine(win, "Sysnative", "nvidia-smi.exe") })
                if (File.Exists(p)) { _smiExe = p; return p; }
        }
        catch { }
        _smiExe = "nvidia-smi";   // 回退 PATH 解析；无 NVIDIA 驱动时调用方按失败计数处理
        return _smiExe;
    }

    private static async Task<string> Smi(string query, string format, int timeoutMs)
    {
        var exe = _smiExe ?? FindSmi();
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            Arguments = "\"" + query + "\" --format=" + format,   // net48 无 ArgumentList，引号包裹即可（参数无特殊字符）
        };
        using var p = Process.Start(psi)!;
        using var cts = new CancellationTokenSource(timeoutMs);
        var readTask = Compat.ReadToEndTok(p.StandardOutput, cts.Token);
        try
        {
            await Compat.WaitExitTok(p, cts.Token);
            return await readTask;
        }
        catch
        {
            try { Compat.KillTree(p); } catch { }
            throw;
        }
    }

    // ================= 系统采样 =================

    private void SampleCpu()
    {
        try
        {
            if (!Native.GetSystemTimes(out var idle, out var kernel, out var user)) return;
            ulong i = Native.ToU64(idle), k = Native.ToU64(kernel), u = Native.ToU64(user);
            if (_prevTotal > 0)
            {
                ulong totalDelta = (k - _prevKernel) + (u - _prevUser);
                if (totalDelta > 0) CpuUtil = Math.Round(100.0 * (totalDelta - (i - _prevIdle)) / totalDelta, 1);
            }
            _prevIdle = i; _prevKernel = k; _prevUser = u;
            _prevTotal = 1;
            CpuHist.Add(CpuUtil); Cap(CpuHist);
            Ram = Native.Memory();
        }
        catch { }
    }

    // ================= 快照 =================

    private static readonly JsonSerializerOptions SnapOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private void PushSnapshot()
    {
        var handler = SnapshotReady;
        if (handler == null) return;
        try
        {
            var snap = BuildSnapshot();
            handler(snap.ToJsonString(SnapOpts));
        }
        catch (Exception e) { Logger.Warn("快照构建失败: " + e.Message); }
    }

    public JsonObject BuildSnapshot()
    {
        long now = NowMs();
        int baseS = Math.Max(1, _cfg.Interval);
        long staleMs = Math.Max(5000, baseS * 2500L);

        var o = new JsonObject
        {
            ["t"] = now,
            ["interval"] = _cfg.Interval,
            ["effBase"] = EffectiveBase(),
            ["paused"] = _cfg.Paused,
            ["theme"] = _cfg.Theme,
            ["opacity"] = _cfg.Opacity,
            ["topmost"] = _cfg.Topmost,
            ["clickThrough"] = _cfg.ClickThrough,
            ["autoHide"] = _cfg.AutoHideDock,
            ["snap"] = _cfg.Snap,
            ["showGpuTable"] = _cfg.ShowGpuTable,
            ["showTopology"] = _cfg.ShowTopology,
            ["smiOn"] = _cfg.NvidiaSmi,
            ["smiStale"] = SmiLastOk > 0 && now - SmiLastOk > staleMs,
            ["smiAgeMs"] = SmiLastOk > 0 ? now - SmiLastOk : -1,
            ["smiDown"] = SmiFails >= 3,
            ["cpu"] = CpuUtil,
            ["cpuHist"] = Arr(CpuHist),
            ["ramTotal"] = (double)Ram.total,
            ["ramAvail"] = (double)Ram.avail,
        };

        var gpus = new JsonArray();
        foreach (var g in Gpus)
        {
            gpus.Add(new JsonObject
            {
                ["index"] = g.Index, ["name"] = g.Name,
                ["util"] = g.Util, ["memUsed"] = g.MemUsed, ["memTotal"] = g.MemTotal,
                ["temp"] = g.Temp, ["power"] = g.Power, ["powerLimit"] = g.PowerLimit,
                ["clkSm"] = g.ClkSm, ["clkMax"] = g.ClkMax,
                ["utilHist"] = Arr(g.UtilHist), ["memHist"] = Arr(g.MemHist),
                ["tempHist"] = Arr(g.TempHist), ["powerHist"] = Arr(g.PowerHist),
            });
        }
        o["gpus"] = gpus;
        o["gpuProcs"] = ProcsRaw.DeepClone();

        var svcs = new JsonArray();
        int online = 0, total = 0;
        foreach (var st in SvcStates.Values)
        {
            if (!st.Def.enabled) continue;
            total++;
            if (st.Online) online++;
            svcs.Add(new JsonObject
            {
                ["id"] = st.Def.id, ["name"] = st.Def.name, ["kind"] = st.Def.kind,
                ["url"] = st.Def.base_url, ["note"] = st.Def.note,
                ["discovered"] = st.Def.discovered ? (JsonNode?)true : null,
                ["online"] = st.Online, ["fails"] = st.Fails,
                ["lat"] = st.Online ? System.Text.Json.Nodes.JsonValue.Create(st.LatencyMs) : null,
                ["lastOk"] = st.LastOkMs > 0 ? System.Text.Json.Nodes.JsonValue.Create(st.LastOkMs) : null,
                ["data"] = st.Data?.DeepClone(),
            });
        }
        o["services"] = svcs;
        o["summary"] = new JsonObject { ["online"] = online, ["total"] = total };
        o["order"] = _cfg.GetNode("order")?.DeepClone() ?? new JsonArray();
        return o;
    }

    private static JsonArray Arr(List<double> h)
    {
        var a = new JsonArray();
        foreach (var v in h) a.Add(Math.Round(v, 2));
        return a;
    }

    private static void Cap(List<double> l) { while (l.Count > 300) l.RemoveAt(0); }
    private static double D(string s) => double.TryParse(s.Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
    public static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
