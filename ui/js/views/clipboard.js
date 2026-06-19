// views/clipboard.js — módulo Clipboard: la in-app clipboard VIEW (historial compartido
// con copy/delete) y la tab Settings consolidada (los 7 knobs de "this device" + el
// roster "Your devices"). Extraído VERBATIM de app.js — solo restyle de shell, lógica
// intacta. El viewer flotante (clipboard-viewer.html) NO se toca; esto es el panel
// in-dashboard. Registrado vía registerClipboardViews(ctx).
//
// Estado del historial: live.clipboardHistory (compartido vía ctx con home/palette).
// loadClipboardHistory vive AQUÍ pero se re-expone en ctx.loadClipboardHistory para que
// home lo siga consumiendo sin ciclo de import (clipboard registra ANTES que home).

export function registerClipboardViews(ctx) {
  const {
    el, icon, iconEl, Bridge, state, live, settings, softRepaint,
    announce, navigate, viewHeader, cfgSection, cfgRow,
    loadClipboardDevices, persistClipboardSettings, thisClipboardDevice,
    registry,
  } = ctx;

  // Devices accordion open/closed state (collapsed by default). Module-scoped so a softRepaint keeps
  // the user's expand state instead of snapping it shut on every data refresh.
  let clipboardDevicesOpen = false;

  // clipToggle(dev, key, opts) — a .toggle bound to dev.settings[key] that persists on flip. opts.sm
  // renders the compact per-device variant. opts.after runs after persisting (e.g. re-register the
  // hotkey). Optimistic: the local model flips immediately so the UI feels instant.
  function clipToggle(dev, key, opts = {}) {
    const get = () => !!(dev.settings && dev.settings[key]);
    const t = el('div', {
      class: opts.sm ? 'toggle toggle--sm' : 'toggle',
      role: 'switch', 'aria-checked': String(get()), tabindex: '0',
      'aria-label': opts.label || key,
    });
    const flip = () => {
      if (!dev.settings) dev.settings = {};
      const v = !get();
      dev.settings[key] = v;
      t.setAttribute('aria-checked', String(v));
      persistClipboardSettings(dev).then(() => { if (opts.after) opts.after(v); });
    };
    t.addEventListener('click', flip);
    t.addEventListener('keydown', (e) => { if (e.key === ' ' || e.key === 'Enter') { e.preventDefault(); flip(); } });
    return t;
  }

  // hotkeyChip(dev) — the editable viewer-hotkey chip. Click (or focus + Enter/Space) to capture the
  // next chord; the first key combo that includes at least one modifier is recorded, persisted, and
  // re-registered as the global hotkey (setClipboardHotkey). Esc cancels capture.
  function hotkeyChip(dev) {
    const chip = el('span', { class: 'cb-hotkey', role: 'button', tabindex: '0' });
    const labelOf = () => (dev.settings && dev.settings.viewerHotkey) || 'Not set';
    chip.textContent = labelOf();
    let capturing = false;

    const stop = () => {
      capturing = false;
      chip.classList.remove('is-capturing');
      chip.textContent = labelOf();
      document.removeEventListener('keydown', onKey, true);
    };
    const begin = () => {
      if (capturing) return;
      capturing = true;
      chip.classList.add('is-capturing');
      chip.textContent = 'Press keys…';
      document.addEventListener('keydown', onKey, true);
    };
    function onKey(e) {
      if (!capturing) return;
      e.preventDefault(); e.stopPropagation();
      if (e.key === 'Escape') { stop(); return; }
      // Ignore lone modifier presses; wait for a real key plus at least one modifier.
      if (['Control', 'Shift', 'Alt', 'Meta'].includes(e.key)) return;
      const parts = [];
      if (e.ctrlKey) parts.push('Ctrl');
      if (e.metaKey) parts.push('Win');
      if (e.altKey) parts.push('Alt');
      if (e.shiftKey) parts.push('Shift');
      if (!parts.length) return; // require a modifier so we don't bind a bare letter
      const key = e.key.length === 1 ? e.key.toUpperCase() : e.key;
      parts.push(key);
      const combo = parts.join('+');
      if (!dev.settings) dev.settings = {};
      dev.settings.viewerHotkey = combo;
      stop();
      // Persist the settings block AND re-register the global hotkey.
      persistClipboardSettings(dev);
      if (Bridge.available) Bridge.call('setClipboardHotkey', combo).catch(() => {});
    }
    chip.addEventListener('click', begin);
    chip.addEventListener('keydown', (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); begin(); } });
    chip.addEventListener('blur', () => { if (capturing) stop(); });
    return chip;
  }

  // densitySegmented(dev) — the Rich / Mini segmented for the viewer density. Persists on change.
  function densitySegmented(dev) {
    const seg = el('div', { class: 'segmented' });
    const buttons = [];
    const current = () => ((dev.settings && dev.settings.density) === 'mini' ? 'mini' : 'rich');
    [['rich', 'Rich'], ['mini', 'Mini']].forEach(([val, label]) => {
      const b = el('button', { class: 'segmented__item', 'aria-pressed': String(current() === val), text: label,
        onclick: () => {
          if (!dev.settings) dev.settings = {};
          dev.settings.density = val;
          buttons.forEach((btn) => btn.setAttribute('aria-pressed', String(btn.dataset.val === val)));
          persistClipboardSettings(dev);
        } });
      b.dataset.val = val;
      buttons.push(b);
      seg.append(b);
    });
    return seg;
  }

  // pasteOpacitySlider() — the App-local "paste panel opacity" control (0..100). The floating hotkey
  // paste panel's card is drawn as a dark fill at this opacity over the desktop (70 = 70% opaque /
  // 30% transparent). App-local (settings.json), NOT a per-device/server setting, so it is NOT routed
  // through persistClipboardSettings — it saves via setPastePanelOpacity. Applies ONLY to the hotkey
  // viewer; the in-dashboard clipboard view is unaffected. A native range + a live numeric readout;
  // the value persists on release (change), while input only updates the readout (no save spam).
  function pasteOpacitySlider() {
    const wrap = el('div', { class: 'cb-opacity' });
    const value = Math.max(0, Math.min(100, Number(settings.pastePanelOpacity)));
    const readout = el('span', { class: 'cb-opacity__val', text: `${value}%` });
    const range = el('input', {
      type: 'range', min: '0', max: '100', step: '5', value: String(value),
      class: 'cb-opacity__range', 'aria-label': 'Paste panel opacity',
    });
    range.addEventListener('input', () => { readout.textContent = `${range.value}%`; });
    range.addEventListener('change', () => {
      const v = Math.max(0, Math.min(100, parseInt(range.value, 10) || 0));
      settings.pastePanelOpacity = v;
      readout.textContent = `${v}%`;
      if (Bridge.available) Bridge.call('setPastePanelOpacity', String(v)).catch(() => {});
    });
    wrap.append(range, readout);
    return wrap;
  }

  // deviceRow(dev, thisId) — one row in the "Your devices" accordion. status dot + name (+ "this
  // device" badge) + last-seen line + compact send/receive toggles. Works for offline devices too.
  function deviceRow(dev, thisId) {
    const isThis = dev.isThis || dev.id === thisId;
    const dot = el('span', { class: 'status-dot', 'data-state': dev.online ? 'ok' : 'offline', 'aria-hidden': 'true' });
    const name = el('div', { class: 'cb-dev__name' },
      el('span', { class: 'cb-dev__name-text', text: dev.name || 'Device' }),
      isThis ? el('span', { class: 'cb-dev__badge', text: 'this device' }) : null);
    const sub = el('div', { class: 'cb-dev__sub', text: dev.online ? 'online' : 'offline' });
    const info = el('div', { class: 'cb-dev__info' }, name, sub);
    const toggles = el('div', { class: 'cb-dev__toggles' },
      el('div', { class: 'cb-dev__tg' }, el('small', { text: 'send' }), clipToggle(dev, 'send', { sm: true, label: `Send from ${dev.name}` })),
      el('div', { class: 'cb-dev__tg' }, el('small', { text: 'receive' }), clipToggle(dev, 'receive', { sm: true, label: `Receive on ${dev.name}` })));
    return el('div', { class: 'cb-dev' }, dot, info, toggles);
  }

  // ---------------- Screen: Clipboard view (shared history with copy/delete) ----------------
  // Desktop App only. The home "Clipboard" tile opens THIS screen (route 'clipboard'), not the settings
  // screen. It lists the shared clipboard history (getClipboardHistory) reusing the floating viewer's
  // item-row style (.cb-row / .cb-av / .cb-title / .cb-meta). Tapping an item reveals a full overlay over
  // it with two icon-only actions: COPY (copyClipboardEntry → sets the OS clipboard, nothing else) and a TRASH that
  // deletes WITHOUT confirmation (optimistic: the row leaves the list at once, then deleteClipboardEntry
  // propagates it to the server + the user's other devices). The live "clipboard:item" / "clipboard:
  // deleted" pushes keep the list fresh while this screen is open. Config stays in Settings → Clipboard.
  // Opening/closing a row's overlay and deleting a row are TARGETED DOM updates (no full repaint): a
  // repaint would rebuild the whole list just to toggle one overlay, visibly flashing on long histories.

  // The id of the item whose action overlay is currently open (only one at a time), or null.
  let clipboardOpenItemId = null;

  // Active type filter for the in-app clipboard view. Module-level so it survives repaints while the
  // App runs (same lifetime as clipboardDevicesOpen). Same values/labels as the floating viewer's.
  let clipboardFilter = 'all';
  const CLIPBOARD_FILTERS = [['all', 'All'], ['text', 'Text'], ['image', 'Img'], ['file', 'File']];

  // loadClipboardHistory — fetch the shared history once and cache it on live.clipboardHistory, then
  // softRepaint the clipboard screen. Once-only until reset (a forced refresh uses refreshClipboardHistory).
  function loadClipboardHistory(repaintView) {
    if (!Bridge.available || live.clipboardHistory !== null || live.clipboardHistoryLoading) return;
    live.clipboardHistoryLoading = true;
    Bridge.call('getClipboardHistory')
      .then((h) => { live.clipboardHistory = Array.isArray(h) ? h : []; })
      .catch(() => { live.clipboardHistory = []; })
      .finally(() => {
        live.clipboardHistoryLoading = false;
        if (repaintView && state.view === repaintView) softRepaint();
      });
  }

  // refreshClipboardHistory — FORCED re-fetch of the shared history, bypassing the once-only guard.
  // The "clipboard:key" push uses it directly, and applyClipboardHistoryItem falls back to it when a
  // targeted insert is not possible. Cheap: only runs while the clipboard view is visible and skips
  // while a fetch is already in flight.
  function refreshClipboardHistory() {
    if (!Bridge.available || live.clipboardHistoryLoading) return;
    if (state.view !== 'clipboard') return;
    live.clipboardHistoryLoading = true;
    Bridge.call('getClipboardHistory')
      .then((h) => { live.clipboardHistory = Array.isArray(h) ? h : []; })
      .catch(() => { /* keep the last good history on a transient failure */ })
      .finally(() => {
        live.clipboardHistoryLoading = false;
        if (state.view === 'clipboard') softRepaint();
      });
  }

  // applyClipboardHistoryItem — live "clipboard:item" push: a new entry from another device, or this
  // device's own just-published copy (the host mirrors local publishes because the server broadcast
  // excludes the origin device). With the clipboard view open over a loaded history this is a
  // TARGETED insert: the new row lands at the top of the list (reusing clipRowEl) with no full
  // repaint, deduped by id (a re-pushed id moves up instead of producing a twin row) and respecting
  // the active type filter. Anything structurally off — no usable item, no loaded cache, no list in
  // the DOM — falls back to the forced re-fetch. The cache is patched even while the view is closed
  // so reopening shows the item without another fetch; the home Clipboard tile shows a state chip,
  // not a count, so it needs no repaint here.
  function applyClipboardHistoryItem(item) {
    if (!item || item.id == null || !Array.isArray(live.clipboardHistory)) {
      refreshClipboardHistory();
      return;
    }
    live.clipboardHistory = [item, ...live.clipboardHistory.filter((x) => x && x.id !== item.id)];
    if (state.view !== 'clipboard') return;
    const list = clipScreenList();
    if (!list) { softRepaint(); return; } // loading/empty state in the DOM: repaint composes the list
    const dup = Array.from(list.querySelectorAll('.cb-row')).find((n) => n.dataset.id === String(item.id));
    if (dup) dup.remove();
    if (clipboardFilter !== 'all' && clipNormalizeType(item) !== clipboardFilter) {
      // The new item is filtered out of view; if removing its older twin emptied the list, repaint so
      // the "no match" state takes its place.
      if (!list.querySelector('.cb-row')) softRepaint();
      return;
    }
    const noMatch = list.querySelector('.cb-empty');
    if (noMatch) noMatch.remove();
    list.prepend(clipRowEl(item));
  }

  // dropClipboardHistoryItem — remove the item with the given id from the cached list and, if the
  // clipboard view is open, remove JUST that row from the DOM (no full repaint). Shared by the trash
  // action (optimistic) and the live "clipboard:deleted" push. Falls back to a full render only when
  // the view structure is missing (e.g. the push raced a navigation), and repaints once the list runs
  // empty so the empty / "no match" state can take its place.
  function dropClipboardHistoryItem(id) {
    if (!id || !Array.isArray(live.clipboardHistory)) return;
    const before = live.clipboardHistory.length;
    live.clipboardHistory = live.clipboardHistory.filter((x) => x && x.id !== id);
    if (clipboardOpenItemId === id) clipboardOpenItemId = null;
    if (live.clipboardHistory.length === before || state.view !== 'clipboard') return;
    const list = clipScreenList();
    if (!list) { softRepaint(); return; }
    const row = Array.from(list.querySelectorAll('.cb-row')).find((n) => n.dataset.id === String(id));
    if (row) row.remove(); // not found = the row is filtered out of view; the DOM is already consistent
    if (!list.querySelector('.cb-row')) softRepaint();
  }

  // clipScreenList — the in-app clipboard view's list element, or null when the view (or its list,
  // e.g. the loading/empty states) is not currently in the DOM.
  function clipScreenList() {
    return document.querySelector('.cb-list--screen');
  }

  // clipCloseOpenOverlay — targeted close of the currently open row's action overlay: drop the acting
  // class and remove the overlay element in place, WITHOUT re-rendering the list. No-op when nothing
  // is open.
  function clipCloseOpenOverlay() {
    clipboardOpenItemId = null;
    const acting = document.querySelector('.cb-list--screen .cb-row--acting');
    if (!acting) return;
    acting.classList.remove('cb-row--acting');
    const overlay = acting.querySelector('.cb-actions');
    if (overlay) overlay.remove();
  }

  // clipOpenOverlayOn(row, item) — targeted open of the action overlay over THIS row (closing any other
  // open one first — only one at a time), WITHOUT re-rendering the list.
  function clipOpenOverlayOn(row, item) {
    clipCloseOpenOverlay();
    clipboardOpenItemId = item.id;
    row.classList.add('cb-row--acting');
    row.append(clipActionOverlay(item));
  }

  // clipNormalizeType — 'text' | 'image' | 'file' from the history item (mirrors the viewer's itemType).
  function clipNormalizeType(item) {
    const t = (item && item.type ? String(item.type) : '').toLowerCase();
    if (t === 'image') return 'image';
    if (t === 'file') return 'file';
    return 'text';
  }

  // clipIsLocked — a Text item whose plaintext is not available yet: the bridge surfaces rows it cannot
  // decrypt (the E2E text key has not been relayed to this device) with text=null, so the user still
  // sees the items exist. The "clipboard:key" push re-fetches once the key lands and unlocks them.
  function clipIsLocked(item) {
    return clipNormalizeType(item) === 'text' && item.text == null;
  }

  // clipTitleOf — the one-line row title (mirrors the viewer's titleOf, plus the locked placeholder).
  function clipTitleOf(item) {
    const type = clipNormalizeType(item);
    if (clipIsLocked(item)) return 'Waiting for key from your other device';
    if (type === 'text') return (item.text || '').replace(/\s+/g, ' ').trim() || '(empty)';
    if (type === 'file') return item.preview || item.fileName || 'File';
    if (item.text) return item.text;
    return type === 'image' ? 'Image' : 'File';
  }

  // Files above this size are never synced byte-for-byte (see the engine cap) — they land as a
  // metadata-only "too large to sync" entry with no Save action. Matches the server MaxBlobBytes.
  const CLIP_MAX_FILE_BYTES = 100 * 1024 * 1024;

  // clipFormatSize — short byte size (mirrors the viewer's formatSize).
  function clipFormatSize(bytes) {
    if (bytes == null || isNaN(bytes)) return '';
    const b = Number(bytes);
    if (b < 1024) return `${b} B`;
    if (b < 1024 * 1024) return `${(b / 1024).toFixed(b < 10 * 1024 ? 1 : 0)} KB`;
    if (b < 1024 * 1024 * 1024) return `${(b / (1024 * 1024)).toFixed(1)} MB`;
    return `${(b / (1024 * 1024 * 1024)).toFixed(1)} GB`;
  }

  // clipRelTime — short relative time (mirrors the viewer's relTime).
  function clipRelTime(iso) {
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

  // clipRowEl(item) — one history row reusing the floating viewer's .cb-row markup. Tapping the row opens
  // the action overlay (copy + trash) over THIS row — a targeted DOM toggle, never a list re-render.
  // The data-id is what dropClipboardHistoryItem uses to remove just this row on delete.
  function clipRowEl(item) {
    const type = clipNormalizeType(item);
    const isImg = type === 'image';
    const hasThumb = isImg && !!item.imagePreviewDataUri;
    const cls = ['cb-row'];
    if (isImg) cls.push('cb-row--img');
    if (hasThumb) cls.push('cb-row--thumb');

    // Avatar slot: image items with a preview render a small inline thumbnail (same markup as the
    // floating viewer's rows); everything else keeps the typed tile.
    const av = hasThumb
      ? el('img', { class: 'cb-av cb-av--thumb', src: item.imagePreviewDataUri, alt: '', 'aria-hidden': 'true' })
      : el('div', {
          class: 'cb-av' + (type === 'file' ? ' cb-av--file' : type === 'image' ? ' cb-av--img' : ''),
          'aria-hidden': 'true',
        }, type === 'text' ? 'T' : type === 'file' ? 'F' : '');

    const title = el('div', {
      class: 'cb-title' + (clipIsLocked(item) ? ' cb-title--waiting' : ''),
      text: clipTitleOf(item),
    });
    const body = el('div', { class: 'cb-body' }, title);

    const metaParts = [];
    const time = clipRelTime(item.createdUtc);
    if (time) metaParts.push(el('span', { text: time }));
    if (type === 'image' || type === 'file') {
      const sz = clipFormatSize(item.sizeBytes);
      if (sz) { metaParts.push(el('span', { class: 'cb-meta__sep', text: '·' })); metaParts.push(el('span', { text: sz })); }
    }
    if (item.originDeviceName) {
      if (metaParts.length) metaParts.push(el('span', { class: 'cb-meta__sep', text: '·' }));
      metaParts.push(el('span', { class: 'cb-meta__from', text: item.originDeviceName }));
    }
    if (metaParts.length) body.append(el('div', { class: 'cb-meta' }, ...metaParts));

    const head = el('div', { class: 'cb-row__head' }, av, body);
    const open = clipboardOpenItemId === item.id;
    const row = el('div', {
      class: cls.join(' ') + (open ? ' cb-row--acting' : ''),
      role: 'button', tabindex: '0', 'aria-label': clipTitleOf(item),
      dataset: { id: String(item.id) },
    }, head);

    // Toggle in place: compare against the LIVE open id (not the render-time `open`) so the row keeps
    // working across targeted opens/closes without being rebuilt.
    const toggle = () => {
      if (clipboardOpenItemId === item.id) clipCloseOpenOverlay();
      else clipOpenOverlayOn(row, item);
    };
    row.addEventListener('click', toggle);
    row.addEventListener('keydown', (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); toggle(); } });

    if (open) row.append(clipActionOverlay(item));
    return row;
  }

  // clipActionOverlay(item) — the full overlay over an open row carrying two icon-only buttons: copy
  // (sets the OS clipboard via copyClipboardEntry — copy ONLY, it never closes the floating viewer,
  // steals focus or synthesizes Ctrl+V the way the viewer's paste action does) and trash (deletes
  // WITHOUT confirmation). Tapping either stops propagation so it does not re-toggle the row.
  function clipActionOverlay(item) {
    const overlay = el('div', { class: 'cb-actions' });

    // Primary action: a File saves to disk (its bytes are fetched on demand via the lazy blob); a
    // text/image copies to the OS clipboard. A file over the sync cap was never uploaded — show a
    // note instead of a Save button.
    let primary;
    if (clipNormalizeType(item) === 'file') {
      const tooLarge = item.sizeBytes != null && Number(item.sizeBytes) > CLIP_MAX_FILE_BYTES;
      primary = tooLarge
        ? el('div', { class: 'cb-note', text: 'Too large to sync' })
        : el('button', {
            class: 'cb-action cb-action--save', type: 'button', 'aria-label': 'Save file', title: 'Save to Downloads',
            onclick: (e) => {
              e.stopPropagation();
              clipCloseOpenOverlay();
              if (Bridge.available) {
                announce('Saving…');
                Bridge.call('saveClipboardFile', item.id)
                  .then((r) => { announce(r && r.ok ? 'Saved to Downloads' : 'File not ready yet — try again in a moment.'); })
                  .catch(() => { announce('Could not save this file.'); });
              }
            },
          }, iconEl('download', 18, 1.7));
    } else {
      primary = el('button', {
        class: 'cb-action cb-action--copy', type: 'button', 'aria-label': 'Copy to clipboard', title: 'Copy',
        onclick: (e) => {
          e.stopPropagation();
          clipCloseOpenOverlay();
          if (Bridge.available) {
            Bridge.call('copyClipboardEntry', item.id)
              .then((r) => { announce(r && r.status === 'ok' ? 'Copied' : 'Could not copy this item.'); })
              .catch(() => { announce('Could not copy this item.'); });
          }
        },
      }, iconEl('copy', 18, 1.7));
    }

    const trashBtn = el('button', {
      class: 'cb-action cb-action--del', type: 'button', 'aria-label': 'Delete', title: 'Delete',
      onclick: (e) => {
        e.stopPropagation();
        // Optimistic, NO confirm: drop the row immediately, then propagate the delete to the server and
        // the user's other devices. A transport failure is swallowed (a stale id is a server no-op).
        const id = item.id;
        dropClipboardHistoryItem(id);
        announce('Deleted');
        if (Bridge.available) Bridge.call('deleteClipboardEntry', id).catch(() => {});
      },
    }, iconEl('trash', 18, 1.7));

    overlay.append(primary, trashBtn);
    // A tap on the overlay backdrop (not a button) closes it without acting.
    overlay.addEventListener('click', (e) => { if (e.target === overlay) { e.stopPropagation(); clipCloseOpenOverlay(); } });
    return overlay;
  }

  // clipboardTabs — segmented [History | Settings] header (spec §4: send/receive y los 7 knobs en UN
  // lugar). `active` es la tab presionada; la otra navega a su ruta hermana.
  function clipboardTabs(active) {
    return el('div', { class: 'segmented', role: 'tablist', style: 'margin-bottom:12px' },
      el('button', { class: 'segmented__item', role: 'tab', 'aria-pressed': String(active === 'history'), text: 'History',
        onclick: active === 'history' ? null : () => navigate('clipboard') }),
      el('button', { class: 'segmented__item', role: 'tab', 'aria-pressed': String(active === 'settings'), text: 'Settings',
        onclick: active === 'settings' ? null : () => navigate('clipboard-settings') }));
  }

  function renderClipboard(root) {
    root.append(viewHeader('Clipboard', { onBack: () => navigate('home') }));

    if (!Bridge.desktopApp) {
      // Defensive: the in-app clipboard view is a desktop concern. Other transports bounce to the hub.
      navigate('home');
      return;
    }

    root.append(clipboardTabs('history'));

    loadClipboardHistory('clipboard');
    const items = live.clipboardHistory;

    const card = el('div', { class: 'glass glass--card cb-screen' });

    if (items === null) {
      card.append(el('div', { class: 'cb-screen__empty' },
        el('span', { class: 'spinner' }), el('span', { text: 'Loading your clipboard…' })));
      root.append(card);
      return;
    }

    if (!items.length) {
      card.append(el('div', { class: 'cb-screen__empty' },
        el('div', { class: 'cb-screen__empty-title', text: 'Nothing copied yet' }),
        el('div', { class: 'cb-screen__empty-sub', text: 'Copy something on any of your devices to see it here.' })));
      root.append(card);
      return;
    }

    // Type filter chips — same segmented control (and labels) as the floating viewer. Switching the
    // filter is a full repaint (the list composition changes anyway); any open overlay is dropped
    // because its row may leave the view.
    const seg = el('div', { class: 'cb-filter' });
    CLIPBOARD_FILTERS.forEach(([val, label]) => {
      seg.append(el('button', {
        class: 'cb-filter__item', type: 'button', 'aria-pressed': String(clipboardFilter === val), text: label,
        onclick: () => {
          if (clipboardFilter === val) return;
          clipboardFilter = val;
          clipboardOpenItemId = null;
          softRepaint();
        },
      }));
    });
    card.append(seg);

    const visible = items.filter((it) => clipboardFilter === 'all' || clipNormalizeType(it) === clipboardFilter);
    const list = el('div', { class: 'cb-list cb-list--screen' });
    if (!visible.length) {
      list.append(el('div', { class: 'cb-empty', text: 'No items match this filter' }));
    } else {
      visible.forEach((item) => list.append(clipRowEl(item)));
    }
    card.append(list);
    root.append(card);
  }

  // Per-account retention window (hours). null = server default (24h). Loaded lazily from the server.
  let clipRetentionHours = null;
  let clipRetentionLoaded = false;
  const CLIP_RETENTION_PRESETS = [[1, '1 hour'], [6, '6 hours'], [24, '24 hours (default)'], [72, '3 days'], [168, '7 days']];

  function retentionSelect() {
    if (!clipRetentionLoaded && Bridge.available) {
      clipRetentionLoaded = true;
      Bridge.call('getClipboardRetention')
        .then((r) => { clipRetentionHours = (r && typeof r.hours === 'number') ? r.hours : null; softRepaint(); })
        .catch(() => {});
    }
    const sel = el('select', { class: 'cfg-select', 'aria-label': 'Clipboard retention' });
    CLIP_RETENTION_PRESETS.forEach(([h, label]) => {
      const opt = el('option', { value: String(h), text: label });
      if ((clipRetentionHours ?? 24) === h) opt.selected = true;
      sel.append(opt);
    });
    sel.addEventListener('change', () => {
      const hours = parseInt(sel.value, 10);
      clipRetentionHours = hours;
      if (Bridge.available) {
        Bridge.call('setClipboardRetention', hours)
          .then(() => announce('Retention updated'))
          .catch(() => announce('Could not update retention'));
      }
    });
    return sel;
  }

  function renderClipboardSettings(root) {
    root.append(viewHeader('Clipboard', { onBack: () => navigate('config') }));

    if (!Bridge.desktopApp) {
      // Defensive: clipboard sync is a desktop concern. Other transports bounce back to the hub.
      navigate('config');
      return;
    }

    root.append(clipboardTabs('settings'));

    loadClipboardDevices('clipboard-settings');
    const data = live.clipboardDevices;

    if (data === null) {
      root.append(cfgSection('This device',
        cfgRow('Loading…', el('div', { class: 'cfg-row__hint', text: 'Reading your devices' }), el('span', { class: 'spinner' }))));
      return;
    }

    // Hydrate the App-local paste-panel opacity from the host (settings.json) so the slider shows the
    // persisted value. It rides along on the devices view (App-local, not a per-device server setting).
    if (typeof data.pastePanelOpacity === 'number') {
      settings.pastePanelOpacity = Math.max(0, Math.min(100, data.pastePanelOpacity));
    }

    const me = thisClipboardDevice();
    const thisName = (me && me.name) ? ` (${me.name})` : '';

    // ---- This device card ----
    if (me) {
      if (!me.settings) me.settings = {};
      root.append(cfgSection(`Clipboard · this device${thisName}`,
        cfgRow('Auto-sync',
          el('div', { class: 'cfg-row__hint', text: 'Copy on another device → Ctrl+V here' }),
          clipToggle(me, 'autoSync', { label: 'Auto-sync' })),
        cfgRow('Send my clipboard', null, clipToggle(me, 'send', { label: 'Send my clipboard' })),
        cfgRow('Receive clipboards', null, clipToggle(me, 'receive', { label: 'Receive clipboards' })),
        cfgRow('Viewer hotkey', null, hotkeyChip(me)),
        cfgRow('Viewer density', null, densitySegmented(me)),
        cfgRow('Paste panel opacity',
          el('div', { class: 'cfg-row__hint', text: 'Transparency of the floating hotkey paste panel' }),
          pasteOpacitySlider()),
        cfgRow('Show shortcut hints',
          el('div', { class: 'cfg-row__hint', text: 'Key bar at the foot of the viewer (Rich only)' }),
          clipToggle(me, 'showHints', { label: 'Show shortcut hints' })),
        cfgRow('Keep clipboard for',
          el('div', { class: 'cfg-row__hint', text: 'Records older than this are deleted automatically' }),
          retentionSelect()));
    } else {
      root.append(cfgSection('Clipboard · this device',
        cfgRow('This device is not registered yet', el('div', { class: 'cfg-row__hint', text: 'Sign in on this device to manage its clipboard.' }), null)));
    }

    // ---- Your devices accordion (collapsed by default) ----
    const devices = data.devices || [];
    const online = devices.filter((d) => d.online).length;
    const section = el('div', { class: 'glass glass--card config-section' });

    const chev = el('span', { class: 'cb-acc__chev', html: icon('chevrondown', { size: 15, stroke: 2.4 }) });
    const titleEl = el('span', { class: 'cb-acc__title' });
    titleEl.append(document.createTextNode('Your devices '), el('b', { text: `(${devices.length})` }),
      document.createTextNode(` · ${online} online`));
    const header = el('button', { class: 'cb-acc', type: 'button', 'aria-expanded': String(clipboardDevicesOpen) },
      titleEl, chev);
    const body = el('div', { class: 'cb-acc__body' });
    if (clipboardDevicesOpen) {
      if (devices.length === 0) {
        body.append(cfgRow('No other devices', el('div', { class: 'cfg-row__hint', text: 'Sign in on another device to mirror your clipboard.' }), null));
      } else {
        devices.forEach((d) => body.append(deviceRow(d, data.thisDeviceId)));
      }
    } else {
      body.hidden = true;
    }
    header.addEventListener('click', () => {
      clipboardDevicesOpen = !clipboardDevicesOpen;
      softRepaint();
    });
    section.append(header, body);
    root.append(section);
  }

  // home/palette consumen loadClipboardHistory por ctx (escribe en live.clipboardHistory, compartido).
  // Re-expuesto aquí porque la función vive en este módulo; registerClipboardViews corre ANTES que
  // registerHomeViews para que home la capture ya definida.
  ctx.loadClipboardHistory = loadClipboardHistory;

  // resetSessionState (shell) limpia el estado de vista módulo-local en sign-out (no puede tocar estas
  // closures directamente). Re-expuesto en ctx; app.js lo invoca dentro de resetSessionState.
  ctx.resetClipboardViewState = () => {
    clipboardDevicesOpen = false;
    clipboardOpenItemId = null;
    clipboardFilter = 'all';
  };

  // Live clipboard history pushes: estas funciones viven ahora en el módulo, así que el módulo se
  // suscribe él mismo (sin ciclo). Las suscripciones presence/settings → refreshClipboardDevices se
  // quedan en boot() de app.js (refreshClipboardDevices es compartido con Devices). registerClipboard-
  // Views corre antes que boot(), así que el orden es seguro.
  if (Bridge.available) {
    Bridge.onEvent('clipboard:item', (item) => applyClipboardHistoryItem(item));
    Bridge.onEvent('clipboard:deleted', (p) => { if (p && p.id) dropClipboardHistoryItem(p.id); });
    Bridge.onEvent('clipboard:key', () => refreshClipboardHistory());
  }

  registry.register('clipboard', {
    render: renderClipboard, soft: true,
    // hidden solo en web panel (NO !Bridge.desktopApp): en demo sin Bridge la vista debe
    // verse con su empty-state.
    nav: { label: 'Clipboard', icon: 'clipboard', order: 3, section: 'modules', hidden: () => Bridge.webPanel },
    statusDot: () => ctx.clipboardStatusDot(),
  });
  registry.register('clipboard-settings', { render: renderClipboardSettings, soft: true, parent: 'clipboard' });
}
