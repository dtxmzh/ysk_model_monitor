using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;

namespace ModelMonitor;

/// <summary>
/// ninfer(KVMem) 引擎日志尾随解析：5s 吞吐行 + req done 行 → 内存样本历史。
/// 该日志行是引擎自带的稳定 INFO 口径（throughput / req#N done），比 /stats 未定型的
/// schema 更可靠，因此作为 KVMem 卡的 prefill/decode 曲线与请求表数据源。
/// 兼容 net48/net10：只用 Regex + StreamReader 基础 API。
/// </summary>
public static class LogTail
{
    public sealed class Sample
    {
        public long Tms;
        public double? Prefill, Decode, Batch, Host;
        public int? Running, PTok, OTok, Cached, HitPct, TTFT, Dur;
        public string? Finish;
    }

    public static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    public static long NowMs() => (long)(DateTime.UtcNow - UnixEpoch).TotalMilliseconds;

    const int Cap = 160;                 // 样本上限（throughput 5s 一条 ≈ 13 分钟窗口）
    const long FirstTailBytes = 150 * 1024;
    const long MaxNewBytes = 64 * 1024;  // 单轮最多读增量，防重启风暴时快照爆炸

    static readonly Regex RxTime = new Regex(@"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\.(\d{3})", RegexOptions.Compiled);
    static readonly Regex RxThroughput = new Regex(
        @"throughput \| [\d.]+s \| (?:prefill ([\d.,]+) tok/s \((\d+) tok\) \| )?decode ([\d.,]+) tok/s \((\d+) tok\) \| running (\d+)(?:[^|]*\| batch ([\d.,]+))? \| host ([\d.,]+)%",
        RegexOptions.Compiled);
    static readonly Regex RxDone = new Regex(
        @"req#(\d+) done \| (\S+) \| (.*?) \| prompt ([\d,]+) \| output ([\d,]+) \| cache ([\d,]+) \(([\d.,]+)%\) \| TTFT ([\d.,]+) ms \| total ([\d.,]+) ms \| prefill ([\d.,]+) tok/s \| decode ([\d.,]+) tok/s",
        RegexOptions.Compiled);

    static double N(string? s)
    {
        if (string.IsNullOrEmpty(s)) return double.NaN;
        s = s!.Replace(",", "").Replace("，", "");
        double v;
        return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out v) ? v : double.NaN;
    }
    static bool Bad(double v) => double.IsNaN(v) || double.IsInfinity(v);

    static long ParseMs(string dateSec, string ms)
    {
        DateTime dt;
        if (!DateTime.TryParseExact(dateSec, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out dt)) return 0;
        // 引擎日志为本地时间；转 UTC 毫秒
        return new DateTimeOffset(dt, TimeZoneInfo.Local.GetUtcOffset(dt)).ToUnixTimeMilliseconds()
             + ParseIntSafe(ms);
    }
    static int ParseIntSafe(string s) { int v; return int.TryParse(s, out v) ? v : 0; }

    public static Sample? ParseThroughput(string line, long fallbackMs)
    {
        var mt = RxThroughput.Match(line);
        if (!mt.Success) return null;
        var tm = RxTime.Match(line);
        var s = new Sample { Tms = tm.Success ? ParseMs(tm.Groups[1].Value, tm.Groups[2].Value) : fallbackMs };
        if (mt.Groups[1].Success) { s.Prefill = N(mt.Groups[1].Value); s.PTok = ParseIntSafe(mt.Groups[2].Value); }
        s.Decode = N(mt.Groups[3].Value); s.OTok = ParseIntSafe(mt.Groups[4].Value);
        s.Running = ParseIntSafe(mt.Groups[5].Value);
        if (mt.Groups[6].Success) s.Batch = N(mt.Groups[6].Value);
        if (mt.Groups[7].Success) s.Host = N(mt.Groups[7].Value);
        return s;
    }

    public static Sample? ParseDone(string line, long fallbackMs)
    {
        var mt = RxDone.Match(line);
        if (!mt.Success) return null;
        var tm = RxTime.Match(line);
        var s = new Sample { Tms = tm.Success ? ParseMs(tm.Groups[1].Value, tm.Groups[2].Value) : fallbackMs };
        s.Finish = mt.Groups[3].Value;
        s.PTok = ParseIntSafe(mt.Groups[4].Value); s.OTok = ParseIntSafe(mt.Groups[5].Value);
        s.Cached = ParseIntSafe(mt.Groups[6].Value); s.HitPct = (int)N(mt.Groups[7].Value);
        s.TTFT = (int)N(mt.Groups[8].Value); s.Dur = (int)N(mt.Groups[9].Value);
        s.Prefill = N(mt.Groups[10].Value); s.Decode = N(mt.Groups[11].Value);
        return s;
    }

    /// <summary>状态袋：每 svc 一个，持有样本历史 + 文件偏移（rotation 感知）。</summary>
    public sealed class Bag
    {
        public readonly List<Sample> Throught = new List<Sample>();   // 吞吐心跳序列
        public readonly List<Sample> Reqs = new List<Sample>();       // req done 序列
        public long Offset = -1;
        public long FileLen;
        public bool RotationWarned;
        public long LastReadMs;
    }

    static void Push(List<Sample> list, Sample s)
    {
        list.Add(s);
        while (list.Count > Cap) list.RemoveAt(0);
    }

    /// <summary>尾随读取 path 增量行并解析。path 不存在/被锁时静默跳过本轮。</summary>
    public static void Update(Bag bag, string path)
    {
        long now = NowMs();
        long len;
        try { len = new System.IO.FileInfo(path).Length; }
        catch { return; }                                  // 文件不在：静默

        if (bag.Offset < 0)
        {
            bag.Offset = Math.Max(0, len - FirstTailBytes);  // 首轮：只回灌最近 ~150KB 历史
            bag.FileLen = len;
        }
        else if (len < bag.FileLen - 4096)
        {
            if (!bag.RotationWarned)
            {
                Logger.Info("LogTail 检测到日志截断/轮转，重置偏移: " + path);
                bag.RotationWarned = true;
            }
            bag.Offset = Math.Max(0, len - FirstTailBytes);
            bag.FileLen = len;
        }
        else if (len >= bag.FileLen - 4096)
        {
            bag.RotationWarned = false;
            bag.FileLen = len;
        }
        if (len <= bag.Offset) return;                     // 无增量

        long limit = Math.Min(len, bag.Offset + MaxNewBytes);
        var sb = new System.Text.StringBuilder();
        try
        {
            using (var fs = new System.IO.FileStream(path, System.IO.FileMode.Open,
                       System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete))
            using (var sr = new System.IO.StreamReader(fs, System.Text.Encoding.UTF8, true))
            {
                fs.Seek(bag.Offset, System.IO.SeekOrigin.Begin);
                long budget = limit - bag.Offset;
                char[] buf = new char[16384];
                int readChars;
                while (budget > 0 && (readChars = sr.Read(buf, 0, (int)Math.Min(buf.Length, budget))) > 0)
                {
                    sb.Append(buf, 0, readChars);
                    budget -= readChars;                   // char≈byte 近似；换行对齐由下方 Offset 修正
                }
                bag.Offset += EncodingWidthGuess(sb.ToString());
            }
        }
        catch { return; }                                  // 引擎持锁：下轮再读

        // 未读完的残行丢弃（下次 seek 已对齐到最后换行）
        int lastNl = sb.ToString().LastIndexOf('\n');
        string text = lastNl >= 0 ? sb.ToString(0, lastNl) : sb.ToString();
        if (lastNl < 0) bag.Offset = limit;                // 整段无换行：视为已消费
        bag.LastReadMs = now;

        foreach (var raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0 || line.IndexOf("throughput", 0, StringComparison.Ordinal) < 0
                                    && line.IndexOf(" done ", 0, StringComparison.Ordinal) < 0) continue;
            var th = ParseThroughput(line, now);
            if (th != null) { Push(bag.Throught, th); continue; }
            var dn = ParseDone(line, now);
            if (dn != null) Push(bag.Reqs, dn);
        }
    }

    // UTF8 多字节会让 char 数 != byte 数；偏移按已读文本的 UTF8 字节长修正（宁可重叠不跳过）
    static long EncodingWidthGuess(string s)
    {
        try { return System.Text.Encoding.UTF8.GetByteCount(s); }
        catch { return s.Length; }
    }

    public static JsonNode Snapshot(Bag bag)
    {
        var o = new JsonObject();
        var th = new JsonArray();
        foreach (var s in bag.Throught) th.Add(SampleNode(s));
        o["throughput"] = th;
        var rq = new JsonArray();
        foreach (var s in bag.Reqs) rq.Add(SampleNode(s));
        o["requests"] = rq;
        return o;
    }

    static JsonObject SampleNode(Sample s)
    {
        var o = new JsonObject { ["t"] = s.Tms / 1000.0 };
        if (s.Prefill.HasValue && !Bad(s.Prefill.Value)) o["prefill"] = Math.Round(s.Prefill.Value, 1);
        if (s.Decode.HasValue && !Bad(s.Decode.Value)) o["decode"] = Math.Round(s.Decode.Value, 1);
        if (s.Running.HasValue) o["running"] = s.Running.Value;
        if (s.Batch.HasValue && !Bad(s.Batch.Value)) o["batch"] = Math.Round(s.Batch.Value, 2);
        if (s.Host.HasValue && !Bad(s.Host.Value)) o["host"] = Math.Round(s.Host.Value, 1);
        if (s.PTok.HasValue) o["prompt"] = s.PTok.Value;
        if (s.OTok.HasValue) o["output"] = s.OTok.Value;
        if (s.Cached.HasValue) o["cached"] = s.Cached.Value;
        if (s.HitPct.HasValue) o["hit_pct"] = s.HitPct.Value;
        if (s.TTFT.HasValue) o["ttft_ms"] = s.TTFT.Value;
        if (s.Dur.HasValue) o["dur_ms"] = s.Dur.Value;
        if (!string.IsNullOrEmpty(s.Finish)) o["finish"] = s.Finish;
        return o;
    }
}
