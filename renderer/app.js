'use strict';

const $ = (id) => document.getElementById(id);

let state = null;
let expanded = false;
let curveOpen = false;
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
function drawSpark(cv, pts, opts) {
  if (!cv) return;
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
    ctx.fillText('采样中…', w / 2, h / 2 + 3);
    return;
  }

  const from = opts.from;
  const to = opts.to;
  const max = Math.max(opts.max || 100, 0.0001);
  const span = Math.max(1, to - from);

  const X = (t) => ((t - from) / span) * w;
  const Y = (v) => padT + plotH - Math.min(1, Math.max(0, v / max)) * plotH;

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

/* 以「本次重置周期」为横轴：5h 用窗口长度；本周从本周一 00:00 起算 */
function windowRange(w, fallbackSec) {
  if (!w || !w.resetAt) return null;
  const sec = w.windowSeconds > 0 ? w.windowSeconds : fallbackSec;
  return { from: w.resetAt - sec * 1000, to: w.resetAt };
}

function weekStartMs(now) {
  const d = new Date(now);
  const off = (d.getDay() + 6) % 7;   // 周一 = 0
  d.setHours(0, 0, 0, 0);
  d.setDate(d.getDate() - off);
  return d.getTime();
}

function weeklyRange(w, now) {
  const from = weekStartMs(now);
  const fallbackTo = from + 7 * 86400e3;
  const to = w && w.resetAt ? w.resetAt : fallbackTo;
  if (to <= from) return { from, to: fallbackTo };
  return { from, to };
}

function inWindow(pts, r) {
  return (pts || []).filter((p) => p[0] >= r.from && p[0] <= r.to);
}

// 曲线画「剩余额度」：used% → remaining%（100 → 0）
function toRemain(pts) {
  return (pts || []).map((p) => [p[0], 100 - p[1]]);
}

// 历史采样 + 当前实时值，拼成本周期的剩余额度曲线
function seriesFor(raw, r, remaining, now) {
  const out = toRemain(inWindow(raw, r)).sort((a, b) => a[0] - b[0]);

  if (typeof remaining === 'number' && isFinite(remaining)) {
    const t = Math.min(Math.max(now, r.from), r.to);
    const last = out[out.length - 1];
    if (!last || t - last[0] > 60 * 1000) out.push([t, remaining]);
  }

  // 周期刚重置且首个采样已接近满额：补一个 100 起点，让曲线从左边满格落下
  if (out.length && out[0][0] - r.from > 15 * 60 * 1000 && out[0][1] >= 95) {
    out.unshift([r.from, 100]);
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

  const h5 = seriesFor(h.h5, r5, u && u.fiveHour ? u.fiveHour.remaining : null, now);
  const wk = seriesFor(h.wk, rw, u && u.weekly ? u.weekly.remaining : null, now);

  drawSpark($('c5'), h5, { from: r5.from, to: r5.to, now, max: 100, color: '#3ddc97' });
  drawSpark($('cw'), wk, { from: rw.from, to: rw.to, now, max: 100, color: '#5b9dff' });

  const top5 = u && u.fiveHour ? Math.round(u.fiveHour.remaining) : null;
  const topW = u && u.weekly ? Math.round(u.weekly.remaining) : null;
  $('c5top').textContent = top5 == null ? '--' : '剩 ' + top5 + '%';
  $('cwtop').textContent = topW == null ? '--' : '剩 ' + topW + '%';

  // 折叠行摘要只报「当前剩余」，不带正负号，避免误读成涨跌
  $('csum').textContent = `5h ${top5 == null ? '--' : top5 + '%'} · 周 ${topW == null ? '--' : topW + '%'}`;

  // 悬停提示：本周期起点 + 本周期已消耗
  const tip = (r, pts) => {
    const d = deltaOf(pts);
    const used = d == null ? '' : `，本周期已用 ${Math.abs(Math.round(d))} 点`;
    return '本周期 ' + fmtTimeOnly(new Date(r.from).toISOString()) + ' 起' + used
      + '（' + fmtRemain(r.to - now) + '）';
  };
  const c5 = $('c5').parentElement;
  const cw = $('cw').parentElement;
  if (c5) c5.title = tip(r5, h5);
  if (cw) cw.title = tip(rw, wk);

  window.__dbg = { r5, rw, h5, wk, now };
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

  const now = Date.now();
  const hourly = (w.hourly || []).map((v, i) => [now - (23 - i) * 3600e3, v]);
  const daily = (w.daily || []).map((v, i) => {
    const d = new Date(now);
    d.setHours(0, 0, 0, 0);
    return [d.getTime() - (6 - i) * 86400e3, v];
  });
  const maxH = Math.max(1, ...(w.hourly || [0]));
  const maxD = Math.max(1, ...(w.daily || [0]));

  drawSpark($('wbH'), hourly, { from: now - 24 * 3600e3, to: now, max: maxH * 1.2, color: '#a78bfa' });
  drawSpark($('wbD'), daily, { from: now - 7 * 86400e3, to: now, max: maxD * 1.2, color: '#a78bfa' });

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
  for (const e of fresh) {
    // 当前看的就是它 → 原地提醒；看的是别人 → 卡片直接翻过去
    if (e.agent !== mode) setMode(e.agent, true);
    barQueue.push(e);
  }
  if (!barTimer) showBar();
}

$('doneBar').addEventListener('click', () => {
  if (barAgent && barAgent !== mode) setMode(barAgent, true);
  barQueue.length = 0;
  showBar();
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
    if (h > 60 && Math.abs(h - lastH) > 0.5) {
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
  syncHeight();
  if (notify) window.pet.setView({ mode, expanded, curveOpen });
}

document.querySelectorAll('#seg button').forEach((b) => {
  b.addEventListener('click', (e) => {
    e.stopPropagation();
    if (b.dataset.mode === mode) return;
    setMode(b.dataset.mode, true);
  });
});

/* ---------- 折叠 ---------- */
function applyExpanded(next, notify) {
  expanded = next;
  $('foldbody').classList.toggle('open', expanded);
  $('chev').classList.toggle('open', expanded);
  syncHeight();
  if (notify) window.pet.setView({ mode, expanded, curveOpen });
}

function applyCurve(next, notify) {
  curveOpen = next;
  $('curveBody').classList.toggle('open', curveOpen);
  $('chev2').classList.toggle('open', curveOpen);
  if (curveOpen) renderCurves(state || {});
  syncHeight();
  if (notify) window.pet.setView({ mode, expanded, curveOpen });
}

$('fold').addEventListener('click', () => applyExpanded(!expanded, true));
$('foldCurve').addEventListener('click', () => applyCurve(!curveOpen, true));

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
  setTimeout(syncHeight, 60);
});
