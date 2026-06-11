// views/calendar.js — módulo Calendar: lista de pares (accordion), wizard add-pair
// (lógica intacta — solo restyle), add-calendar, y la tab Accounts (la vieja
// renderCalendarSettings reubicada dentro del módulo, spec §4). Extraído de app.js.

import { registerPaletteSource } from '../palette.js';

export function registerCalendarViews(ctx) {
  const {
    $, el, iconEl, icon, Bridge, state, live, navigate, rerender, rerenderInPlace, softRepaint,
    announce, openModal, confirmModal, showToast,
    viewHeader, navRow, actionChip, activityRow, pairBadge,
    cfgSection, cfgRow,
    loadPairs, loadAccounts, loadCalendars, loadLocalCalendars, ensurePairCalendarNames,
    pairViewModel, resolveCalendarLabel, comAvailable, fmtMMSS, COM_SLOW_TIMEOUT_MS,
    runPairNow, setPairState, deletePair, runSync, syncPairRemote,
    openPairs, PAIRS,
    registry,
  } = ctx;

  // ======== datos demo (solo estas vistas los consumen) ========
  const CALENDAR_LIBRARY = [
    { id: 'c-out-personal', svc: 'Outlook', acct: 'Personal',      email: 'daniel@outlook.com' },
    { id: 'c-out-work',     svc: 'Outlook', acct: 'Work Calendar', email: 'd.lopez@acme.com' },
    { id: 'c-gmail',        svc: 'Gmail',   acct: 'daniel.lopez',  email: 'daniel.lopez@gmail.com' },
    { id: 'c-ic-family',    svc: 'iCloud',  acct: 'Family',        email: 'daniel@icloud.com' },
    { id: 'c-ic-mirror',    svc: 'iCloud',  acct: 'Mirror',        email: 'daniel@icloud.com' },
  ];

  const PROVIDERS = [
    { id: 'outlook',  name: 'Microsoft 365',   sub: 'Outlook · Office 365',   tone: 'azure', letter: 'M' },
    { id: 'google',   name: 'Google Calendar', sub: 'Gmail · Workspace',      tone: 'warn',  letter: 'G' },
    { id: 'icloud',   name: 'iCloud Calendar', sub: 'Apple ID',                    tone: 'ink',   letter: 'i' },
    { id: 'exchange', name: 'Exchange Server', sub: 'Self-hosted · IMAP/EWS', tone: 'terra', letter: 'E' },
  ];

  const DISCOVERED = [
    { id: 'd1', name: 'Personal',          color: '#5b8cff', desc: 'Default · 142 events' },
    { id: 'd2', name: 'Family',            color: '#4ec07f', desc: 'Shared with 3 people' },
    { id: 'd3', name: 'Birthdays',         color: '#f4b53b', desc: 'Read-only · auto-generated' },
    { id: 'd4', name: 'Travel',            color: '#d97757', desc: '12 upcoming trips' },
    { id: 'd5', name: 'Holidays in Spain', color: '#9b7adf', desc: 'Subscribed · public' },
  ];

  // ======== Pairs (accordion) ========
  function pairAccordion(pair) {
    // p3 mirrors the global syncing state so the demo can drive a live progress bar.
    const liveState = pair.id === 'p3'
      ? (state.sync === 'syncing' ? 'syncing' : state.sync === 'error' ? 'error' : state.sync === 'offline' ? 'offline' : 'ok')
      : pair.state;
    const isSyncing = liveState === 'syncing';
    const isError = liveState === 'error';
    const isOffline = liveState === 'offline';
    // Real, client-tracked single-flight state for a live pair: a sync (local or remote request) is in
    // flight right now. Distinct from the demo p3 progress path (isSyncing). When busy, the Sync now
    // button shows a spinner, is disabled, and additional clicks are ignored.
    const busy = !!pair.inFlight;
    const dotState = (isSyncing || busy) ? 'sync' : isError ? 'error' : isOffline ? 'offline' : 'ok';
    const open = openPairs.has(pair.id);
    const progress = (pair.id === 'p3' && state.sync === 'syncing') ? state.progress : { done: 0, total: 1 };
    const nextStr = isOffline || isSyncing ? '—' : fmtMMSS(pair.nextSync);

    const card = el('div', { class: `pair glass glass--card${open ? ' is-open' : ''}`, dataset: { state: liveState } });

    const head = el('button', { class: 'pair__head', 'aria-expanded': String(open), onclick: () => togglePair(pair.id) },
      el('div', { class: 'pair__route' },
        pairBadge(pair.src.svc),
        el('div', { class: 'pair__route-text' }, el('span', { class: 'pair__name', text: `${pair.src.svc} · ${pair.src.acct}` })),
        el('span', { class: 'pair__arrow-ico', html: icon('arrowright', { size: 11, stroke: 1.8 }) }),
        pairBadge(pair.dst.svc),
        el('div', { class: 'pair__route-text' }, el('span', { class: 'pair__name', text: `${pair.dst.svc} · ${pair.dst.acct}` })),
      ),
      el('div', { class: 'pair__meta' },
        el('span', { class: 'status-dot', dataset: { state: dotState }, style: 'width:7px;height:7px' }),
        el('span', { class: 'pair__last num', text: pair.lastSync }),
        el('span', { class: `pair__chevron${open ? ' pair__chevron--open' : ''}`, html: icon('chevrondown', { size: 14, stroke: 1.8 }) }),
      ),
    );
    card.append(head);

    const substat = (lab, val) => el('span', null,
      el('span', { class: 'route__stat-label', text: lab }), el('span', { class: 'route__stat-val num', text: String(val) }));

    // Track B — COM device-pinning. The "Sync now" button is disabled when:
    //   comOffline   — the pair reads Outlook on ANOTHER device whose lease has expired (a local run /
    //                  request-sync would be a no-op);
    //   comUnclaimed — the source is COM but no device has claimed it yet AND this device cannot read
    //                  COM, so there is no origin to run it (a local run would fail immediately).
    // Otherwise the click routes either to a local run (this PC is the origin) or to a request-sync
    // signal (the origin runs it).
    const comBlocked = pair.comOffline || pair.comUnclaimed;
    // a11y — a disabled button is not focusable and its `title` is not announced by screen readers, so
    // carry the disabled reason in aria-label and point aria-describedby at the pin-note element below.
    const pinNoteId = pair.id ? `pin-note-${pair.id}` : null;
    const disabledReason = isOffline
      ? 'Sync now unavailable — the app is offline'
      : pair.comOffline
        ? `Sync now unavailable — origin device ${pair.pinnedDeviceName || 'is'} is offline`
        : pair.comUnclaimed
          ? 'Sync now unavailable — no source device has claimed this sync yet'
          : '';
    const syncBtnAttrs = {
      class: 'pair__sync-btn',
      // Single-flight: disabled while a run is in flight (busy) so stacked clicks cannot launch more.
      disabled: isOffline || comBlocked || busy,
      title: busy ? 'Sync in progress…' : disabledReason,
      onclick: (e) => {
        e.stopPropagation();
        if (busy) return;                          // single-flight guard at the click site too
        if (!Bridge.available || !pair.id) { runSync(); return; }
        if (pair.comRemote) syncPairRemote(pair); else runPairNow(pair.id);
      },
    };
    if (busy) {
      // a11y — announce the busy state on the control itself; aria-busy reflects the in-flight work.
      syncBtnAttrs['aria-busy'] = 'true';
      syncBtnAttrs['aria-label'] = 'Sync in progress';
    } else if (disabledReason) {
      syncBtnAttrs['aria-label'] = disabledReason;
      if (pinNoteId) syncBtnAttrs['aria-describedby'] = pinNoteId;
    }
    // Spinner whenever busy (real in-flight) OR the demo p3 progress path is active.
    const spinning = busy || isSyncing;
    const syncBtnLabel = busy ? 'Syncing…' : isSyncing ? `${progress.done}/${progress.total}` : 'Sync now';
    const syncBtn = el('button', syncBtnAttrs,
      spinning ? el('span', { class: 'spinner', style: 'width:12px;height:12px;border-width:1.6px' }) : el('span', { style: 'display:inline-flex', html: icon('sync', { size: 12, stroke: 1.8 }) }),
      el('span', { class: 'num', text: syncBtnLabel }),
    );
    // Header layout (point 2): in a ~340px window all five items + the button cannot share one row
    // without the "Sync now" button being clipped at the right edge. So the stats (Events / Next /
    // Status) sit on their own row, and "Sync now" gets a dedicated full-width row directly below —
    // the button is therefore always complete and clickable. "Attempt N" is moved OUT of this row (it
    // was the item pushing the button off-screen) and is rendered as a sub-badge next to "Recent
    // events" further down.
    const substatChildren = [
      substat('Events', pair.eventCount),
      el('span', null, el('span', { class: 'route__stat-label', text: 'Next' }), el('span', { class: 'route__stat-val num', text: nextStr })),
      el('span', null, el('span', { class: 'route__stat-label', text: 'Status' }),
        el('span', { class: 'route__stat-val', text: (isSyncing || busy) ? 'Syncing…' : isError ? 'Failed' : isOffline ? 'Offline' : 'Connected' })),
    ];
    card.append(el('div', { class: 'pair__substats' }, ...substatChildren));
    // Sync now on its own row, full-width — never clipped.
    card.append(el('div', { class: 'pair__sync-row' }, syncBtn));

    // Track B — COM device-pinning note. Tells the user WHERE this pair's Outlook source is read:
    //   local     → "Source is on this PC" (this device runs it);
    //   remote    → "Runs on <device>" (another device is the origin), with an "origin offline" hint
    //               when that device's lease has expired so the signal cannot be picked up right now;
    //   unclaimed → neutral "No source device yet" — the source is COM but no device has claimed it and
    //               this device cannot read COM, so we do NOT assert it runs here.
    if (pair.srcCom && (pair.comLocal || pair.comRemote || pair.comUnclaimed)) {
      const kind = pair.comLocal ? 'local' : pair.comUnclaimed ? 'unclaimed' : (pair.comOffline ? 'offline' : 'remote');
      const pinNoteOpts = { class: 'pair__pin-note', dataset: { kind } };
      if (pinNoteId) pinNoteOpts.id = pinNoteId;
      const pinNote = el('div', pinNoteOpts);
      const noteIcon = pair.comLocal ? 'check' : pair.comUnclaimed ? 'pin' : 'sync';
      pinNote.append(el('span', { style: 'display:inline-flex', html: icon(noteIcon, { size: 12, stroke: 1.8 }) }));
      if (pair.comLocal) {
        pinNote.append(el('span', { text: 'Source is on this PC' }));
      } else if (pair.comUnclaimed) {
        pinNote.append(el('span', { text: 'No source device yet' }));
        pinNote.append(el('span', { class: 'pair__pin-note-sub', text: '· open the app where Outlook is installed' }));
      } else {
        const who = pair.pinnedDeviceName || 'another device';
        pinNote.append(el('span', { text: `Runs on ${who}` }));
        if (pair.comOffline) pinNote.append(el('span', { class: 'pair__pin-note-sub', text: '· origin offline' }));
      }
      card.append(pinNote);
    }

    if (open) {
      const body = el('div', { class: 'pair__body' });
      const block = (label, name, email) => el('div', { class: 'pair__route-block' },
        el('div', { class: 'route__label', text: label }),
        el('div', { class: 'pair__route-name-full', text: name }),
        el('div', { class: 'pair__route-email', text: email }),
      );
      body.append(el('div', { class: 'pair__route-detail' },
        block('SOURCE', `${pair.src.svc} · ${pair.src.acct}`, pair.src.email),
        el('div', { class: 'pair__route-divider', html: icon('arrowright', { size: 14, stroke: 1.8 }) }),
        block('DESTINATION', `${pair.dst.svc} · ${pair.dst.acct}`, pair.dst.email),
      ));

      if (isSyncing) {
        const pct = (progress.done / progress.total) * 100;
        const nowTitle = pair.events[progress.done % pair.events.length]?.title || '…';
        body.append(el('div', { class: 'route__progress', style: 'margin-top:0' },
          el('div', { class: 'route__progress-head' }, el('span', { text: 'Mirroring events' }),
            el('span', { class: 'num', text: `${progress.done} / ${progress.total}` })),
          el('div', { class: 'route__progress-bar' }, el('div', { style: `width:${pct}%` })),
          el('div', { class: 'route__progress-now' },
            el('span', { class: 'status-dot', dataset: { state: 'sync' }, style: 'width:6px;height:6px' }),
            el('span', { class: 'route__progress-item', text: nowTitle })),
        ));
      }

      // RECENT EVENTS — driven by the per-pair session log (every attempt, ok + fail) when present,
      // otherwise the last-run summary from the server. The counter reflects how many rows are shown.
      const activityHead = el('div', { class: 'pair__activity-head' },
        el('span', { text: 'Recent events' }),
        el('span', { class: 'num', text: String(pair.events.length) }));
      // "Attempt N" relocated here (out of the cramped header row) as a subtle sub-badge.
      if (pair.attempts > 0) {
        activityHead.append(el('span', { class: 'pair__attempts', title: `${pair.attempts} sync ${pair.attempts === 1 ? 'attempt' : 'attempts'} this session` },
          el('span', { class: 'num', text: `Attempt ${pair.attempts}` })));
      }
      body.append(activityHead);
      const act = el('div', { class: 'pair__activity' });
      if (pair.events.length) pair.events.forEach((row) => act.append(activityRow(row)));
      else act.append(el('div', { class: 'activity__sub', style: 'padding:8px 2px', text: 'No changes yet.' }));
      body.append(act);

      // Live controls (native shell only): pause/resume, disable/enable, delete.
      if (Bridge.available && pair.id) {
        const paused = pair.serverState === 'paused';
        const disabled = pair.serverState === 'disabled';
        // Footer controls live on a SINGLE line and are ICON-ONLY so all five fit, uncut, even in a
        // ~340px-wide window. Each button carries an aria-label + title (tooltip) describing its action;
        // there is no visible text label to truncate. Layout/sizing lives in layout.css (.pair__ctrl-btn
        // is a fixed square icon button). Delete sits last, tinted with --err.
        const controls = el('div', { class: 'pair__controls' });

        const pauseLabel = paused ? 'Resume' : 'Pause';
        controls.append(el('button', { class: 'btn btn--ghost pair__ctrl-btn', 'aria-label': pauseLabel, title: pauseLabel,
          onclick: (e) => { e.stopPropagation(); setPairState(pair.id, paused ? 'active' : 'paused'); } },
          iconEl(paused ? 'sync' : 'pause', 15, 1.8)));

        const disableLabel = disabled ? 'Enable' : 'Disable';
        controls.append(el('button', { class: 'btn btn--ghost pair__ctrl-btn', 'aria-label': disableLabel, title: disableLabel,
          onclick: (e) => { e.stopPropagation(); setPairState(pair.id, disabled ? 'active' : 'disabled'); } },
          iconEl(disabled ? 'check' : 'disable', 15, 1.8)));

        // Export .txt — per-pair export of the pair's SOURCE calendar. Routing is by source
        // provider, inside openExportTxtModal: a COM source exports the local Outlook via
        // generateTxt (requires Outlook installed); a Graph source exports via the server, which then
        // saves the .txt through a desktop save dialog. BOTH write to a local path, so the whole
        // affordance is desktop-only — the browser panel has no save-to-disk channel (exportSourceTxt
        // is inert in web mode and would falsely report "File saved."). Gate the button to
        // Bridge.desktopApp so it never shows in the web panel.
        if (Bridge.desktopApp) {
          const isCom = (pair.src && pair.src.provider || '').toLowerCase() === 'outlookcom';
          const comBlocked = isCom && !comAvailable();
          const txtTitle = comBlocked ? 'Outlook is not available on this device' : 'Export .txt';
          const txtBtn = el('button', { class: 'btn btn--ghost pair__ctrl-btn',
            disabled: comBlocked, 'aria-label': 'Export .txt', title: txtTitle,
            onclick: (e) => { e.stopPropagation(); if (!comBlocked) openExportTxtModal(pair); } },
            iconEl('download', 15, 1.7));
          controls.append(txtBtn);
        }

        // Edit — open the wizard preloaded with this pair (F2). Reuses renderAddPairLive in edit mode.
        controls.append(el('button', { class: 'btn btn--ghost pair__ctrl-btn', 'aria-label': 'Edit', title: 'Edit',
          onclick: (e) => { e.stopPropagation(); startEditPair(pair.id); } },
          iconEl('pencil', 15, 1.8)));

        controls.append(el('button', { class: 'btn btn--ghost pair__ctrl-btn pair__ctrl-btn--danger', 'aria-label': 'Delete', title: 'Delete',
          onclick: (e) => {
            e.stopPropagation();
            confirmModal({
              title: 'Delete sync pair',
              text: `"${pair.name}" will stop syncing and its configuration will be removed. Events already copied to the destination are not deleted.`,
              confirmLabel: 'Delete pair',
            }).then((yes) => {
              if (!yes) return;
              deletePair(pair.id);
              showToast('Sync pair deleted');
            });
          } },
          iconEl('trash', 15, 1.7)));

        body.append(controls);
      }

      card.append(body);
    }
    return card;
  }

  // Segmented [Pairs | Accounts] tab control shared by renderCalendar / renderCalendarSettings.
  function calendarTabs(active) {
    return el('div', { class: 'segmented', role: 'tablist', style: 'margin-bottom:12px' },
      el('button', { class: 'segmented__item', role: 'tab', 'aria-pressed': String(active === 'pairs'),
        text: 'Pairs', onclick: active === 'pairs' ? undefined : () => navigate('calendar') }),
      el('button', { class: 'segmented__item', role: 'tab', 'aria-pressed': String(active === 'accounts'),
        text: 'Accounts', onclick: active === 'accounts' ? undefined : () => navigate('calendar-settings') }));
  }

  function renderCalendar(root) {
    const addPairBtn = el('button', { class: 'btn btn--ghost', style: 'height:28px;padding:0 8px', onclick: () => navigate('add-pair') },
      iconEl('plus', 12, 2), el('span', { style: 'font-size:12px', text: 'Add pair' }));
    // Calendar v2 — entry to the unified day/week view (a sub-route of Calendar, no nav entry).
    const dayViewBtn = el('button',
      { class: 'btn btn--ghost', style: 'height:28px;padding:0 8px', id: 'openCalendarDay',
        onclick: () => navigate('calendar-day') },
      iconEl('calendar', 12, 2), el('span', { style: 'font-size:12px', text: 'Day view' }));
    // viewHeader takes a single `action`; group both buttons in one container.
    const headerActions = el('div', { style: 'display:flex;gap:8px' }, dayViewBtn, addPairBtn);
    root.append(viewHeader('Calendar Sync', { onBack: () => navigate('home'), action: headerActions }));
    root.append(calendarTabs('pairs'));

    // Bridge: render the live pairs snapshot (refreshing in the background). Browser: mock.
    const pairs = Bridge.available
      ? (live.pairs || []).map(pairViewModel)
      : PAIRS;

    const list = el('div', { class: 'pair-list' });
    if (Bridge.available && pairs.length === 0) {
      list.append(el('div', { class: 'glass glass--card', style: 'padding:18px;text-align:center;color:var(--ink-3)' },
        el('div', { text: live.loadedPairs ? 'No sync pairs yet.' : 'Loading sync pairs…' })));
    } else {
      pairs.forEach((p) => list.append(pairAccordion(p)));
    }
    root.append(list);

    // Kick a one-shot background refresh the first time we open this screen; repaint when it
    // lands. Live actions (run/pause/delete) refresh explicitly, so we don't poll on paint.
    if (Bridge.available && !live.loadedPairs && !live.loadingPairs) {
      loadPairs().then(() => { if (state.view === 'calendar') rerenderInPlace(); });
    }
  }

  // ---------------- Wizard stepper (shared) ----------------
  function wizardStepper(step, labels) {
    const stepper = el('div', { class: 'stepper' });
    labels.forEach((lab, i) => {
      const st = i < step ? 'done' : i === step ? 'active' : null;
      stepper.append(el('div', { class: 'stepper__dot', dataset: st ? { state: st } : {}, html: i < step ? icon('check', { size: 11, stroke: 2.4 }) : '' },
        i < step ? '' : String(i + 1)));
      if (i < labels.length - 1) stepper.append(el('div', { class: 'stepper__line', dataset: i < step ? { state: 'done' } : {} }));
    });
    const labelsRow = el('div', { class: 'wizard-stepper-labels' });
    labels.forEach((lab, i) => labelsRow.append(el('div', { class: i === step ? 'is-active' : i < step ? 'is-done' : '', text: lab })));
    return el('div', { class: 'glass glass--card wizard-stepper' }, stepper, labelsRow);
  }

  // shared slider + interval rows
  function sliderRow(label, get, set) {
    const fill = el('div', { class: 'slider__fill' });
    const thumb = el('div', { class: 'slider__thumb' });
    const slider = el('div', { class: 'slider' }, fill, thumb);
    const hintVal = el('b', { class: 'num', style: 'color:var(--ink-1)', text: String(get()) });
    const paint = () => { const pct = ((get() - 7) / 23) * 100; fill.style.width = `${pct}%`; thumb.style.left = `${pct}%`; hintVal.textContent = String(get()); };
    slider.addEventListener('click', (e) => {
      const r = slider.getBoundingClientRect();
      const pct = Math.min(1, Math.max(0, (e.clientX - r.left) / r.width));
      set(Math.round(7 + pct * 23)); paint();
    });
    paint();
    return el('div', { class: 'cfg-row' },
      el('div', null, el('div', { class: 'cfg-row__label', text: label }),
        el('div', { class: 'cfg-row__hint' }, 'Next ', hintVal, ' days · past events are never touched')), slider);
  }
  // intervalRow — segmented 5/15/30/60-minute control. Selecting an option updates aria-pressed on
  // the buttons IN PLACE (the same technique the theme picker uses) instead of repainting the whole
  // view, so picking an interval never reconstructs the screen or flickers. The caller's `set` still
  // runs (to persist / update state) but must NOT trigger a full rerender for this control.
  function intervalRow(get, set) {
    const seg = el('div', { class: 'segmented' });
    const buttons = [];
    [5, 15, 30, 60].forEach((n) => {
      const b = el('button', { class: 'segmented__item', 'aria-pressed': String(get() === n),
        onclick: () => { set(n); buttons.forEach((btn) => btn.setAttribute('aria-pressed', String(Number(btn.dataset.val) === n))); } },
        el('span', { class: 'num', text: String(n) }), 'm');
      b.dataset.val = String(n);
      buttons.push(b);
      seg.append(b);
    });
    return el('div', { class: 'cfg-row' },
      el('div', null, el('div', { class: 'cfg-row__label', text: 'Interval' }), el('div', { class: 'cfg-row__hint', text: 'How often we check for changes' })), seg);
  }

  // ---------------- Screen: Add Pair wizard ----------------
  // Mock-flow state (browser standalone). Bridged flow uses addPairLive below.
  const addPair = { step: 0, srcId: null, dstId: null, name: '', windowDays: 14, intervalMin: 15 };

  // Bridged flow: source is local Outlook COM or an online account+calendar; destination is
  // always an online account+calendar. accountRef/calendarId come from the host.
  const addPairLive = {
    step: 0,
    editId: null,               // null = creating; a pair id = editing that pair (F2)
    sourceKind: null,           // 'com' | 'online'
    srcAccountRef: null, srcCalendarId: null, srcCalendarName: null,
    // Feature 2 — source multi-calendar selection. srcAllCalendars defaults ON ("all of the origin").
    // srcCalendarNames: COM display names; srcCalendarIds: Graph calendar ids (subset when not all).
    // The single srcCalendarId/srcCalendarName above are kept as the legacy/back-compat anchor (the
    // server still requires a non-empty source.calendarId), and as the label for the preview.
    srcAllCalendars: true, srcCalendarNames: [], srcCalendarIds: [],
    dstAccountRef: null, dstCalendarId: null, dstCalendarName: null,
    name: '', intervalMin: 15,
    // Originals captured on edit so step 3 can warn when source/destination changed (fresh start /
    // orphans left in the old destination). Unused when creating.
    origSourceKind: null, origSrcAccountRef: null, origSrcCalendarId: null,
    origSrcAllCalendars: true, origSrcCalendarNames: [], origSrcCalendarIds: [],
    origDstAccountRef: null, origDstCalendarId: null,
    origDstCalendarName: null,
    // Opt-in cleanup of the PREVIOUS destination when the destination is re-targeted (F2). Default
    // OFF — it is destructive. cleanupCount is the live "N events this pair copied there" the bridge
    // reports (null = not loaded yet, 'err' = the count call failed). The count is keyed to the old
    // destination so it is invalidated whenever that endpoint signature changes.
    cleanupPrevDest: false, cleanupCount: null, cleanupCountKey: null,
    // Inline error surfaced on the configure step (step 2) when create/updatePair fails. Kept on the
    // wizard state (not a local) so a failed save leaves the user on step 2 with everything they
    // entered intact and a red message + Retry, instead of silently dropping their work.
    submitError: null, submitting: false,
    // Subset mode (online source): a pending account switch awaiting confirmation. When the user taps
    // a calendar of a DIFFERENT account while a subset selection already exists, we stash the intent
    // here and show an inline confirm instead of silently wiping their prior selection.
    srcSwitchPrompt: null, // null | { accountRef, calendarId }
    // Account-accordion expand state for the wizard pickers (Sets of accountRefs). null = not yet
    // initialized; each picker lazily opens the selected/only account on first render.
    expandedSrc: null, expandedDst: null,
  };
  function resetAddPairLive() {
    Object.assign(addPairLive, {
      step: 0, editId: null, sourceKind: null,
      srcAccountRef: null, srcCalendarId: null, srcCalendarName: null,
      srcAllCalendars: true, srcCalendarNames: [], srcCalendarIds: [],
      dstAccountRef: null, dstCalendarId: null, dstCalendarName: null,
      name: '', intervalMin: 15,
      origSourceKind: null, origSrcAccountRef: null, origSrcCalendarId: null,
      origSrcAllCalendars: true, origSrcCalendarNames: [], origSrcCalendarIds: [],
      origDstAccountRef: null, origDstCalendarId: null, origDstCalendarName: null,
      cleanupPrevDest: false, cleanupCount: null, cleanupCountKey: null,
      submitError: null, submitting: false,
      srcSwitchPrompt: null,
      expandedSrc: null, expandedDst: null,
    });
  }

  // startEditPair(id) — preload the wizard from a SyncPair and open it in edit mode (F2). Looks the
  // raw pair up in live.pairs (it carries source/destination; the view model does not). Captures the
  // original source/destination so step 3 can warn when they change.
  function startEditPair(id) {
    if (!Bridge.available || !id) return;
    const pair = (live.pairs || []).find((p) => p && p.id === id);
    if (!pair) return;
    const src = pair.source || {};
    const dst = pair.destination || {};
    const com = (src.provider || '').toLowerCase() === 'outlookcom';
    // Feature 2 — preload the source selection. A legacy pair has no allCalendars/calendarIds/
    // calendarNames; treat the absence as "all" (the server's legacy fallback also reads everything
    // configured), so an unedited legacy pair round-trips without forcing a subset.
    const srcNames = Array.isArray(src.calendarNames) ? src.calendarNames.slice() : [];
    const srcIds = Array.isArray(src.calendarIds) ? src.calendarIds.slice() : [];
    const hasSubset = com ? srcNames.length > 0 : srcIds.length > 0;
    const allCalendars = src.allCalendars === true || (!src.allCalendars && !hasSubset);
    Object.assign(addPairLive, {
      step: 0,
      editId: id,
      sourceKind: com ? 'com' : 'online',
      srcAccountRef: com ? null : (src.accountRef || null),
      srcCalendarId: src.calendarId || (com ? 'local' : null),
      srcCalendarName: src.calendarName || (com ? 'Outlook (this PC)' : null),
      srcAllCalendars: allCalendars,
      srcCalendarNames: srcNames,
      srcCalendarIds: srcIds,
      dstAccountRef: dst.accountRef || null,
      dstCalendarId: dst.calendarId || null,
      dstCalendarName: dst.calendarName || null,
      name: pair.name || '',
      intervalMin: pair.intervalMin || 15,
      origSourceKind: com ? 'com' : 'online',
      origSrcAccountRef: com ? null : (src.accountRef || null),
      origSrcCalendarId: src.calendarId || (com ? 'local' : null),
      origSrcAllCalendars: allCalendars,
      origSrcCalendarNames: srcNames.slice(),
      origSrcCalendarIds: srcIds.slice(),
      origDstAccountRef: dst.accountRef || null,
      origDstCalendarId: dst.calendarId || null,
      origDstCalendarName: dst.calendarName || null,
      cleanupPrevDest: false, cleanupCount: null, cleanupCountKey: null,
      submitError: null, submitting: false, srcSwitchPrompt: null,
      expandedSrc: null, expandedDst: null,
    });
    navigate('add-pair');
  }

  function calendarPicker(value, onChange, exclude) {
    const wrap = el('div', { class: 'glass glass--card', style: 'padding:4px' });
    const list = el('div', { class: 'cal-list' });
    CALENDAR_LIBRARY.forEach((c) => {
      const disabled = exclude.includes(c.id);
      const selected = value === c.id;
      list.append(el('button', { class: `cal-item${selected ? ' is-selected' : ''}`, disabled, onclick: () => onChange(c.id) },
        pairBadge(c.svc),
        el('div', null, el('div', { class: 'cal-item__name', text: `${c.svc} · ${c.acct}` }), el('div', { class: 'cal-item__sub', text: c.email })),
        el('div', { class: 'cal-item__check', html: selected ? icon('check', { size: 12, stroke: 2.6 }) : '' }),
      ));
    });
    wrap.append(list);
    wrap.append(el('button', { class: 'cal-add', onclick: () => { state.returnTo = 'add-pair'; navigate('add-calendar'); } },
      iconEl('plus', 13, 2.2), el('span', { text: 'Connect a new calendar account…' })));
    return wrap;
  }

  function renderAddPair(root) {
    if (Bridge.available) { renderAddPairLive(root); return; }
    const labels = ['Source', 'Destination', 'Configure'];
    const src = CALENDAR_LIBRARY.find((c) => c.id === addPair.srcId);
    const dst = CALENDAR_LIBRARY.find((c) => c.id === addPair.dstId);
    if (src && dst && !addPair.name) addPair.name = `${src.acct} → ${dst.acct}`;

    root.append(viewHeader('Add a sync pair', { onBack: () => { if (addPair.step === 0) navigate('calendar'); else { addPair.step--; rerender(); } } }));
    root.append(wizardStepper(addPair.step, labels));

    if (addPair.step === 0) {
      root.append(el('div', { class: 'wizard-title', text: 'Pick the source calendar' }));
      root.append(el('div', { class: 'wizard-sub', text: 'Changes here will be mirrored to the destination. The source is never modified.' }));
      root.append(calendarPicker(addPair.srcId, (id) => { addPair.srcId = id; rerender(); }, [addPair.dstId]));
      root.append(el('div', { class: 'wizard-foot' },
        el('button', { class: 'btn btn--ghost', text: 'Cancel', onclick: () => navigate('calendar') }),
        el('button', { class: 'btn btn--primary', disabled: !addPair.srcId, onclick: () => { addPair.step = 1; rerender(); } },
          el('span', { text: 'Continue' }), iconEl('arrowright', 14, 1.8))));
    } else if (addPair.step === 1) {
      root.append(el('div', { class: 'wizard-title', text: 'Pick the destination' }));
      root.append(el('div', { class: 'wizard-sub', text: 'Events will be written here. Past events on the destination are never touched.' }));
      root.append(calendarPicker(addPair.dstId, (id) => { addPair.dstId = id; rerender(); }, [addPair.srcId]));
      root.append(el('div', { class: 'wizard-foot' },
        el('button', { class: 'btn btn--ghost', text: 'Back', onclick: () => { addPair.step = 0; rerender(); } }),
        el('button', { class: 'btn btn--primary', disabled: !addPair.dstId, onclick: () => { addPair.step = 2; rerender(); } },
          el('span', { text: 'Continue' }), iconEl('arrowright', 14, 1.8))));
    } else if (addPair.step === 2 && src && dst) {
      root.append(el('div', { class: 'wizard-title', text: 'Configure the sync' }));
      root.append(el('div', { class: 'wizard-sub', text: 'Review the pair and tune how often it runs.' }));
      const col = (label, name, email) => el('div', { class: 'pair-preview__col' },
        el('div', { class: 'route__label', text: label }), el('div', { class: 'pair-preview__name', text: name }), el('div', { class: 'pair-preview__email', text: email }));
      root.append(el('div', { class: 'glass glass--card pair-preview' },
        pairBadge(src.svc), col('SOURCE', `${src.svc} · ${src.acct}`, src.email),
        el('span', { style: 'color:var(--ink-3);display:inline-flex', html: icon('arrowright', { size: 14, stroke: 1.8 }) }),
        pairBadge(dst.svc), col('DESTINATION', `${dst.svc} · ${dst.acct}`, dst.email)));

      const cfg = el('div', { class: 'glass glass--card config-section', style: 'margin-top:10px' });
      const nameInput = el('input', { class: 'field-input', value: addPair.name });
      nameInput.addEventListener('input', () => { addPair.name = nameInput.value; });
      cfg.append(el('div', { class: 'cfg-row' },
        el('div', null, el('div', { class: 'cfg-row__label', text: 'Pair name' }), el('div', { class: 'cfg-row__hint', text: 'Shown on your dashboard' })), nameInput));
      cfg.append(sliderRow('Sync window', () => addPair.windowDays, (v) => { addPair.windowDays = v; }));
      cfg.append(intervalRow(() => addPair.intervalMin, (v) => { addPair.intervalMin = v; }));
      root.append(cfg);

      root.append(el('div', { class: 'wizard-foot' },
        el('button', { class: 'btn btn--ghost', text: 'Back', onclick: () => { addPair.step = 1; rerender(); } }),
        el('button', { class: 'btn btn--primary', onclick: () => completeAddPair() }, iconEl('check', 14, 2.2), el('span', { text: 'Create pair' }))));
    }
  }
  function completeAddPair() {
    // Mock/standalone-only path (the bridged shell routes through renderAddPairLive →
    // createPair). No bridge call here: persisting a near-empty config would clobber settings.
    addPair.step = 0; addPair.srcId = null; addPair.dstId = null; addPair.name = '';
    navigate('calendar');
  }

  // ---------------- Add Pair wizard (bridged / live data) ----------------
  // accountAccordionHead(acc, expandedSet, summary, active) — a clickable collapsible header for an
  // account in the wizard pickers. Toggling flips membership in expandedSet (a Set of accountRefs kept
  // on addPairLive, so it survives rerenders) and re-renders. `summary` is the right-side hint shown
  // (e.g. calendar count / selection); `active` highlights the header when this account is the chosen
  // source/destination. Returns { head, open } so the caller renders the calendars only when open.
  function accountAccordionHead(acc, expandedSet, summary, active) {
    const open = expandedSet.has(acc.accountRef);
    const accName = acc.displayName || acc.accountRef;
    const head = el('button', {
      class: `cal-acct-head${open ? ' is-open' : ''}${active ? ' is-active' : ''}`,
      type: 'button', 'aria-expanded': String(open),
      onclick: () => {
        if (open) expandedSet.delete(acc.accountRef); else expandedSet.add(acc.accountRef);
        if (state.view === 'add-pair') rerender();
      },
    },
      el('span', { class: 'cal-acct-head__chev', 'aria-hidden': 'true' }, iconEl('chevrondown', 14, 2.4)),
      el('span', { class: 'cal-acct-head__name', text: accName }),
      summary ? el('span', { class: 'cal-acct-head__sum', text: summary }) : null);
    return { head, open };
  }

  // A list of online accounts (from listAccounts) as collapsible accordions: an "+ Add account" button
  // sits on TOP, and each account's calendars (from listCalendars) show only when that account is
  // expanded. Selecting a calendar invokes onPick with the chosen endpoint. When `allowCreate` is true
  // (the DESTINATION step) each writable account also gets a "+ New calendar" affordance.
  function onlineCalendarPicker(selectedCalendarId, onPick, allowCreate) {
    const a = addPairLive;
    const wrap = el('div', { class: 'glass glass--card', style: 'padding:4px' });

    const accounts = live.accounts || [];

    // "+ Add account" on TOP (above the accounts), not buried after the calendars. A destination must
    // be writable, so connect read/write; clear any chosen calendar so Continue can't point at the old
    // account's calendar, and auto-expand the freshly added account.
    if (allowCreate) wrap.append(addAccountRow('readwrite', (newRef) => {
      a.dstAccountRef = newRef; a.dstCalendarId = null; a.dstCalendarName = null;
      if (newRef) { (a.expandedDst = a.expandedDst || new Set()).add(newRef); }
    }));

    if (accounts.length === 0) {
      wrap.append(el('div', { class: 'cal-item__sub', style: 'padding:12px', text: 'No connected accounts yet.' }));
      return wrap;
    }

    // Accordion expand state (survives rerenders). Initialize once: open the selected account, or the
    // only account when there is just one; otherwise everything starts collapsed.
    if (!a.expandedDst) {
      a.expandedDst = new Set();
      if (a.dstAccountRef) a.expandedDst.add(a.dstAccountRef);
      else if (accounts.length === 1) a.expandedDst.add(accounts[0].accountRef);
    }

    const list = el('div', { class: 'cal-list', role: 'list' });

    accounts.forEach((acc) => {
      const accName = acc.displayName || acc.accountRef;
      const cals = live.calendars[acc.accountRef];
      const active = a.dstAccountRef === acc.accountRef;
      const summary = cals ? `${cals.length} calendar${cals.length === 1 ? '' : 's'}` : '';

      const { head, open } = accountAccordionHead(acc, a.expandedDst, summary, active);
      const group = el('div', { class: `cal-acct-group${open ? ' is-open' : ''}`, role: 'group', 'aria-label': accName });
      group.append(head);
      list.append(group);
      if (!open) return;

      const body = el('div', { class: 'cal-acct-body' });
      group.append(body);

      if (!cals) {
        body.append(el('div', { class: 'cal-item__sub', style: 'padding:6px 12px', text: 'Loading calendars…' }));
        loadCalendars(acc.accountRef).then(() => { if (state.view === 'add-pair') rerender(); });
        return;
      }
      if (cals.length === 0) {
        body.append(el('div', { class: 'cal-item__sub', style: 'padding:6px 12px', text: 'No calendars on this account.' }));
      }
      cals.forEach((c) => {
        const selected = selectedCalendarId === c.id;
        body.append(el('button', { class: `cal-item${selected ? ' is-selected' : ''}`,
          onclick: () => onPick(acc, c) },
          pairBadge('Outlook'),
          el('div', null,
            el('div', { class: 'cal-item__name', text: c.displayName || c.id }),
            el('div', { class: 'cal-item__sub', text: acc.displayName || acc.accountRef })),
          el('div', { class: 'cal-item__check', html: selected ? icon('check', { size: 12, stroke: 2.6 }) : '' })));
      });

      // "+ New calendar" — destination only, and only on a WRITABLE account. Creating a calendar is a
      // Graph write, so a read-only destination account must be upgraded first (handled in the select
      // path); offering create here would write before the upgrade and fail.
      if (allowCreate && acc.scope !== 'read') body.append(newCalendarRow(acc, onPick));
    });

    wrap.append(list);
    return wrap;
  }

  // newCalendarRow(acc, onPick) — an inline "+ New calendar" control for one account. Clicking it
  // swaps in a name input + Create/Cancel; Create calls createCalendarFor, which on success refreshes
  // the account's calendars, selects the new one (onPick), and re-renders the wizard. Failures show
  // inline feedback without disturbing the rest of the picker. Pattern mirrors connectAccount.
  function newCalendarRow(acc, onPick) {
    const row = el('div', { class: 'cal-new' });

    const showForm = () => {
      row.replaceChildren();
      const input = el('input', { class: 'field-input cal-new__input', type: 'text', placeholder: 'New calendar name', 'aria-label': 'New calendar name', maxlength: '120' });
      const feedback = el('div', { class: 'cfg-row__hint cal-new__feedback' });
      const createBtn = el('button', { class: 'btn btn--primary cal-new__create', type: 'button' }, iconEl('plus', 12, 2), el('span', { text: 'Create' }));
      const cancelBtn = el('button', { class: 'btn btn--ghost cal-new__cancel', type: 'button', text: 'Cancel', onclick: showButton });
      const submit = () => {
        const name = (input.value || '').trim();
        if (!name) { feedback.textContent = 'Enter a name.'; feedback.style.color = 'var(--err)'; input.focus(); return; }
        createBtn.disabled = true; cancelBtn.disabled = true; input.disabled = true;
        const span = createBtn.querySelector('span'); if (span) span.textContent = 'Creating…';
        feedback.textContent = ''; feedback.style.color = '';
        createCalendarFor(acc.accountRef, name, onPick, (err) => {
          // Failure path: restore the form with an inline error.
          createBtn.disabled = false; cancelBtn.disabled = false; input.disabled = false;
          if (span) span.textContent = 'Create';
          feedback.textContent = err || 'Could not create the calendar.'; feedback.style.color = 'var(--err)';
          input.focus();
        });
      };
      createBtn.addEventListener('click', submit);
      input.addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); submit(); } });
      row.append(el('div', { class: 'cal-new__form' }, input, createBtn, cancelBtn), feedback);
      input.focus();
    };

    const showButton = () => {
      row.replaceChildren();
      row.append(el('button', { class: 'cal-new__trigger', type: 'button', onclick: showForm },
        iconEl('plus', 13, 2.2), el('span', { text: 'New calendar' })));
    };

    showButton();
    return row;
  }

  // addAccountRow(scope, onAdded) — inline "+ Add account" control for the wizard, mirroring
  // newCalendarRow. Clicking it runs connectAccount (OAuth in the system browser) with a Cancel; on
  // success onAdded(newRef) fires so the step can select the new account (newRef may be null if the
  // account was already connected — Phase B idempotency — in which case we just re-render and the user
  // taps it in the list). `scope` is 'read' for the source step, 'readwrite' for the destination step.
  function addAccountRow(scope, onAdded) {
    // role=listitem so it traverses cleanly when appended inside the wizard's role=list calendar list.
    // The trigger keeps `connect-cal__label` (connectAccount swaps that span to "Connecting…") but NOT
    // `connect-cal` itself, so it shares newCalendarRow's left-aligned pill look instead of stretching
    // full-width — the two inline "+ New calendar" / "+ Add account" rows then render consistently.
    const row = el('div', { class: 'cal-new', role: 'listitem' });
    const trigger = el('button', { class: 'cal-new__trigger', type: 'button' },
      iconEl('plus', 13, 2.2), el('span', { class: 'connect-cal__label', text: 'Add account' }));
    trigger.addEventListener('click', () => {
      connectAccount({ scope, btn: trigger, wrap: row, onConnected: (newRef) => {
        if (newRef && typeof onAdded === 'function') onAdded(newRef);
        if (state.view === 'add-pair') rerender();
      } });
    });
    row.append(trigger);
    return row;
  }

  // createCalendarFor(accountRef, name, onPick, onError) — create a calendar on the account through
  // the createCalendar bridge action, then invalidate + reload that account's calendar list, select
  // the freshly-created calendar (onPick) and re-render the wizard. onError gets a message on failure.
  function createCalendarFor(accountRef, name, onPick, onError) {
    if (!Bridge.available || !accountRef) { if (onError) onError('No bridge available.'); return; }
    Bridge.call('createCalendar', JSON.stringify({ accountRef, name }))
      .then((created) => {
        // Drop the cached calendars so the new one is included on reload, then select it.
        delete live.calendars[accountRef];
        return loadCalendars(accountRef).then((cals) => {
          const newId = created && created.id;
          const picked = (cals || []).find((c) => c.id === newId)
            || (created && created.id ? { id: created.id, displayName: created.displayName || name } : null);
          const acc = (live.accounts || []).find((a) => a.accountRef === accountRef) || { accountRef };
          if (picked && onPick) onPick(acc, picked);
          if (state.view === 'add-pair') rerender();
        });
      })
      .catch((e) => { if (onError) onError((e && e.message) || 'Could not create the calendar.'); });
  }

  // Feature 2 — the "All calendars" header row shared by both source multi-selects. A toggle that,
  // when ON, means "read every calendar of this origin"; when OFF, the per-calendar checkboxes below
  // drive the explicit subset.
  function allCalendarsRow(get, set) {
    // The toggle gets the REAL setter (plus a rerender) so BOTH mouse click and keyboard
    // (Space/Enter on the role=switch) flip the state and repaint. A no-op setter here would
    // make keyboard activation move aria-checked without changing the actual selection.
    const toggle = toggleLocal(get, (v) => { set(v); rerender(); }, 'All calendars');
    // Plain div (not <label>) and no row-level click handler: a label+click wrapper around the
    // switch double-fires on mouse (label click + toggle click) and bypasses the switch on keyboard.
    // Clicking the text label area instead forwards to the toggle so the whole row stays clickable.
    const row = el('div', { class: 'cal-multi__all' },
      el('div', { class: 'cal-multi__all-text' },
        el('div', { class: 'cal-item__name', text: 'All calendars' }),
        el('div', { class: 'cal-item__sub', text: 'Mirror every calendar from this source into the destination' })),
      toggle);
    row.querySelector('.cal-multi__all-text').addEventListener('click', () => { set(!get()); rerender(); });
    return row;
  }

  // A single multi-select calendar checkbox row (used for both COM names and Graph ids).
  function calCheckRow(label, sub, checked, onToggle) {
    return el('button', { class: `cal-item${checked ? ' is-selected' : ''}`, type: 'button', onclick: () => { onToggle(); rerender(); } },
      pairBadge('Outlook'),
      el('div', null,
        el('div', { class: 'cal-item__name', text: label }),
        sub ? el('div', { class: 'cal-item__sub', text: sub }) : null),
      el('div', { class: 'cal-item__check', html: checked ? icon('check', { size: 12, stroke: 2.6 }) : '' }));
  }

  // COM source multi-select: "All calendars" + a checkbox per local Outlook calendar (display names).
  // Lazy-loads the device's local calendars via the listLocalCalendars bridge action.
  function comSourceMultiSelect() {
    const a = addPairLive;
    const wrap = el('div', { class: 'glass glass--card cal-multi', style: 'padding:4px' });
    wrap.append(allCalendarsRow(() => a.srcAllCalendars, (v) => { a.srcAllCalendars = v; }));

    if (live.localCalendars === null) {
      if (!live.localCalendarsLoading)
        loadLocalCalendars().then(() => { if (state.view === 'add-pair') rerender(); });
      wrap.append(el('div', { class: 'cal-item__sub', style: 'padding:8px 12px', text: 'Loading calendars…' }));
      return wrap;
    }

    if (live.localCalendarsError) {
      wrap.append(el('div', { class: 'cal-item__sub', style: 'padding:8px 12px', text: 'Could not list local calendars. Outlook may not be available; "All calendars" will be used.' }));
      return wrap;
    }

    if (a.srcAllCalendars) return wrap; // subset list is hidden while "All" is on

    if (live.localCalendars.length === 0) {
      wrap.append(el('div', { class: 'cal-item__sub', style: 'padding:8px 12px', text: 'No local calendars found.' }));
      return wrap;
    }

    const list = el('div', { class: 'cal-list' });
    live.localCalendars.forEach((name) => {
      const checked = a.srcCalendarNames.includes(name);
      list.append(calCheckRow(name, 'Local Outlook', checked, () => {
        if (checked) a.srcCalendarNames = a.srcCalendarNames.filter((n) => n !== name);
        else a.srcCalendarNames = a.srcCalendarNames.concat([name]);
      }));
    });
    wrap.append(list);
    return wrap;
  }

  // Online source multi-select: an "+ Add account" button on TOP, then "All calendars", then each
  // connected account as a COLLAPSIBLE accordion — its calendars show only when the account is
  // expanded. Selecting calendars records their Graph ids in srcCalendarIds and pins srcAccountRef to
  // the account of the first selected calendar (a source pair targets ONE account's calendars).
  function onlineSourceMultiSelect() {
    const a = addPairLive;
    const wrap = el('div', { class: 'glass glass--card cal-multi', style: 'padding:4px' });

    const accounts = live.accounts || [];

    // "+ Add account" on TOP (above the accounts/options), not buried after the calendars. A source
    // only needs to be read, so connect read-only; auto-expand the freshly added account.
    wrap.append(addAccountRow('read', (newRef) => {
      a.srcAccountRef = newRef; a.srcSwitchPrompt = null;
      if (newRef) { (a.expandedSrc = a.expandedSrc || new Set()).add(newRef); }
    }));

    if (accounts.length === 0) {
      wrap.append(el('div', { class: 'cal-item__sub', style: 'padding:12px', text: 'No connected accounts yet.' }));
      return wrap;
    }

    // Accordion expand state (survives rerenders). Initialize once: open the selected account, or the
    // only account when there is just one; otherwise everything starts collapsed.
    if (!a.expandedSrc) {
      a.expandedSrc = new Set();
      if (a.srcAccountRef) a.expandedSrc.add(a.srcAccountRef);
      else if (accounts.length === 1) a.expandedSrc.add(accounts[0].accountRef);
    }

    // Flipping All on/off makes a pending subset account-switch confirm meaningless — clear it.
    wrap.append(allCalendarsRow(() => a.srcAllCalendars, (v) => { a.srcAllCalendars = v; a.srcSwitchPrompt = null; }));

    const list = el('div', { class: 'cal-list', role: 'list' });

    // applySwitch — commit a confirmed account switch in subset mode: drop the old account's
    // selection, pin the new account, and select the tapped calendar as the first member.
    const applySwitch = (acc, c, cals) => {
      a.srcAccountRef = acc.accountRef;
      a.srcCalendarIds = [c.id];
      a.srcCalendarId = c.id;
      a.srcCalendarName = c.displayName || c.id;
      a.srcSwitchPrompt = null;
    };

    accounts.forEach((acc) => {
      const accName = acc.displayName || acc.accountRef;
      const cals = live.calendars[acc.accountRef];
      const accountChosen = a.srcAccountRef === acc.accountRef;

      // Collapsed summary: the selection state when chosen, else the calendar count.
      let summary;
      if (accountChosen && a.srcAllCalendars) summary = 'Source · all';
      else if (accountChosen && a.srcCalendarIds.length) summary = `${a.srcCalendarIds.length} selected`;
      else if (cals) summary = `${cals.length} calendar${cals.length === 1 ? '' : 's'}`;
      else summary = '';

      const { head, open } = accountAccordionHead(acc, a.expandedSrc, summary, accountChosen);
      const group = el('div', { class: `cal-acct-group${open ? ' is-open' : ''}`, role: 'group', 'aria-label': accName });
      group.append(head);
      list.append(group);
      if (!open) return;

      const body = el('div', { class: 'cal-acct-body' });
      group.append(body);

      if (!cals) {
        body.append(el('div', { class: 'cal-item__sub', style: 'padding:6px 12px', text: 'Loading calendars…' }));
        loadCalendars(acc.accountRef).then(() => { if (state.view === 'add-pair') rerender(); });
        return;
      }
      if (cals.length === 0) {
        body.append(el('div', { class: 'cal-item__sub', style: 'padding:6px 12px', text: 'No calendars on this account.' }));
        return;
      }

      // While "All" is on, individual calendar checkboxes do NOT apply — "All" already covers every
      // calendar of the chosen account. Instead of faking each calendar as checked (confusing: a tap
      // re-pins the same account with no real change), pick the ACCOUNT explicitly and render its
      // calendars as a read-only, informative list so it is clear what "All" will include.
      if (a.srcAllCalendars) {
        body.append(calCheckRow(
          accName,
          accountChosen ? 'Source account — All calendars below will be mirrored' : 'Use this account as the source',
          accountChosen,
          () => {
            a.srcAccountRef = acc.accountRef;
            // Anchor the legacy fields on the account's first calendar (server requires a calendarId).
            const first = cals[0];
            a.srcCalendarId = first ? first.id : null;
            a.srcCalendarName = first ? (first.displayName || first.id) : null;
          }));
        // Read-only "included by All" list, as a semantic list of the account's calendars.
        const roList = el('div', { class: 'cal-readonly-list', role: 'list', 'aria-label': `Calendars on ${accName}` });
        cals.forEach((c) => {
          roList.append(el('div', { class: `cal-item is-readonly${accountChosen ? '' : ' is-muted'}`, role: 'listitem', 'aria-disabled': 'true' },
            pairBadge('Outlook'),
            el('div', null,
              el('div', { class: 'cal-item__name', text: c.displayName || c.id }),
              el('div', { class: 'cal-item__sub', text: accountChosen ? 'Included by “All calendars”' : 'Pick this account to include' }))));
        });
        body.append(roList);
        return;
      }

      // Subset mode: each calendar is an independently selectable checkbox.
      cals.forEach((c) => {
        const checked = accountChosen && a.srcCalendarIds.includes(c.id);
        const itemRow = el('div', { role: 'listitem' });
        itemRow.append(calCheckRow(c.displayName || c.id, accName, checked, () => {
          const switchingAccount = a.srcAccountRef && a.srcAccountRef !== acc.accountRef;
          // Switching to a DIFFERENT account would wipe the existing subset. Don't do it silently:
          // stash the intent and surface an inline confirm (rendered just below this row). A tap on
          // the SAME account, or when nothing is selected yet, applies immediately as before.
          if (switchingAccount && a.srcCalendarIds.length > 0) {
            a.srcSwitchPrompt = { accountRef: acc.accountRef, calendarId: c.id };
            return;
          }
          if (a.srcAccountRef !== acc.accountRef) { a.srcAccountRef = acc.accountRef; a.srcCalendarIds = []; }
          a.srcSwitchPrompt = null;
          if (a.srcCalendarIds.includes(c.id)) a.srcCalendarIds = a.srcCalendarIds.filter((x) => x !== c.id);
          else a.srcCalendarIds = a.srcCalendarIds.concat([c.id]);
          // Keep the legacy anchor pointing at the first selected calendar (server requires calendarId).
          const first = a.srcCalendarIds[0];
          const fc = first ? cals.find((x) => x.id === first) : null;
          a.srcCalendarId = first || null;
          a.srcCalendarName = fc ? (fc.displayName || fc.id) : null;
        }));
        body.append(itemRow);

        // Inline confirm for the pending account switch, anchored under the tapped calendar.
        if (a.srcSwitchPrompt && a.srcSwitchPrompt.accountRef === acc.accountRef && a.srcSwitchPrompt.calendarId === c.id) {
          const prevName = (accounts.find((x) => x.accountRef === a.srcAccountRef) || {});
          const prevLabel = prevName.displayName || prevName.accountRef || 'the other account';
          const confirm = el('div', { class: 'glass glass--card cal-switch-confirm', role: 'alert' },
            el('div', { class: 'cal-switch-confirm__text',
              text: `A source pair uses one account. Switching to “${accName}” clears the ${a.srcCalendarIds.length} calendar${a.srcCalendarIds.length === 1 ? '' : 's'} selected on “${prevLabel}”.` }),
            el('div', { class: 'cal-switch-confirm__actions' },
              el('button', { class: 'btn btn--ghost', type: 'button', text: 'Keep current', onclick: () => { a.srcSwitchPrompt = null; rerender(); } }),
              el('button', { class: 'btn btn--primary', type: 'button', text: 'Switch account', onclick: () => { applySwitch(acc, c, cals); rerender(); } })));
          body.append(confirm);
        }
      });
    });
    wrap.append(list);
    return wrap;
  }

  // Human label for the source selection on the configure/preview step.
  function sourceSelectionLabel() {
    const a = addPairLive;
    if (a.srcAllCalendars) return 'All calendars';
    if (a.sourceKind === 'com') {
      const n = a.srcCalendarNames.length;
      return n === 0 ? '(no calendars selected)' : (n === 1 ? a.srcCalendarNames[0] : `${n} calendars`);
    }
    const n = a.srcCalendarIds.length;
    return n === 0 ? '(no calendars selected)' : (n === 1 ? (a.srcCalendarName || '1 calendar') : `${n} calendars`);
  }

  // True when the source selection is complete enough to continue.
  function sourceSelectionReady() {
    const a = addPairLive;
    if (!a.sourceKind) return false;
    if (a.srcAllCalendars) {
      // COM all-calendars needs nothing else; online all-calendars needs an account pinned.
      return a.sourceKind === 'com' || !!a.srcAccountRef;
    }
    return a.sourceKind === 'com'
      ? a.srcCalendarNames.length > 0
      : (!!a.srcAccountRef && a.srcCalendarIds.length > 0);
  }

  function renderAddPairLive(root) {
    const labels = ['Source', 'Destination', 'Configure'];
    const a = addPairLive;
    const editing = !!a.editId;

    root.append(viewHeader(editing ? 'Edit sync pair' : 'Add a sync pair', { onBack: () => { if (a.step === 0) { resetAddPairLive(); navigate('calendar'); } else { a.step--; rerender(); } } }));
    root.append(wizardStepper(a.step, labels));

    if (live.accounts === null) {
      loadAccounts().then(() => { if (state.view === 'add-pair') rerender(); });
    }

    // Auto-pin a single online account: when exactly one account is connected and the online source
    // has no account selected yet, preselect it so the user is not forced to pick the only option.
    if (a.step <= 1 && Array.isArray(live.accounts) && live.accounts.length === 1) {
      const only = live.accounts[0].accountRef;
      if (a.sourceKind === 'online' && !a.srcAccountRef) a.srcAccountRef = only;
      if (a.step === 1 && !a.dstAccountRef && !a.dstCalendarId) a.dstAccountRef = only;
    }

    if (a.step === 0) {
      root.append(el('div', { class: 'wizard-title', text: 'Pick the source calendar' }));
      root.append(el('div', { class: 'wizard-sub', text: 'Changes here are mirrored to the destination. The source is never modified.' }));

      // Two source kinds: local Outlook (COM) or an online account calendar. The COM tile is ALWAYS
      // rendered, but disabled (no-op onclick) when Outlook is not available on this device.
      const kinds = el('div', { class: 'provider-grid' });
      const comOff = !comAvailable();
      kinds.append(el('button', { class: `provider-tile glass${a.sourceKind === 'com' ? ' is-selected' : ''}${comOff ? ' is-disabled' : ''}`,
        disabled: comOff,
        title: comOff ? 'Not available on this device' : undefined,
        onclick: () => { if (comOff) return; a.sourceKind = 'com'; a.srcCalendarId = 'local'; a.srcCalendarName = 'Outlook (this PC)'; a.srcAccountRef = null; rerender(); } },
        el('div', { class: 'provider-tile__logo', dataset: { tone: 'azure' }, text: 'PC' }),
        el('div', { class: 'provider-tile__name', text: 'Outlook on this PC' }),
        el('div', { class: 'provider-tile__sub', text: comOff ? 'Not available on this device' : 'Local Outlook · read via COM' })));
      kinds.append(el('button', { class: `provider-tile glass${a.sourceKind === 'online' ? ' is-selected' : ''}`,
        onclick: () => { a.sourceKind = 'online'; rerender(); } },
        el('div', { class: 'provider-tile__logo', dataset: { tone: 'ink' }, text: 'M' }),
        el('div', { class: 'provider-tile__name', text: 'Online account' }),
        el('div', { class: 'provider-tile__sub', text: 'outlook.com · via the server' })));
      root.append(kinds);

      // Feature 2 — once a source kind is chosen, show the multi-calendar selection for THAT origin:
      // "All calendars" (default) or an explicit subset. The destination (step 1) stays single.
      if (a.sourceKind === 'com') {
        root.append(comSourceMultiSelect());
        // Track B — tell the user up front that a COM source is read on THIS machine, mirroring the
        // dashboard pin-note. The pair will be pinned to this device (completeAddPairLive sends our
        // deviceId), so it can only sync while this computer's app is running.
        root.append(el('div', { class: 'pair__pin-note', dataset: { kind: 'local' } },
          el('span', { style: 'display:inline-flex', html: icon('check', { size: 12, stroke: 1.8 }) }),
          el('span', { text: 'This sync will run on this computer (where Outlook is installed).' })));
      } else if (a.sourceKind === 'online') {
        root.append(onlineSourceMultiSelect());
      }

      const srcReady = sourceSelectionReady();
      root.append(el('div', { class: 'wizard-foot' },
        el('button', { class: 'btn btn--ghost', text: 'Cancel', onclick: () => { resetAddPairLive(); navigate('calendar'); } }),
        el('button', { class: 'btn btn--primary', disabled: !srcReady, onclick: () => { a.step = 1; rerender(); } },
          el('span', { text: 'Continue' }), iconEl('arrowright', 14, 1.8))));
      if (!srcReady) root.append(el('div', { class: 'wizard-sub', text: 'Pick a source calendar to continue' }));
    } else if (a.step === 1) {
      root.append(el('div', { class: 'wizard-title', text: 'Pick the destination' }));
      root.append(el('div', { class: 'wizard-sub', text: 'Events are written here. Past events on the destination are never touched.' }));
      root.append(onlineCalendarPicker(a.dstCalendarId, (acc, c) => {
        // A destination must be writable. If this account is read-only, grant read/write first
        // (interactive consent) before committing it as the destination; on cancel/fail, do nothing.
        if (acc.scope === 'read') {
          announce('Granting write access to this account…');
          Bridge.call('upgradeAccountScope', JSON.stringify(acc.accountRef), 210000)
            .then((r) => {
              if (r && r.connected) {
                live.accounts = null;
                return loadAccounts().then(() => {
                  a.dstAccountRef = acc.accountRef; a.dstCalendarId = c.id; a.dstCalendarName = c.displayName || c.id;
                  rerender();
                });
              }
              announce((r && r.cancelled) ? 'Upgrade cancelled.' : 'Could not grant write access.');
            })
            .catch(() => announce('Could not grant write access.'));
          return;
        }
        a.dstAccountRef = acc.accountRef; a.dstCalendarId = c.id; a.dstCalendarName = c.displayName || c.id; rerender();
      }, true));
      root.append(el('div', { class: 'wizard-foot' },
        el('button', { class: 'btn btn--ghost', text: 'Back', onclick: () => { a.step = 0; rerender(); } }),
        el('button', { class: 'btn btn--primary', disabled: !a.dstCalendarId, onclick: () => { a.step = 2; rerender(); } },
          el('span', { text: 'Continue' }), iconEl('arrowright', 14, 1.8))));
      if (!a.dstCalendarId) root.append(el('div', { class: 'wizard-sub', text: 'Pick a destination calendar to continue' }));
    } else if (a.step === 2) {
      if (!a.name) a.name = `${sourceSelectionLabel()} → ${a.dstCalendarName}`;
      root.append(el('div', { class: 'wizard-title', text: editing ? 'Edit the sync' : 'Configure the sync' }));
      root.append(el('div', { class: 'wizard-sub', text: 'Review the pair and tune how often it runs.' }));

      const col = (label, name, sub) => el('div', { class: 'pair-preview__col' },
        el('div', { class: 'route__label', text: label }), el('div', { class: 'pair-preview__name', text: name }), el('div', { class: 'pair-preview__email', text: sub }));
      root.append(el('div', { class: 'glass glass--card pair-preview' },
        pairBadge('Outlook'), col('SOURCE', sourceSelectionLabel(), a.sourceKind === 'com' ? 'Local Outlook' : (a.srcAccountRef || '')),
        el('span', { style: 'color:var(--ink-3);display:inline-flex', html: icon('arrowright', { size: 14, stroke: 1.8 }) }),
        pairBadge('Outlook'), col('DESTINATION', a.dstCalendarName, a.dstAccountRef)));

      // F2 warning: when editing and the source OR destination changed, the next run starts fresh
      // (the new destination has no events tagged with this source).
      if (editing && editEndpointsChanged()) {
        const destChanged = editDestChanged();
        if (destChanged) {
          // The previous destination keeps the events this pair copied there. Offer an opt-in,
          // destructive cleanup of ONLY those events (the bridge enforces "this pair only" and refuses
          // the current destination). Default OFF; the live count is shown once the bridge reports it.
          root.append(buildCleanupPrevDestCard());
        } else {
          root.append(el('div', { class: 'glass glass--card wizard-warn' },
            el('div', { class: 'wizard-warn__text', text: 'Changing the source restarts the sync. The destination will be reconciled against the new source on the next run.' })));
        }
      }

      const cfg = el('div', { class: 'glass glass--card config-section', style: 'margin-top:10px' });
      const nameInput = el('input', { class: 'field-input', value: a.name });
      nameInput.addEventListener('input', () => { a.name = nameInput.value; });
      cfg.append(el('div', { class: 'cfg-row' },
        el('div', null, el('div', { class: 'cfg-row__label', text: 'Pair name' }), el('div', { class: 'cfg-row__hint', text: 'Shown on your dashboard' })), nameInput));
      cfg.append(intervalRow(() => a.intervalMin, (v) => { a.intervalMin = v; }));
      root.append(cfg);

      // Secondary action while editing: export this month of the pair's source to a .txt, reusing
      // the same modal the per-pair Export button opens (routes by source provider inside).
      // The export reads the SAVED source from the server (the value before this edit's PATCH), so
      // while there are unsaved endpoint changes it would export the OLD source — confusing and wrong.
      // Disable it in that case with a clear "save first" hint; for a COM source it also needs Outlook.
      // Desktop-only: the export ends in a local save dialog (no save-to-disk channel in the web
      // panel), so the affordance is hidden unless Bridge.desktopApp — same gate as the per-pair button.
      if (editing && Bridge.desktopApp) {
        const srcCom = a.sourceKind === 'com';
        const unsavedEndpoints = editEndpointsChanged();
        const exportBlocked = unsavedEndpoints || (srcCom && !comAvailable());
        const blockTitle = unsavedEndpoints
          ? 'Save changes before exporting'
          : (srcCom && !comAvailable() ? 'Outlook is not available on this device' : 'Export a month of the source calendar to a .txt file');
        root.append(el('div', { style: 'margin-top:10px' },
          el('button', { class: 'btn btn--ghost', style: 'width:100%',
            disabled: exportBlocked,
            title: blockTitle,
            onclick: () => { if (!exportBlocked) openExportTxtModal(editPairForModal()); } },
            iconEl('folder', 13, 1.6),
            el('span', { text: unsavedEndpoints ? 'Save changes before exporting' : 'Export this month to .txt' }))));
      }

      // Inline error from a failed create/updatePair: a red message right above the actions so the
      // user sees WHY the save failed without leaving the step (their entries are untouched).
      if (a.submitError) {
        root.append(el('div', { class: 'glass glass--card wizard-warn wizard-warn--err', role: 'alert' },
          el('div', { class: 'wizard-warn__text', text: a.submitError })));
      }

      const submitBtn = el('button', { class: 'btn btn--primary', disabled: !!a.submitting, onclick: () => completeAddPairLive() },
        iconEl('check', 14, 2.2),
        el('span', { text: a.submitting ? 'Saving…' : (a.submitError ? 'Retry' : (editing ? 'Save changes' : 'Create pair')) }));
      root.append(el('div', { class: 'wizard-foot' },
        el('button', { class: 'btn btn--ghost', text: 'Back', disabled: !!a.submitting, onclick: () => { a.step = 1; rerender(); } }),
        submitBtn));
    }
  }

  // True when the wizard is editing and the source or destination differs from the loaded original.
  function editEndpointsChanged() {
    const a = addPairLive;
    if (!a.editId) return false;
    // Compare a sorted copy so reordering selections is not treated as a change.
    const sameSet = (x, y) => {
      const ax = (x || []).slice().sort();
      const ay = (y || []).slice().sort();
      return ax.length === ay.length && ax.every((v, i) => v === ay[i]);
    };
    const selectionChanged = (!!a.srcAllCalendars !== !!a.origSrcAllCalendars)
      || !sameSet(a.srcCalendarNames, a.origSrcCalendarNames)
      || !sameSet(a.srcCalendarIds, a.origSrcCalendarIds);
    const srcChanged = a.sourceKind !== a.origSourceKind
      || a.srcCalendarId !== a.origSrcCalendarId
      || ((a.srcAccountRef || null) !== (a.origSrcAccountRef || null))
      || selectionChanged;
    const dstChanged = a.dstCalendarId !== a.origDstCalendarId
      || ((a.dstAccountRef || null) !== (a.origDstAccountRef || null));
    return srcChanged || dstChanged;
  }

  // True when editing and the DESTINATION specifically changed (account or calendar). The
  // source-only case is handled separately — only a destination change can orphan events in a
  // previous calendar.
  function editDestChanged() {
    const a = addPairLive;
    if (!a.editId) return false;
    return a.dstCalendarId !== a.origDstCalendarId
      || ((a.dstAccountRef || null) !== (a.origDstAccountRef || null));
  }

  // The PREVIOUS destination endpoint (the value before this edit), as the bridge expects it. Always
  // MicrosoftGraph — destinations are online accounts. Returns null when there is no original (creating).
  function oldDestinationEndpoint() {
    const a = addPairLive;
    if (!a.editId || !a.origDstCalendarId) return null;
    return {
      provider: 'MicrosoftGraph',
      accountRef: a.origDstAccountRef || null,
      calendarId: a.origDstCalendarId,
      calendarName: a.origDstCalendarName || a.origDstCalendarId,
    };
  }

  // A stable signature of the old destination, used to invalidate the cached cleanup count when the
  // edit context changes (e.g. a different pair is opened).
  function oldDestinationKey() {
    const a = addPairLive;
    return `${a.editId || ''}|${a.origDstAccountRef || ''}|${a.origDstCalendarId || ''}`;
  }

  // buildCleanupPrevDestCard() — the destination-changed card: a plain notice that the old
  // destination keeps its copied events, plus an OPT-IN (default OFF) toggle to delete only the events
  // THIS pair copied there. Shows the live count once the bridge reports it. The toggle drives
  // addPairLive.cleanupPrevDest, which completeAddPairLive reads after a successful PATCH.
  function buildCleanupPrevDestCard() {
    const a = addPairLive;
    const card = el('div', { class: 'glass glass--card wizard-warn' });
    card.append(el('div', { class: 'wizard-warn__text',
      text: 'Changing the destination starts a fresh sync into the new calendar. Events this pair already copied into the previous destination are left there unless you remove them below.' }));

    // The count is tied to the old destination; refetch when the context key changes.
    const key = oldDestinationKey();
    if (a.cleanupCountKey !== key) { a.cleanupCount = null; a.cleanupCountKey = key; }

    const countLabel = el('span', { class: 'cleanup-opt__count' });
    const renderCount = () => {
      if (a.cleanupCount === null) { countLabel.textContent = 'Counting…'; countLabel.dataset.state = 'loading'; }
      else if (a.cleanupCount === 'err') { countLabel.textContent = 'count unavailable'; countLabel.dataset.state = 'err'; }
      else { countLabel.textContent = `${a.cleanupCount} event${a.cleanupCount === 1 ? '' : 's'}`; countLabel.dataset.state = 'ok'; }
    };
    renderCount();

    // Fetch the count once per context (only when a bridge is available and there is an old dest).
    if (a.cleanupCount === null && Bridge.available) {
      const dest = oldDestinationEndpoint();
      if (dest) {
        Bridge.call('countManagedInDestination', JSON.stringify({ pairId: a.editId, destination: dest }))
          .then((r) => { a.cleanupCount = (r && typeof r.count === 'number') ? r.count : 0; })
          .catch(() => { a.cleanupCount = 'err'; })
          .then(() => { if (state.view === 'add-pair') renderCount(); });
      } else {
        a.cleanupCount = 0; renderCount();
      }
    }

    const toggle = toggleLocal(() => a.cleanupPrevDest, (v) => { a.cleanupPrevDest = v; }, 'Remove the events already copied to the previous destination');
    const optRow = el('label', { class: 'cleanup-opt' },
      el('div', { class: 'cleanup-opt__text' },
        el('div', { class: 'cleanup-opt__title', text: 'Also remove the events already copied to the previous destination' }),
        el('div', { class: 'cleanup-opt__hint' },
          el('span', { text: 'Permanently deletes only the events this pair created there — ' }), countLabel, el('span', { text: '. Events you created yourself are never touched.' }))),
      toggle);
    card.append(optRow);
    return card;
  }

  // Builds a minimal pair-like object the export modal understands (it reads pair.id and
  // pair.src.provider), from the in-edit wizard state.
  function editPairForModal() {
    const a = addPairLive;
    return {
      id: a.editId,
      src: { provider: a.sourceKind === 'com' ? 'OutlookCom' : 'MicrosoftGraph' },
    };
  }

  function completeAddPairLive() {
    const a = addPairLive;
    // Feature 2 — carry the source multi-calendar selection. allCalendars=true => read every calendar
    // of the origin; otherwise the typed subset (calendarNames for COM, calendarIds for Graph). The
    // single calendarId stays as the server-required anchor (a non-empty calendarId). The destination
    // is always a single calendar and never carries the selection fields.
    const source = a.sourceKind === 'com'
      ? {
          provider: 'OutlookCom', calendarId: 'local', calendarName: 'Outlook (this PC)',
          allCalendars: !!a.srcAllCalendars,
          calendarNames: a.srcAllCalendars ? [] : a.srcCalendarNames.slice(),
        }
      : {
          provider: 'MicrosoftGraph', accountRef: a.srcAccountRef,
          calendarId: a.srcCalendarId, calendarName: a.srcCalendarName,
          allCalendars: !!a.srcAllCalendars,
          calendarIds: a.srcAllCalendars ? [] : a.srcCalendarIds.slice(),
        };
    const destination = { provider: 'MicrosoftGraph', accountRef: a.dstAccountRef, calendarId: a.dstCalendarId, calendarName: a.dstCalendarName };

    // Decide cleanup BEFORE the save mutates state: only when editing, the destination changed, the
    // user opted in, and there is a real previous destination. Capture the old endpoint + label now.
    const wantsCleanup = !!a.editId && editDestChanged() && a.cleanupPrevDest;
    const oldDest = wantsCleanup ? oldDestinationEndpoint() : null;
    const oldDestName = a.origDstCalendarName || a.origDstCalendarId || 'the previous calendar';
    const pairId = a.editId;
    const knownCount = (typeof a.cleanupCount === 'number') ? a.cleanupCount : null;

    // Edit (F2) updates in place (preserving the id); create makes a new pair. Both send the same
    // {name, source, destination, intervalMin}; updatePair additionally carries the id.
    // Enter the submitting state and clear any prior error before the call (drives the button label +
    // disables Back/Submit so the user can't double-submit or navigate mid-flight).
    a.submitting = true;
    a.submitError = null;
    if (state.view === 'add-pair') rerender();

    // Track B — pin a COM source to THIS device up front, using the deviceId cached by ensureLocalDevice.
    // The server pins the pair at creation so the dashboard shows "Source is on this PC" immediately
    // instead of waiting for the first push to claim it. Only for create (edit keeps its existing pin)
    // and only when the source is COM and we actually know our deviceId; otherwise omit it (the server
    // ignores a pin on a non-COM pair, and claim-on-first-push remains the safety net).
    const createPayload = { name: a.name, source, destination, intervalMin: a.intervalMin };
    if (a.sourceKind === 'com' && live.device && live.device.deviceId)
      createPayload.pinnedDeviceId = live.device.deviceId;

    const call = a.editId
      ? Bridge.call('updatePair', JSON.stringify({ id: a.editId, name: a.name, intervalMin: a.intervalMin, source, destination }))
      : Bridge.call('createPair', JSON.stringify(createPayload));

    const finish = () => loadPairs().then(() => { resetAddPairLive(); navigate('calendar'); });

    call
      .then(() => {
        // PATCH succeeded. Run the destructive cleanup ONLY now (never if the save failed), behind an
        // explicit confirm. cleanupOldDestination deletes only the events this pair created in the old
        // destination — the server refuses the pair's CURRENT destination, so this can't undo the save.
        if (wantsCleanup && oldDest) {
          return confirmCleanupOldDestination(pairId, oldDest, oldDestName, knownCount).then(finish);
        }
        return finish();
      })
      .catch((e) => {
        // Save failed: do NOT discard what the user entered or navigate away. Surface the reason
        // inline on the configure step and let them fix/retry — the button flips to "Retry".
        a.submitting = false;
        a.submitError = (e && e.message)
          ? `Could not ${a.editId ? 'save the pair' : 'create the pair'}: ${e.message}`
          : `Could not ${a.editId ? 'save the pair' : 'create the pair'}. Please try again.`;
        if (state.view === 'add-pair') rerender();
      });
  }

  // confirmCleanupOldDestination(pairId, oldDest, oldDestName, knownCount) — a secondary, explicit
  // confirm for the destructive cleanup, then the cleanupOldDestination bridge call. Always resolves
  // (never rejects): a declined confirm or a failed cleanup still lets the edit finish cleanly — the
  // PATCH already succeeded, and the cleanup is idempotent (a later edit can retry). Returns a Promise.
  function confirmCleanupOldDestination(pairId, oldDest, oldDestName, knownCount) {
    return new Promise((resolve) => {
      let settled = false;
      const done = () => { if (!settled) { settled = true; resolve(); } };

      const countText = (knownCount === null)
        ? `the events this pair copied to ${oldDestName}`
        : `${knownCount} event${knownCount === 1 ? '' : 's'} this pair copied to ${oldDestName}`;

      const feedback = el('div', { class: 'cfg-row__hint modal__feedback', style: 'min-height:16px' });
      const cancelBtn = el('button', { class: 'btn btn--ghost', type: 'button', text: 'Keep them' });
      const confirmBtn = el('button', { class: 'btn btn--danger', type: 'button' }, iconEl('alert', 13, 1.8), el('span', { text: 'Delete them' }));

      const body = el('div', { class: 'cleanup-confirm' },
        el('div', { class: 'cleanup-confirm__text',
          text: `This permanently deletes ${countText}. Events you created yourself in that calendar are never touched.` }),
        feedback,
        el('div', { class: 'modal__foot' }, cancelBtn, confirmBtn));

      const modal = openModal({ title: 'Remove old copies?', body, onClose: done });

      cancelBtn.addEventListener('click', () => modal.close());
      confirmBtn.addEventListener('click', () => {
        const span = confirmBtn.querySelector('span');
        confirmBtn.disabled = true; cancelBtn.disabled = true;
        if (span) span.textContent = 'Removing…';
        feedback.textContent = ''; feedback.style.color = '';
        Bridge.call('cleanupOldDestination', JSON.stringify({ pairId, destination: oldDest }), 120000)
          .then((r) => {
            const deleted = (r && typeof r.deleted === 'number') ? r.deleted : 0;
            const failures = (r && Array.isArray(r.failures)) ? r.failures.length : 0;
            announce(failures ? `Removed ${deleted}; ${failures} could not be removed.` : `Removed ${deleted} old ${deleted === 1 ? 'copy' : 'copies'}.`);
            if (span) span.textContent = 'Done';
            feedback.style.color = failures ? 'var(--warn)' : 'var(--ok)';
            feedback.textContent = failures ? `Removed ${deleted}; ${failures} could not be removed.` : `Removed ${deleted}.`;
            setTimeout(() => modal.close(), 800);
          })
          .catch((e) => {
            // The edit is already saved; surface the cleanup error but let the user dismiss and move on.
            confirmBtn.disabled = false; cancelBtn.disabled = false;
            if (span) span.textContent = 'Delete them';
            feedback.textContent = (e && e.message) || 'Could not remove the old copies.';
            feedback.style.color = 'var(--err)';
          });
      });
    });
  }

  // ---------------- Screen: Add Calendar wizard ----------------
  const addCal = { step: 0, providerId: null, selected: new Set(['d1', 'd2']), timer: null };

  function renderAddCalendar(root) {
    // Demo-only wizard. Every literal below (PROVIDERS / DISCOVERED, the fabricated
    // `zyncmaster.app/auth/.../8h3-4f2a` link, the 2.4s OAuth-simulating timer) is mock data, so it
    // must never paint in a real transport. In the App / web panel, connecting an account is the
    // server-driven flow (renderAddPairLive → onlineCalendarPicker, sourced from listAccounts /
    // listCalendars), so bounce there instead of showing this fabricated wizard.
    if (Bridge.available) {
      clearTimeout(addCal.timer);
      navigate('add-pair');
      return;
    }

    const labels = ['Provider', 'Authorize', 'Calendars'];
    const provider = PROVIDERS.find((p) => p.id === addCal.providerId);

    root.append(viewHeader('Connect a calendar account', { onBack: () => { if (addCal.step === 0) backFromAddCalendar(); else { addCal.step--; rerender(); } } }));
    root.append(wizardStepper(addCal.step, labels));

    if (addCal.step === 0) {
      root.append(el('div', { class: 'wizard-title', text: 'Choose a provider' }));
      root.append(el('div', { class: 'wizard-sub', text: 'Zync Master will use a read-only or read/write connection to this account, as you choose later.' }));
      const grid = el('div', { class: 'provider-grid' });
      PROVIDERS.forEach((p) => grid.append(el('button', { class: 'provider-tile glass', onclick: () => { addCal.providerId = p.id; addCal.step = 1; rerender(); } },
        el('div', { class: 'provider-tile__logo', dataset: { tone: p.tone }, text: p.id === 'icloud' ? 'i' : p.letter }),
        el('div', { class: 'provider-tile__name', text: p.name }),
        el('div', { class: 'provider-tile__sub', text: p.sub }))));
      root.append(grid);
      root.append(el('div', { class: 'wizard-foot' },
        el('button', { class: 'btn btn--ghost', text: 'Cancel', onclick: () => backFromAddCalendar() }),
        el('span', { style: 'font-size:11px;color:var(--ink-3)', text: 'Your credentials stay with the provider.' })));
    } else if (addCal.step === 1 && provider) {
      const orb = el('div', { class: 'provider-tile__logo', dataset: { tone: provider.tone }, style: 'width:56px;height:56px;margin:4px auto 0;border-radius:14px;position:relative' },
        el('span', { style: 'font-size:22px;font-weight:700', text: provider.id === 'icloud' ? 'i' : provider.letter }),
        el('span', { class: 'spinner', style: 'position:absolute;inset:-6px;width:68px;height:68px;border-width:1.5px;border-color:transparent;border-top-color:currentColor;border-radius:16px' }));
      root.append(el('div', { class: 'glass glass--card pair-card' },
        orb,
        el('div', { class: 'pair-title', text: 'Approve in your browser' }),
        el('div', { class: 'pair-sub' }, 'We opened a sign-in page for ', el('b', { style: 'color:var(--ink-1)', text: provider.name }), '. Approve there to grant Zync Master access to your calendars.'),
        el('button', { class: 'pair-link' }, iconEl('copy', 13, 1.6), el('span', { text: `zyncmaster.app/auth/${provider.id}/8h3-4f2a` })),
        el('button', { class: 'btn btn--ghost', text: 'Cancel', onclick: () => { addCal.step = 0; rerender(); } }),
      ));
      clearTimeout(addCal.timer);
      addCal.timer = setTimeout(() => { if (state.view === 'add-calendar' && addCal.step === 1) { addCal.step = 2; rerender(); } }, 2400);
    } else if (addCal.step === 2 && provider) {
      root.append(el('div', { class: 'wizard-title', text: 'Choose calendars to import' }));
      root.append(el('div', { class: 'wizard-sub' }, 'Pick which calendars from ', el('b', { style: 'color:var(--ink-1)', text: provider.name }), ' you want to make available inside Zync Master.'));
      const wrap = el('div', { class: 'glass glass--card', style: 'padding:4px' });
      DISCOVERED.forEach((d) => {
        const cb = el('input', { type: 'checkbox' });
        cb.checked = addCal.selected.has(d.id);
        cb.addEventListener('change', () => { if (cb.checked) addCal.selected.add(d.id); else addCal.selected.delete(d.id); rerender(); });
        wrap.append(el('label', { class: 'discover-item' }, cb,
          el('span', { class: 'discover-item__dot', style: `background:${d.color}` }),
          el('div', null, el('div', { class: 'discover-item__name', text: d.name }), el('div', { class: 'discover-item__sub', text: d.desc }))));
      });
      root.append(wrap);
      const count = addCal.selected.size;
      root.append(el('div', { class: 'wizard-foot' },
        el('button', { class: 'btn btn--ghost', text: 'Use a different provider', onclick: () => { addCal.step = 0; rerender(); } }),
        el('button', { class: 'btn btn--primary', disabled: count === 0, onclick: () => backFromAddCalendar() },
          el('span', { text: `Connect ${count} ${count === 1 ? 'calendar' : 'calendars'}` }))));
    }
  }
  function backFromAddCalendar() { clearTimeout(addCal.timer); addCal.step = 0; navigate(state.returnTo || 'calendar'); }

  // ensureCalendarAccounts — lazy-load ONLY the connected accounts once for the Calendar module, then
  // do a SINGLE softRepaint when the list settles. The module no longer lists calendars per account
  // (the target-calendar select moved to the per-pair wizard), so it never precharges
  // live.calendars[*] — that would be wasted fetches and extra repaints. Guarded so it runs once: if
  // live.accounts is already populated there is nothing to do. softRepaint() avoids replaying the
  // entrance animation when the data arrives.
  function ensureCalendarAccounts(view) {
    if (!Bridge.available) return;
    // Also prime live.pairs (desktop App) so the forget confirm can count the syncs that will be
    // deleted. Without this, opening Calendar Settings without first visiting the pair list leaves
    // live.pairs === null, and confirmDisconnectAccount would under-warn ("no syncs") about a delete
    // the server will still perform. loadPairs() is idempotent/cached, so this is cheap on repeat.
    if (Bridge.desktopApp && live.pairs === null) loadPairs();
    if (live.accounts !== null) return;
    loadAccounts().finally(() => { if (state.view === view) softRepaint(); });
  }

  // ---------------- Calendar · Accounts (internal tab) ----------------
  function renderCalendarSettings(root) {
    root.append(viewHeader('Calendar', { onBack: () => navigate('home') }));
    root.append(calendarTabs('accounts'));

    if (!Bridge.desktopApp) {
      // Defensive: this module is desktop-only. Any other transport bounces back to the hub.
      navigate('config');
      return;
    }

    ensureCalendarAccounts('calendar-settings');
    const accounts = live.accounts || [];

    // Calendar accounts — one humane row per connected account (email as the headline, a friendly
    // sub-line, a clear Disconnect with confirm), plus a Connect affordance. The internal accountRef
    // (a GUID) is NEVER shown to the user.
    const accountRows = [];
    if (live.accounts === null) {
      accountRows.push(cfgRow('Loading…', el('div', { class: 'cfg-row__hint', text: 'Reading your connected accounts' }), el('span', { class: 'spinner' })));
    } else if (accounts.length === 0) {
      accountRows.push(el('div', { class: 'empty-cal' },
        el('div', { class: 'empty-cal__title', text: 'No calendar accounts yet' }),
        el('div', { class: 'empty-cal__sub', text: 'Connect a calendar below to pick a target and start syncing.' })));
    } else {
      accounts.forEach((acc) => { accountRows.push(accountCard(acc)); });
    }

    root.append(cfgSection('Your calendar accounts', ...accountRows));

    // Connect section — its own card so the heading explains what connecting does, with a one-click
    // "Use <identity email>" primary when the signed-in identity is a Microsoft account.
    root.append(connectAccountCard());

    // Note: the per-sync target calendar, interval/window and the .txt export used to live here as
    // global settings. They are now per-pair concerns — target + interval are picked in the Add-pair
    // wizard, and the .txt export is a per-pair action with its own popup on the dashboard.
    root.append(el('div', { class: 'cfg-note', style: 'margin-top:10px;padding:0 4px;font-size:11px;color:var(--ink-3);line-height:16px' },
      'Target calendar, interval and .txt export are set per sync. Manage them on each pair from the Calendar Sync screen.'));
  }

  // accountLabel(acc) — the humane { title, sub } for a connected account. The headline is the real
  // email when known, then a display name, and only ever a dignified generic ("Microsoft account")
  // if neither is present — NEVER the internal accountRef GUID. The sub-line is a short, human note
  // about what the account is, defaulting to "Connected" when we have nothing more specific.
  function accountLabel(acc) {
    const email = (acc.email || '').trim();
    const display = (acc.displayName || '').trim();
    // A displayName that is actually the GUID (older servers fell back to the ref) is not a name.
    const displayIsRef = display && acc.accountRef && display === acc.accountRef;
    const niceDisplay = displayIsRef ? '' : display;

    // Headline preference: email > a real (non-GUID) display name > a dignified generic.
    const title = email || niceDisplay || 'Microsoft account';

    // Sub-line: if the headline is the email, optionally show the person's name; otherwise a short
    // human descriptor. We don't have the scope on /api/accounts, so keep it generic and friendly.
    let sub;
    if (email && niceDisplay && niceDisplay.toLowerCase() !== email.toLowerCase()) {
      sub = niceDisplay;
    } else if (acc.isDefault) {
      sub = 'Microsoft calendar - default account';
    } else {
      sub = 'Microsoft calendar - connected';
    }
    return { title, sub };
  }

  // accountCard(acc) — one connected-account row: a calendar glyph, the email headline + friendly
  // sub-line, and a clear Disconnect action (which confirms before removing). Reuses the cfg-row
  // layout + glass tokens so it matches the rest of Settings.
  function accountCard(acc) {
    const { title, sub } = accountLabel(acc);
    const avatar = el('div', { class: 'acct-avatar', 'aria-hidden': 'true' }, iconEl('calendar', 15, 1.7));

    // Per-account consent badge (spec Pieza 1). Legacy accounts without a scope show no badge.
    const scopeLabel = acc.scope === 'read' ? 'Read-only (source)'
      : (acc.scope === 'readwrite' ? 'Read & write' : '');
    const subEl = el('div', { class: 'acct-text__sub cfg-row__hint', text: sub });
    // Own class (not a nested cfg-row__hint) so the muted styling isn't applied twice (compounded).
    if (scopeLabel) subEl.append(el('span', { class: 'acct-text__scope', text: ` · ${scopeLabel}` }));

    const text = el('div', { class: 'acct-text' },
      el('div', { class: 'acct-text__title', text: title }),
      subEl);
    const left = el('div', { class: 'acct-id' }, avatar, text);
    const disconnectBtn = el('button', {
      class: 'btn btn--ghost acct-disconnect',
      title: `Disconnect ${title}`,
      'aria-label': `Disconnect ${title}`,
      onclick: () => confirmDisconnectAccount(acc),
    }, el('span', { text: 'Disconnect' }));
    return el('div', { class: 'cfg-row acct-row' }, left, disconnectBtn);
  }

  // confirmDisconnectAccount(acc) — confirm before forgetting a calendar account. It DELETES every sync
  // pair that uses this account (as source or destination), on every device, while events already
  // created in the destination calendars stay untouched. You stay signed in. The shown count is an
  // estimate from the loaded pairs (the server resolves the canonical accountId, so legacy/pool mixes
  // can differ); unlinkAccount announces the real deleted count from affectedPairIds afterwards.
  function confirmDisconnectAccount(acc) {
    const { title } = accountLabel(acc);
    const ref = acc.accountRef;
    // Three states: known-some (count > 0), known-none (pairs loaded, count 0), and UNKNOWN
    // (live.pairs === null — the pair list was never loaded on this screen). We must NOT collapse
    // unknown into "no syncs": that would tell the user nothing destructive happens when the server
    // may still delete syncs. So when pairs are unknown we show the destructive copy unconditionally
    // (dropping the "about N" clause), and only show the benign copy when we KNOW the count is zero.
    const pairsKnown = live.pairs !== null;
    const affected = (live.pairs || []).filter((p) => {
      const s = (p.source && p.source.accountRef) || null;
      const d = (p.destination && p.destination.accountRef) || null;
      return s === ref || d === ref;
    }).length;
    const destructive = !pairsKnown || affected > 0;

    let detail;
    if (affected > 0) {
      detail = `Forgetting this account deletes the syncs that use it as a source or destination ` +
        `(about ${affected}), on all your devices. Events already created in the destination ` +
        `calendars are NOT deleted. You stay signed in to the app.`;
    } else if (!pairsKnown) {
      detail = `Forgetting this account deletes the syncs that use it as a source or destination, on ` +
        `all your devices. Events already created in the destination calendars are NOT deleted. You ` +
        `stay signed in to the app.`;
    } else {
      detail = `This calendar account has no syncs. Forgetting it only removes the connection, on all ` +
        `your devices. You stay signed in to the app.`;
    }

    const cancelBtn = el('button', { class: 'btn btn--ghost', type: 'button', text: 'Keep connected' });
    const confirmBtn = el('button', { class: 'btn btn--primary acct-disconnect--confirm', type: 'button' },
      el('span', { text: destructive ? 'Forget and delete syncs' : 'Forget' }));

    const body = el('div', { class: 'disconnect-modal' },
      el('div', { class: 'disconnect-modal__lead', text: `Forget ${title}?` }),
      el('div', { class: 'cfg-row__hint disconnect-modal__detail', text: detail }),
      el('div', { class: 'modal__foot' }, cancelBtn, confirmBtn));

    const modal = openModal({ title: 'Forget calendar account', body });
    cancelBtn.addEventListener('click', () => modal.close());
    confirmBtn.addEventListener('click', () => {
      confirmBtn.disabled = true; cancelBtn.disabled = true;
      const span = confirmBtn.querySelector('span'); if (span) span.textContent = 'Forgetting…';
      modal.close();
      unlinkAccount(acc.accountRef);
    });
  }

  // connectAccountCard() — the "add a calendar account" card on the Calendar screen (which is ONLY the
  // account list; identity lives in Settings). Adding an account never changes your sign-in; the copy
  // says so. Two scopes: a source-only mailbox connects read-only (least privilege, friendlier to
  // enterprise tenants); a destination connects read/write.
  function connectAccountCard() {
    const wrap = el('div', { class: 'connect-cal__wrap' });
    const repaint = () => { if (state.view === 'calendar-settings') softRepaint(); };

    const sourceBtn = el('button', { class: 'btn btn--primary connect-cal',
      onclick: () => connectAccount({ scope: 'read', btn: sourceBtn, wrap, onConnected: repaint }) },
      iconEl('link', 13, 1.8), el('span', { class: 'connect-cal__label', text: 'Add a source account (read-only)' }));

    const destBtn = el('button', { class: 'btn btn--ghost connect-cal connect-cal--other',
      onclick: () => connectAccount({ scope: 'readwrite', btn: destBtn, wrap, onConnected: repaint }) },
      el('span', { class: 'connect-cal__label', text: 'Add a destination account (read & write)' }));

    wrap.append(sourceBtn, destBtn);

    return cfgSection('Add a calendar account',
      el('div', { class: 'cfg-row__hint', style: 'padding:0 2px 6px',
        text: 'Connect the calendar of an account you want to sync. Pick read-only for a source you only ' +
              'mirror FROM, or read & write for a destination you sync INTO. This does not change your sign-in.' }),
      wrap);
  }

  // ---------------- Per-pair .txt export modal ----------------
  // openExportTxtModal(pair) — month/year/include-cancelled options for exporting the pair's SOURCE
  // calendar for one month. Routing is by source provider:
  //   * OutlookCom source → generateTxt (reads the local Outlook on THIS PC via COM). Requires
  //     Outlook installed; the modal does not open when COM is unavailable (defence in depth).
  //   * MicrosoftGraph source → exportSourceTxt (the server reads the online source calendar and
  //     returns the .txt; the App saves it). No COM dependency.
  // The copy adapts to the provider, so a Graph source never claims "local Outlook on this PC".
  const MONTH_LABELS = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];
  function openExportTxtModal(pair) {
    if (!Bridge.available || !pair) return;
    const isCom = (pair.src && pair.src.provider || '').toLowerCase() === 'outlookcom';
    // A COM source needs Outlook on this device; bail rather than opening a modal that can't export.
    if (isCom && !comAvailable()) return;
    const now = new Date();
    const selected = { month: now.getMonth() + 1, year: now.getFullYear(), includeCancelled: true };

    // Month select.
    const monthSel = el('select', { class: 'field-select', 'aria-label': 'Month' });
    MONTH_LABELS.forEach((label, i) => monthSel.append(el('option', { value: String(i + 1), text: label, selected: (i + 1) === selected.month })));
    monthSel.addEventListener('change', () => { selected.month = Number(monthSel.value); });

    // Year select — current year and the four preceding years.
    const yearSel = el('select', { class: 'field-select', 'aria-label': 'Year' });
    for (let y = now.getFullYear(); y >= now.getFullYear() - 4; y--) {
      yearSel.append(el('option', { value: String(y), text: String(y), selected: y === selected.year }));
    }
    yearSel.addEventListener('change', () => { selected.year = Number(yearSel.value); });

    // Include cancelled toggle (default on).
    const cancelToggle = toggleLocal(() => selected.includeCancelled, (v) => { selected.includeCancelled = v; }, 'Include cancelled events');

    const feedback = el('div', { class: 'cfg-row__hint modal__feedback', style: 'min-height:16px' });
    // Explicit Cancel (btn--ghost) alongside the X/overlay/Escape, for parity with the cleanup and
    // new-calendar modals. Closes the modal — the close handler also cancels any pending export.
    const cancelBtn = el('button', { class: 'btn btn--ghost', type: 'button', text: 'Cancel' });
    const confirmBtn = el('button', { class: 'btn btn--primary', type: 'button' }, iconEl('folder', 13, 1.6), el('span', { text: 'Export .txt' }));

    const srcText = isCom
      ? 'Exports your local Outlook calendar on this PC for the selected month.'
      : 'Exports the source calendar (read online by the server) for the selected month.';
    const body = el('div', { class: 'export-modal' },
      el('div', { class: 'export-modal__src cfg-row__hint', style: 'margin-bottom:4px' }, srcText),
      el('div', { class: 'glass glass--card config-section' },
        cfgRow('Month', null, monthSel),
        cfgRow('Year', null, yearSel),
        cfgRow('Include cancelled', el('div', { class: 'cfg-row__hint', text: 'Keep events marked as cancelled' }), cancelToggle)),
      feedback,
      el('div', { class: 'modal__foot' }, cancelBtn, confirmBtn));

    // Pending guard: a slow COM export (Outlook cold start + the host save dialog) can outlive the
    // modal if the user closes it mid-flight. Track that so the late .then/.catch becomes a no-op
    // instead of poking a detached span and leaving the UI "stuck", and so onClose can fire the
    // host's cancel for the in-flight action rather than dangling the COM call.
    let pending = false;
    let stale = false;

    const modal = openModal({
      title: 'Export to .txt',
      body,
      onClose: () => {
        // Closing while an export is in flight: mark the response stale and ask the host to abort the
        // COM/server export so it doesn't keep a save dialog / Graph read alive behind a closed modal.
        if (pending) { stale = true; Bridge.call('cancelExport').catch(() => {}); }
      },
    });

    cancelBtn.addEventListener('click', () => modal.close());

    confirmBtn.addEventListener('click', () => {
      const span = confirmBtn.querySelector('span');
      pending = true;
      confirmBtn.disabled = true;
      cancelBtn.disabled = false; // Cancel stays live so the user can bail out of a slow export.
      if (span) span.textContent = 'Saving…';
      feedback.textContent = ''; feedback.style.color = '';
      // COM source → generateTxt (local Outlook); Graph source → exportSourceTxt (server reads the
      // online source). The latter needs the pair id so the server knows which source to read.
      const action = isCom ? 'generateTxt' : 'exportSourceTxt';
      const req = isCom
        ? { year: selected.year, month: selected.month, includeCancelled: selected.includeCancelled }
        : { pairId: pair.id, year: selected.year, month: selected.month, includeCancelled: selected.includeCancelled };
      // Generous timeout: a cold Outlook plus the host save dialog can run well past the 60s default,
      // mirroring the connect flow. Without this the call would reject as "bridge timeout" mid-save.
      Bridge.call(action, JSON.stringify(req), COM_SLOW_TIMEOUT_MS)
        .then((r) => {
          pending = false;
          if (stale) return; // modal already closed; nothing to update.
          if (r && r.cancelled) {
            // The host's save dialog was dismissed — leave the modal open so the user can retry.
            confirmBtn.disabled = false;
            if (span) span.textContent = 'Export .txt';
            feedback.textContent = 'Save cancelled.'; feedback.style.color = 'var(--ink-2)';
            return;
          }
          if (span) span.textContent = 'Saved';
          feedback.textContent = 'File saved.'; feedback.style.color = 'var(--ok)';
          setTimeout(() => modal.close(), 900);
        })
        .catch((e) => {
          pending = false;
          if (stale) return; // modal already closed; do not poke a detached UI.
          confirmBtn.disabled = false;
          if (span) span.textContent = 'Export .txt';
          feedback.textContent = (e && e.message) || 'Export failed.'; feedback.style.color = 'var(--err)';
        });
    });
  }

  // ---------------- Account actions (toggle / unlink / connect) ----------------
  // toggleLocal — a toggle whose set() does NOT call pushConfig (the shared toggle() pushes settings to
  // the host on every flip, which is wrong for an ephemeral in-modal option). Same look + a11y. The
  // optional label becomes the accessible name (aria-label) — the role=switch element has no visible
  // text of its own, so without it screen readers announce an unnamed switch.
  function toggleLocal(get, set, label) {
    const attrs = { class: 'toggle', role: 'switch', 'aria-checked': String(get()), tabindex: '0' };
    if (label) attrs['aria-label'] = label;
    const t = el('div', attrs);
    const flip = () => { const v = !get(); set(v); t.setAttribute('aria-checked', String(v)); };
    t.addEventListener('click', flip);
    t.addEventListener('keydown', (e) => { if (e.key === ' ' || e.key === 'Enter') { e.preventDefault(); flip(); } });
    return t;
  }

  // unlinkAccount(accountRef?) — unlink a calendar account. With an explicit accountRef (the
  // Calendar module's per-account Unlink button) it targets exactly that account; with no argument
  // it falls back to the default/first connected account. Refreshes accounts + pairs and repaints
  // whichever settings screen is up.
  function unlinkAccount(accountRef) {
    if (!Bridge.available) return;
    const run = () => {
      const accounts = live.accounts || [];
      const target = accountRef
        ? accounts.find((x) => x.accountRef === accountRef) || { accountRef }
        : accounts.find((x) => x.isDefault) || accounts[0];
      if (!target) { announce('No connected account to unlink.'); return; }
      let removed = 0;
      Bridge.call('unlinkAccount', target.accountRef)
        .then((r) => { removed = (r && r.affectedPairIds || []).length; live.accounts = null; return loadPairs(); })
        .then(() => loadAccounts())
        .then(() => {
          announce(removed > 0
            ? `Forgot the account; removed ${removed} sync${removed === 1 ? '' : 's'}.`
            : 'Forgot the account.');
          if (state.view === 'config' || state.view === 'calendar-settings') softRepaint();
        })
        .catch(() => { announce('Unlink failed.'); });
    };
    if (live.accounts === null && !accountRef) loadAccounts().then(run); else run();
  }

  // connectAccount({ scope, btn, wrap, onConnected }) — core OAuth connect of a calendar account into
  // the per-user pool. `scope` is 'read' (source-only; least privilege, friendlier to enterprise
  // tenants) or 'readwrite' (destination). Long-running interactive flow (system browser) with a Cancel
  // affordance. On success it refreshes the account list and calls onConnected(newRef): newRef is the
  // accountRef that appeared since the pre-connect snapshot, or null when the connect refreshed an
  // already-listed account (server connect is idempotent by email — Phase B). All feedback is inline.
  function connectAccount({ scope, btn, wrap, onConnected }) {
    if (!Bridge.desktopApp) return;
    const span = btn && btn.querySelector('span.connect-cal__label');
    const orig = span ? span.textContent : '';
    if (span) span.textContent = 'Connecting…';
    if (btn) btn.disabled = true;

    const before = new Set((live.accounts || []).map((a) => a.accountRef));

    let cancelBtn = null;
    if (wrap) {
      cancelBtn = el('button', { class: 'btn btn--ghost connect-cal__cancel', text: 'Cancel',
        onclick: () => { if (cancelBtn) cancelBtn.disabled = true; Bridge.call('cancelConnect').catch(() => {}); } });
      wrap.append(cancelBtn);
    }
    const removeCancel = () => { if (cancelBtn && cancelBtn.parentNode) cancelBtn.parentNode.removeChild(cancelBtn); cancelBtn = null; };

    Bridge.call('connectCalendar', JSON.stringify({ scope: scope === 'readwrite' ? 'readwrite' : 'read' }), 210000)
      .then((r) => {
        if (r && r.connected) {
          announce('Calendar account connected.');
          live.accounts = null;
          return loadAccounts().then(() => {
            (live.accounts || []).forEach((acc) => { if (!live.calendars[acc.accountRef]) loadCalendars(acc.accountRef); });
            const newRef = (live.accounts || []).map((a) => a.accountRef).find((ref) => !before.has(ref)) || null;
            if (typeof onConnected === 'function') onConnected(newRef);
          });
        }
        if (r && r.cancelled) { if (span) span.textContent = 'Cancelled'; }
        else { if (span) span.textContent = 'Failed'; announce((r && r.error) ? `Connect failed: ${r.error}` : 'Connect failed.'); }
      })
      .catch(() => { if (span) span.textContent = 'Failed'; announce('Connect failed.'); })
      .finally(() => {
        removeCancel();
        if (btn) btn.disabled = false;
        if (span && span.textContent !== orig) setTimeout(() => { if (span) span.textContent = orig; }, 1800);
      });
  }

  function togglePair(id) {
    if (openPairs.has(id)) openPairs.delete(id); else openPairs.add(id);
    rerender();
  }

  // ======== registry ========
  registry.register('calendar', {
    render: renderCalendar, soft: true,
    nav: { label: 'Calendar', icon: 'calendar', order: 2, section: 'modules' },
    statusDot: () => ctx.calendarStatusDot(),
  });
  registry.register('add-pair', { render: renderAddPair, parent: 'calendar' });
  registry.register('add-calendar', { render: renderAddCalendar, parent: 'calendar' });
  registry.register('calendar-settings', { render: renderCalendarSettings, soft: true, parent: 'calendar' });

  // Fuente del palette de los pares — vive AQUÍ porque openPairs es módulo-local en el dominio del
  // calendario. Su run abre el detalle del par (spec §3.2), no solo la lista. Reemplaza la fuente
  // transicional de app.js, que se BORRA en este corte.
  registerPaletteSource(() => (live.pairs || []).filter(Boolean).map((p) => ({
    group: 'Sync pairs',
    label: p.name || 'Sync pair',
    hint: 'open pair',
    run: () => { openPairs.add(p.id); navigate('calendar'); },
  })));
}
