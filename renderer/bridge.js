'use strict';

/* WebView2 桥：给渲染层提供与 Electron 版一致的 window.pet API */
(function () {
  const wv = window.chrome && window.chrome.webview;
  const handlers = [];
  let seq = 0;
  const pending = new Map();

  if (wv) {
    wv.addEventListener('message', (e) => {
      const d = e.data;
      if (!d || typeof d !== 'object') return;
      if (d.type === 'state') {
        for (const h of handlers) { try { h(d.state); } catch (_) {} }
      } else if (d.type === 'res' && pending.has(d.id)) {
        const p = pending.get(d.id);
        pending.delete(d.id);
        if (d.ok) p.resolve(d.result); else p.reject(new Error(d.error || 'bridge error'));
      }
    });
  }

  const send = (cmd, args) => new Promise((resolve, reject) => {
    if (!wv) { resolve(null); return; }
    const id = ++seq;
    pending.set(id, { resolve, reject });
    wv.postMessage({ id, cmd, args: args === undefined ? null : args });
  });

  window.pet = {
    getState: () => send('get-state'),
    refresh: () => send('refresh'),
    onState: (cb) => { if (typeof cb === 'function') handlers.push(cb); },
    // 拖动是高频消息，直接投递，不等待回执
    drag: (dx, dy, sx, sy) => { if (wv) wv.postMessage({ id: 0, cmd: 'drag', args: [dx, dy, sx, sy] }); },
    getPosition: () => send('get-position'),
    setExpanded: (v) => send('set-view', { expanded: v }),
    setView: (v) => send('set-view', v),
    resize: (h) => { if (wv) wv.postMessage({ id: 0, cmd: 'resize', args: h }); },
    probe: (tag) => { if (wv) wv.postMessage({ id: 0, cmd: 'probe', args: tag || '' }); },
    quit: () => send('quit'),
    // 右键菜单交给宿主弹原生菜单（网页里的 contextmenu 默认被禁掉了）
    menu: (o) => { if (wv) wv.postMessage({ id: 0, cmd: 'menu', args: o || {} }); },
    // 打开某个 agent 当前会话的文件（资源管理器里选中）
    openSession: (agent) => { if (wv) wv.postMessage({ id: 0, cmd: 'open-session', args: agent || '' }); },
    openExternal: (url) => send('open-external', url)
  };
})();
