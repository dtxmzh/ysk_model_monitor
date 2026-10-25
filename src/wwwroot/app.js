/* 本机模型监控 · 前端渲染（无外部库，全部手写 SVG） */
"use strict";

/* ---------------- 桥接 ---------------- */
const send = (m) => { try { window.chrome.webview.postMessage(m); } catch (e) {} };
let menuTree = null;        // 与托盘菜单同一数据源（宿主推送）
let firstSnap = true;
let snap = null;
let showAllReqs = false;
let expanded = new Set();   // 展开的服务卡 id
let scrollRestored = false;

/* ---------------- 工具 ---------------- */
const $ = (s) => document.querySelector(s);
const el = (tag, cls, html) => { const n = document.createElement(tag); if (cls) n.className = cls; if (html != null) n.innerHTML = html; return n; };
const esc = (s) => String(s == null ? "" : s).replace(/[&<>"]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
const DASH = "–";
const num = (v, d = 1) => (v == null || isNaN(v)) ? DASH : Number(v).toFixed(d);
const mib2gb = (m) => m == null ? null : m / 1024;
const bytes2gb = (b) => b == null ? null : b / 1073741824;
const hhmmss = (ms) => { const d = new Date(ms); return d.toTimeString().slice(0, 8); };
const trunc = (s, n) => { s = String(s ?? ""); return s.length > n ? s.slice(0, n - 1) + "…" : s; };

/* 内联 SVG 图标（统一 1.6 描边） */
const IC = {
  speed: '<svg viewBox="0 0 16 16"><path d="M8 9.5L11.5 5M2.5 13a6.5 6.5 0 1 1 11 0"/></svg>',
  gpu: '<svg viewBox="0 0 16 16"><rect x="2" y="5" width="12" height="8" rx="1.5"/><path d="M5 5V3.5M11 5V3.5M5 13v1.5M11 13v1.5"/></svg>',
  vram: '<svg viewBox="0 0 16 16"><rect x="2.5" y="4.5" width="11" height="7" rx="1"/><path d="M5 11.5v2M8 11.5v2M11 11.5v2M5 4.5v-2M8 4.5v-2M11 4.5v-2"/></svg>',
  temp: '<svg viewBox="0 0 16 16"><path d="M6.5 9.5V3.5a1.5 1.5 0 0 1 3 0v6a3 3 0 1 1-3 0z"/></svg>',
  power: '<svg viewBox="0 0 16 16"><path d="M8.5 2L4 9h3l-.5 5L12 7H9z"/></svg>',
  pcie: '<svg viewBox="0 0 16 16"><path d="M2 8h12M10 4l4 4-4 4"/></svg>',
  cpu: '<svg viewBox="0 0 16 16"><rect x="4.5" y="4.5" width="7" height="7" rx="1"/><path d="M6.5 2v2.5M9.5 2v2.5M6.5 11.5V14M9.5 11.5V14M2 6.5h2.5M2 9.5h2.5M11.5 6.5H14M11.5 9.5H14"/></svg>',
  disk: '<svg viewBox="0 0 16 16"><circle cx="8" cy="8" r="5.5"/><circle cx="8" cy="8" r="1.5"/></svg>',
  chev: '<svg viewBox="0 0 16 16"><path d="M4 6l4 4 4-4"/></svg>',
  pin: '<svg viewBox="0 0 16 16"><path d="M9.5 2l4.5 4.5-2 2-.6-.6-3.3 3.3.4 2.3-1.4 1.4-3-3-3.6 3.6-.7-.7 3.6-3.6-3-3 1.4-1.4 2.3.4 3.3-3.3-.6-.6z"/></svg>',
  grip: '<svg viewBox="0 0 16 16"><path d="M6 3.5v9M10 3.5v9"/></svg>',
  empty: '<svg viewBox="0 0 24 24"><path d="M4 17h16M6 17V9m4 8V6m4 11v-6m4 6V8"/><path d="M3 20h18"/></svg>',
};

/* sparkline：照抄 Strata 规格 viewBox 0 0 100 32 */
function sparkPath(vals) {
  if (!vals || vals.length < 2) return null;
  const top = Math.max(Math.max(...vals) || 0, 1);
  const n = vals.length;
  const pts = vals.map((v, i) => `${(i / (n - 1) * 100).toFixed(2)},${(30 - (v / top) * 26).toFixed(2)}`);
  const line = "M" + pts.join("L");
  return { line, area: line + "L100,32L0,32Z" };
}
function sparkSVG(vals, cls = "") {
  const p = sparkPath(vals);
  if (!p) return `<svg class="spark" viewBox="0 0 100 32" preserveAspectRatio="none"></svg>`;
  return `<svg class="spark" viewBox="0 0 100 32" preserveAspectRatio="none">
    <path class="area ${cls}" d="${p.area}"/><path class="line ${cls}" d="${p.line}"/></svg>`;
}
function sparkDualSVG(a, b) {
  const pa = sparkPath(a), pb = sparkPath(b);
  let s = `<svg class="spark" viewBox="0 0 100 32" preserveAspectRatio="none">`;
  if (pb) s += `<path class="area i2" d="${pb.area}"/><path class="line i2" d="${pb.line}"/>`;
  if (pa) s += `<path class="area" d="${pa.area}"/><path class="line" d="${pa.line}"/>`;
  return s + `</svg>`;
}

/* 环形仪表：viewBox 0 0 120 120, r=50, 270° 弧 */
function gaugeSVG(pct, label, sub) {
  pct = Math.max(0, Math.min(100, pct || 0));
  const C = 2 * Math.PI * 50;               // 314.2
  const arc = C * 0.75;                     // 235.6
  const fill = arc * pct / 100;
  const tone = pct > 90 ? "var(--danger)" : pct > 80 ? "var(--warn)" : "var(--accent)";
  return `<svg class="gauge" viewBox="0 0 120 120">
    <circle class="track" cx="60" cy="60" r="50" stroke-dasharray="${arc.toFixed(1)} ${C.toFixed(1)}" transform="rotate(135 60 60)"/>
    <circle class="fill" cx="60" cy="60" r="50" style="stroke:${tone}" stroke-dasharray="${fill.toFixed(1)} ${C.toFixed(1)}" transform="rotate(135 60 60)"/>
    <text class="g-num" x="60" y="58" text-anchor="middle">${Math.round(pct)}<tspan class="g-unit">%</tspan></text>
    <text class="g-unit" x="60" y="76" text-anchor="middle">${esc(label)}</text>
    ${sub ? `<text class="g-unit" x="60" y="90" text-anchor="middle">${esc(sub)}</text>` : ""}
  </svg>`;
}

function progressBar(pct, tone) {
  const t = tone || (pct > 90 ? "danger" : pct > 80 ? "warn" : "");
  return `<div class="progress" ${t ? `data-tone="${t}"` : ""}><i style="width:${Math.min(100, Math.max(0, pct))}%"></i></div>`;
}

/* ---------------- 骨架屏 ---------------- */
function renderSkeleton() {
  const sk = (lines) => { const c = el("div", "card"); let h = `<div class="skel skel-big"></div>`; for (let i = 0; i < lines; i++) h += `<div class="skel skel-line" style="width:${60 + (i * 13) % 35}%"></div>`; c.innerHTML = h; return c; };
  $("#gpu-zone").append(sk(3), sk(3));
  $("#svc-zone").append(sk(4), sk(2), sk(2));
}

/* ---------------- L1 GPU ---------------- */
function gpuCard(g, procs) {
  const card = el("div", "card gpu-card");
  const memPct = g.memTotal ? g.memUsed / g.memTotal * 100 : 0;
  const tempTone = g.temp > 85 ? "danger" : g.temp > 75 ? "warn" : "";
  const stale = snap.smiStale ? `<span class="stale-tag">陈旧 · ${Math.round((snap.smiAgeMs || 0) / 1000)}s 前</span>` : "";
  card.innerHTML = `
    <div class="card-head">
      <span class="card-title">GPU ${g.index} · ${esc(g.name)}</span>
      <span class="head-spacer"></span>${stale}
    </div>
    <div class="mgrid" style="grid-template-columns:repeat(2,1fr)">
      <div class="metric"><div class="m-label">${IC.gpu}利用率</div>
        <div class="m-value">${num(g.util, 0)}<small>%</small></div>${sparkSVG(g.utilHist)}</div>
      <div class="metric"><div class="m-label">${IC.temp}温度</div>
        <div class="m-value ${tempTone ? "tone-" + tempTone : ""}">${num(g.temp, 0)}<small>°C</small></div>${sparkSVG(g.tempHist)}</div>
    </div>
    <div class="p-row"><span class="p-label">显存</span>${progressBar(memPct, memPct > 90 ? "danger" : memPct > 80 ? "warn" : "")}
      <span class="p-val">${num(mib2gb(g.memUsed), 1)} / ${num(mib2gb(g.memTotal), 1)} GB</span></div>
    <div class="p-row"><span class="p-label">功率</span>${progressBar(g.powerLimit ? g.power / g.powerLimit * 100 : 0, "info")}
      <span class="p-val">${num(g.power, 1)} / ${num(g.powerLimit, 0)} W</span></div>
    <div class="kid" style="margin-top:6px"><span class="k-name">SM 时钟</span><span class="k-val">${num(g.clkSm, 0)} / ${num(g.clkMax, 0)} MHz</span></div>`;

  // 从属进程（缩进+竖线）
  const onGpu = procs.filter(p => (p.per["g" + g.index] || 0) > 0);
  if (snap.showGpuTable && onGpu.length) {
    const kids = el("div", "kids");
    for (const p of onGpu) {
      const mib = p.per["g" + g.index];
      const others = Object.entries(p.per).filter(([k, v]) => k !== "g" + g.index && v > 0)
        .map(([k, v]) => `↔ 另占 GPU${k.slice(1)} ${num(mib2gb(v), 1)} GB`).join(" ");
      const svcName = p.service ? (svcById(p.service)?.name || p.service) : null;
      const row = el("div", "kid" + (svcName ? "" : " dim"));
      row.innerHTML = `<span class="k-name" title="${esc(p.name)} (PID ${p.pid})">${esc(svcName || p.name)}${svcName ? " · " + esc(p.name) : ""} <span style="color:var(--text-3)">PID ${p.pid}</span></span>
        <span class="k-val">${num(mib2gb(mib), 1)} GB</span>${others ? `<span class="k-xref">${others}</span>` : ""}`;
      kids.append(row);
    }
    const otherSum = procs.filter(p => !p.service && Object.entries(p.per).some(([k, v]) => k === "g" + g.index && v > 0));
    if (otherSum.length === 0 && onGpu.every(p => p.service)) { /* 全匹配 */ }
    card.append(kids);
  }
  return card;
}

/* ---------------- L2 Strata 主卡 ---------------- */
/* prefill 实测坑：引擎 history.prefill_tok_s_mean 恒 0、live.prefill_tok_s_mean 仅 reading 瞬间非空。
   引擎自身口径 = prompt_read/prompt_ms（实际计算词元；reused 部分走对话缓存不计入）。
   前端对齐：当前值=live 值优先，否则 read/prompt_ms*1000（缺 read 回退全量）；曲线=前端按请求完成时刻自攒 */
/* 公共主引擎卡设施（区域3 = 谁在跑显示谁）：各引擎曲线独立积累 */
const ENGINE_KINDS = new Set(["strata", "ninfer"]);
const engPrefill = {};   // id -> prefill 曲线（Strata 按请求完成时刻自攒；KVMem 直接用 5s 心跳数组）
const engSeen = {};
function reqPrefill(r) {
  if (!r) return null;
  const read = r.prompt_read ?? (r.reused != null ? Math.max(0, r.prompt_tokens - r.reused) : r.prompt_tokens);
  if (read && r.prompt_ms) return read / r.prompt_ms * 1000;
  return null;
}
function trackPrefill(id, r0) {
  if (!r0 || !r0.time || engSeen[id] === r0.time) return;
  engSeen[id] = r0.time;
  const pf = reqPrefill(r0);
  if (pf && isFinite(pf) && pf > 0) { const a = engPrefill[id] ??= []; a.push(Math.round(pf)); if (a.length > 300) a.shift(); }
}
function prefillNow(live, r0) {
  if (live?.prefill_tok_s_mean > 0) return live.prefill_tok_s_mean;
  return reqPrefill(r0);
}

/* ---------------- L2 公共主引擎卡 ---------------- */
/* 区域3 = 谁在跑显示谁：strata 走 /metrics 富 JSON；ninfer(KVMem) 走日志尾随 + /stats */
function engineMainCard(s) { return s.kind === "ninfer" ? ninferCard(s) : strataCard(s); }

function ninferCard(s) {
  const card = el("div", "card strata-card open");
  const lt = s.data?.logtail || {}, th = lt.throughput || [], reqs = lt.requests || [];
  const stats = s.data?.stats || {}, props = s.data?.props || {};
  const pick = (...vs) => { for (const v of vs) if (typeof v === "number" && isFinite(v)) return v; return null; };
  const pathName = (p) => p ? String(p).split(/[\\/]/).pop() : null;
  const lastTh = th[th.length - 1], lastReq = reqs[reqs.length - 1];
  const fresh = !!lastTh && (snap.t / 1000 - lastTh.t) < 20;
  const state = s.online ? ((fresh && lastTh.running > 0) ? "generating" : "idle") : null;
  const badgeCls = { idle: "on", reading: "read", generating: "gen", queued: "queue", error: "err" };
  let badges = ["idle", "generating", "queued", "error"].map(st =>
    `<span class="badge ${st === state ? badgeCls[st] : "off"}">${st}</span>`).join("");

  const model = stats.model || props.model || pathName(stats.model_path) || pathName(props.model_path) || DASH;
  const engVer = stats.version || props.version || "ninfer";
  const ctxTotal = pick(stats.context, stats.n_ctx, stats.max_context, stats.max_ctx,
    props.n_ctx, props.context, props.max_context,
    stats.kvmem?.context, props.kvmem?.context, props.kvmem?.budget_tokens);
  const ctxCur = lastReq ? (lastReq.prompt || 0) + (lastReq.output || 0) : 0;

  const dec = fresh ? lastTh.decode : (lastReq?.decode ?? null);
  const pfl = fresh ? (lastTh.prefill ?? null) : (lastReq?.prefill ?? null);
  const decHist = th.map(x => x.decode ?? 0), pfHist = th.map(x => x.prefill ?? 0);
  const g = snap.gpus || [];
  const gUtil = g.length ? g.reduce((a, x) => a + (x.util || 0), 0) / g.length : null;
  const vUsed = g.reduce((a, x) => a + (x.memUsed || 0), 0), vTot = g.reduce((a, x) => a + (x.memTotal || 0), 0);
  const temp = g.length ? Math.max(...g.map(x => x.temp || 0)) : null;
  const pow = g.reduce((a, x) => a + (x.power || 0), 0);
  const powLim = g.reduce((a, x) => a + (x.powerLimit || 0), 0);
  const metrics = [
    { ic: "speed", label: "Speed t/s", dual: true, v1: dec, v2: pfl, hist: sparkDualSVG(decHist, pfHist), sub: `decode · prefill${fresh ? "" : "（最近请求）"}` },
    { ic: "cpu", label: "Host 占用 %", v: fresh ? lastTh.host : null, unit: "%", sub: `batch ${num(fresh ? lastTh.batch : null, 2)} · running ${fresh ? lastTh.running : 0}` },
    { ic: "gpu", label: "GPU load %", v: gUtil, hist: sparkSVG(g[0]?.utilHist || []), sub: `${g.length} 卡合计均值` },
    { ic: "vram", label: "VRAM GB", v: vTot ? vUsed / 1024 : null, vFmt: 1, unit: "GB", hist: sparkSVG(g[0]?.memHist || []), sub: `共 ${num(vTot / 1024, 1)} GB`, tone: vTot && vUsed / vTot > .9 ? "danger" : "" },
    { ic: "temp", label: "GPU temp °C", v: temp, unit: "°C", hist: sparkSVG(g[0]?.tempHist || []), tone: temp > 85 ? "danger" : temp > 75 ? "warn" : "" },
    { ic: "power", label: "Power W", v: pow, unit: "W", hist: sparkSVG(g[0]?.powerHist || []), sub: `上限 ${num(powLim, 0)} W` },
    { ic: "cpu", label: "CPU %", v: snap.cpu, unit: "%", hist: sparkSVG(snap.cpuHist), sub: `${(snap.ramTotal && snap.ramAvail != null) ? "内存 " + num(bytes2gb(snap.ramTotal - snap.ramAvail), 1) + " / " + num(bytes2gb(snap.ramTotal), 1) + " GB" : ""}` },
  ];
  let prog = fresh ? `<div class="m-sub">推理中 running ${lastTh.running} · batch ${num(lastTh.batch, 2)} · host ${num(lastTh.host, 1)}%（5s 心跳）</div>` : "";

  let tbl = "";
  if (reqs.length) {
    const shown = showAllReqs ? reqs : reqs.slice(-8).reverse();
    tbl = `<div class="detail"><div class="detail-title">Recent requests（窗口内 ${reqs.length}${reqs.length > 8 ? (showAllReqs ? " · 全部" : " · 显示 8") : ""}
      ${reqs.length > 8 ? `<button class="card-toggle" id="req-more" style="width:auto;padding:0 6px;color:var(--accent);font-size:11px">${showAllReqs ? "收起" : "Show all"}</button>` : ""}</div>
      <div class="tbl-scroll"><table class="tbl"><thead><tr>
        <th>时间</th><th class="num">时长s</th><th>结束</th><th class="num">Prompt</th><th class="num col-opt">Cached</th><th class="num">Output</th><th class="num">t/s</th><th class="num col-opt">Hit%</th><th class="num col-opt">TTFTms</th>
      </tr></thead><tbody>` +
      shown.map(r => `<tr>
        <td>${hhmmss((r.t || 0) * 1000)}</td>
        <td class="num">${num((r.dur_ms || 0) / 1000, 1)}</td>
        <td>${esc(r.finish || DASH)}</td>
        <td class="num">${r.prompt ?? DASH}</td>
        <td class="num col-opt">${r.cached ?? DASH}</td>
        <td class="num">${r.output ?? DASH}</td>
        <td class="num">${num(r.decode, 1)}</td>
        <td class="num col-opt">${r.hit_pct ?? DASH}</td>
        <td class="num col-opt">${r.ttft_ms ?? DASH}</td>
      </tr>`).join("") + `</tbody></table></div>
      <div class="facts" style="margin-top:6px"><span>窗口内 <b>${reqs.length}</b> 请求</span><span>prompt <b>${reqs.reduce((a, r) => a + (r.prompt || 0), 0).toLocaleString()}</b></span><span>cached <b>${reqs.reduce((a, r) => a + (r.cached || 0), 0).toLocaleString()}</b></span><span>output <b>${reqs.reduce((a, r) => a + (r.output || 0), 0).toLocaleString()}</b></span><span>日志窗口 ≈ ${Math.max(th.length ? Math.round(((th[th.length - 1].t || 0) - (th[0].t || 0)) / 60) : 0, 1)} 分钟</span></div>
    </div>`;
  }

  card.innerHTML = `
    <div class="card-head">
      <span class="card-title">${esc(s.name)} <span style="color:var(--text-3);font-weight:400">:${portOf(s.url)}</span></span>
      ${badges}<span class="head-spacer"></span>
      ${s.lat != null ? `<span class="card-sub">${s.lat}ms</span>` : ""}
    </div>
    ${s.online ? `
      <div class="kid" style="margin-top:6px"><span class="k-name">模型 <b style="color:var(--text)">${esc(model)}</b></span>
        <span class="k-val">引擎 ${esc(engVer)} · ctx ${ctxTotal ? ctxTotal.toLocaleString() : DASH}</span></div>
      ${prog}
      ${ctxTotal ? `<div class="gauge-wrap">
        ${gaugeSVG(ctxTotal ? ctxCur / ctxTotal * 100 : 0, "Context", `${ctxCur.toLocaleString()} / ${ctxTotal.toLocaleString()}`)}
        <div style="flex:1;min-width:0">
          <div class="m-sub">KV 分层驻留 Host（详见 ${esc(s.note || "KVMem")}）</div>
          ${lastReq ? `<div class="m-sub">上请求 prompt ${lastReq.prompt ?? DASH} · output ${lastReq.output ?? DASH} · hit ${lastReq.hit_pct ?? DASH}%</div>` : ""}
        </div>
      </div>` : ""}
      <div class="mgrid">${metrics.map(mt => `
        <div class="metric"><div class="m-label">${IC[mt.ic]}${mt.label}</div>
        <div class="m-value ${mt.dual ? "dual" : ""} ${mt.tone ? "tone-" + mt.tone : ""}">${mt.dual
          ? `${num(mt.v1, 1)}<small>+</small><span class="alt">${num(mt.v2, 0)}</span><small>t/s</small></div>`
          : `${mt.v == null ? DASH : (mt.vFmt != null ? Number(mt.v).toFixed(mt.vFmt) : Math.round(mt.v))}${mt.unit ? `<small>${mt.unit}</small>` : ""}</div>`}
        ${mt.sub ? `<div class="m-sub">${esc(mt.sub)}</div>` : ""}${mt.hist}</div>`).join("")}
      </div>
      ${tbl}` : `
      <div class="m-sub" style="margin-top:6px">离线${s.lastOk ? ` · 最后在线 ${hhmmss(s.lastOk)}` : ""}</div>`}`;

  const more = card.querySelector("#req-more");
  if (more) more.addEventListener("click", (e) => { e.stopPropagation(); showAllReqs = !showAllReqs; render(); });
  return card;
}

function strataCard(s) {
  const card = el("div", "card strata-card open");
  const m = s.data?.metrics, h = m?.history, live = m?.live, eng = m?.engine, hw = m?.hardware, cc = m?.conversation_cache;
  const states = ["idle", "reading", "generating", "queued", "error"];
  const badgeCls = { idle: "on", reading: "read", generating: "gen", queued: "queue", error: "err" };
  const state = live?.state || (s.online ? "idle" : null);

  const ctxTotal = eng?.context || eng?.max_context || 0;
  const ctxCur = (live?.state === "reading" || live?.state === "generating") ? (live?.prompt_total ?? live?.prompt_tokens ?? 0)
    : (m?.requests?.[0]?.prompt_tokens ?? 0);
  const ctxPct = ctxTotal ? ctxCur / ctxTotal * 100 : 0;

  const progPct = (live?.prompt_total && live?.prompt_read != null && live.state === "reading") ? live.prompt_read / live.prompt_total * 100 : null;

  const reqs0 = m?.requests?.[0];
  trackPrefill(s.id, reqs0);
  const pfNow = prefillNow(live, reqs0);
  const reusePct = (reqs0?.prompt_tokens && reqs0.reused != null) ? reqs0.reused / reqs0.prompt_tokens * 100 : null;
  const pfNote = reusePct != null && reusePct > 0.5 ? `（${reusePct.toFixed(1)}% 走缓存，仅算实算词元）` : "（实算词元）";

  const metrics = h ? [
    { ic: "speed", label: "Speed t/s", dual: true, v1: live?.tok_s, v2: pfNow, hist: sparkDualSVG(h.tok_s, engPrefill[s.id] || []), sub: `decode · prefill${pfNote}` },
    { ic: "gpu", label: "GPU load %", v: hw?.gpu_util, hist: sparkSVG(h.gpu_util), sub: `${hw?.gpus?.length ?? DASH} 卡合计均值` },
    { ic: "vram", label: "VRAM GB", v: bytes2gb(hw?.gpu_mem_used), vFmt: 1, unit: "GB", hist: sparkSVG(h.gpu_mem_used), sub: `共 ${num(bytes2gb(hw?.gpu_mem_total), 1)} GB`, tone: hw?.gpu_mem_total && hw.gpu_mem_used / hw.gpu_mem_total > .9 ? "danger" : "" },
    { ic: "temp", label: "GPU temp °C", v: hw?.gpu_temp, unit: "°C", hist: sparkSVG(h.gpu_temp), tone: hw?.gpu_temp > 85 ? "danger" : hw?.gpu_temp > 75 ? "warn" : "" },
    { ic: "power", label: "Power W", v: hw?.gpu_power, unit: "W", hist: sparkSVG(h.gpu_power), sub: `上限 ${num(hw?.gpu_power_limit, 0)} W` },
    { ic: "pcie", label: "PCIe MB/s", v: hw?.gpu_pcie_rx_mb, vFmt: 2, unit: "MB/s", hist: sparkSVG(h.gpu_pcie_rx_mb), tone: "info", sub: `gen ${hw?.gpu_pcie_gen ?? DASH} ×${hw?.gpu_pcie_width ?? DASH}` },
    { ic: "cpu", label: "CPU %", v: snap.cpu, unit: "%", hist: sparkSVG(snap.cpuHist), sub: `${(snap.ramTotal && snap.ramAvail != null) ? "内存 " + num(bytes2gb(snap.ramTotal - snap.ramAvail), 1) + " / " + num(bytes2gb(snap.ramTotal), 1) + " GB" : ""}` },
    { ic: "disk", label: "Disk read MB/s", v: hw?.disk_read_mb, vFmt: 2, unit: "MB/s", hist: sparkSVG(h.disk_read_mb), tone: "info" },
  ] : [];

  let badges = states.map(st => `<span class="badge ${st === state ? badgeCls[st] : "off"}">${st}</span>`).join("");
  let prog = progPct != null ? `<div class="p-row"><span class="p-label">读取</span>${progressBar(progPct, "info")}<span class="p-val">${num(live.prompt_read)} / ${num(live.prompt_total)} 词元</span></div>` : "";
  if (live?.queued > 0) prog += `<div class="m-sub">排队 ${live.queued} · 阶段 ${esc(live.phase || DASH)} · 已耗时 ${num(live.elapsed_s, 1)}s</div>`;

  const reqs = m?.requests || [];
  const tot = m?.totals;
  const shown = showAllReqs ? reqs : reqs.slice(0, 8);
  let tbl = "";
  if (reqs.length) {
    tbl = `<div class="detail"><div class="detail-title">Recent requests（${reqs.length}${reqs.length > 8 ? (showAllReqs ? " · 全部" : " · 显示 8") : ""}
      ${reqs.length > 8 ? `<button class="card-toggle" id="req-more" style="width:auto;padding:0 6px;color:var(--accent);font-size:11px">${showAllReqs ? "收起" : "Show all"}</button>` : ""}</div>
      <div class="tbl-scroll"><table class="tbl"><thead><tr>
        <th>时间</th><th class="num">时长s</th><th>结束</th><th class="num">Prompt</th><th class="num col-opt">Reused</th><th class="num">Output</th><th class="num">t/s</th><th class="num col-opt">Hit%</th><th class="num col-opt">PCIe%</th>
      </tr></thead><tbody>` +
      shown.map(r => `<tr>
        <td>${hhmmss((r.time || 0) * 1000)}</td>
        <td class="num">${num(r.duration_s, 1)}</td>
        <td>${esc(r.finish || DASH)}</td>
        <td class="num">${r.prompt_tokens ?? DASH}</td>
        <td class="num col-opt">${r.reused ?? DASH}</td>
        <td class="num">${r.output_tokens ?? DASH}</td>
        <td class="num">${num(r.decode_tok_s, 1)}</td>
        <td class="num col-opt">${r.hit_rate != null ? num(r.hit_rate * 100, 0) : DASH}</td>
        <td class="num col-opt">${r.pcie_share != null ? num(r.pcie_share * 100, 0) : DASH}</td>
      </tr>`).join("") + `</tbody></table></div>
      ${tot ? `<div class="facts" style="margin-top:6px"><span>累计 <b>${tot.requests ?? DASH}</b> 请求</span><span>prompt <b>${(tot.prompt_tokens ?? 0).toLocaleString()}</b></span><span>reused <b>${(tot.reused ?? 0).toLocaleString()}</b></span><span>output <b>${(tot.output_tokens ?? 0).toLocaleString()}</b></span><span>draft 接受 <b>${tot.drafts_accepted ?? DASH}/${tot.drafts_offered ?? DASH}</b></span></div>` : ""}
    </div>`;
  }

  // 对话缓存卡（L3）
  let ccHtml = "";
  if (cc && cc.enabled) {
    const engCc = eng || {};
    const parkedPct = cc.slots ? cc.parked / cc.slots * 100 : 0;
    const memPct = engCc.conversation_cache_mib ? bytes2gb(cc.bytes) / engCc.conversation_cache_mib * 100 : 0;
    ccHtml = `<div class="detail"><div class="detail-title">对话缓存（${engCc.conversation_cache_mib ?? DASH} MiB · ${engCc.conversation_cache_slots ?? DASH} 槽 · ≥${engCc.conversation_cache_min_tokens ?? DASH} 词元）</div>
      <div class="p-row"><span class="p-label">Parked</span>${progressBar(parkedPct)}<span class="p-val">${cc.parked ?? 0} / ${cc.slots ?? DASH}</span></div>
      <div class="p-row"><span class="p-label">Their mem</span>${progressBar(memPct, "info")}<span class="p-val">${num(bytes2gb(cc.bytes), 1)} / ${num(engCc.conversation_cache_mib, 0)} MiB</span></div>
      <div class="facts" style="margin-top:6px"><span>parks <b>${cc.parks ?? DASH}</b></span><span>restores <b>${cc.restores ?? DASH}</b></span><span>evictions <b>${cc.evictions ?? DASH}</b></span><span>requests <b>${cc.requests ?? DASH}</b></span><span>复用 <b>${cc.requests_reused ?? DASH}</b></span><span>reused tokens <b>${(cc.reused_tokens ?? 0).toLocaleString()}</b></span></div>
    </div>`;
  }

  card.innerHTML = `
    <div class="card-head">
      <span class="card-title">${esc(s.name)} <span style="color:var(--text-3);font-weight:400">:${portOf(s.url)}</span></span>
      ${badges}<span class="head-spacer"></span>
      ${s.lat != null ? `<span class="card-sub">${s.lat}ms</span>` : ""}
    </div>
    ${s.online ? `
      <div class="kid" style="margin-top:6px"><span class="k-name">模型 <b style="color:var(--text)">${esc(eng?.model || s.data?.health?.model || DASH)}</b></span>
        <span class="k-val">引擎 ${esc(eng?.version || DASH)} · ctx ${eng?.context ? eng.context.toLocaleString() : DASH} · KV ${esc(eng?.kv || DASH)}</span></div>
      ${prog}
      <div class="gauge-wrap">
        ${gaugeSVG(ctxPct, "Context", `${(ctxCur || 0).toLocaleString()} / ${ctxTotal ? ctxTotal.toLocaleString() : DASH}`)}
        <div style="flex:1;min-width:0">
          <div class="m-sub">专家槽 ${eng?.expert_slots ? eng.expert_slots.toLocaleString() : DASH} · 缓存 ${eng?.expert_cache_mib ? (eng.expert_cache_mib / 1024).toFixed(1) : DASH} GiB · 空闲显存 ${eng?.vram_free_mib ? (eng.vram_free_mib / 1024).toFixed(1) : DASH} GiB</div>
          <div class="m-sub">spec ${eng?.spec ?? DASH} · mtp ${eng?.mtp_max ?? DASH} · lookup ${eng?.lookup ?? DASH} · pcie ${esc(eng?.pcie_frac ?? DASH)}</div>
          ${live?.generated != null && live?.max_tokens ? `<div class="m-sub">已生成 ${live.generated} / ${live.max_tokens} 词元 · ${num(live.tok_s, 1)} t/s</div>` : ""}
        </div>
      </div>
      <div class="mgrid">${metrics.map(mt => `
        <div class="metric"><div class="m-label">${IC[mt.ic]}${mt.label}</div>
        <div class="m-value ${mt.dual ? "dual" : ""} ${mt.tone ? "tone-" + mt.tone : ""}">${mt.dual
          ? `${num(mt.v1, 1)}<small>+</small><span class="alt">${num(mt.v2, 0)}</span><small>t/s</small></div>`
          : `${mt.v == null ? DASH : (mt.vFmt != null ? Number(mt.v).toFixed(mt.vFmt) : Math.round(mt.v))}${mt.unit ? `<small>${mt.unit}</small>` : ""}</div>`}
        ${mt.sub ? `<div class="m-sub">${esc(mt.sub)}</div>` : ""}${mt.hist}</div>`).join("")}
      </div>
      ${ccHtml}${tbl}` : `
      <div class="m-sub" style="margin-top:6px">离线${s.lastOk ? ` · 最后在线 ${hhmmss(s.lastOk)}` : ""}</div>`}`;

  const more = card.querySelector("#req-more");
  if (more) more.addEventListener("click", (e) => { e.stopPropagation(); showAllReqs = !showAllReqs; render(); });
  return card;
}

/* ---------------- L2 其他服务卡 ---------------- */
function portOf(url) { try { return new URL(url).port || "80"; } catch { return DASH; } }
function svcById(id) { return snap?.services.find(s => s.id === id); }

function llamaBody(s) {
  const p = s.data?.props, slots = s.data?.slots || [];
  let h = `<div class="kid"><span class="k-name">量化</span><span class="k-val">${esc(p?.model_ftype || DASH)}</span></div>`;
  if (p?.model_path) h += `<div class="kid"><span class="k-name" title="${esc(p.model_path)}">GGUF ${esc(p.model_path.split("\\").pop())}</span><span class="k-val">${esc(p.build_info || DASH)}</span></div>`;
  h += `<div class="kid"><span class="k-name">槽位</span><span class="k-val">${p?.total_slots ?? DASH} · n_ctx ${slots[0]?.n_ctx ?? p?.n_ctx ?? DASH}</span></div>`;
  for (const sl of slots) {
    const st = sl.is_processing ? `<span class="badge gen">处理中</span>` : `<span class="badge on">空闲</span>`;
    h += `<div class="kid"><span class="k-name">槽位 ${sl.id} · ${sl.n_ctx ?? DASH} ctx ${st}</span>
      <span class="k-val">已处理 ${sl.n_prompt_tokens_processed ?? DASH} · 缓存 ${sl.n_prompt_tokens_cache ?? DASH}</span></div>`;
  }
  return h;
}

function ollamaBody(s) {
  const ps = s.data?.ps?.models || [], tags = s.data?.tags?.models || [];
  let h = `<div class="kid"><span class="k-name">版本</span><span class="k-val">${esc(s.data?.version?.version || DASH)}</span>
    <span class="k-name">已装模型</span><span class="k-val">${tags.length}</span></div>`;
  if (!ps.length) h += `<div class="kid dim"><span class="k-name">无模型驻留（懒加载）</span></div>`;
  for (const m of ps) {
    let left = DASH;
    if (m.expires_at) { const sRem = Math.max(0, Math.round((new Date(m.expires_at) - Date.now()) / 1000)); left = sRem > 0 ? `${Math.floor(sRem / 60)}m${sRem % 60}s` : "即将卸载"; }
    h += `<div class="kid"><span class="k-name" title="${esc(m.name)}">${esc(m.name)}</span>
      <span class="k-val">驻留 ${num(bytes2gb(m.size_vram), 1)} GB · 剩 ${left}</span></div>`;
  }
  return h;
}

function ttsBody(s) {
  const d = s.data?.health || {};
  let unload = DASH;
  if (d.idle_unload_sec != null && d.last_load_sec) {
    const left = d.idle_unload_sec - (Date.now() / 1000 - d.last_load_sec);
    unload = left > 0 ? `${Math.floor(left / 60)} 分后卸载` : "即将卸载";
  } else if (d.idle_unload_sec != null && !d.last_load_sec) unload = `模型未加载（${d.idle_unload_sec}s 空闲阈值）`;
  return `<div class="kid"><span class="k-name">声线</span><span class="k-val">${esc(d.voice || DASH)}</span>
      <span class="k-name">档位</span><span class="k-val">${esc(d.active_model || DASH)}（可切 ${(d.available_models || []).join("/")}）</span></div>
    <div class="kid"><span class="k-name">TTS</span><span class="k-val">${esc(d.state || DASH)}</span>
      <span class="k-name">STT</span><span class="k-val">${esc(d.stt_state || DASH)}</span></div>
    <div class="kid"><span class="k-name">空闲自动卸载</span><span class="k-val">${unload}</span></div>`;
}

function comfyBody(s) {
  const d = s.data?.health || {};
  let h = `<div class="kid"><span class="k-name">ComfyUI</span><span class="k-val">${esc(d.comfyui_version || DASH)}</span>
    <span class="k-name">Python</span><span class="k-val">${esc(d.python_version || DASH)}</span></div>`;
  for (const dv of d.devices || []) {
    h += `<div class="kid"><span class="k-name">${esc(dv.name || "GPU")}</span>
      <span class="k-val">显存 ${num(bytes2gb((dv.vram_total || 0) - (dv.vram_free || 0)), 1)} / ${num(bytes2gb(dv.vram_total), 1)} GB</span></div>`;
  }
  return h;
}

function genericBody(s) {
  return `<div class="kid"><span class="k-name">HTTP 探活</span><span class="k-val">${s.online ? "200 OK" : "离线"}</span></div>`;
}

function serviceCard(s) {
  if (ENGINE_KINDS.has(s.kind)) return engineMainCard(s);
  const card = el("div", "card svc-card" + (expanded.has(s.id) ? " open" : "") + (s.online ? "" : " offline"));
  const alias = s.data?.props?.model_alias || (s.kind === "tts" ? s.data?.health?.voice : null) ||
    (s.kind === "ollama" ? `${(s.data?.tags?.models || []).length} 模型` : null) ||
    (s.kind === "comfyui" ? s.data?.health?.comfyui_version : null) || s.note || "";
  const sleeping = s.data?.props?.is_sleeping ? `<span class="badge queue">休眠</span>` : "";
  const bodyFn = { llama: llamaBody, ollama: ollamaBody, tts: ttsBody, comfyui: comfyBody }[s.kind] || genericBody;
  card.innerHTML = `
    <div class="card-head">
      <span class="card-title">${esc(s.name)} <span style="color:var(--text-3);font-weight:400">:${portOf(s.url)}</span></span>
      <span class="badge ${s.online ? "on" : "off"}">${s.online ? "在线" : "离线"}</span>${sleeping}
      <span class="head-spacer"></span>
      ${s.discovered ? `<button class="card-toggle" data-act="promote:${esc(s.id)}" title="固化到 services.json" style="color:var(--accent)"><svg viewBox="0 0 16 16"><path d="M8 2v8M5 7l3 3 3-3M4 13h8"/></svg></button>` : ""}
      ${s.lat != null ? `<span class="card-sub">${s.lat}ms</span>` : (s.lastOk && !s.online ? `<span class="card-sub">最后在线 ${hhmmss(s.lastOk)}</span>` : "")}
      <button class="card-toggle ${expanded.has(s.id) ? "open" : ""}" data-fold="1">${IC.chev}</button>
    </div>
    <div class="summary-line">${esc(alias) || DASH}</div>
    <div class="fold kids" style="margin-top:8px">${s.online ? bodyFn(s) : `<div class="kid dim"><span class="k-name">连接被拒 · 服务停止</span></div>`}</div>`;
  card.querySelector("[data-fold]")?.addEventListener("click", (e) => {
    e.stopPropagation();
    if (expanded.has(s.id)) expanded.delete(s.id); else expanded.add(s.id);
    render();
  });
  card.querySelector("[data-act]")?.addEventListener("click", (e) => {
    e.stopPropagation();
    send("act:" + e.currentTarget.dataset.act);
  });
  return card;
}

/* ---------------- 拓扑图（可选） ---------------- */
function topologyCard() {
  const card = el("div", "card topo-card");
  card.innerHTML = `<div class="card-head"><span class="card-title">服务 ↔ GPU 显存拓扑</span></div>`;
  const procs = (snap.gpuProcs || []).filter(p => p.total > 0);
  const svcs = snap.services.filter(s => s.online);
  if (!procs.length) {
    card.insertAdjacentHTML("beforeend", `<div class="empty">${IC.empty}<div>暂无 GPU 占用</div></div>`);
    return card;
  }
  const W = Math.max(320, card.clientWidth || 420), H = 210;
  const maxMib = Math.max(...procs.map(p => p.total));
  const leftNodes = [], rightNodes = [];
  const svcMap = new Map();
  for (const p of procs) {
    const key = p.service || ("proc:" + p.pid);
    if (!svcMap.has(key)) svcMap.set(key, { label: p.service ? (svcById(p.service)?.name || p.service) : p.name, off: false, procs: [] });
    svcMap.get(key).procs.push(p);
  }
  const svcArr = [...svcMap.values()];
  const gpuArr = snap.gpus.map(g => ({ g }));
  const lh = 26, gap = Math.min(14, (H - 20 - svcArr.length * lh) / Math.max(1, svcArr.length - 1));
  const y0 = (H - (svcArr.length * lh + (svcArr.length - 1) * gap)) / 2;
  svcArr.forEach((s, i) => { s.x = 8; s.y = y0 + i * (lh + gap); s.w = Math.min(150, W * 0.36); s.h = lh; });
  gpuArr.forEach((n, i) => { n.x = W - 158; n.y = H / 2 - (gpuArr.length * 44) / 2 + i * 52; n.w = 150; n.h = 44; });

  let edges = "", nodes = "";
  const edgeList = [];
  for (const s of svcArr) for (const p of s.procs) for (const [gk, v] of Object.entries(p.per)) {
    if (v <= 0) continue;
    const gi = +gk.slice(1); const gn = gpuArr.find(n => n.g.index === gi); if (!gn) continue;
    const wpx = Math.max(1, Math.min(6, v / maxMib * 6));
    const x1 = s.x + s.w, y1 = s.y + s.h / 2, x2 = gn.x, y2 = gn.y + gn.h / 2;
    const mx = (x1 + x2) / 2;
    edgeList.push({ key: s.label + "|" + gi, d: `M${x1},${y1} C${mx},${y1} ${mx},${y2} ${x2},${y2}`, w: wpx, tip: `PID ${p.pid} · ${p.name} · ${Math.round(v)} MiB` });
  }
  edges = edgeList.map((e, i) => `<path class="edge" data-i="${i}" d="${e.d}" stroke-width="${e.w.toFixed(1)}"/>`).join("");
  nodes = svcArr.map((s, i) => `<g class="node" data-svc="${i}"><rect x="${s.x}" y="${s.y}" width="${s.w}" height="${s.h}" rx="9"/>
      <text x="${s.x + 8}" y="${s.y + 17}" >${esc(trunc(s.label, 20))}</text></g>`).join("") +
    gpuArr.map((n, i) => {
      const pct = n.g.memTotal ? n.g.memUsed / n.g.memTotal : 0;
      return `<g class="node"><rect x="${n.x}" y="${n.y}" width="${n.w}" height="${n.h}" rx="9"/>
      <text x="${n.x + 8}" y="${n.y + 16}">GPU ${n.g.index}</text>
      <rect x="${n.x + 8}" y="${n.y + 24}" width="${n.w - 16}" height="8" rx="4" fill="var(--track)"/>
      <rect class="fillbar" x="${n.x + 8}" y="${n.y + 24}" width="${(n.w - 16) * pct}" height="8" rx="4"/>
      <text x="${n.x + 8}" y="${n.y + 41}" style="font-size:9px;fill:var(--text-2)">${num(mib2gb(n.g.memUsed), 1)} / ${num(mib2gb(n.g.memTotal), 1)} GB</text></g>`;
    }).join("");
  card.insertAdjacentHTML("beforeend", `<svg class="topo" viewBox="0 0 ${W} ${H}" preserveAspectRatio="xMidYMid meet">${edges}${nodes}</svg>`);
  const svg = card.querySelector(".topo");
  const tip = el("div", "topo-tip hidden");
  card.append(tip);
  svg.addEventListener("mousemove", (e) => {
    const t = e.target;
    if (t.classList.contains("edge")) {
      tip.classList.remove("hidden");
      tip.textContent = edgeList[+t.dataset.i].tip;
      tip.style.left = (e.clientX + 10) + "px"; tip.style.top = (e.clientY + 10) + "px";
      svg.querySelectorAll(".edge").forEach(x => x.classList.add("dim"));
      t.classList.remove("dim"); t.classList.add("hot");
    } else if (t.closest(".node")) {
      const gi = [...svg.querySelectorAll(".node")].indexOf(t.closest(".node"));
      const hotSvc = gi < svcArr.length ? svcArr[gi].label : null;
      svg.querySelectorAll(".edge").forEach((x, i) => {
        const isHot = hotSvc ? edgeList[i].key.startsWith(hotSvc + "|") : edgeList[i].key.endsWith("|" + gpuArr[gi - svcArr.length]?.g?.index);
        x.classList.toggle("dim", !isHot); x.classList.toggle("hot", !!isHot);
      });
      tip.classList.add("hidden");
    } else {
      tip.classList.add("hidden");
      svg.querySelectorAll(".edge").forEach(x => { x.classList.remove("dim"); x.classList.remove("hot"); });
    }
  });
  svg.addEventListener("mouseleave", () => { tip.classList.add("hidden"); svg.querySelectorAll(".edge").forEach(x => x.classList.remove("dim", "hot")); });
  return card;
}

/* ---------------- 主渲染 ---------------- */
function render() {
  if (!snap) return;
  if (firstSnap) { $("#gpu-zone").innerHTML = ""; $("#svc-zone").innerHTML = ""; firstSnap = false; }

  document.documentElement.dataset.theme = snap.theme || "light";
  const a = snap.opacity ?? 0.72;
  document.documentElement.style.setProperty("--card-bg",
    (snap.theme === "dark" ? `rgba(16,18,24,${a})` : `rgba(250,250,252,${a})`));

  // L0
  const sum = snap.summary || { online: 0, total: 0 };
  const pill = $("#pill");
  pill.textContent = `${sum.online}/${sum.total} 在线`;
  pill.className = "pill" + (sum.online === 0 ? " err" : sum.online < sum.total ? " warn" : "");
  $("#refresh").textContent = (snap.paused ? "已暂停" : hhmmss(snap.t));
  const pin = $("#btn-pin");
  pin.classList.toggle("pinned", !!snap.topmost);
  pin.title = snap.topmost ? "取消置顶" : "置于顶层";
  pin.setAttribute("aria-label", pin.title);
  pin.innerHTML = `<svg viewBox="0 0 16 16"><path d="M9.5 2l4.5 4.5-2 2-.6-.6-3.3 3.3.4 2.3-1.4 1.4-3-3-3.6 3.6-.7-.7 3.6-3.6-3-3 1.4-1.4 2.3.4 3.3-3.3-.6-.6z"/></svg>`;

  const ghost = $("#btn-ghost");
  if (ghost) {
    ghost.classList.toggle("pinned", !!snap.clickThrough);
    ghost.classList.toggle("dimdock", !!snap.autoHide);   // 与贴边互斥：贴边开着时变暗
    ghost.title = snap.clickThrough ? "取消鼠标穿透" : "鼠标穿透（开启后只能从托盘图标关闭）";
    ghost.setAttribute("aria-label", ghost.title);
  }
  const dock = $("#btn-dock");
  if (dock) {
    dock.classList.toggle("pinned", !!snap.autoHide);
    dock.classList.toggle("dimdock", !!snap.clickThrough);
    dock.title = snap.autoHide ? "关闭贴边自动隐藏" : "贴边自动隐藏（拖到屏幕边缘收起，悬停触发线展开）";
    dock.setAttribute("aria-label", dock.title);
  }

  const gz = $("#gpu-zone"), sz = $("#svc-zone");
  gz.innerHTML = ""; sz.innerHTML = "";

  // L1
  if (snap.smiDown || !snap.smiOn) {
    const c = el("div", "card");
    c.innerHTML = `<div class="card-head"><span class="card-title">GPU</span></div><div class="m-sub">${snap.smiOn ? "GPU 数据不可用（nvidia-smi 连续失败，30s 后重试）" : "nvidia-smi 采集已关闭"}</div>`;
    gz.append(c);
  } else {
    const procs = snap.gpuProcs || [];
    for (const g of snap.gpus) gz.append(gpuCard(g, procs));
    if (!snap.gpus.length) { const c = el("div", "card"); c.innerHTML = `<div class="m-sub">未检测到 GPU</div>`; gz.append(c); }
  }
  if (snap.showTopology) gz.append(topologyCard());

  // L2：按 order 排序
  const order = snap.order || [];
  const svcs = [...snap.services];
  svcs.sort((a, b) => {
    const ea = ENGINE_KINDS.has(a.kind), eb = ENGINE_KINDS.has(b.kind);
    if (ea !== eb) return ea ? -1 : 1;                       // 公共主引擎卡恒占区域3首位
    if (ea && eb) {                                          // 在线引擎排前（Strata 离线不占位）
      if (a.online !== b.online) return a.online ? -1 : 1;
      const ke = order.indexOf(a.id), kf = order.indexOf(b.id);
      if (ke === kf) return 0;
      if (ke === -1) return 1; if (kf === -1) return -1; return ke - kf;
    }
    const ka = order.indexOf(a.id), kb = order.indexOf(b.id);
    if (ka === -1 && kb === -1) return 0;
    if (ka === -1) return 1; if (kb === -1) return -1; return ka - kb;
  });
  if (!svcs.some(s => s.online)) {
    const c = el("div", "card");
    c.innerHTML = `<div class="empty">${IC.empty}<div>本机暂无模型服务在运行</div></div>`;
    sz.append(c);
  } else for (const s of svcs) if (s.enabled !== false) sz.append(serviceCard(s));

  if (!scrollRestored) {
    scrollRestored = true;
    const sc = parseFloat(localStorage.getItem("scroll") || "0");
    if (sc > 0) $("#content").scrollTop = sc;
  }
}

/* ---------------- 右键菜单（与托盘同一数据源；子菜单层叠弹出防过长） ---------------- */
const flyouts = [];
let closeTimer = null;
let lastOpenPath = null;

function destroyFlyouts() {
  clearTimeout(closeTimer);
  for (const f of flyouts.splice(0)) f.remove();
}
function openFly(row, fly) {
  clearTimeout(closeTimer);
  // 关掉不是自己祖先/后代的其他浮层（同级与更深层）
  for (const f of flyouts)
    if (f !== fly && !fly.contains(f) && !f.contains(fly)) f.classList.add("hidden");
  fly.classList.remove("hidden");
  const r = row.getBoundingClientRect();
  const w = fly.offsetWidth, h = fly.offsetHeight;
  let x = r.right + 2;
  if (x + w > innerWidth - 4) x = Math.max(4, r.left - w - 2);   // 放不下就翻左边
  let y = r.top;
  if (y + h > innerHeight - 4) y = Math.max(4, innerHeight - h - 4);
  fly.style.left = x + "px"; fly.style.top = y + "px";
  lastOpenPath = row.dataset.id || null;
}
function scheduleClose(fly) {
  clearTimeout(closeTimer);
  closeTimer = setTimeout(() => fly.classList.add("hidden"), 220);
}
function renderMenu(tree) {
  const box = $("#ctxmenu");
  destroyFlyouts();
  box.innerHTML = "";
  const build = (items, container) => {
    for (const it of items) {
      if (it.type === "header") {
        if (container.children.length) container.append(el("div", "ctx-sep"));
        container.append(el("div", "ctx-h", esc(it.label)));
        continue;
      }
      const row = el("div", "ctx-i" + (it.checked ? " checked" : ""));
      row.dataset.id = it.id;
      row.innerHTML = `<span class="ck"><svg viewBox="0 0 16 16" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2"><path d="M3 8.5l3.5 3.5L13 5"/></svg></span>
        <span class="lab" ${it.tooltip ? `title="${esc(it.tooltip)}"` : ""}>${esc(it.label)}</span>
        ${it.type === "sub" ? `<span class="arr"><svg viewBox="0 0 16 16"><path d="M6 4l4 4-4 4"/></svg></span>` : ""}`;
      container.append(row);
      let fly = null;
      if (it.type === "sub" && it.children) {
        fly = el("div", "ctx-fly hidden");
        build(it.children, fly);
        document.body.append(fly);
        flyouts.push(fly);
        row.addEventListener("mouseenter", () => openFly(row, fly));
        row.addEventListener("mouseleave", () => scheduleClose(fly));
        fly.addEventListener("mouseenter", () => clearTimeout(closeTimer));
        fly.addEventListener("mouseleave", () => scheduleClose(fly));
      }
      row.addEventListener("click", (e) => {
        e.stopPropagation();
        if (it.type === "sub") { if (fly) openFly(row, fly); return; }
        if (it.id === "act:reset") { showConfirm(); closeMenu(); return; }
        send("act:" + it.id);
        if (it.id.startsWith("act:interval:") && (it.id.endsWith(":+") || it.id.endsWith(":-"))) {
          lastOpenPath = "sub:interval";
          send("menu:query");          // 连点保持打开并刷新数值（重渲染后按路径重开）
        } else closeMenu();
      });
    }
  };
  build(tree, box);
  // 重渲染后按记录路径重开浮层（interval ± 连点场景）
  if (lastOpenPath) {
    const row = box.querySelector(`.ctx-i[data-id="${CSS.escape(lastOpenPath)}"]`);
    if (row) row.dispatchEvent(new Event("mouseenter"));
  }
}
function openMenu(x, y) {
  const box = $("#ctxmenu");
  renderMenu(menuTree || []);
  box.classList.remove("hidden");
  const r = box.getBoundingClientRect();
  box.style.left = Math.min(x, innerWidth - r.width - 8) + "px";
  box.style.top = Math.min(y, innerHeight - r.height - 8) + "px";
  send("menu:query");
}
function closeMenu() { destroyFlyouts(); lastOpenPath = null; $("#ctxmenu").classList.add("hidden"); }

function showConfirm() {
  const c = $("#confirm");
  c.innerHTML = `<span>恢复默认设置？</span><button class="yes" id="cf-y">确定</button><button id="cf-n">取消</button>`;
  c.classList.remove("hidden");
  $("#cf-y").onclick = () => { c.classList.add("hidden"); send("act:act:reset".slice(4)); };
  $("#cf-n").onclick = () => c.classList.add("hidden");
}

/* ---------------- 事件 ---------------- */
function bindEvents() {
  $("#btn-min").addEventListener("click", () => send("win:min"));
  $("#btn-pin").addEventListener("click", () => send("act:act:p".slice(4)));
  $("#btn-ghost")?.addEventListener("click", () => send("act:clickthrough"));
  $("#btn-dock")?.addEventListener("click", () => send("act:autohide"));

  // 拖动：标题条 + 卡片头部
  const dragStart = (e) => {
    if (e.button !== 0) return;
    if (e.target.closest("button, a, .tbl, .topo, input")) return;
    send("win:drag");
  };
  document.addEventListener("mousedown", (e) => {
    if (e.target.closest("#titlebar, .card-head")) dragStart(e);
    else if (!e.target.closest("#ctxmenu, .about, .confirm")) closeMenu();
  });

  // 右键菜单
  document.addEventListener("contextmenu", (e) => {
    e.preventDefault();
    openMenu(e.clientX, e.clientY);
  });

  // Ctrl+滚轮缩放 / Alt+滚轮透明度
  document.addEventListener("wheel", (e) => {
    if (e.ctrlKey) { e.preventDefault(); send(e.deltaY < 0 ? "act:act:zoom:+".slice(4) : "act:act:zoom:-".slice(4)); }
    else if (e.altKey) { e.preventDefault(); send(e.deltaY < 0 ? "act:act:opacity:+".slice(4) : "act:act:opacity:-".slice(4)); }
  }, { passive: false });

  // 快捷键
  document.addEventListener("keydown", (e) => {
    if (e.key === "Escape") { if (!$("#ctxmenu").classList.contains("hidden")) { closeMenu(); return; } if (!$("#about").classList.contains("hidden")) { $("#about").classList.add("hidden"); return; } send("act:act:esc".slice(4)); return; }
    if (e.ctrlKey && (e.key === "q" || e.key === "Q")) { send("act:act:ctrlq".slice(4)); return; }
    if (e.ctrlKey || e.altKey || e.metaKey) return;
    const map = { t: "t", T: "t", p: "p", P: "p", "+": "plus", "=": "plus", "-": "minus", "_": "minus", " ": "space" };
    if (map[e.key]) { e.preventDefault(); send("act:" + map[e.key]); }
  });

  // 滚动记忆
  let scT = 0;
  $("#content").addEventListener("scroll", () => {
    clearTimeout(scT);
    scT = setTimeout(() => { localStorage.setItem("scroll", String($("#content").scrollTop)); send("scroll:" + $("#content").scrollTop); }, 400);
  });

  // resize 反馈（描边提亮）+ 防抖重排
  let rsT = 0;
  window.addEventListener("resize", () => {
    document.body.classList.add("resizing");
    clearTimeout(rsT);
    rsT = setTimeout(() => { document.body.classList.remove("resizing"); render(); }, 150);
  });
}

/* ---------------- 宿主消息（WebView2 专用事件源） ---------------- */
const onHostMessage = (e) => {
  const d = e.data;
  if (typeof d !== "string") return;
  try {
    if (d.startsWith("menu:")) { menuTree = JSON.parse(d.slice(5)); if (!$("#ctxmenu").classList.contains("hidden")) renderMenu(menuTree); return; }
    if (d.startsWith("about:")) {
      const a = JSON.parse(d.slice(6));
      const box = $("#about");
      box.innerHTML = `<button class="wbtn close" id="ab-x"><svg viewBox="0 0 16 16"><path d="M4 4l8 8M12 4l-8 8"/></svg></button>
        <h3>本机模型监控</h3><p>版本 ${esc(a.ver)} · 编译 ${esc(a.build)}</p><p>${esc(a.exe)}</p><p>数据：${esc(a.data)}</p>`;
      box.classList.remove("hidden");
      box.querySelector("#ab-x").onclick = () => box.classList.add("hidden");
      return;
    }
    if (d === "flash:interval") {
      const f = $("#flash");
      f.textContent = `刷新 ${snap ? snap.interval : ""}s`;
      f.classList.remove("hidden"); f.classList.add("show");
      setTimeout(() => { f.classList.remove("show"); setTimeout(() => f.classList.add("hidden"), 350); }, 1500);
      return;
    }
    if (d.startsWith("{")) { snap = JSON.parse(d); render(); }
  } catch (err) { console.error("msg", err); }
};
if (window.chrome && window.chrome.webview) {
  window.chrome.webview.addEventListener("message", onHostMessage);
} else {
  // 浏览器里调试用（非 WebView2 环境）
  window.addEventListener("message", onHostMessage);
}

/* ---------------- 启动 ---------------- */
renderSkeleton();
bindEvents();
send("menu:query");
