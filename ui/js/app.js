// app.js — view switching, theme, mock data, sync state machine, countdown,
// config autosave, pairing flow. Vanilla ES module, no framework, no build.
//
// Note on innerHTML: it is used ONLY for the static, author-controlled SVG icon
// strings from icons.js (no external/user input ever flows into them). Any
// dynamic text (activity titles, device names, counts) is written with
// textContent so there is no injection surface even if mock data changed.

import { icon, hydrateIcons } from './icons.js';

const $ = (sel, root = document) => root.querySelector(sel);
const $$ = (sel, root = document) => [...root.querySelectorAll(sel)];

// ---------------- Native bridge (optional) ----------------
// When SyncMaster runs inside the desktop host the UI is served over a loopback
// HttpListener that exposes /__bridge/send (web->native) and /__bridge/poll
// (native->web long-poll), or a real embedded WebView injects window.chrome.webview.
// In either case we route getStatus/syncNow/pair/saveConfig/setPaused to the host and
// listen for unsolicited {event:"status"} messages. When NO bridge is present (the
// page opened standalone as file:// in a plain browser) everything falls back to the
// existing mock behaviour, so index.html still works on its own.
const Bridge = (() => {
  const hasWebView = typeof window !== 'undefined' && window.chrome && window.chrome.webview;
  // Loopback host: served over http(s) from 127.0.0.1/localhost (not file://).
  const isLoopback =
    typeof location !== 'undefined' &&
    /^https?:$/.test(location.protocol) &&
    /^(127\.0\.0\.1|localhost)$/.test(location.hostname);
  const available = !!hasWebView || isLoopback;

  const pending = new Map(); // correlationId -> { resolve, reject }
  let statusCb = null;
  let seq = 0;

  function newId() { return `c${Date.now()}_${seq++}`; }

  function handleInbound(text) {
    let msg;
    try { msg = JSON.parse(text); } catch (_) { return; }
    if (msg && msg.event === 'status') { if (statusCb) statusCb(msg.payload); return; }
    if (msg && msg.correlationId && pending.has(msg.correlationId)) {
      const p = pending.get(msg.correlationId);
      pending.delete(msg.correlationId);
      if (msg.ok) p.resolve(msg.payload ? safeParse(msg.payload) : null);
      else p.reject(new Error(msg.error || 'bridge error'));
    }
  }

  function safeParse(s) { try { return JSON.parse(s); } catch (_) { return s; } }

  function send(obj) {
    if (hasWebView) { window.chrome.webview.postMessage(JSON.stringify(obj)); return; }
    fetch('/__bridge/send', { method: 'POST', body: JSON.stringify(obj) }).catch(() => {});
  }

  // For the loopback host, long-poll for native->web messages. WebView2 instead
  // raises window.chrome.webview 'message' events, wired below.
  async function pollLoop() {
    // eslint-disable-next-line no-constant-condition
    while (true) {
      try {
        const res = await fetch('/__bridge/poll');
        if (res.status === 200) handleInbound(await res.text());
      } catch (_) {
        await new Promise((r) => setTimeout(r, 1000));
      }
    }
  }

  function start() {
    if (!available) return;
    if (hasWebView) {
      window.chrome.webview.addEventListener('message', (e) => {
        const data = typeof e.data === 'string' ? e.data : JSON.stringify(e.data);
        handleInbound(data);
      });
    } else {
      pollLoop();
    }
  }

  function call(action, payload) {
    if (!available) return Promise.reject(new Error('no bridge'));
    const correlationId = newId();
    return new Promise((resolve, reject) => {
      pending.set(correlationId, { resolve, reject });
      send({ action, correlationId, payload: payload == null ? null : String(payload) });
      setTimeout(() => {
        if (pending.has(correlationId)) { pending.delete(correlationId); reject(new Error('bridge timeout')); }
      }, 120000);
    });
  }

  return {
    get available() { return available; },
    start,
    call,
    onStatus(cb) { statusCb = cb; },
  };
})();

// setIcon — replace an element's content with a controlled icon SVG only.
function setIcon(el, name, size) {
  el.innerHTML = icon(name, size ? { size } : {}); // safe: name maps to static markup
}

// ---------------- Mock data ----------------
const MOCK = {
  status: 'connected', // connected | unpaired | offline
  lastSyncMinutes: 2,
  nextSyncSeconds: 7 * 60 + 43, // 07:43
  device: { name: 'Ezequiel Mac', paired: true, key: '••••3f2a' },
  activity: [
    { time: '14:32', title: 'Team standup', action: 'updated' },
    { time: '14:31', title: 'Dentist appointment', action: 'created' },
    { time: '14:30', title: 'Old project review', action: 'deleted' },
    { time: '14:28', title: 'Lunch with Marco', action: 'skipped' },
    { time: '14:28', title: 'Quarterly planning', action: 'created' },
    { time: '11:05', title: 'Gym session', action: 'updated' },
  ],
};

const ACTION_ICON = { created: 'plus', updated: 'sync', deleted: 'close', skipped: 'chevron' };

// ---------------- Aurora reactivity ----------------
function setAurora(state) {
  document.body.dataset.aurora = state;
}

// ARIA live announcements for sync state
function announce(msg) {
  const live = $('#liveRegion');
  if (live) live.textContent = msg;
}

// ---------------- Theme ----------------
const THEME_KEY = 'syncmaster.theme';
function applyTheme(mode) {
  const root = document.documentElement;
  if (mode === 'auto') root.removeAttribute('data-theme');
  else root.setAttribute('data-theme', mode);
  try { localStorage.setItem(THEME_KEY, mode); } catch (_) {}
  updateThemeSeg(mode);
}
function currentTheme() {
  try { return localStorage.getItem(THEME_KEY) || 'auto'; } catch (_) { return 'auto'; }
}
function updateThemeSeg(mode) {
  const seg = $('#appearanceSeg');
  if (!seg) return;
  const order = ['auto', 'light', 'dark'];
  const i = Math.max(0, order.indexOf(mode));
  $('.seg__thumb', seg).style.transform = `translateX(${i * 100}%)`;
  $$('.seg__opt', seg).forEach((o) => o.setAttribute('aria-selected', String(o.dataset.theme === mode)));
}

// ---------------- View switching ----------------
function showView(name) {
  $$('.view').forEach((v) => {
    const match = v.dataset.view === name;
    v.hidden = !match;
    if (match) {
      v.classList.remove('enter');
      void v.offsetWidth; // reflow to retrigger the stagger entrance
      v.classList.add('enter');
    }
  });
  $$('.nav__item').forEach((n) => n.classList.toggle('is-active', n.dataset.go === name));
}

// ---------------- Countdown ----------------
let countdown = MOCK.nextSyncSeconds;
function fmt(sec) {
  const m = Math.floor(sec / 60);
  const s = sec % 60;
  return `${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}`;
}
function tickCountdown() {
  const el = $('#nextSync');
  if (!el) return;
  if (MOCK.status !== 'connected') { el.textContent = '—'; return; }
  countdown = countdown <= 0 ? MOCK.nextSyncSeconds : countdown - 1;
  el.textContent = fmt(countdown);
}

// ---------------- Dashboard render ----------------
function renderDashboard() {
  const orb = $('#orb');
  const statusLabel = $('#statusLabel');
  const sub = $('#statusSub');
  const next = $('#nextWrap');
  const btn = $('#syncBtn');
  const btnLabel = $('.label', btn);

  if (MOCK.status === 'unpaired') {
    orb.dataset.state = 'warn';
    setIcon(orb.querySelector('.orb__icon'), 'link', 40);
    statusLabel.textContent = 'Not paired';
    sub.textContent = 'Pair this device to begin syncing';
    next.style.visibility = 'hidden';
    btnLabel.textContent = 'Pair this device';
    btn.dataset.act = 'pair';
    btn.disabled = false;
    setAurora('idle');
  } else if (MOCK.status === 'offline') {
    orb.dataset.state = 'offline';
    setIcon(orb.querySelector('.orb__icon'), 'wifi', 40);
    statusLabel.textContent = 'Offline — will retry';
    sub.textContent = 'Reconnecting automatically';
    next.style.visibility = 'hidden';
    btnLabel.textContent = 'Sync now';
    btn.dataset.act = 'sync';
    btn.disabled = true;
    setAurora('paused');
  } else {
    orb.dataset.state = 'ok';
    setIcon(orb.querySelector('.orb__icon'), 'check', 40);
    statusLabel.textContent = 'Connected';
    sub.textContent = `Last sync ${MOCK.lastSyncMinutes} min ago`;
    next.style.visibility = 'visible';
    btnLabel.textContent = 'Sync now';
    btn.dataset.act = 'sync';
    btn.disabled = false;
    setAurora('idle');
  }

  // activity list — build with safe DOM nodes; only icon SVG via setIcon.
  const list = $('#activityList');
  list.replaceChildren();
  MOCK.activity.forEach((a) => {
    const row = document.createElement('div');
    row.className = 'activity';
    row.tabIndex = 0;

    const time = document.createElement('span');
    time.className = 'activity__time tnum';
    time.textContent = a.time;

    const title = document.createElement('span');
    title.className = 'activity__title';
    title.textContent = a.title;

    const pill = document.createElement('span');
    pill.className = `pill pill--${a.action}`;
    const pillIcon = document.createElement('span');
    pillIcon.style.display = 'inline-flex';
    setIcon(pillIcon, ACTION_ICON[a.action], 13);
    const pillText = document.createElement('span');
    pillText.textContent = a.action.charAt(0).toUpperCase() + a.action.slice(1);
    pill.append(pillIcon, pillText);

    row.append(time, title, pill);
    list.appendChild(row);
  });
}

// ---------------- Native status mapping ----------------
// Translate a native AppStatus into the mock view-model the renderer already drives,
// so the existing dashboard rendering is reused unchanged.
function applyNativeStatus(s) {
  if (!s) return;
  if (!s.paired) MOCK.status = 'unpaired';
  else if (s.status === 'Error') MOCK.status = 'offline';
  else MOCK.status = 'connected';

  if (s.lastSyncUtc) {
    const mins = Math.max(0, Math.round((Date.now() - new Date(s.lastSyncUtc).getTime()) / 60000));
    MOCK.lastSyncMinutes = mins;
  }
  MOCK.device.paired = !!s.paired;
  renderDashboard();
}

// ---------------- Sync state machine ----------------
let syncing = false;
function runSync({ fail = false } = {}) {
  const btn = $('#syncBtn');
  if (syncing || btn.disabled) return;
  if (btn.dataset.act === 'pair') {
    if (Bridge.available) { Bridge.call('pair').catch(() => {}); }
    showView('pairing'); resetPairing(); return;
  }

  // When a native bridge is present, ask the host to run a real cycle and reflect the
  // result; otherwise fall through to the mock progress animation below.
  if (Bridge.available && !fail) {
    Bridge.call('syncNow')
      .then(() => Bridge.call('getStatus'))
      .then((s) => applyNativeStatus(s))
      .catch(() => {});
  }

  syncing = true;
  btn.classList.remove('is-done', 'shake');
  btn.classList.add('is-busy', 'is-sweep');
  setTimeout(() => btn.classList.remove('is-sweep'), 420);
  setAurora('syncing');
  announce('Sync started');

  const total = 20;
  let done = 0;
  const count = $('.count', btn);
  count.textContent = `0/${total}`;

  const step = setInterval(() => {
    done += Math.ceil(Math.random() * 3);
    if (done >= total) done = total;
    count.textContent = `${done}/${total}`;
    if (done >= total) {
      clearInterval(step);
      finishSync(fail);
    }
  }, 220);
}

function finishSync(fail) {
  const btn = $('#syncBtn');
  btn.classList.remove('is-busy');
  if (fail) {
    btn.classList.add('shake');
    setAurora('error');
    announce('Sync failed. Will retry.');
    showToast('Sync failed — will retry', 'alert');
    setTimeout(() => { btn.classList.remove('shake'); setAurora('idle'); syncing = false; }, 2500);
  } else {
    btn.classList.add('is-done');
    setAurora('success');
    const label = $('.label', btn);
    const prev = label.textContent;
    label.textContent = 'Synced 20 events';
    announce('Sync complete. 20 events synchronised.');
    MOCK.lastSyncMinutes = 0;
    countdown = MOCK.nextSyncSeconds;
    $('#statusSub').textContent = 'Last sync just now';
    setTimeout(() => {
      btn.classList.remove('is-done');
      label.textContent = prev;
      setAurora('idle');
      syncing = false;
    }, 2500);
  }
}

// ---------------- Toast ----------------
let toastTimer;
function showToast(msg, kind = 'check') {
  const t = $('#toast');
  if (!t) return;
  setIcon(t, kind, 15);
  const span = document.createElement('span');
  span.textContent = msg;
  t.appendChild(span);
  t.classList.add('is-show');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => t.classList.remove('is-show'), 2200);
}

// ---------------- Config autosave + validation ----------------
function wireConfig() {
  const seg = $('#appearanceSeg');
  if (seg) {
    $$('.seg__opt', seg).forEach((opt, i) => {
      opt.addEventListener('click', () => {
        $('.seg__thumb', seg).style.transform = `translateX(${i * 100}%)`;
        $$('.seg__opt', seg).forEach((o) => o.setAttribute('aria-selected', String(o === opt)));
        applyTheme(opt.dataset.theme);
        pulseSaved();
      });
    });
  }

  // toggles
  $$('.toggle').forEach((tg) => {
    tg.addEventListener('click', () => {
      tg.setAttribute('aria-checked', String(tg.getAttribute('aria-checked') !== 'true'));
      pulseSaved();
    });
    tg.addEventListener('keydown', (e) => {
      if (e.key === ' ' || e.key === 'Enter') { e.preventDefault(); tg.click(); }
    });
  });

  // selects
  $$('#view-config .select').forEach((s) => s.addEventListener('change', () => {
    if (s.value === '__new') { showToast('New calendar created', 'plus'); s.selectedIndex = 0; }
    pulseSaved();
  }));

  // sync window slider
  const slider = $('#windowSlider');
  if (slider) {
    const out = $('#windowVal');
    const sync = () => {
      const v = Number(slider.value);
      out.textContent = `+${v} days`;
      slider.style.setProperty('--fill', `${(v / Number(slider.max)) * 100}%`);
    };
    slider.addEventListener('input', sync);
    slider.addEventListener('change', pulseSaved);
    sync();
  }

  // device name validation
  const nameField = $('#deviceNameField');
  const nameInput = $('#deviceName');
  if (nameInput) {
    nameInput.addEventListener('input', () => {
      nameField.classList.toggle('is-invalid', nameInput.value.trim() === '');
    });
    nameInput.addEventListener('change', () => {
      if (nameInput.value.trim() !== '') pulseSaved();
    });
  }

  // inline unpair confirm (never a blocking dialog)
  const unpairBtn = $('#unpairBtn');
  const confirmBox = $('#unpairConfirm');
  if (unpairBtn) {
    unpairBtn.addEventListener('click', () => { confirmBox.hidden = false; unpairBtn.hidden = true; });
    $('#unpairCancel').addEventListener('click', () => { confirmBox.hidden = true; unpairBtn.hidden = false; });
    $('#unpairYes').addEventListener('click', () => {
      confirmBox.hidden = true; unpairBtn.hidden = false;
      MOCK.status = 'unpaired'; MOCK.device.paired = false;
      showToast('Device unpaired', 'link');
      renderDashboard();
    });
  }
}

let savedTimer;
function pulseSaved() {
  // Persist the current config to the host when running inside the desktop app.
  pushConfigToHost();
  const t = $('#savedToast');
  if (!t) return;
  setIcon(t, 'check', 15);
  const span = document.createElement('span');
  span.textContent = 'Saved';
  t.appendChild(span);
  t.classList.add('is-show');
  clearTimeout(savedTimer);
  savedTimer = setTimeout(() => t.classList.remove('is-show'), 1400);
}

// ---------------- Pairing flow ----------------
let pairTimers = [];
function clearPairTimers() { pairTimers.forEach(clearTimeout); pairTimers = []; }

function gotoPairStep(n) {
  $$('.pair__step').forEach((s) => s.classList.toggle('is-current', Number(s.dataset.step) === n));
  $$('.stepper__node').forEach((node) => {
    const k = Number(node.dataset.node);
    node.classList.toggle('is-active', k === n);
    node.classList.toggle('is-done', k < n);
    if (k < n) setIcon(node, 'check', 18);
    else node.textContent = String(k);
  });
  $$('.stepper__link').forEach((link) => {
    const k = Number(link.dataset.link);
    link.style.setProperty('--progress', k < n ? '100%' : '0%');
  });

  if (n === 2) {
    clearPairTimers();
    pairTimers.push(setTimeout(() => gotoPairStep(3), 2600)); // simulate browser approval
  }
  if (n === 3) {
    setAurora('success');
    announce('Device paired successfully.');
    pairTimers.push(setTimeout(() => setAurora('idle'), 2600));
  }
}

function resetPairing() {
  clearPairTimers();
  $('#pairName').value = '';
  $('#pairNameField').classList.remove('is-invalid');
  gotoPairStep(1);
}

function wirePairing() {
  $('#pairContinue').addEventListener('click', () => {
    const name = $('#pairName').value.trim();
    if (!name) { $('#pairNameField').classList.add('is-invalid'); return; }
    MOCK.device.name = name;
    gotoPairStep(2);
  });
  $('#pairName').addEventListener('input', () => $('#pairNameField').classList.remove('is-invalid'));
  $('#pairCopy').addEventListener('click', async () => {
    try { await navigator.clipboard.writeText('https://outlook.office.com/pair/9F2A-3D7C'); } catch (_) {}
    showToast('Link copied', 'copy');
  });
  $('#pairDone').addEventListener('click', () => {
    MOCK.status = 'connected'; MOCK.device.paired = true;
    const sel = $('#stateDemo'); if (sel) sel.value = 'connected';
    renderDashboard();
    showView('dashboard');
  });
  $('#pairRestart').addEventListener('click', resetPairing);
}

// ---------------- Boot ----------------
// Collect the current config from the panel inputs into a settings object the host
// understands (matches SyncMaster.App.Configuration.AppSettings JSON shape).
function collectConfig() {
  const deviceName = $('#deviceName') ? $('#deviceName').value.trim() : '';
  const slider = $('#windowSlider');
  const cfg = {};
  if (deviceName) cfg.deviceName = deviceName;
  if (slider) cfg.syncWindowDays = Number(slider.value);
  return cfg;
}

function pushConfigToHost() {
  if (!Bridge.available) return;
  Bridge.call('saveConfig', JSON.stringify(collectConfig())).catch(() => {});
}

function boot() {
  hydrateIcons();
  applyTheme(currentTheme());

  // Wire the native bridge first so initial status (if any) drives the dashboard.
  if (Bridge.available) {
    Bridge.start();
    Bridge.onStatus((s) => applyNativeStatus(s));
    Bridge.call('getStatus').then((s) => applyNativeStatus(s)).catch(() => {});
  }

  // nav + any [data-go] navigation control
  $$('[data-go]').forEach((b) => b.addEventListener('click', () => {
    const v = b.dataset.go;
    showView(v);
    if (v === 'pairing') resetPairing();
  }));

  // titlebar theme toggle cycles auto -> light -> dark
  $('#themeToggle').addEventListener('click', () => {
    const order = ['auto', 'light', 'dark'];
    const nextMode = order[(order.indexOf(currentTheme()) + 1) % 3];
    applyTheme(nextMode);
    showToast(`Theme: ${nextMode}`, 'gear');
  });

  // sync button + error demo
  $('#syncBtn').addEventListener('click', () => runSync());
  $('#simErrorBtn').addEventListener('click', () => runSync({ fail: true }));

  // dashboard state demo selector (connected / unpaired / offline)
  const stateSel = $('#stateDemo');
  if (stateSel) stateSel.addEventListener('change', () => {
    MOCK.status = stateSel.value;
    renderDashboard();
  });

  wireConfig();
  wirePairing();
  renderDashboard();
  showView('dashboard');

  setInterval(tickCountdown, 1000);
  tickCountdown();
}

if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot);
else boot();
