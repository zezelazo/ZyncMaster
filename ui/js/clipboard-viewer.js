// clipboard-viewer.js — the clipboard paste overlay (ui/clipboard-viewer.html).
//
// This page is its own frameless window the desktop App pops up on the global viewer hotkey. It
// loads the SAME bundled ui/** assets as the main app (tokens / glass / clipboard css). The C#
// stream owns the window + how it navigates here; this module owns the page contents and behaviour.
//
// It speaks the SHARED BRIDGE CONTRACT directly (it does NOT import app.js, which would boot the
// whole dashboard). The transport here mirrors app.js's Bridge: native WebView2 postMessage, or the
// loopback long-poll host. Actions used: getClipboardDevices, getClipboardHistory,
// pasteClipboardEntry, closeClipboardViewer. Push consumed: the "clipboard:item" event.

// ===================== Bridge (viewer-scoped) =====================
// Mirrors the relevant slice of app.js's Bridge so the viewer can call actions and receive the
// "clipboard:item" push without dragging the whole app module in. native = WebView2; loopback =
// the App's 127.0.0.1 host exposing /__bridge. Anything else is inert (the overlay is a desktop
// concern), which also keeps it harmless when opened standalone for design review.
const Bridge = (() => {
  const hasWebView = typeof window !== 'undefined' && window.chrome && window.chrome.webview;
  const isHttp = typeof location !== 'undefined' && /^https?:$/.test(location.protocol);
  const couldBeLoopback = isHttp && /^(127\.0\.0\.1|localhost)$/.test((location && location.hostname) || '');
  // native if embedded in WebView2; loopback if served from the App's local host; else inert.
  let mode = hasWebView ? 'native' : (couldBeLoopback ? 'loopback' : 'inert');

  const pending = new Map();
  const eventCbs = new Map();
  let seq = 0;
  const newId = () => `v${Date.now()}_${seq++}`;
  const safeParse = (s) => { try { return JSON.parse(s); } catch (_) { return s; } };

  function dispatch(name, payload) {
    const set = eventCbs.get(name);
    if (!set) return;
    set.forEach((cb) => { try { cb(payload); } catch (_) { /* a faulty listener must not break the channel */ } });
  }

  function handleInbound(text) {
    let msg;
    try { msg = JSON.parse(text); } catch (_) { return; }
    // Named push (e.g. "clipboard:item"). The host serializes payload as an OBJECT (same shape as
    // the status push), so pass it through; only re-parse when it arrived as a JSON string.
    if (msg && msg.event) { dispatch(msg.event, typeof msg.payload === 'string' ? safeParse(msg.payload) : msg.payload); return; }
    if (msg && msg.correlationId && pending.has(msg.correlationId)) {
      const p = pending.get(msg.correlationId);
      pending.delete(msg.correlationId);
      if (msg.ok) p.resolve(msg.payload ? safeParse(msg.payload) : null);
      else p.reject(new Error(msg.error || 'bridge error'));
    }
  }

  function send(obj) {
    if (mode === 'native') { window.chrome.webview.postMessage(JSON.stringify(obj)); return; }
    if (mode === 'loopback') { fetch('/__bridge/send', { method: 'POST', body: JSON.stringify(obj) }).catch(() => {}); }
  }

  async function pollLoop() {
    // eslint-disable-next-line no-constant-condition
    while (true) {
      try {
        const res = await fetch('/__bridge/poll');
        if (res.status === 200) { handleInbound(await res.text()); continue; }
        await new Promise((r) => setTimeout(r, 500));
      } catch (_) {
        await new Promise((r) => setTimeout(r, 1000));
      }
    }
  }

  function start() {
    if (mode === 'native') {
      window.chrome.webview.addEventListener('message', (e) => {
        const data = typeof e.data === 'string' ? e.data : JSON.stringify(e.data);
        handleInbound(data);
      });
    } else if (mode === 'loopback') {
      pollLoop();
    }
  }

  function call(action, payload, timeoutMs) {
    if (mode === 'inert') return Promise.reject(new Error('no bridge'));
    const correlationId = newId();
    return new Promise((resolve, reject) => {
      pending.set(correlationId, { resolve, reject });
      send({ action, correlationId, payload: payload == null ? null : String(payload) });
      setTimeout(() => {
        if (pending.has(correlationId)) { pending.delete(correlationId); reject(new Error('bridge timeout')); }
      }, timeoutMs || 30000);
    });
  }

  return {
    get available() { return mode !== 'inert'; },
    get mode() { return mode; },
    start, call,
    onEvent(name, cb) {
      let set = eventCbs.get(name);
      if (!set) { set = new Set(); eventCbs.set(name, set); }
      set.add(cb);
      return () => { const s = eventCbs.get(name); if (s) s.delete(cb); };
    },
  };
})();

// ===================== tiny DOM helper =====================
function el(tag, props = {}, ...children) {
  const node = document.createElement(tag);
  if (props) {
    for (const [k, v] of Object.entries(props)) {
      if (v == null) continue;
      if (k === 'class') node.className = v;
      else if (k === 'text') node.textContent = v;
      else if (k === 'html') node.innerHTML = v; // author-controlled icon markup only
      else if (k === 'style') node.setAttribute('style', v);
      else if (k.startsWith('on') && typeof v === 'function') node.addEventListener(k.slice(2), v);
      else if (v === true) node.setAttribute(k, '');
      else if (v === false) { /* skip */ }
      else node.setAttribute(k, v);
    }
  }
  for (const c of children.flat()) {
    if (c == null || c === false) continue;
    node.append(typeof c === 'string' || typeof c === 'number' ? document.createTextNode(String(c)) : c);
  }
  return node;
}

// ===================== pure helpers (exported for unit tests) =====================
const GROUP_SIZE = 10;

// visibleOf(state) — the items the given state would render: filtered by the active rich filter, or
// the raw list in mini (mini ignores the filter). Mirrors visibleItems() but is pure over its arg so
// applyNewItem can re-anchor selection against the SAME list state.sel indexes.
function visibleOf(state) {
  const items = Array.isArray(state.items) ? state.items : [];
  if (state.density === 'mini') return items;
  return items.filter((it) => filterMatch(it, state.filter));
}

// applyNewItem(state, item) — prepend a freshly-arrived history item, keeping selection stable.
// Pure: returns a NEW state object, never mutates the input (so it is trivially testable). The
// selection is anchored to the currently-selected ITEM (by id) so a prepend that shifts indices
// does not move the highlight; if nothing was selected we keep index 0 (the new item becomes
// selected, which is the natural "newest" behaviour). A duplicate id is de-duplicated (the incoming
// copy wins and moves to the top).
//
// state.sel is an index into the VISIBLE (filtered/density) list everywhere else in the viewer, so we
// anchor and re-map against the visible list — not the raw items array. With an active rich filter the
// raw and visible lists diverge, and anchoring against the raw list would read the wrong id and set a
// sel the render mis-applies to the filtered list (moving/losing the highlight).
function applyNewItem(state, item) {
  if (!item || item.id == null) return state;
  const prevItems = Array.isArray(state.items) ? state.items : [];
  const prevVisible = visibleOf(state);
  const selectedId = prevVisible.length ? (prevVisible[state.sel] && prevVisible[state.sel].id) : null;
  const deduped = prevItems.filter((it) => it.id !== item.id);
  const items = [item, ...deduped];
  // Re-anchor selection to the same item within the NEW visible list; otherwise keep the top (0).
  let sel = 0;
  if (selectedId != null) {
    const nextVisible = visibleOf({ ...state, items });
    const idx = nextVisible.findIndex((it) => it.id === selectedId);
    sel = idx >= 0 ? idx : 0;
  }
  return { ...state, items, sel };
}

// formatSize(bytes) — humane file size, or '' when unknown.
function formatSize(bytes) {
  if (bytes == null || isNaN(bytes)) return '';
  const b = Number(bytes);
  if (b < 1024) return `${b} B`;
  if (b < 1024 * 1024) return `${(b / 1024).toFixed(b < 10 * 1024 ? 1 : 0)} KB`;
  if (b < 1024 * 1024 * 1024) return `${(b / (1024 * 1024)).toFixed(1)} MB`;
  return `${(b / (1024 * 1024 * 1024)).toFixed(1)} GB`;
}

// relTime(iso) — short relative time ("12s", "3 min", "1 h", "2 d"), or '' when unparseable.
function relTime(iso) {
  if (!iso) return '';
  const t = Date.parse(iso);
  if (isNaN(t)) return '';
  const s = Math.max(0, Math.floor((Date.now() - t) / 1000));
  if (s < 60) return `${s}s`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m} min`;
  const h = Math.floor(m / 60);
  if (h < 24) return `${h} h`;
  return `${Math.floor(h / 24)} d`;
}

// itemType(item) — normalised 'text' | 'image' | 'file'. The contract only carries Text/Image; a
// future File type degrades to the terra "file" avatar. Anything unknown reads as text.
function itemType(item) {
  const t = (item && item.type ? String(item.type) : '').toLowerCase();
  if (t === 'image') return 'image';
  if (t === 'file') return 'file';
  return 'text';
}

// titleOf(item) — the one-line title for a row. Text shows its (already-decrypted) plaintext,
// collapsed to a single line; image/file show their label or a typed placeholder.
function titleOf(item) {
  const type = itemType(item);
  if (type === 'text') return (item.text || '').replace(/\s+/g, ' ').trim() || '(empty)';
  if (item.text) return item.text; // some images/files carry a caption/filename in text
  return type === 'image' ? 'Image' : 'File';
}

// filterMatch(item, filter) — 'all' | 'text' | 'image' | 'file'.
function filterMatch(item, filter) {
  if (filter === 'all') return true;
  return itemType(item) === filter;
}

// ===================== state =====================
const state = {
  items: [],
  filter: 'all',          // all | text | image | file (rich only)
  density: 'rich',        // rich | mini
  showHints: true,        // rich only
  sel: 0,
  thisDeviceId: null,
};

const FILTERS = [['all', 'All'], ['text', 'Text'], ['image', 'Img'], ['file', 'File']];

// visibleItems() — the items after the active filter (rich). Mini ignores the filter entirely.
function visibleItems() {
  return visibleOf(state);
}

// ===================== render =====================
const root = () => document.getElementById('cbRoot');

function rowEl(item, index, selected) {
  const type = itemType(item);
  const isImg = type === 'image';
  const cls = ['cb-row'];
  if (isImg) cls.push('cb-row--img');
  if (selected) cls.push('is-selected');

  const av = el('div', {
    class: 'cb-av' + (type === 'file' ? ' cb-av--file' : type === 'image' ? ' cb-av--img' : ''),
    'aria-hidden': 'true',
  }, type === 'text' ? 'T' : type === 'file' ? 'F' : '');

  const mini = state.density === 'mini';

  // Title. In mini, files show their size inline (no separate meta line).
  const titleChildren = [titleOf(item)];
  if (mini && (type === 'file' || type === 'image')) {
    const sz = formatSize(item.sizeBytes);
    if (sz) titleChildren.push(el('span', { class: 'cb-title__size', text: ` · ${sz}` }));
  }
  const title = el('div', { class: 'cb-title' }, ...titleChildren);

  const body = el('div', { class: 'cb-body' }, title);

  // Meta line (rich only): time ALWAYS first; size only for image/file; device last.
  if (!mini) {
    const metaParts = [];
    const time = relTime(item.createdUtc);
    if (time) metaParts.push(el('span', { text: time }));
    if (type === 'image' || type === 'file') {
      const sz = formatSize(item.sizeBytes);
      if (sz) { metaParts.push(el('span', { class: 'cb-meta__sep', text: '·' })); metaParts.push(el('span', { text: sz })); }
    }
    if (item.originDeviceName) {
      if (metaParts.length) metaParts.push(el('span', { class: 'cb-meta__sep', text: '·' }));
      metaParts.push(el('span', { class: 'cb-meta__from', text: item.originDeviceName }));
    }
    if (metaParts.length) body.append(el('div', { class: 'cb-meta' }, ...metaParts));
  }

  // Head wrapper (needed so an expanded image keeps avatar+title+meta on one line above the preview).
  const head = el('div', { class: 'cb-row__head' }, av, body);
  const row = el('div', { class: cls.join(' '), 'data-index': String(index), role: 'option', 'aria-selected': String(selected) }, head);

  // Selected image (rich) expands to a large preview keeping aspect ratio. A typed tile + size is
  // shown when no preview data URI is available (DIB->PNG is a known best-effort follow-up).
  if (isImg && selected && !mini) {
    if (item.imagePreviewDataUri) {
      row.append(el('img', { class: 'cb-preview', src: item.imagePreviewDataUri, alt: titleOf(item) }));
    } else {
      const sz = formatSize(item.sizeBytes);
      row.append(el('div', { class: 'cb-preview cb-preview--tile' }, `Image${sz ? ` · ${sz}` : ''}`));
    }
  }

  // Mouse: single click selects; double-click pastes (consistent choice, kept everywhere).
  row.addEventListener('click', () => { selectIndex(index); });
  row.addEventListener('dblclick', () => { selectIndex(index); pasteSelected(); });
  return row;
}

function render() {
  const host = root();
  if (!host) return;
  host.replaceChildren();

  const mini = state.density === 'mini';
  const card = el('div', { class: 'cb-viewer' + (mini ? ' cb-viewer--mini' : ''), role: 'listbox', 'aria-label': 'Clipboard history', tabindex: '0' });

  // Header.
  const count = state.items.length;
  card.append(el('div', { class: 'cb-top' },
    el('span', { class: 'cb-top__dot', 'aria-hidden': 'true' }),
    el('span', { text: `Clipboard · ${count} ${count === 1 ? 'item' : 'items'}` })));

  // Filters (rich only).
  if (!mini) {
    const seg = el('div', { class: 'cb-filter' });
    FILTERS.forEach(([val, label]) => {
      seg.append(el('button', { class: 'cb-filter__item', 'aria-pressed': String(state.filter === val), text: label,
        onclick: () => { state.filter = val; state.sel = 0; render(); } }));
    });
    card.append(seg);
  }

  const list = el('div', { class: 'cb-list' });
  const items = visibleItems();

  if (!items.length) {
    list.append(el('div', { class: 'cb-empty', text: count === 0 ? 'Nothing copied yet' : 'No items match this filter' }));
  } else {
    items.forEach((item, i) => {
      // Group separators every GROUP_SIZE: a labelled header in rich, a subtle centred indicator
      // in mini (NOT a "11-20" label, NOT an arrow/date).
      if (i > 0 && i % GROUP_SIZE === 0) {
        if (mini) {
          list.append(el('div', { class: 'cb-group-ind', 'aria-hidden': 'true' },
            el('span', { class: 'cb-group-ind__line' }),
            el('span', { class: 'cb-group-ind__mark', text: '•' }),
            el('span', { class: 'cb-group-ind__line' })));
        } else {
          list.append(el('div', { class: 'cb-group', text: `${i + 1} – ${Math.min(i + GROUP_SIZE, items.length)}` }));
        }
      } else if (i === 0 && !mini) {
        list.append(el('div', { class: 'cb-group', text: `1 – ${Math.min(GROUP_SIZE, items.length)}` }));
      }
      list.append(rowEl(item, i, i === state.sel));
    });
  }
  card.append(list);

  // Help bar: rich + showHints only (mini never shows it).
  if (!mini && state.showHints) {
    card.append(el('div', { class: 'cb-help', html:
      '<b>↑↓</b> move · <b>PgUp/Dn</b> group · <b>←→</b> filter · <b>↵</b> paste · <b>esc</b> close' }));
  }

  host.append(card);
  // Keep the selected row in view after a render.
  scrollSelectedIntoView();
}

// ===================== navigation =====================
function clampSel(n) {
  const max = Math.max(0, visibleItems().length - 1);
  return Math.min(Math.max(0, n), max);
}

function selectIndex(n) {
  const next = clampSel(n);
  if (next === state.sel) { scrollSelectedIntoView(); return; }
  state.sel = next;
  // Update selection in place (cheaper than a full render, and keeps an expanded image smooth).
  render();
}

function moveSel(delta) { selectIndex(state.sel + delta); }

// Jump selection to the first item of the previous/next group of GROUP_SIZE.
function jumpGroup(dir) {
  const items = visibleItems();
  if (!items.length) return;
  const currentGroup = Math.floor(state.sel / GROUP_SIZE);
  const targetGroup = currentGroup + (dir < 0 ? -1 : 1);
  const idx = targetGroup * GROUP_SIZE;
  selectIndex(idx);
}

// Change the active filter (rich only) by stepping left/right through FILTERS.
function stepFilter(dir) {
  if (state.density === 'mini') return;
  const idx = FILTERS.findIndex(([v]) => v === state.filter);
  const next = FILTERS[(idx + dir + FILTERS.length) % FILTERS.length][0];
  if (next === state.filter) return;
  state.filter = next;
  state.sel = 0;
  render();
}

function scrollSelectedIntoView() {
  const host = root();
  if (!host) return;
  const sel = host.querySelector('.cb-row.is-selected');
  if (sel && sel.scrollIntoView) sel.scrollIntoView({ block: 'nearest' });
}

function pasteSelected() {
  const items = visibleItems();
  const item = items[state.sel];
  if (!item || item.id == null) { closeViewer(); return; }
  if (!Bridge.available) { closeViewer(); return; }
  Bridge.call('pasteClipboardEntry', String(item.id))
    .catch(() => {})
    .finally(() => closeViewer());
}

function closeViewer() {
  if (Bridge.available) Bridge.call('closeClipboardViewer').catch(() => {});
}

function onKeyDown(e) {
  switch (e.key) {
    case 'ArrowDown': e.preventDefault(); moveSel(1); break;
    case 'ArrowUp': e.preventDefault(); moveSel(-1); break;
    case 'PageDown': e.preventDefault(); jumpGroup(1); break;
    case 'PageUp': e.preventDefault(); jumpGroup(-1); break;
    case 'ArrowRight': e.preventDefault(); stepFilter(1); break;
    case 'ArrowLeft': e.preventDefault(); stepFilter(-1); break;
    case 'Enter': e.preventDefault(); pasteSelected(); break;
    case 'Escape': e.preventDefault(); closeViewer(); break;
    default: break;
  }
}

// ===================== boot =====================
function applyTheme() {
  // Mirror app.js's theme resolution so the overlay matches the dashboard. Default dark.
  let theme = 'dark';
  try { theme = localStorage.getItem('zyncmaster.theme') || 'auto'; } catch (_) { theme = 'auto'; }
  if (theme === 'auto') {
    theme = (window.matchMedia && window.matchMedia('(prefers-color-scheme: light)').matches) ? 'light' : 'dark';
  }
  document.documentElement.setAttribute('data-theme', theme);
}

async function boot() {
  applyTheme();
  document.body.classList.add('cb-viewer-body');
  Bridge.start();

  // Live updates: a new clipboard entry arriving over the server WebSocket is pushed App -> UI as
  // "clipboard:item" (per the shared contract). Prepend it (selection-stable) and re-render.
  Bridge.onEvent('clipboard:item', (item) => {
    Object.assign(state, applyNewItem(state, item));
    render();
  });

  // The E2E text key just arrived ("clipboard:key"): items that decrypted to the placeholder are
  // readable now, so re-hydrate the whole list (the per-item text was resolved host-side).
  Bridge.onEvent('clipboard:key', () => {
    if (!Bridge.available) return;
    Bridge.call('getClipboardHistory')
      .then((items) => { state.items = Array.isArray(items) ? items : []; render(); })
      .catch(() => {});
  });

  // First paint immediately (empty/loading), then hydrate from the bridge.
  render();

  if (Bridge.available) {
    // Density / showHints / thisDeviceId come from the THIS-device settings in getClipboardDevices.
    Bridge.call('getClipboardDevices')
      .then((d) => {
        if (d) {
          state.thisDeviceId = d.thisDeviceId || null;
          const me = (d.devices || []).find((x) => x.isThis) || (d.devices || []).find((x) => x.id === d.thisDeviceId);
          if (me && me.settings) {
            state.density = me.settings.density === 'mini' ? 'mini' : 'rich';
            state.showHints = me.settings.showHints !== false;
          }
        }
        render();
      })
      .catch(() => {});

    Bridge.call('getClipboardHistory')
      .then((items) => { state.items = Array.isArray(items) ? items : []; state.sel = 0; render(); })
      .catch(() => {});
  }

  // Global key handling for navigation + paste + close (works regardless of focus).
  document.addEventListener('keydown', onKeyDown);
  // Wheel scrolls the list natively; nothing extra needed since .cb-list is the scroll container.
}

if (typeof document !== 'undefined') {
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot);
  else boot();
}

// Export the pure pieces for unit tests (node ESM). Guarded so the browser <script type=module>
// load is unaffected.
export { applyNewItem, formatSize, relTime, itemType, titleOf, filterMatch };
