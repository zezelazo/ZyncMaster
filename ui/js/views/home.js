// views/home.js — el tablero "today" (spec §3.3): next events + recent clipboard + sync
// health. Reescritura del home (el grid de tiles murió): denso, datos reales, vacíos
// honestos. Registrado vía registerHomeViews(ctx) — ver el contrato ctx en app.js.

import { eventRows, clipboardRows } from '../core/board-model.js';
import { healthRows } from '../core/status-model.js';

export function registerHomeViews(ctx) {
  const { el, icon, Bridge, live, state, navigate, rerenderInPlace, announce, registry,
    thisClipboardDevice, loadClipboardDevices, loadClipboardHistory, loadPairs } = ctx;

  // Snapshot del día: pedido una vez por entrada a Home; null hasta que llega (o si el
  // endpoint todavía no existe server-side — Calendar v2). Nunca bloquea el paint.
  const day = { payload: null, attempted: false };

  function ensureDayLoaded() {
    if (!Bridge.available || day.attempted) return;
    day.attempted = true;
    Bridge.call('getCalendarDay')
      .then((p) => { day.payload = p; if (state.view === 'home') rerenderInPlace(); })
      .catch(() => { /* endpoint ausente o server caído: vacío honesto */ });
  }

  function card(title, ...children) {
    return el('section', { class: 'board-card glass' },
      el('h2', { class: 'board-card__hd', text: title }),
      ...children);
  }

  function emptyRow(text) {
    return el('div', { class: 'board-row' }, el('span', { class: 'board-row__meta', text }));
  }

  function eventsCard() {
    const rows = eventRows(day.payload);
    const body = rows.length
      ? rows.map((r) => el('div', { class: 'board-row' },
          el('span', { class: 'board-row__time num', text: r.time }),
          el('span', { class: 'board-row__title', text: r.title }),
          r.source ? el('span', { class: 'chip chip--info', text: r.source }) : null))
      : [emptyRow('No upcoming events today')];
    return card('Next events', ...body);
  }

  function clipboardCard() {
    const rows = clipboardRows(live.clipboardHistory);
    const body = rows.length
      ? rows.map((r) => {
          const copyBtn = el('button', { class: 'board-copy', type: 'button', 'aria-label': `Copy: ${r.title}` }, 'Copy');
          copyBtn.addEventListener('click', () => {
            Bridge.call('copyClipboardEntry', String(r.id))
              .then(() => { announce('Copied to clipboard.'); copyBtn.textContent = 'Copied'; setTimeout(() => { copyBtn.textContent = 'Copy'; }, 1500); })
              .catch(() => announce('Copy failed.'));
          });
          return el('div', { class: 'board-row' },
            el('span', { class: 'board-row__title board-row__title--dim', text: r.kind === 'text' ? r.title : `${r.kind === 'image' ? '🖼 ' : '📄 '}${r.title}` }),
            el('span', { class: 'board-row__meta num', text: r.device }),
            copyBtn);
        })
      : [emptyRow(Bridge.desktopApp ? 'Nothing copied yet' : 'Clipboard lives in the desktop App')];
    return card('Recent clipboard', ...body);
  }

  function healthCard() {
    const devices = (live.clipboardDevices && live.clipboardDevices.devices) || [];
    const rows = healthRows(live.pairs, devices);
    const body = rows.length
      ? rows.map((r) => el('div', { class: 'board-row board-row--health' },
          el('span', { class: 'board-row__title', text: r.name }),
          el('span', { class: `board-stat board-stat--${r.state} num`,
            text: r.kind === 'pair'
              ? `● ${r.lastRun ? `Last run ${r.lastRun}` : 'Never ran'}${r.changes != null ? ` · ${r.changes} changes` : ''}${r.intervalMin ? ` · every ${r.intervalMin} min` : ''}`
              : `● ${r.detail}` })))
      : [emptyRow('No sync pairs yet — add one in Calendar')];
    const add = el('button', { class: 'board-copy', type: 'button', onclick: () => navigate('calendar') }, 'Open Calendar');
    return el('section', { class: 'board-card board-card--wide glass' },
      el('h2', { class: 'board-card__hd', text: 'Sync health' }), ...body,
      el('div', { class: 'board-row' }, add));
  }

  function renderHome(root) {
    if (Bridge.available && !live.loadedPairs && !live.loadingPairs && !live.pairsAttempted) {
      live.pairsAttempted = true;
      loadPairs().then(() => { if (state.view === 'home') rerenderInPlace(); });
    }
    if (Bridge.desktopApp) {
      loadClipboardDevices('home');
      loadClipboardHistory('home');
    }
    ensureDayLoaded();
    root.append(el('div', { class: 'board' }, eventsCard(), clipboardCard(), healthCard()));
  }

  registry.register('home', {
    render: renderHome,
    soft: true,
    nav: { label: 'Home', icon: 'home', order: 1, section: 'modules' },
    header: () => ({
      title: 'Today',
      meta: new Date().toLocaleDateString([], { weekday: 'long', month: 'long', day: 'numeric' }),
    }),
  });
}
