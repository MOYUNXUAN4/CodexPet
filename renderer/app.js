'use strict';

const $ = (id) => document.getElementById(id);

let state = null;
let expanded = false;
let curveOpen = false;
let monthOpen = false;
let heatOpen = false;
let petOpen = true;
let collapsed = false;
let mode = 'codex';

/* ---------- 工具 ---------- */
function fmtRemain(ms) {
  if (ms <= 0) return '即将重置';
  const m = Math.floor(ms / 60000);
  if (m < 60) return `${m}m 后重置`;
  const h = Math.floor(m / 60);
  if (h < 24) return `${h}h${String(m % 60).padStart(2, '0')}m 后重置`;
  const d = Math.floor(h / 24);
  return `${d}d${h % 24}h 后重置`;
}

function fmtClock(iso) {
  if (!iso) return '--';
  const d = new Date(iso);
  if (isNaN(d.getTime())) return '--';
  const p = (n) => String(n).padStart(2, '0');
  return `${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}`;
}

function fmtTimeOnly(iso) {
  if (!iso) return '--';
  const d = new Date(iso);
  if (isNaN(d.getTime())) return '--';
  const p = (n) => String(n).padStart(2, '0');
  return `${p(d.getHours())}:${p(d.getMinutes())}`;
}

function fmtAgo(ts) {
  if (!ts) return '--';
  const m = Math.floor((Date.now() - ts) / 60000);
  if (m < 1) return '刚刚';
  if (m < 60) return `${m} 分钟前`;
  const h = Math.floor(m / 60);
  if (h < 24) return `${h} 小时前`;
  return `${Math.floor(h / 24)} 天前`;
}

function fmtNum(n) {
  if (typeof n !== 'number' || !isFinite(n)) return '--';
  if (n >= 10000) return (n / 1000).toFixed(1) + 'k';
  if (n >= 100) return String(Math.round(n));
  if (n >= 10) return n.toFixed(1);
  return n.toFixed(2);
}

function level(p) {
  if (p >= 50) return '';
  if (p >= 20) return 'warn';
  return 'crit';
}
function colorOf(p) {
  if (p >= 50) return '#3ddc97';
  if (p >= 20) return '#ffb648';
  return '#ff5c72';
}
function esc(s) {
  return String(s == null ? '' : s).replace(/[&<>"']/g, (c) => (
    { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]
  ));
}

/* ---------- 折线图 ---------- */
const sparkState = new Map();          // canvas id -> { pts, opts, geom }

function roundRect(ctx, x, y, w, h, r) {
  ctx.beginPath();
  ctx.moveTo(x + r, y);
  ctx.lineTo(x + w - r, y);
  ctx.quadraticCurveTo(x + w, y, x + w, y + r);
  ctx.lineTo(x + w, y + h - r);
  ctx.quadraticCurveTo(x + w, y + h, x + w - r, y + h);
  ctx.lineTo(x + r, y + h);
  ctx.quadraticCurveTo(x, y + h, x, y + h - r);
  ctx.lineTo(x, y + r);
  ctx.quadraticCurveTo(x, y, x + r, y);
  ctx.closePath();
}

function drawSpark(cv, pts, opts) {
  if (!cv) return;
  const prev = sparkState.get(cv.id);
  // 数据刷新时保留鼠标停留的准星位置，否则每 5 秒重绘一次就被擦掉了
  const hi = prev && prev.hi != null && pts && pts.length ? Math.min(prev.hi, pts.length - 1) : null;
  sparkState.set(cv.id, { pts: pts || [], opts: opts || {}, geom: null, hi });
  paintSpark(cv);
}

// 画布重绘；准星位置来自 meta.hi（鼠标悬停时写入）
function paintSpark(cv) {
  const meta = sparkState.get(cv.id);
  if (!meta) return;
  const pts = meta.pts;
  const opts = meta.opts;
  const hoverIdx = meta.hi;

  const w = cv.clientWidth || 120;
  const h = cv.clientHeight || 34;
  const dpr = window.devicePixelRatio || 1;
  const pw = Math.round(w * dpr);
  const ph = Math.round(h * dpr);
  if (cv.width !== pw || cv.height !== ph) { cv.width = pw; cv.height = ph; }

  const ctx = cv.getContext('2d');
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.clearRect(0, 0, w, h);

  const color = opts.color || '#3ddc97';
  const padT = 3;
  const padB = 2;
  const plotH = h - padT - padB;

  // 基线
  ctx.strokeStyle = 'rgba(255,255,255,0.07)';
  ctx.lineWidth = 1;
  ctx.beginPath();
  ctx.moveTo(0, h - padB + 0.5);
  ctx.lineTo(w, h - padB + 0.5);
  ctx.stroke();

  if (!pts || pts.length < 2) {
    ctx.fillStyle = 'rgba(140,152,170,0.55)';
    ctx.font = '8px "Segoe UI", sans-serif';
    ctx.textAlign = 'center';
    ctx.textBaseline = 'alphabetic';
    ctx.fillText('采样中…', w / 2, h / 2 + 3);
    meta.geom = null;
    return;
  }

  const from = opts.from;
  const to = opts.to;
  const max = Math.max(opts.max || 100, 0.0001);
  const span = Math.max(1, to - from);

  const X = (t) => ((t - from) / span) * w;
  const Y = (v) => padT + plotH - Math.min(1, Math.max(0, v / max)) * plotH;
  meta.geom = { X, Y, w, h, padT, padB, from, to, max };

  // 面积
  const grad = ctx.createLinearGradient(0, padT, 0, h - padB);
  grad.addColorStop(0, color + '44');
  grad.addColorStop(1, color + '00');
  ctx.beginPath();
  ctx.moveTo(X(pts[0][0]), h - padB);
  for (const p of pts) ctx.lineTo(X(p[0]), Y(p[1]));
  ctx.lineTo(X(pts[pts.length - 1][0]), h - padB);
  ctx.closePath();
  ctx.fillStyle = grad;
  ctx.fill();

  // 折线
  ctx.beginPath();
  for (let i = 0; i < pts.length; i++) {
    const x = X(pts[i][0]);
    const y = Y(pts[i][1]);
    if (i === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
  }
  ctx.strokeStyle = color;
  ctx.lineWidth = 1.4;
  ctx.lineJoin = 'round';
  ctx.lineCap = 'round';
  ctx.stroke();

  // 末点
  const last = pts[pts.length - 1];
  ctx.beginPath();
  ctx.arc(X(last[0]), Y(last[1]), 1.9, 0, Math.PI * 2);
  ctx.fillStyle = color;
  ctx.fill();

  // 当前时刻标记
  if (opts.now && opts.now > from && opts.now < to) {
    const nx = X(opts.now);
    ctx.setLineDash([2, 2]);
    ctx.strokeStyle = 'rgba(255,255,255,0.16)';
    ctx.lineWidth = 1;
    ctx.beginPath();
    ctx.moveTo(nx, padT);
    ctx.lineTo(nx, h - padB);
    ctx.stroke();
    ctx.setLineDash([]);
  }

  // 鼠标准星 + 数值气泡
  if (hoverIdx != null && pts[hoverIdx]) {
    const p = pts[hoverIdx];
    const x = Math.min(Math.max(X(p[0]), 0.5), w - 0.5);
    const y = Y(p[1]);

    ctx.strokeStyle = 'rgba(255,255,255,0.3)';
    ctx.lineWidth = 1;
    ctx.beginPath();
    ctx.moveTo(x, padT);
    ctx.lineTo(x, h - padB);
    ctx.stroke();

    const label = opts.hoverLabel ? opts.hoverLabel(p[1]) : String(Math.round(p[1]));
    // 第一行给「时刻」（周曲线是「周三 14:30」），第二行给数值
    const tlabel = opts.timeLabel ? opts.timeLabel(p[0]) : '';

    ctx.font = '600 8.5px "Segoe UI", "Microsoft YaHei", sans-serif';
    const lw = ctx.measureText(label).width;
    ctx.font = '500 7.5px "Segoe UI", "Microsoft YaHei", sans-serif';
    const twTime = tlabel ? ctx.measureText(tlabel).width : 0;

    const tw = Math.min(w, Math.max(lw, twTime) + 9);
    const bh = tlabel ? 21 : 12;
    const bx = Math.min(Math.max(x - tw / 2, 0), Math.max(0, w - tw));
    let by = y - bh - 3;
    if (by < 0) by = Math.min(h - bh, y + 4);

    ctx.fillStyle = 'rgba(10,13,19,0.96)';
    roundRect(ctx, bx, by, tw, bh, 3);
    ctx.fill();
    ctx.strokeStyle = color + '77';
    ctx.lineWidth = 1;
    roundRect(ctx, bx, by, tw, bh, 3);
    ctx.stroke();

    if (tlabel) {
      ctx.font = '500 7.5px "Segoe UI", "Microsoft YaHei", sans-serif';
      ctx.fillStyle = 'rgba(178,190,206,0.95)';
      ctx.textAlign = 'center';
      ctx.textBaseline = 'middle';
      ctx.fillText(tlabel, bx + tw / 2, by + 6.5);
    }
    ctx.font = '600 8.5px "Segoe UI", "Microsoft YaHei", sans-serif';
    ctx.fillStyle = color;
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    ctx.fillText(label, bx + tw / 2, by + bh - (tlabel ? 6.5 : bh / 2) + 0.5);

    ctx.beginPath();
    ctx.arc(x, y, 2.4, 0, Math.PI * 2);
    ctx.fillStyle = color;
    ctx.fill();
    ctx.strokeStyle = 'rgba(8,10,14,0.9)';
    ctx.stroke();
  }
}

/* ---------- 曲线悬停：显示那一刻的数值 ---------- */
const HEAD_OF = { c5: 'c5top', cw: 'cwtop', cmo: 'cmotop', wbH: 'wbHTop', wbD: 'wbDTop' };

function nearestIdx(pts, t) {
  if (!pts || !pts.length) return null;
  if (t <= pts[0][0]) return 0;
  const n = pts.length - 1;
  if (t >= pts[n][0]) return n;
  let lo = 0, hi = n;
  while (lo < hi - 1) {
    const mid = (lo + hi) >> 1;
    if (pts[mid][0] <= t) lo = mid; else hi = mid;
  }
  return (t - pts[lo][0] <= pts[hi][0] - t) ? lo : hi;
}

// 悬停时右侧要写「周四 09:34 · 剩 85%」，左边的周期标签只留主词（本周 / 本月 / 5 小时）
function shortHeadLabel(head, on) {
  const sp = head && head.querySelector('span');
  if (!sp) return;
  if (sp.dataset.full == null) sp.dataset.full = sp.textContent;
  const full = sp.dataset.full;
  sp.textContent = on ? ((full.split('·')[0] || full).trim() || full) : full;
}

// 头部小标题跟着悬停显示「时刻 · 数值」
function applyHoverHeader(cv) {
  const meta = sparkState.get(cv.id);
  const id = HEAD_OF[cv.id];
  if (!meta || !id) return;
  const el = $(id);
  if (!el) return;
  const head = el.parentElement;
  if (meta.hi == null || !meta.pts[meta.hi]) {
    if (head) { head.classList.remove('hoving'); shortHeadLabel(head, false); }
    return;
  }
  const tt = meta.opts.timeLabel ? meta.opts.timeLabel(meta.pts[meta.hi][0]) : '';
  const vv = meta.opts.hoverLabel ? meta.opts.hoverLabel(meta.pts[meta.hi][1]) : String(Math.round(meta.pts[meta.hi][1]));
  el.textContent = tt ? tt + ' · ' + vv : vv;
  if (head) { head.classList.add('hoving'); shortHeadLabel(head, true); }
}

function bindHover(cv) {
  if (!cv || cv.__hover) return;
  cv.__hover = true;

  cv.addEventListener('mousemove', (e) => {
    const meta = sparkState.get(cv.id);
    if (!meta || !meta.geom || meta.pts.length < 2) return;
    const rect = cv.getBoundingClientRect();
    const x = e.clientX - rect.left;
    if (x < -1 || x > rect.width + 1) return;
    const { from, to } = meta.geom;
    const t = from + (x / Math.max(1, rect.width)) * (to - from);
    const i = nearestIdx(meta.pts, t);
    if (i == null) return;
    meta.hi = i;
    paintSpark(cv);
    applyHoverHeader(cv);
  });

  cv.addEventListener('mouseleave', () => {
    const meta = sparkState.get(cv.id);
    if (!meta || meta.hi == null) return;
    meta.hi = null;
    paintSpark(cv);
    renderCurves(state || {});      // 头部文字回到「剩 xx%」
  });
}

/* 点击探针：用于确认鼠标事件确实进到了页面 */
document.addEventListener('pointerdown', (e) => {
  const t = e.target && (e.target.id || e.target.className || e.target.tagName) || '';
  if (window.pet && window.pet.probe) window.pet.probe(String(t));
}, true);

/* ---------- 模型 / 子代理代号 ---------- */
const EFFORT_LABEL = {
  none: '', minimal: '最低', low: '低', medium: '中', high: '高', xhigh: '超高', max: '最高'
};

function modelText(model, effort) {
  const parts = [];
  if (model) {
    const m = String(model)
      .replace(/^openai[-_ ]?/i, '')
      .replace(/^gpt[-_ ]?/i, '')
      .replace(/[-_]/g, ' ')
      .trim();
    if (m) parts.push(m);
  }
  if (effort) {
    const e = EFFORT_LABEL[effort] != null ? EFFORT_LABEL[effort] : effort;
    if (e) parts.push(e);
  }
  return parts.join(' ');
}

function modelLabel(ss) {
  if (!ss) return '—';
  let out = modelText(ss.model, ss.effort);
  if (ss.subagent) out = (out ? out + ' · ' : '') + ss.subagent;
  return out || '—';
}

// 5 小时额度：显示重置的具体时刻（本周仍用剩余时长）
function resetClockText(ts) {
  if (!ts) return '--';
  if (ts <= Date.now()) return '即将重置';
  const d = new Date(ts);
  const now = new Date();
  const p = (n) => String(n).padStart(2, '0');
  const hm = `${p(d.getHours())}:${p(d.getMinutes())}`;
  if (d.toDateString() === now.toDateString()) return hm + ' 重置';
  const tmr = new Date(now.getFullYear(), now.getMonth(), now.getDate() + 1);
  if (d.toDateString() === tmr.toDateString()) return '明日 ' + hm + ' 重置';
  return `${p(d.getMonth() + 1)}-${p(d.getDate())} ${hm} 重置`;
}

function deltaOf(pts) {
  if (!pts || pts.length < 2) return null;
  const a = pts[0][1];
  const b = pts[pts.length - 1][1];
  return Math.round((b - a) * 10) / 10;
}

function fmtDelta(d) {
  if (d == null) return '--';
  if (Math.abs(d) < 0.5) return '±0%';
  return (d > 0 ? '+' : '') + Math.round(d) + '%';
}

/* ---------- Codex ---------- */
// 本次对话实际调用过的 skill（后端已按工具调用行过滤，最多 4 个）
function renderSkills(el, list) {
  if (!el) return;
  const arr = (list || []).filter((x) => x && String(x).length > 1);
  if (!arr.length) { el.innerHTML = ''; el.title = ''; return; }
  const max = 3;
  el.innerHTML = '<span class="sklab">技能</span>'
    + arr.slice(0, max).map((n) => `<span class="skchip" title="${esc(n)}">${esc(n)}</span>`).join('')
    + (arr.length > max ? `<span class="skmore">+${arr.length - max}</span>` : '');
  el.title = '本次对话用到的 skill：' + arr.join(' / ');
}

function renderCodex(s) {
  const u = s.usage;
  if (u) {
    const paint = (vId, bId, rId, w, right) => {
      if (!w) {
        $(vId).innerHTML = '--';
        $(rId).textContent = '--';
        return;
      }
      const p = w.remaining;
      $(vId).innerHTML = `${Math.round(p)}<small>%</small>`;
      $(vId).style.color = colorOf(p);
      const bar = $(bId);
      bar.style.width = p + '%';
      bar.className = level(p);
      $(rId).textContent = right ? right(w) : fmtRemain(w.resetAt ? w.resetAt - Date.now() : 0);
    };
    paint('v5', 'b5', 'r5', u.fiveHour, (w) => resetClockText(w.resetAt));
    paint('vw', 'bw', 'rw', u.weekly, null);
  }

  const ss = s.session;
  if (ss) {
    $('sdot').className = 'sdot' + (ss.running ? ' on' : '');
    $('sstate').textContent = ss.running ? '进行中' : '空闲';
    $('sctx').textContent = ss.ctxPercent == null ? 'ctx --' : `ctx ${ss.ctxPercent}%`;
    const nameEl = $('sname');
    nameEl.textContent = '';
    // 主代理在前、子代理在后，正在跑的那个用高亮色标出来
    const mainEl = document.createElement('span');
    mainEl.className = 'smain' + (ss.running ? ' on' : '');
    mainEl.textContent = modelLabel(ss);
    nameEl.appendChild(mainEl);

    const subTxt = ss.subModel ? modelText(ss.subModel, ss.subEffort) : '';
    if (subTxt) {
      const sepEl = document.createElement('span');
      sepEl.className = 'ssep';
      sepEl.textContent = ' · ';
      const subEl = document.createElement('span');
      subEl.className = 'ssub' + (ss.subRunning ? ' on' : '');
      subEl.textContent = '子 ' + subTxt;
      nameEl.appendChild(sepEl);
      nameEl.appendChild(subEl);
    }
    nameEl.title = [ss.model, ss.effort, ss.cwd,
      ss.subModel ? `子代理 ${ss.subModel}${ss.subEffort ? '/' + ss.subEffort : ''}${ss.subNick ? ' (' + ss.subNick + ')' : ''}` : ss.subagent]
      .filter(Boolean).join(' · ');
  } else {
    $('sdot').className = 'sdot';
    $('sstate').textContent = '无会话';
    $('sctx').textContent = 'ctx --';
    $('sname').textContent = '—';
  }

  renderSkills($('skCodex'), ss && ss.skills);

  const f = s.forecast;
  if (f) {
    const a = f.p24 == null ? '--' : f.p24 + '%';
    const b = f.p48 == null ? '--' : f.p48 + '%';
    $('fsum').textContent = `24h ${a} · 48h ${b}`;
    $('p24').textContent = a;
    $('p48').textContent = b;
    $('age').textContent = f.ageDays == null ? '--' : f.ageDays.toFixed(1) + 'd';

    const bits = [];
    if (f.lastResetAt) bits.push('上次重置 ' + fmtClock(f.lastResetAt));
    if (f.cadence) bits.push('近期间隔中位数 ' + f.cadence + ' 天');
    $('fmeta').textContent = bits.join(' · ') || '数据加载中…';

    const hint = (f.hint || '').replace(/^👀\s*/, '');
    if (hint) {
      $('fhint').textContent = hint.length > 78 ? hint.slice(0, 78) + '…' : hint;
      $('fhint').classList.add('show');
    } else {
      $('fhint').classList.remove('show');
    }
  } else {
    $('fsum').textContent = s.forecastError ? '数据不可用' : '加载中';
  }
}

/* 以「本次重置周期」为横轴：5h 用窗口长度 */
function windowRange(w, fallbackSec) {
  if (!w || !w.resetAt) return null;
  const sec = w.windowSeconds > 0 ? w.windowSeconds : fallbackSec;
  return { from: w.resetAt - sec * 1000, to: w.resetAt };
}

// 本周从「周一 09:00」起算（不是 00:00——那是上周的尾巴）
function weekStartMs(now) {
  const d = new Date(now);
  const off = (d.getDay() + 6) % 7;   // 周一 = 0
  const mon = new Date(d.getFullYear(), d.getMonth(), d.getDate() - off, 9, 0, 0, 0);
  if (mon.getTime() > now) mon.setDate(mon.getDate() - 7);   // 周一还没到 9 点 → 上周一
  return mon.getTime();
}

function weeklyRange(w, now) {
  const from = weekStartMs(now);
  const end = from + 7 * 86400e3;
  // 只画到「现在」为止，否则右边会空出一大截，看着像没数据
  return { from, to: Math.min(end, Math.max(now, from + 60e3)), end };
}

// 本月从「每月 5 号 00:00」起算
function monthStartMs(now) {
  const d = new Date(now);
  const start = new Date(d.getFullYear(), d.getMonth(), 5, 0, 0, 0, 0);
  if (start.getTime() > now) start.setMonth(start.getMonth() - 1);   // 还没到 5 号 → 上月 5 号
  return start.getTime();
}

function monthRange(now) {
  const from = monthStartMs(now);
  const d = new Date(from);
  const end = new Date(d.getFullYear(), d.getMonth() + 1, 5, 0, 0, 0, 0).getTime();
  return { from, to: Math.min(end, Math.max(now, from + 60e3)), end };
}

function inWindow(pts, r) {
  return (pts || []).filter((p) => p[0] >= r.from && p[0] <= r.to);
}

// 曲线画「剩余额度」：used% → remaining%（100 → 0）
function toRemain(pts) {
  return (pts || []).map((p) => [p[0], 100 - p[1]]);
}

// 本月累计消耗（点）：周额度 used% 每一次上升都算进本月，下降说明周额度重置、不抵扣
function monthCum(raw, r, liveUsed, now) {
  const pts = (raw || [])
    .filter((p) => p[0] >= r.from && p[0] <= r.to)
    .sort((a, b) => a[0] - b[0]);

  const out = [[r.from, 0]];
  let cum = 0;
  let prev = null;
  for (const p of pts) {
    if (prev && p[1] > prev[1]) cum += p[1] - prev[1];
    prev = p;
    const last = out[out.length - 1];
    if (p[0] - last[0] > 60e3) out.push([p[0], cum]);
    else out[out.length - 1] = [p[0], cum];
  }
  // 把当前实时值接上，曲线才能长到现在
  if (typeof liveUsed === 'number' && isFinite(liveUsed)) {
    if (prev && liveUsed > prev[1]) cum += liveUsed - prev[1];
    const t = Math.min(now, r.to);
    const last = out[out.length - 1];
    if (!last || t - last[0] > 60e3) out.push([t, cum]);
    else out[out.length - 1] = [t, cum];
  }
  return { pts: out, cum };
}

// 历史采样 + 当前实时值，拼成本周期的剩余额度曲线
// forceFull：5 小时窗口一开就是满额，从 100% 开始往下掉
function seriesFor(raw, r, remaining, now, forceFull) {
  const out = toRemain(inWindow(raw, r)).sort((a, b) => a[0] - b[0]);

  if (typeof remaining === 'number' && isFinite(remaining)) {
    const t = Math.min(Math.max(now, r.from), r.to);
    const last = out[out.length - 1];
    if (!last || t - last[0] > 60 * 1000) out.push([t, remaining]);
  }

  if (out.length) {
    const lateStart = out[0][0] - r.from > 60 * 1000;
    if (forceFull && lateStart) {
      // 窗口起点补成 100%，曲线从左上角落下来
      if (out[0][1] < 99.5) out.unshift([r.from, 100]);
      else out[0] = [r.from, 100];
    } else if (lateStart && out[0][0] - r.from > 15 * 60 * 1000 && out[0][1] >= 95) {
      // 周期刚重置且首个采样已接近满额
      out.unshift([r.from, 100]);
    }
  }
  // 只有实时值（窗口刚开、还没有历史）：从窗口起点画一条平线，别显示成空白
  if (out.length === 1 && out[0][0] - r.from > 60 * 1000) {
    out.unshift([r.from, out[0][1]]);
  }
  return out;
}

function renderCurves(s) {
  const h = (s && s.history) || {};
  const now = Date.now();
  const u = s && s.usage;

  const r5 = windowRange(u && u.fiveHour, 5 * 3600) || { from: now - 5 * 3600e3, to: now };
  const rw = weeklyRange(u && u.weekly, now);
  const rm = monthRange(now);

  const h5 = seriesFor(h.h5, r5, u && u.fiveHour ? u.fiveHour.remaining : null, now, true);
  const wk = seriesFor(h.wk, rw, u && u.weekly ? u.weekly.remaining : null, now);
  // 本月曲线改成直接看 token：数据来自热力图的小时桶累加，不再用周额度倒推
  const ht = (s && s.heat) || {};
  const moPts = (ht.month || []).slice();
  const moTotal = ht.monthTotal || 0;
  const moMax = Math.max(10, Math.ceil((moTotal * 1.15) / 10) * 10);
  const rmo = { from: ht.monthStart || rm.from, to: now, ready: !!ht.ready };
  const mo = { pts: moPts, cum: moTotal };

  const hm = (t) => {
    const d = new Date(t);
    const p = (n) => String(n).padStart(2, '0');
    return `${p(d.getHours())}:${p(d.getMinutes())}`;
  };
  // 周几：周曲线跨度好几天，光看时分认不出是哪天
  const WD = ['日', '一', '二', '三', '四', '五', '六'];
  const wdOf = (t) => '周' + WD[new Date(t).getDay()];
  const wdhm = (t) => wdOf(t) + ' ' + hm(t);
  // 5 小时窗口可能跨过零点：同一天只报时分，跨天补上「周几」
  const hmDay = (t) => (new Date(t).toDateString() === new Date().toDateString() ? hm(t) : wdhm(t));
  const mdhm = (t) => {
    const d = new Date(t);
    const p = (n) => String(n).padStart(2, '0');
    return `${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}`;
  };
  const pctLabel = (v) => '剩 ' + Math.round(v) + '%';

  drawSpark($('c5'), h5, {
    from: r5.from, to: r5.to, now, max: 100, color: '#3ddc97',
    hoverLabel: pctLabel, timeLabel: hmDay
  });
  drawSpark($('cw'), wk, {
    from: rw.from, to: rw.to, now, max: 100, color: '#5b9dff',
    hoverLabel: pctLabel, timeLabel: wdhm
  });
  drawSpark($('cmo'), moPts, {
    from: rmo.from, to: rmo.to, now, max: moMax, color: '#ffb648',
    hoverLabel: (v) => '累计 ' + heatVal(v) + ' token', timeLabel: mdhm
  });

  const top5 = u && u.fiveHour ? Math.round(u.fiveHour.remaining) : null;
  const topW = u && u.weekly ? Math.round(u.weekly.remaining) : null;
  // 先清掉上一次的悬停态，下面 applyHoverHeader 会按鼠标实际位置再加回来
  Object.values(HEAD_OF).forEach((id) => {
    const b = $(id);
    if (!b || !b.parentElement) return;
    b.parentElement.classList.remove('hoving');
    shortHeadLabel(b.parentElement, false);
  });
  $('c5top').textContent = top5 == null ? '--' : '剩 ' + top5 + '%';
  $('cwtop').textContent = topW == null ? '--' : '剩 ' + topW + '%';
  // 本月用量旁边顺手折算成钱：按配置的「每百万 token 美元价 × 汇率」估算
  const perTok = s && s.costPerMTok > 0 ? s.costPerMTok : 2;
  const fxRate = s && s.fxRate > 0 ? s.fxRate : 7.1;
  const costText = (kTok) => {
    if (!kTok) return '';
    const cny = (kTok / 1000) * perTok * fxRate;
    return '≈¥' + (cny >= 10 ? Math.round(cny) : cny.toFixed(1));
  };
  const moText = rmo.ready ? '共 ' + heatVal(moTotal) + ' · ' + costText(moTotal) : '统计中';
  $('cmotop').textContent = moText;
  $('mosum').textContent = moText;

  // 折叠行摘要只报「当前剩余」，不带正负号，避免误读成涨跌
  $('csum').textContent = `5h ${top5 == null ? '--' : top5 + '%'} · 周 ${topW == null ? '--' : topW + '%'}`;
  // 整卡折叠成小条时，就靠这两个数字看额度
  const cq = (lab, v) => v == null
    ? `<span class="cq"><em>${lab}</em><b>--</b></span>`
    : `<span class="cq"><em>${lab}</em><b style="color:${colorOf(v)}">${v}%</b></span>`;
  $('csum2').innerHTML = cq('5h', top5) + '<span class="cqs">·</span>' + cq('周', topW);

  // 悬停提示：本周期起点 + 本周期已消耗
  const tip = (r, pts, startText) => {
    const d = deltaOf(pts);
    const used = d == null ? '' : `，本周期已用 ${Math.abs(Math.round(d))} 点`;
    return startText + used;
  };
  const c5 = $('c5').parentElement;
  const cw = $('cw').parentElement;
  const cm = $('cmo').parentElement;
  if (c5) c5.title = tip(r5, h5, '5 小时窗口 ' + fmtTimeOnly(new Date(r5.from).toISOString()) + ' 起');
  if (cw) cw.title = tip(rw, wk, '周一 09:00 → 下周一 09:00 的曲线')
    + (u && u.weekly && u.weekly.resetAt ? '；周额度 ' + fmtRemain(u.weekly.resetAt - now) : '');
  if (cm) cm.title = rmo.ready
    ? '本月 5 号起共用 ' + heatVal(moTotal) + ' token · ' + costText(moTotal)
      + '（按 $' + perTok + '/M token、汇率 ' + fxRate + ' 估算，可在 codex-pet-config.json 里改）'
    : '正在统计本月用量…';

  ['c5', 'cw', 'cmo'].forEach((id) => bindHover($(id)));
  // 重绘会覆盖头部文字，若鼠标还停在图上就把悬停值补回去
  ['c5', 'cw', 'cmo'].forEach((id) => { const cv = $(id); if (cv) applyHoverHeader(cv); });

  window.__dbg = { r5, rw, rm, rmo, h5, wk, mo, now };
}

/* ---------- 活跃热力图 ---------- */
function heatVal(k) {
  if (typeof k !== 'number' || !isFinite(k) || k <= 0) return '0';
  return k >= 1000 ? (k / 1000).toFixed(1) + 'M' : k + 'k';
}

function heatColor(t) {
  if (t <= 0) return 'rgba(255, 255, 255, 0.045)';
  const a = 0.1 + 0.9 * Math.min(1, t);
  return 'rgba(61, 220, 151, ' + a.toFixed(3) + ')';
}

function renderHeat(s) {
  const el = $('heat');
  if (!el) return;
  const h = (s && s.heat) || {};
  const grid = h.grid || [];
  if (!h.ready || !grid.length) {
    el.innerHTML = '<div class="heatwait">正在统计近 30 天用量…</div>';
    return;
  }
  const max = Math.max(1, h.max || 1);
  const days = h.days || [];
  const out = [];
  for (let d = 0; d < grid.length; d++) {
    for (let hr = 0; hr < 24; hr++) {
      const v = grid[d][hr] || 0;
      const t = v > 0 ? Math.pow(v / max, 0.55) : 0;
      out.push('<i data-v="' + v + '" style="background:' + heatColor(t) + '" title="'
        + (days[d] || '') + ' ' + String(hr).padStart(2, '0') + ':00 · ' + heatVal(v) + ' token"></i>');
    }
  }
  el.innerHTML = out.join('');
  $('heatPeak').textContent = h.peakText ? '最疯 ' + h.peakText : '近 30 天没有记录';
  $('heatTotal').textContent = '合计 ' + heatVal(h.total) + ' token';
  $('heatFrom').textContent = days[0] || '';
  $('heatTo').textContent = days[days.length - 1] || '';
}

function syncHeatHeader(s) {
  const h = (s && s.heat) || {};
  const sum = $('heatsum');
  if (sum) sum.textContent = h.ready ? '共 ' + heatVal(h.total) : '统计中';
  if (heatOpen) renderHeat(s);
}

/* ---------- Antigravity ---------- */
function fmtCredit(c) {
  if (!c || typeof c.left !== 'number' || typeof c.total !== 'number' || !c.total) return '--';
  const one = (n) => (n >= 1000 ? Math.round(n / 100) / 10 + 'k' : String(Math.round(n)));
  return `${one(c.left)} / ${one(c.total)}`;
}

function agBar(label, q, kind) {
  if (!q) return '';
  const ms = q.resetAt ? Date.parse(q.resetAt) : 0;
  const right = kind === '5h' ? resetClockText(ms) : fmtRemain(ms ? ms - Date.now() : 0);
  return `
      <div class="metric">
        <div class="mline ag">
          <span class="lab agname">${label}</span>
          <span class="grow"></span>
          <span class="val agval" style="color:${colorOf(q.remaining)}">${q.remaining}<small>%</small></span>
          <span class="sub agsub">${right}</span>
        </div>
        <div class="track"><i class="${level(q.remaining)}" style="width:${q.remaining}%"></i></div>
      </div>`;
}

function renderAg(s) {
  const list = $('agList');
  const foot = $('agFoot');
  const a = s.antigravity;

  if (!a) {
    list.innerHTML = `<div class="agerr">Antigravity 未运行或未登录。<br>打开 <span>Antigravity</span> 后会自动读取额度。</div>`;
    foot.textContent = '';
    return;
  }

  const groups = a.groups || [];
  if (groups.length) {
    list.innerHTML = groups.map((g) => `
      <div class="aggrp">
        <div class="aggh" title="${esc(g.desc || g.name)}">${esc(g.name)}</div>
        ${agBar('周', g.weekly, 'weekly')}
        ${agBar('5h', g.fiveHour, '5h')}
      </div>`).join('');
  } else if (a.models && a.models.length) {
    // 老接口兜底：只有每个模型的滚动窗口
    list.innerHTML = a.models.map((m) => `
      <div class="metric">
        <div class="mline ag">
          <span class="lab agname" title="${esc(m.label)}">${esc(m.label)}</span>
          <span class="grow"></span>
          <span class="val agval" style="color:${colorOf(m.remaining)}">${m.remaining}<small>%</small></span>
          <span class="sub agsub">${fmtTimeOnly(m.resetAt)} 重置</span>
        </div>
        <div class="track"><i class="${level(m.remaining)}" style="width:${m.remaining}%"></i></div>
      </div>`).join('');
  } else {
    list.innerHTML = `<div class="agerr">未取到模型额度。</div>`;
  }

  foot.innerHTML = `<span>Prompt <b>${fmtCredit(a.credits.prompt)}</b></span>`
    + `<span>Flow <b>${fmtCredit(a.credits.flow)}</b></span>`;
}

/* ---------- WorkBuddy ---------- */
function renderWb(s) {
  const w = s && s.wb;
  if (!w) {
    $('wbModel').textContent = '--';
    $('wbModelSub').textContent = '';
    $('wbToday').textContent = '--';
    $('wbWeek').textContent = '--';
    $('wbTodayReq').textContent = '--';
    $('wbWeekReq').textContent = '--';
    $('wbSess').textContent = '读取中…';
    $('wbLast').textContent = '';
    renderSkills($('skWb'), null);
    return;
  }

  const mEl = $('wbModel');
  mEl.textContent = w.model ? String(w.model).replace(/[-_]/g, ' ') : '--';
  mEl.classList.toggle('on', !!w.running);
  const norm = (x) => String(x || '').replace(/[-_ ]/g, '').toLowerCase();
  const sameModel = w.modelLabel && norm(w.modelLabel) === norm(w.model);
  $('wbModelSub').textContent = [sameModel ? null : w.modelLabel, w.agent ? String(w.agent).toUpperCase() : null]
    .filter(Boolean).join(' · ');

  $('wbToday').innerHTML = `${fmtNum(w.todayCredit)}<small> cr</small>`;
  $('wbWeek').innerHTML = `${fmtNum(w.weekCredit)}<small> cr</small>`;
  $('wbTodayReq').textContent = `${w.todayReq || 0} 次调用`;
  $('wbWeekReq').textContent = `${w.weekReq || 0} 次调用`;
  $('wbSess').textContent = `今日 ${w.sessions || 0} 个会话`;
  $('wbLast').textContent = w.lastActiveAt ? `最后活动 ${fmtAgo(w.lastActiveAt)}` : '';
  renderSkills($('skWb'), w.skills);

  const now = Date.now();
  const hourly = (w.hourly || []).map((v, i) => [now - (23 - i) * 3600e3, v]);
  const daily = (w.daily || []).map((v, i) => {
    const d = new Date(now);
    d.setHours(0, 0, 0, 0);
    return [d.getTime() - (6 - i) * 86400e3, v];
  });
  const maxH = Math.max(1, ...(w.hourly || [0]));
  const maxD = Math.max(1, ...(w.daily || [0]));

  drawSpark($('wbH'), hourly, {
    from: now - 24 * 3600e3, to: now, max: maxH * 1.2, color: '#a78bfa',
    hoverLabel: (v) => fmtNum(v) + ' cr', timeLabel: (t) => new Date(t).getHours() + ':00'
  });
  drawSpark($('wbD'), daily, {
    from: now - 7 * 86400e3, to: now, max: maxD * 1.2, color: '#a78bfa',
    hoverLabel: (v) => fmtNum(v) + ' cr',
    timeLabel: (t) => `${new Date(t).getMonth() + 1}-${new Date(t).getDate()}`
  });
  bindHover($('wbH'));
  bindHover($('wbD'));

  $('wbHTop').textContent = '峰值 ' + fmtNum(maxH);
  $('wbDTop').textContent = '合计 ' + fmtNum((w.daily || []).reduce((a, b) => a + b, 0));
}

/* ---------- agent 完成提示 ---------- */
const AGENT_META = {
  codex: { name: 'CODEX', color: '#3ddc97' },
  antigravity: { name: 'ANTIGRAVITY', color: '#5b9dff' },
  workbuddy: { name: 'WORKBUDDY', color: '#a78bfa' }
};

let doneSeqSeen = -1;                  // -1 = 还没拿到基线
const barQueue = [];
let barTimer = null;
let barAgent = null;
let flashTimer = null;

function metaOf(agent) {
  return AGENT_META[agent] || { name: String(agent || '').toUpperCase(), color: '#8b9ab0' };
}

function flashCard(color) {
  const app = $('app');
  if (!app) return;
  app.style.setProperty('--fc', color);
  app.classList.remove('flash');
  void app.offsetWidth;               // 强制回流，让动画能重播
  app.classList.add('flash');
  clearTimeout(flashTimer);
  flashTimer = setTimeout(() => app.classList.remove('flash'), 1600);
}

function showBar() {
  const bar = $('doneBar');
  const ev = barQueue.shift();
  if (!ev) {
    bar.classList.remove('show', 'in');
    barAgent = null;
    if (barTimer) { clearTimeout(barTimer); barTimer = null; }
    syncHeight();
    return;
  }
  const m = metaOf(ev.agent);
  barAgent = ev.agent;
  bar.style.setProperty('--dc', m.color);
  $('dAgent').textContent = m.name;
  $('dLabel').textContent = ev.label ? ' · ' + ev.label : '';
  $('dMore').textContent = barQueue.length > 0 ? '+' + barQueue.length : '';
  $('dTime').textContent = '完成';
  bar.classList.add('show');
  bar.classList.remove('in');
  void bar.offsetWidth;
  bar.classList.add('in');
  flashCard(m.color);
  syncHeight();
  if (barTimer) clearTimeout(barTimer);
  barTimer = setTimeout(() => { barTimer = null; showBar(); }, 5200);
}

function onDoneEvents(s) {
  const list = (s && s.done) || [];
  const seq = (s && s.doneSeq) || 0;

  if (doneSeqSeen < 0) {              // 首次只记基线，不把历史事件当成新完成
    doneSeqSeen = seq;
    return;
  }
  if (seq <= doneSeqSeen) return;

  const fresh = list.filter((e) => e.seq > doneSeqSeen).sort((a, b) => a.seq - b.seq);
  doneSeqSeen = seq;
  // 折叠成一条时有人完成任务：先展开再提醒，否则提示条被藏起来了
  if (collapsed) applyCollapsed(false, true);
  for (const e of fresh) {
    // 当前看的就是它 → 原地提醒；看的是别人 → 卡片直接翻过去
    if (e.agent !== mode) setMode(e.agent, true);
    barQueue.push(e);
  }
  if (!barTimer) showBar();
}

// 单击：切到那个 agent 并清队列；双击：连会话文件一起打开（省得自己翻目录）
let barClickTimer = null;
$('doneBar').addEventListener('click', () => {
  if (barClickTimer) {
    clearTimeout(barClickTimer);
    barClickTimer = null;
    const who = barAgent;
    if (who) {
      if (who !== mode) setMode(who, true);
      window.pet.openSession(who);
      barQueue.length = 0;
      showBar();
    }
    try { document.getSelection().removeAllRanges(); } catch (_) {}
    return;
  }
  barClickTimer = setTimeout(() => {
    barClickTimer = null;
    if (barAgent && barAgent !== mode) setMode(barAgent, true);
    barQueue.length = 0;
    showBar();
  }, 240);
});

/* ---------- 桌宠小人 ---------- */
const petEl = $('petStrip');
const petSay = $('petSay');
const petSub = $('petSub');
let petHoldUntil = 0;
let petHoldMsg = null;
let petBusySeen = 0;

function petAccent() {
  return mode === 'workbuddy' ? '#a78bfa' : mode === 'antigravity' ? '#5b9dff' : '#3ddc97';
}

function petMinQuota(s) {
  const vals = [];
  if (mode === 'codex' && s.usage) {
    [s.usage.fiveHour, s.usage.weekly].forEach((w) => { if (w && typeof w.remaining === 'number') vals.push(w.remaining); });
  } else if (mode === 'antigravity' && s.antigravity && s.antigravity.groups) {
    s.antigravity.groups.forEach((g) => {
      [g.weekly, g.fiveHour].forEach((q) => { if (q && typeof q.remaining === 'number') vals.push(q.remaining); });
    });
  }
  return vals.length ? Math.min.apply(null, vals) : null;
}

function petBusy(s) {
  if (mode === 'codex') return !!(s.session && (s.session.running || s.session.subRunning));
  if (mode === 'workbuddy') return !!(s.wb && s.wb.running);
  return Date.now() - petBusySeen < 8000;
}

function setPet(nextState, msg, sub) {
  if (petEl.dataset.state !== nextState) petEl.dataset.state = nextState;
  if (petEl.dataset.accent !== mode) petEl.dataset.accent = mode;
  if (msg != null && petSay.textContent !== msg) petSay.textContent = msg;
  const sum = $('petSum');
  if (msg != null && sum && sum.textContent !== msg) sum.textContent = msg;
  const t = sub == null ? '' : sub;
  if (petSub.textContent !== t) petSub.textContent = t;
}

// 短时状态（完成 / 被摸头 / 递咖啡）：hold 一段时间，别被 5 秒一次的状态刷新冲掉
function petHold(stateName, msg, sub, ms) {
  petHoldUntil = Date.now() + (ms || 2000);
  petHoldMsg = { state: stateName, msg, sub };
  petEl.dataset.state = 'idle';       // 先复位，动画才会重播
  void petEl.offsetWidth;
  setPet(stateName, msg, sub);
}

function petReact(agent, label) {
  const m = metaOf(agent);
  petBusySeen = Date.now();
  petHold('done', m.name + ' 交活了', label || '这一轮跑完了', 1900);
}

// 连续工作时长：每 2 小时递一杯咖啡（跨重启也记得，用 localStorage）
const BUSY_SINCE = 'petBusySince';
const BUSY_FREE = 5 * 60e3;          // 空闲超过 5 分钟就算这段连续工作结束
const COFFEE_GAP = 2 * 3600e3;

function lsNum(k) {
  try { return parseInt(localStorage.getItem(k) || '0', 10) || 0; } catch (_) { return 0; }
}
function lsPut(k, v) {
  try { localStorage.setItem(k, String(v)); } catch (_) {}
}

function trackBusy(busy) {
  const now = Date.now();
  const since = lsNum(BUSY_SINCE);
  if (!busy) {
    if (since && now - since > BUSY_FREE) lsPut(BUSY_SINCE, 0);
    return;
  }
  if (!since) { lsPut(BUSY_SINCE, now); return; }
  const run = now - since;
  if (run < COFFEE_GAP) return;
  if (now - lsNum('petCoffeeAt') > COFFEE_GAP) {
    lsPut('petCoffeeAt', now);
    petHold('coffee', '喝口水吧', '已经连着跑了 ' + Math.round(run / 3600e3) + ' 小时', 6500);
  }
}

function updatePet(s) {
  if (!s || !petEl) return;
  const busy = petBusy(s);
  if (busy) petBusySeen = Date.now();
  trackBusy(busy);

  if (petHoldMsg && Date.now() < petHoldUntil) {
    setPet(petHoldMsg.state || 'done', petHoldMsg.msg, petHoldMsg.sub);
    return;
  }
  petHoldMsg = null;

  const low = petMinQuota(s);
  if (low != null && low < 10) {
    // 见底了就别装淡定了：蹲下抱头
    setPet('hug', '抱头…', '只剩 ' + Math.round(low) + '%，省着点用');
    return;
  }

  // 凌晨 1 点后还在干活：打着瞌睡干活，比"干活中"更真实
  const hh = new Date().getHours();
  if (busy && hh >= 1 && hh < 5) {
    setPet('sleep', '打瞌睡了…', '凌晨 ' + hh + ' 点还在跑');
    return;
  }

  if (busy) {
    let sub = '正在输出';
    if (mode === 'codex' && s.session) {
      const ss = s.session;
      if (ss.subRunning) sub = '子代理' + (ss.subNick ? ' ' + ss.subNick : '') + ' 在跑';
      else if (ss.model) sub = modelText(ss.model, ss.effort).replace(/\s*中$/, '') + ' 正在输出';
    } else if (mode === 'workbuddy' && s.wb) {
      sub = '今日 ' + (s.wb.todayReq || 0) + ' 次调用';
    }
    setPet('work', '干活中…', sub);
    return;
  }

  let sub = '';
  if (mode === 'codex' && s.session) sub = s.session.lastActiveAt ? '上次活动 ' + fmtAgo(s.session.lastActiveAt) : '';
  else if (mode === 'antigravity') sub = '等 Antigravity 派活';
  else if (mode === 'workbuddy' && s.wb) sub = '今日 ' + (s.wb.sessions || 0) + ' 个会话';
  setPet('idle', '待命中', sub);
}

// 眼睛的眨眼延迟随机化，别每次都同时眨
document.querySelectorAll('.petstrip .peye').forEach((e) => {
  e.style.animationDelay = (-Math.random() * 6.4).toFixed(2) + 's';
});

/* ---------- 点一下 / 双击摸头 ---------- */
const PET_TICKLE = ['嗷，好痒', '别戳我啦', '痒痒的…', '再点就要收钱了'];
const PET_PAT = ['嘿嘿…', '害羞了', '摸头收到', '再摸一下也行'];
let patCount = lsNum('petPats');

function refreshPetTitle() {
  petEl.title = patCount > 0 ? '点我一下 · 已被摸头 ' + patCount + ' 次' : '点我一下';
}
refreshPetTitle();

let petClickTimer = null;
petEl.addEventListener('click', () => {
  // 双击 = 摸头：脸红 + 计数；单击 = 嫌痒
  if (petClickTimer) {
    clearTimeout(petClickTimer);
    petClickTimer = null;
    patCount += 1;
    lsPut('petPats', patCount);
    refreshPetTitle();
    petHold('shy', PET_PAT[patCount % PET_PAT.length], '已被摸头 ' + patCount + ' 次', 1900);
    return;
  }
  petClickTimer = setTimeout(() => {
    petClickTimer = null;
    petHold('idle', PET_TICKLE[Math.floor(Math.random() * PET_TICKLE.length)], '点我不会加快速度', 1400);
  }, 230);
});

/* ---------- 头部同步 ---------- */
function syncHeader() {
  const isAg = mode === 'antigravity';
  const isWb = mode === 'workbuddy';
  $('brand').textContent = isWb ? 'WORKBUDDY' : (isAg ? 'ANTIGRAVITY' : 'CODEX');

  let planText = '--';
  let low = 100;
  if (isWb) {
    planText = 'LOCAL';
    $('plan').textContent = planText;
    $('dot').style.background = '#a78bfa';
    $('dot').style.boxShadow = '0 0 6px #a78bfa';
    return;
  }
  if (isAg) {
    const a = state && state.antigravity;
    planText = a ? (a.plan || 'PRO') : 'AG';
    if (a && a.models.length) low = Math.min(...a.models.map((m) => m.remaining));
  } else if (state && state.usage) {
    planText = state.usage.plan || '--';
    const vals = [state.usage.fiveHour, state.usage.weekly]
      .filter(Boolean).map((w) => w.remaining);
    if (vals.length) low = Math.min(...vals);
  }
  $('plan').textContent = planText;

  const c = isAg ? '#5b9dff' : colorOf(low);
  $('dot').style.background = c;
  $('dot').style.boxShadow = `0 0 6px ${c}`;
}

/* ---------- 高度自适应 ---------- */
let lastH = 0;
let syncQueued = false;
function syncHeight() {
  if (syncQueued) return;
  syncQueued = true;
  requestAnimationFrame(() => {
    syncQueued = false;
    const app = $('app');
    if (!app) return;
    const h = Math.ceil(app.getBoundingClientRect().height);
    if (h > 18 && Math.abs(h - lastH) > 0.5) {
      lastH = h;
      window.pet.resize(h);
    }
  });
}

function render(s) {
  state = s;
  window.__state = s;
  if (!s) return;
  renderCodex(s);
  renderAg(s);
  renderWb(s);
  renderCurves(s);
  syncHeader();
  updatePet(s);
  syncHeatHeader(s);
  onDoneEvents(s);
  syncHeight();
}

/* ---------- 模式切换 ---------- */
function setMode(next, notify) {
  mode = next;
  document.body.classList.toggle('ag', mode === 'antigravity');
  document.body.classList.toggle('wb', mode === 'workbuddy');
  $('viewCodex').classList.toggle('hidden', mode !== 'codex');
  $('viewAg').classList.toggle('hidden', mode !== 'antigravity');
  $('viewWb').classList.toggle('hidden', mode !== 'workbuddy');
  document.querySelectorAll('#seg button').forEach((b) => {
    b.classList.toggle('on', b.dataset.mode === mode);
  });
  syncHeader();
  renderCurves(state || {});
  renderWb(state || {});
  updatePet(state || {});
  if (heatOpen) renderHeat(state || {});
  syncHeight();
  if (notify) pushView();
}

document.querySelectorAll('#seg button').forEach((b) => {
  b.addEventListener('click', (e) => {
    e.stopPropagation();
    if (b.dataset.mode === mode) return;
    setMode(b.dataset.mode, true);
  });
});

/* ---------- 折叠 ---------- */
function pushView() {
  window.pet.setView({ mode, expanded, curveOpen, monthOpen, heatOpen, petClosed: !petOpen, collapsed });
}

function applyExpanded(next, notify) {
  expanded = next;
  $('foldbody').classList.toggle('open', expanded);
  $('chev').classList.toggle('open', expanded);
  syncHeight();
  if (notify) pushView();
}

function applyCurve(next, notify) {
  curveOpen = next;
  $('curveBody').classList.toggle('open', curveOpen);
  $('chev2').classList.toggle('open', curveOpen);
  if (curveOpen) renderCurves(state || {});
  syncHeight();
  if (notify) pushView();
}

// 曲线的二级折叠：本月累计
function applyMonth(next, notify) {
  monthOpen = next;
  $('monthBody').classList.toggle('open', monthOpen);
  $('chev3').classList.toggle('open', monthOpen);
  if (monthOpen) setTimeout(() => renderCurves(state || {}), 20);   // 展开后按真实宽度重画
  syncHeight();
  if (notify) pushView();
}

// 整张卡片折叠成一条：只留头部（品牌 + 额度摘要 + 展开按钮）
function applyCollapsed(next, notify) {
  collapsed = next;
  $('app').classList.toggle('collapsed', collapsed);
  document.body.classList.toggle('collapsed', collapsed);
  if (notify) pushView();
  syncHeight();
  if (!collapsed) setTimeout(() => renderCurves(state || {}), 30);   // 展开后重画，避免宽度按隐藏时的尺寸算
}

// 小人的折叠
function applyPet(next, notify) {
  petOpen = next;
  $('petBody').classList.toggle('open', petOpen);
  $('chevPet').classList.toggle('open', petOpen);
  syncHeight();
  if (notify) pushView();
}

// 曲线的二级折叠：活跃热力图
function applyHeat(next, notify) {
  heatOpen = next;
  $('heatBody').classList.toggle('open', heatOpen);
  $('chev4').classList.toggle('open', heatOpen);
  if (heatOpen) renderHeat(state || {});
  syncHeight();
  if (notify) pushView();
}

$('fold').addEventListener('click', () => applyExpanded(!expanded, true));
$('foldCurve').addEventListener('click', () => applyCurve(!curveOpen, true));
$('foldMonth').addEventListener('click', () => applyMonth(!monthOpen, true));
$('foldHeat').addEventListener('click', () => applyHeat(!heatOpen, true));
$('foldPet').addEventListener('click', () => applyPet(!petOpen, true));
$('collapse').addEventListener('click', (e) => {
  e.stopPropagation();
  applyCollapsed(!collapsed, true);
});

/* ---------- 拖动 ---------- */
(function enableDrag() {
  const handle = $('drag');
  let dragging = false;
  let start = { x: 0, y: 0 };
  let pos = { x: 0, y: 0 };

  handle.addEventListener('pointerdown', async (e) => {
    if (e.button !== 0 || e.target.closest('button')) return;
    dragging = true;
    start = { x: e.screenX, y: e.screenY };
    pos = await window.pet.getPosition();
    handle.setPointerCapture(e.pointerId);
  });
  handle.addEventListener('pointermove', (e) => {
    if (!dragging) return;
    window.pet.drag(e.screenX - start.x, e.screenY - start.y, pos.x, pos.y);
  });
  handle.addEventListener('pointerup', (e) => {
    dragging = false;
    try { handle.releasePointerCapture(e.pointerId); } catch (_) {}
  });
})();

/* ---------- 右键菜单 ---------- */
window.addEventListener('contextmenu', (e) => {
  e.preventDefault();
  window.pet.menu({ x: Math.round(e.screenX), y: Math.round(e.screenY), mode });
});

/* ---------- 刷新 ---------- */
$('refresh').addEventListener('click', async (e) => {
  const btn = e.currentTarget;
  btn.classList.add('spin');
  await window.pet.refresh();
  setTimeout(() => btn.classList.remove('spin'), 400);
});

/* ---------- 计时 ---------- */
setInterval(() => {
  if (!state || !state.usage) return;
  const u = state.usage;
  if (u.fiveHour && u.fiveHour.resetAt) $('r5').textContent = resetClockText(u.fiveHour.resetAt);
  if (u.weekly && u.weekly.resetAt) $('rw').textContent = fmtRemain(u.weekly.resetAt - Date.now());
}, 1000);

/* ---------- 初始化 ---------- */
window.pet.onState((s) => render(s));
window.pet.getState().then((s) => {
  render(s);
  setMode((s && s.mode) || 'codex', false);
  applyExpanded(!!(s && s.expanded), false);
  applyCurve(!!(s && s.curveOpen), false);
  applyMonth(!!(s && s.monthOpen), false);
  applyHeat(!!(s && s.heatOpen), false);
  applyPet(!(s && s.petClosed), false);
  applyCollapsed(!!(s && s.collapsed), false);
  setTimeout(syncHeight, 60);
});
