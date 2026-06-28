// ui/js/views/calendar-day.js — módulo Calendar v2: vista día/semana unificada + paneles
// (replicate / prefix-rules / new-event). Sigue el patrón de views/clipboard.js: un export
// registerCalendarDayView(ctx) que destructura ctx, define el estado/render/paneles como
// funciones módulo-locales, y registra la(s) vista(s) en el registry. NO toca app.js salvo el
// import + la llamada de boot. Los helpers PUROS viven en ../calendar-day.js (test node).
import {
  accountColorClass, clampBlock, localMinutes, replicateButtonLabel,
  maskedTitle, weekDates, shiftDate, freshnessLabel, replicateRequest, prefixRulePayload,
  newEventPayload, formatDayLabel, formatWeekday, formatShortDate,
} from '../calendar-day.js';

export function registerCalendarDayView(ctx) {
  const {
    Bridge, state, navigate, rerenderInPlace, announce, registry,
    // Status popup (read-only) + config gear deps. The popup reuses the SAME pair shapes + run-now
    // bridge actions the pairs screen uses, so a Force-sync here behaves exactly like "Sync now" there.
    live, openModal, el, iconEl, icon, pairViewModel, runPairNow, syncPairRemote, fmtMMSS,
    subscribeSyncState,
  } = ctx;

  // ---------------- Calendar v2: unified day/week view state ----------------
  function todayIso() {
    const d = new Date();
    return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
  }
  // formatDayLabel / formatWeekday / formatShortDate (and their isoToLocalDate base) are PURE,
  // DOM-free helpers and live in ../calendar-day.js so plain `node --test` can freeze them against
  // regression (tests/js-unit/calendar-day.test.mjs). They are imported above, not inlined here.
  const calDay = {
    date: todayIso(),
    mode: 'day',            // 'day' | 'week'
    days: {},               // dateIso -> parsed /api/calendar/day payload
    loading: false,
    error: null,
    selected: null,         // { ev, account } | null
    panel: null,            // null | 'replicate' | 'new-event' | 'rules'
    accounts: [],           // CalendarAccountSummary[] ({id, kind, provider, accountEmail, scope,
                            // status, displayName}) enriched with .calendars (CalendarInfo[]:
                            // {id, displayName, isDefault, owner}) — replicate destinations
    rules: [],              // parsed prefix rules
    rulesError: null,
    hiddenAccounts: {},     // email -> true when the user toggled that account's column off
  };

  // Loads the unified day payload(s) for the current date/mode plus the panel data sources.
  // Day mode = 1 fetch; week mode = 7 parallel fetches of the same endpoint (no week endpoint
  // in v1 by design). Results land in calDay.days keyed by date so Today/‹/› hit the cache.
  function loadCalendarDay() {
    const dates = calDay.mode === 'week' ? weekDates(calDay.date) : [calDay.date];
    calDay.loading = true; calDay.error = null;
    const fetches = dates.map((d) =>
      Bridge.call('getCalendarDay', d).then((raw) => {
        calDay.days[d] = typeof raw === 'string' ? JSON.parse(raw) : raw;
      }));
    fetches.push(Bridge.call('listPrefixRules', null).then((raw) => {
      calDay.rules = typeof raw === 'string' ? JSON.parse(raw) : (raw || []);
    }).catch((err) => { calDay.rulesError = err.message; }));
    // Destinations come from the REAL bridge shapes: listCalendarAccounts returns serialized
    // CalendarAccountSummary objects (id/kind/provider/accountEmail/scope/status/displayName —
    // src/ZyncMaster.App/Bridge/ICalendarServerClient.cs via UiBridge's camelCase JsonOptions),
    // and the per-account calendars come from the EXISTING listCalendars bridge action
    // (CalendarInfo[]: id/displayName/isDefault/owner). No accountId/email/calendars fields exist
    // on the summary — do not invent them.
    fetches.push(Bridge.call('listCalendarAccounts', null).then(async (accts) => {
      const accounts = accts || [];
      await Promise.all(accounts.map(async (acct) => {
        try { acct.calendars = (await Bridge.call('listCalendars', acct.id)) || []; }
        catch { acct.calendars = []; }
      }));
      calDay.accounts = accounts;
    }).catch(() => { calDay.accounts = []; }));
    return Promise.all(fetches)
      .catch((err) => { calDay.error = err.message || 'Could not load the calendar.'; })
      .finally(() => {
        calDay.loading = false;
        if (state.view === 'calendar-day') rerenderInPlace();
      });
  }

  // ---------------- Calendar v2: unified day/week view ----------------
  const CALDAY_START_HOUR = 7, CALDAY_END_HOUR = 21, CALDAY_PX_PER_HOUR = 66;

  // Escape: close any open detail panel (Replicate / New event / Rules). This view is a top-level
  // sidebar destination now (the "Calendar" nav lands here), so there is nothing to navigate "back"
  // to when no panel is open — Escape on a bare grid is a no-op, the sidebar drives navigation.
  // Registered once at module scope and guarded on the active view so it never leaks onto other
  // screens or stacks.
  function onCalDayKeydown(e) {
    if (e.key !== 'Escape') return;
    // An overlay open OVER the calendar-day view (command palette via Ctrl+K, or a modal — including
    // the Status popup) registers its own Escape handler in the CAPTURE phase and calls
    // e.preventDefault() before this bubble handler runs. Bail when the event was already consumed so
    // a single Escape that closes the overlay does NOT also close the calendar's detail panel.
    if (e.defaultPrevented) return;
    if (state.view !== 'calendar-day') return;
    // Detail surfaces are modals now; openModal owns Escape (it closes the open modal in the capture
    // phase). With no inline panel left to dismiss, Escape on the bare grid is a no-op.
  }
  document.addEventListener('keydown', onCalDayKeydown);

  function renderCalendarDay(root) {
    const wrap = document.createElement('section');
    wrap.className = 'view-calday';

    const head = document.createElement('div');
    head.className = 'calday-head';
    // This view is the top-level "Calendar" sidebar destination (consume + trigger): the day/week
    // grid first, then the Day/Week toggle + date nav, a Status button (read-only run state +
    // Force-sync), and a gear that opens the pairs/accounts CONFIGURATION sub-route. There is no
    // Back button — the sidebar is the navigation; nothing sits "above" this screen to return to.
    head.innerHTML = `
      <h1>Calendar</h1>
      <div class="calday-seg" role="tablist">
        <button id="calDayModeDay" class="${calDay.mode === 'day' ? 'on' : ''}">Day</button>
        <button id="calDayModeWeek" class="${calDay.mode === 'week' ? 'on' : ''}">Week</button>
      </div>
      <button class="btn" id="calDayPrev" aria-label="Previous">‹</button>
      <button class="btn" id="calDayToday">Today</button>
      <button class="btn" id="calDayNext" aria-label="Next">›</button>
      <span class="num" id="calDayLabel"></span>
      <span class="calday-legend" id="calDayLegend"></span>
      <button class="btn calday-statusicon" id="calDayStatus" aria-label="Sync status" title="Sync status"></button>
      <button class="btn calday-gear" id="calDayConfig" aria-label="Calendar sync settings" title="Sync settings"></button>
      <button class="btn primary" id="calDayNew">+ New event</button>`;
    head.querySelector('#calDayConfig').appendChild(iconEl('settings', 15, 1.7));
    // Status button shows a health glyph: a warn triangle when any active pair's last run failed or its
    // pinned origin device is offline (the same signal status-model.calendarDot uses), else a check.
    (() => {
      const raws = (live.pairs || []).filter((p) => p && p.state === 'active');
      const anyError = raws.some((p) => (p.lastResult && (p.lastResult.failed || 0) > 0) || p.pinnedDeviceOnline === false);
      const btn = head.querySelector('#calDayStatus');
      btn.classList.toggle('warn', anyError);
      btn.appendChild(iconEl(anyError ? 'alert' : 'check', 15, 1.8));
    })();
    wrap.appendChild(head);

    const body = document.createElement('div');
    body.className = 'calday calday--full';
    const gridWrap = document.createElement('div');
    gridWrap.className = 'calday-grid-wrap';
    body.appendChild(gridWrap);
    wrap.appendChild(body);
    root.appendChild(wrap);

    head.querySelector('#calDayLabel').textContent = formatDayLabel(calDay.date);
    head.querySelector('#calDayModeDay').onclick = () => { calDay.mode = 'day'; loadCalendarDay(); rerenderInPlace(); };
    head.querySelector('#calDayModeWeek').onclick = () => { calDay.mode = 'week'; loadCalendarDay(); rerenderInPlace(); };
    head.querySelector('#calDayPrev').onclick = () => { calDay.date = shiftDate(calDay.date, calDay.mode === 'week' ? -7 : -1); loadCalendarDay(); rerenderInPlace(); };
    head.querySelector('#calDayNext').onclick = () => { calDay.date = shiftDate(calDay.date, calDay.mode === 'week' ? 7 : 1); loadCalendarDay(); rerenderInPlace(); };
    head.querySelector('#calDayToday').onclick = () => { calDay.date = todayIso(); loadCalendarDay(); rerenderInPlace(); };
    head.querySelector('#calDayNew').onclick = () => openCalPanel('new-event');
    // Gear -> the pairs/accounts CONFIGURATION sub-route (unchanged content, just relocated here from
    // the old "Calendar Sync" landing). Status -> read-only run-state popup with per-pair Force-sync.
    head.querySelector('#calDayConfig').onclick = () => navigate('calendar');
    head.querySelector('#calDayStatus').onclick = openStatusPopup;

    const data = calDay.days[calDay.date];
    const noAccounts = data && !(data.accounts || []).length;
    if (calDay.loading && !data) { gridWrap.innerHTML = '<p class="calday-empty">Loading the day…</p>'; }
    else if (calDay.error) { gridWrap.innerHTML = `<p class="calday-empty">${escapeHtml(calDay.error)}</p>`; }
    else if (noAccounts) {
      gridWrap.innerHTML = '<p class="calday-empty">No calendar accounts are visible here yet. '
        + 'Connect a calendar (gear → accounts) — your signed-in account can be connected in one click.</p>';
    }
    else if (calDay.mode === 'day') renderCalDayGrid(gridWrap, head.querySelector('#calDayLegend'), data, calDay.date, true);
    else renderCalWeekGrid(gridWrap, head.querySelector('#calDayLegend'));

    if (!data && !calDay.loading) loadCalendarDay();
  }

  // One day: a column per ACCOUNT inside one hour grid (the mock's layered day view).
  // `dateIso` drives the sticky date header; pass showDateHeader=true (day mode) for the full
  // "Wed, June 13" banner above the columns, false (week mode, where each cell already carries its
  // own weekday+date header) to skip it.
  function renderCalDayGrid(container, legendEl, data, dateIso, showDateHeader) {
    const allAccounts = (data && data.accounts) || [];
    const accounts = allAccounts.filter((a) => !calDay.hiddenAccounts[a.email]);
    if (legendEl) {
      legendEl.replaceChildren(...allAccounts.map((a, i) => {
        const off = !!calDay.hiddenAccounts[a.email];
        const s = document.createElement('button');
        s.type = 'button';
        s.className = 'calday-legend-chip' + (off ? ' off' : '');
        const fresh = freshnessLabel(a.freshness);
        s.innerHTML = `<span class="sw" data-acc="${i}"></span>${escapeHtml(a.email || 'Account')}`
          + (fresh ? `<span class="calday-fresh">${fresh}</span>` : '');
        s.querySelector('.sw').style.background = `var(--${{ az: 'azure', aq: 'aqua', cy: 'cyan' }[accountColorClass(i)]})`;
        s.onclick = () => { calDay.hiddenAccounts[a.email] = !off; rerenderInPlace(); };
        return s;
      }));
    }

    const grid = document.createElement('div');
    grid.className = 'calday-grid';
    grid.style.height = `${(CALDAY_END_HOUR - CALDAY_START_HOUR) * CALDAY_PX_PER_HOUR}px`;
    for (let h = CALDAY_START_HOUR; h < CALDAY_END_HOUR; h++) {
      const line = document.createElement('div');
      line.className = 'calday-hour num';
      line.style.top = `${(h - CALDAY_START_HOUR) * CALDAY_PX_PER_HOUR}px`;
      line.textContent = `${String(h).padStart(2, '0')}:00`;
      grid.appendChild(line);
    }
    const cols = document.createElement('div');
    cols.className = 'calday-cols';
    cols.style.gridTemplateColumns = `repeat(${Math.max(accounts.length, 1)}, 1fr)`;
    // Color each column by the account's STABLE position in allAccounts (not its filtered index), so a
    // visible account keeps the same swatch as its legend chip even when another account is toggled off.
    accounts.forEach((account) => cols.appendChild(buildCalDayColumn(account, allAccounts.indexOf(account))));
    grid.appendChild(cols);

    // Sticky date banner above the grid so "what day is this?" is answered without reading the head
    // (day mode). It scrolls with the grid wrapper but pins to the top on overflow (CSS position:
    // sticky). Week mode renders its own per-column headers instead, so it skips this banner.
    const children = [];
    if (showDateHeader && dateIso) {
      const dateHead = document.createElement('div');
      dateHead.className = 'calday-datehead';
      dateHead.textContent = formatDayLabel(dateIso);
      children.push(dateHead);
    }
    children.push(grid);
    container.replaceChildren(...children);
  }

  function buildCalDayColumn(account, index) {
    const col = document.createElement('div');
    col.className = 'calday-col';
    (account.events || []).forEach((ev) => {
      const sMin = localMinutes(ev.start);
      const eMin = localMinutes(ev.end);
      const block = clampBlock(sMin, eMin, {
        startHour: CALDAY_START_HOUR, endHour: CALDAY_END_HOUR, pxPerHour: CALDAY_PX_PER_HOUR,
      });
      if (!block) return;
      const btn = document.createElement('button');
      btn.type = 'button';
      btn.className = `calday-ev ${accountColorClass(index)}`
        + (calDay.selected && calDay.selected.ev.eventId === ev.eventId
           && calDay.selected.ev.accountId === ev.accountId ? ' sel' : '');
      btn.style.top = `${block.top}px`;
      btn.style.height = `${block.height}px`;
      const replicas = (ev.replicas || []).length;
      const link = replicas ? ` <span class="calday-link">⛓ ${replicas}</span>` : '';
      const mark = ev.isReplica ? ' <span class="calday-link">⛓ replica</span>' : '';
      btn.innerHTML = `<b>${escapeHtml(ev.title || '(no title)')}${link}${mark}</b>`
        + `<span class="num">${fmtRange(ev.start, ev.end)}</span>`;
      btn.title = `${ev.title || '(no title)'} · ${fmtRange(ev.start, ev.end)} · ${account.email || ''}`;
      btn.onclick = () => { calDay.selected = { ev, account }; openCalPanel('replicate'); };
      col.appendChild(btn);
    });
    return col;
  }

  // Week: 7 mini day-grids side by side (one fetch per day, cached in calDay.days).
  function renderCalWeekGrid(container, legendEl) {
    const days = weekDates(calDay.date);
    const row = document.createElement('div');
    row.style.display = 'grid';
    row.style.gridTemplateColumns = 'repeat(7, 1fr)';
    row.style.gap = 'var(--s-2)';
    const todayKey = todayIso();
    days.forEach((d) => {
      const cell = document.createElement('div');
      // Per-column weekday + date header (sticky) instead of the raw ISO string, so each week
      // column says "Wed / Jun 13" at a glance; the current/today columns get a highlight.
      const title = document.createElement('div');
      title.className = 'calday-colhead'
        + (d === calDay.date ? ' on' : '')
        + (d === todayKey ? ' today' : '');
      title.innerHTML = `<span class="calday-colhead-wd">${escapeHtml(formatWeekday(d))}</span>`
        + `<span class="calday-colhead-dt num">${escapeHtml(formatShortDate(d))}</span>`;
      cell.appendChild(title);
      renderCalDayGrid(cell, d === calDay.date ? legendEl : null, calDay.days[d], d, false);
      row.appendChild(cell);
    });
    container.replaceChildren(row);
  }

  function fmtRange(startIso, endIso) {
    const f = (iso) => {
      const d = new Date(iso);
      return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
    };
    return `${f(startIso)} – ${f(endIso)}`;
  }

  function escapeHtml(s) {
    return String(s).replace(/[&<>"']/g, (c) =>
      ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  }

  // Opens one calendar detail surface (replicate / new-event / prefix-rules) as a modal. openModal
  // supplies the title bar + close affordance (its own ✕ / Esc / backdrop), so the render functions
  // no longer draw their own header — they receive `close` (which dismisses the modal) and `onClose`
  // resets state. Prefix rules is reached only from the Replicate panel ("Manage"); when it carries a
  // selection it round-trips back to Replicate on close so the user lands where they started.
  function openCalPanel(kind) {
    const body = document.createElement('div');
    body.className = 'calday-modal';
    calDay.panel = kind;
    const returnToReplicate = kind === 'rules' && !!calDay.selected;
    let modal = null;
    const close = () => { if (modal) modal.close(); };
    let title = 'Calendar';
    if (kind === 'replicate' && calDay.selected) { title = 'Replicate event'; renderReplicatePanel(body, close); }
    else if (kind === 'new-event') { title = 'New event'; renderNewEventPanel(body, close); }
    else if (kind === 'rules') { title = 'Prefix rules'; renderPrefixRulesPanel(body, close); }
    modal = openModal({
      title, body,
      onClose: () => {
        calDay.panel = null;
        if (returnToReplicate) openCalPanel('replicate');
        else { calDay.selected = null; rerenderInPlace(); }
      },
    });
  }

  // ---------------- Calendar v2: Replicate panel (task 7) ----------------
  // Renders into a modal body; openCalPanel supplies the title bar + close. `close` dismisses the modal.
  function renderReplicatePanel(panel, close) {
    const { ev, account } = calDay.selected;
    panel.innerHTML = `
      <div class="calday-sect">
        <div class="calday-src"><span class="bar"></span><div>
          <b>${escapeHtml(ev.title || '(no title)')}</b>
          <span class="num" style="font-size:var(--t-meta);color:var(--ink-3)">
            ${calDay.date} · ${fmtRange(ev.start, ev.end)} · ${escapeHtml(account.email || '')}</span>
        </div></div>
        <p class="calday-hint">A replica copies only time and availability. Title, notes and
          participants never cross accounts.</p>
      </div>
      <div class="calday-sect"><h3>Destinations</h3><div id="calDayDests"></div></div>
      <div class="calday-sect"><h3>Prefix rules
        <button class="btn" id="calDayManageRules" style="float:right">Manage</button></h3>
        <div id="calDayRulesSummary"></div></div>
      <div class="calday-actions">
        <button class="btn" id="calDayCancelReplicate">Cancel</button>
        <button class="btn primary" id="calDayCreateReplicas" disabled>Select a destination</button>
      </div>`;

    const destsEl = panel.querySelector('#calDayDests');
    // One row per destination calendar from the REAL bridge shapes (see 6.3):
    // CalendarAccountSummary {id, kind, provider, accountEmail, scope, status, displayName}
    // enriched with .calendars = CalendarInfo[] {id, displayName, isDefault, owner}.
    // The event's own ACCOUNT is excluded (self-replication is meaningless and the server
    // rejects it). Scope casing is the server's enum ToString ("Read"/"ReadWrite") — compare
    // case-insensitively so a future casing change cannot silently disable every destination.
    const rows = [];
    (calDay.accounts || []).forEach((acct) => {
      if (acct.id === ev.accountId) return;
      const writable = (acct.scope || '').toLowerCase() === 'readwrite';
      (acct.calendars || []).forEach((cal) => {
        const row = { checked: false, accountId: acct.id, calendarId: cal.id, title: '' };
        const div = document.createElement('div');
        div.className = 'calday-dest';
        div.innerHTML = `
          <div class="head">
            <input type="checkbox" ${writable ? '' : 'disabled'} aria-label="Use this destination">
            <span class="name">${escapeHtml(cal.displayName || 'Calendar')}</span>
            <span class="acct">${escapeHtml(acct.accountEmail || '')} · ${escapeHtml(acct.scope || '')}${writable ? '' : ' — upgrade scope to enable'}</span>
          </div>
          <div class="mask" hidden>
            <label>Visible title in destination</label>
            <input type="text" placeholder="Busy">
            <div class="calday-hint">Required — the original title never crosses accounts.</div>
          </div>`;
        const cb = div.querySelector('input[type="checkbox"]');
        const maskWrap = div.querySelector('.mask');
        const maskInput = div.querySelector('input[type="text"]');
        cb.onchange = () => {
          row.checked = cb.checked;
          div.classList.toggle('on', cb.checked);
          maskWrap.hidden = !cb.checked;
          updateCta();
        };
        maskInput.oninput = () => { row.title = maskInput.value; updateCta(); };
        rows.push(row);
        destsEl.appendChild(div);
      });
    });
    if (!rows.length) destsEl.innerHTML = '<p class="calday-empty">No other calendars connected yet.</p>';

    appendPrefixRulesSummary(panel.querySelector('#calDayRulesSummary'));

    const cta = panel.querySelector('#calDayCreateReplicas');
    function updateCta() {
      // Decision D6: the CTA only enables when every checked destination has a typed title.
      const checked = rows.filter((r) => r.checked);
      const missing = checked.some((r) => !maskedTitle(r.title));
      cta.disabled = !checked.length || missing;
      cta.textContent = replicateButtonLabel(checked.length, missing);
    }
    panel.querySelector('#calDayCancelReplicate').onclick = closePanel;
    panel.querySelector('#calDayManageRules').onclick = () => {
      // Round-trip to Prefix rules and back: remember the source event across the modal swap so closing
      // rules returns to THIS replicate panel (close() clears selected via the modal's onClose).
      const sel = calDay.selected;
      close();
      calDay.selected = sel;
      openCalPanel('rules');
    };
    cta.onclick = () => {
      const req = replicateRequest(ev.accountId, ev.eventId, rows);
      if (!req) return;
      cta.disabled = true;
      cta.textContent = 'Creating…';
      Bridge.call('createEventReplicas', JSON.stringify(req))
        .then(() => {
          announce('Replicas created');
          closePanel();
          delete calDay.days[calDay.date]; // refetch: the day now carries the link icons
          loadCalendarDay();
        })
        .catch((err) => {
          updateCta();
          announce(`Replicate failed: ${err.message}`);
          const msg = document.createElement('p');
          msg.className = 'calday-hint';
          msg.style.color = 'var(--err)';
          msg.textContent = err.message;
          cta.parentElement.appendChild(msg);
        });
    };

    function closePanel() { close(); }
  }

  // Read-only summary of the prefix rules inside the Replicate panel (full CRUD is task 8).
  function appendPrefixRulesSummary(target) {
    if (!target) return;
    if (!calDay.rules.length) {
      target.innerHTML = '<p class="calday-hint">No prefix rules yet. A “[Lunch] X” event is renamed to “X” and replicated as “Lunch”. Graph readwrite accounts only.</p>';
      return;
    }
    target.replaceChildren(...calDay.rules.map((r) => {
      const div = document.createElement('div');
      div.className = 'calday-rule';
      const n = (r.destinations || []).length;
      div.innerHTML = `<code>[${escapeHtml(r.prefix)}]</code><span style="color:var(--ink-4)">→</span>`
        + `<span>“${escapeHtml(r.maskTitle)}”</span>`
        + `<span class="n num">${n} destination${n === 1 ? '' : 's'}</span>`
        + `<span style="font-size:var(--t-micro);font-weight:var(--w-micro);color:${r.enabled ? 'var(--ok)' : 'var(--ink-4)'}">${r.enabled ? 'on' : 'off'}</span>`;
      return div;
    }));
  }

  // ---------------- Calendar v2: New event with replicate-on-create (task 9) ----------------
  // Renders into a modal body; openCalPanel supplies the title bar + close. `close` dismisses the modal.
  function renderNewEventPanel(panel, close) {
    // REAL bridge shapes (see 6.3): CalendarAccountSummary {id, accountEmail, scope, displayName,
    // ...} + .calendars CalendarInfo[]; scope compared case-insensitively ("ReadWrite").
    const writable = (calDay.accounts || []).filter((a) => (a.scope || '').toLowerCase() === 'readwrite');
    panel.innerHTML = `
      <div class="calday-sect calday-form">
        <label for="evtCalendar">Calendar</label>
        <select id="evtCalendar"></select>
        <label for="evtTitle">Title</label>
        <input id="evtTitle" type="text" placeholder="Use [Prefix] Title to trigger a prefix rule">
        <label for="evtDate">Date</label>
        <input id="evtDate" type="date" value="${calDay.date}">
        <label for="evtStart">Start</label>
        <input id="evtStart" type="time" value="09:00">
        <label for="evtDuration">Duration (minutes)</label>
        <input id="evtDuration" type="number" min="5" step="5" value="60">
        <label for="evtShowAs">Show as</label>
        <select id="evtShowAs">
          <option value="busy" selected>Busy</option>
          <option value="free">Free</option>
          <option value="tentative">Tentative</option>
          <option value="oof">Out of office</option>
        </select>
      </div>
      <div class="calday-sect"><h3>Replicate on create</h3>
        <p class="calday-hint">Optional: create masked replicas in other calendars in the same step.
          Only time and availability cross — pick the visible title per destination.</p>
        <div id="evtReplicas"></div>
      </div>
      <div class="calday-sect">
        <p class="calday-hint" id="evtError" style="color:var(--err)" hidden></p>
      </div>
      <div class="calday-actions">
        <button class="btn" id="evtCancel">Cancel</button>
        <button class="btn primary" id="evtCreate">Create event</button>
      </div>`;

    // Calendar select: one option per writable account/calendar (creating needs readwrite on the
    // account the event LIVES in — calendar-v2 spec §4; read accounts are not offered at all).
    const calSel = panel.querySelector('#evtCalendar');
    writable.forEach((acct) => {
      (acct.calendars || []).forEach((cal) => {
        const opt = document.createElement('option');
        opt.value = `${acct.id}|${cal.id}`;
        opt.textContent = `${cal.displayName || 'Calendar'} — ${acct.accountEmail || acct.displayName || ''}`;
        calSel.appendChild(opt);
      });
    });
    if (!calSel.options.length) {
      panel.querySelector('.calday-form').innerHTML =
        '<p class="calday-empty">Connect a readwrite calendar account to create events.</p>';
    }

    // Replicate-on-create rows: same row contract as the Replicate panel (task 7).
    const replicaRows = [];
    const repEl = panel.querySelector('#evtReplicas');
    writable.forEach((acct) => {
      (acct.calendars || []).forEach((cal) => {
        const row = { checked: false, accountId: acct.id, calendarId: cal.id, title: '' };
        const div = document.createElement('div');
        div.className = 'calday-dest';
        div.innerHTML = `
          <div class="head"><input type="checkbox" aria-label="Replicate here">
            <span class="name">${escapeHtml(cal.displayName || 'Calendar')}</span>
            <span class="acct">${escapeHtml(acct.accountEmail || '')}</span></div>
          <div class="mask" hidden>
            <label>Visible title in destination</label>
            <input type="text" placeholder="Busy">
            <div class="calday-hint">Required — the event title never crosses accounts.</div>
          </div>`;
        const cb = div.querySelector('input[type="checkbox"]');
        const maskWrap = div.querySelector('.mask');
        const maskInput = div.querySelector('.mask input');
        cb.onchange = () => { row.checked = cb.checked; div.classList.toggle('on', cb.checked); maskWrap.hidden = !cb.checked; };
        maskInput.oninput = () => { row.title = maskInput.value; };
        replicaRows.push(row);
        repEl.appendChild(div);
      });
    });

    panel.querySelector('#evtCancel').onclick = close;
    panel.querySelector('#evtCreate').onclick = () => {
      const [accountId, calendarId] = (calSel.value || '|').split('|');
      const errEl = panel.querySelector('#evtError');
      const payload = newEventPayload({
        accountId, calendarId,
        title: panel.querySelector('#evtTitle').value,
        date: panel.querySelector('#evtDate').value,
        startTime: panel.querySelector('#evtStart').value,
        durationMinutes: panel.querySelector('#evtDuration').value,
        showAs: panel.querySelector('#evtShowAs').value,
      }, replicaRows);
      if (typeof payload === 'string') { errEl.textContent = payload; errEl.hidden = false; return; }
      errEl.hidden = true;
      const btn = panel.querySelector('#evtCreate');
      btn.disabled = true; btn.textContent = 'Creating…';
      Bridge.call('createCalendarEvent', JSON.stringify(payload))
        .then(() => {
          announce('Event created');
          close();
          delete calDay.days[payload.start.slice(0, 10)];
          delete calDay.days[calDay.date];
          loadCalendarDay();
        })
        .catch((err) => {
          btn.disabled = false; btn.textContent = 'Create event';
          errEl.textContent = err.message; errEl.hidden = false;
        });
    };
  }

  // ---------------- Calendar v2: Prefix rules panel (task 8) ----------------
  // Renders into a modal body; openCalPanel supplies the title bar + close. Reached only from the
  // Replicate panel, so closing the modal round-trips back to Replicate (handled in openCalPanel).
  function renderPrefixRulesPanel(panel, close) {
    panel.innerHTML = `
      <div class="calday-sect">
        <p class="calday-hint">A “[Lunch] X” event is renamed to “X” and replicated as “Lunch” to
          every destination of the rule. Graph readwrite accounts only.</p>
        <div id="calDayRulesList"></div>
        ${calDay.rulesError ? `<p class="calday-hint" style="color:var(--err)">${escapeHtml(calDay.rulesError)}</p>` : ''}
      </div>
      <div class="calday-sect calday-form"><h3>New rule</h3>
        <label for="ruleNewPrefix">Prefix (without brackets)</label>
        <input id="ruleNewPrefix" type="text" placeholder="Lunch">
        <label for="ruleNewMask">Mask title</label>
        <input id="ruleNewMask" type="text" placeholder="Lunch">
        <label>Destinations</label>
        <div id="ruleNewDests"></div>
        <p class="calday-hint" id="ruleNewError" style="color:var(--err)" hidden></p>
        <div class="calday-actions" style="padding:var(--s-3) 0 0">
          <button class="btn primary" id="ruleNewSave">Add rule</button>
        </div>
      </div>`;

    // ---- existing rules: enabled toggle + delete ----
    const list = panel.querySelector('#calDayRulesList');
    if (!calDay.rules.length) list.innerHTML = '<p class="calday-empty">No rules yet.</p>';
    calDay.rules.forEach((rule) => {
      const row = document.createElement('div');
      row.className = 'calday-rule';
      const n = (rule.destinations || []).length;
      row.innerHTML = `<code>[${escapeHtml(rule.prefix)}]</code><span style="color:var(--ink-4)">→</span>`
        + `<span>“${escapeHtml(rule.maskTitle)}”</span>`
        + `<span class="n num">${n} destination${n === 1 ? '' : 's'}</span>`;
      const toggle = document.createElement('button');
      toggle.className = 'btn';
      toggle.textContent = rule.enabled ? 'on' : 'off';
      toggle.setAttribute('aria-label', `Toggle rule ${rule.prefix}`);
      toggle.onclick = () => {
        Bridge.call('savePrefixRule', JSON.stringify({ ...rule, enabled: !rule.enabled }))
          .then(() => loadCalendarDay())
          .catch((err) => announce(`Rule update failed: ${err.message}`));
      };
      const del = document.createElement('button');
      del.className = 'btn';
      del.textContent = 'Delete';
      del.onclick = () => {
        if (!window.confirm(`Delete the [${rule.prefix}] rule? Existing replicas are kept.`)) return;
        Bridge.call('deletePrefixRule', rule.id)
          .then(() => loadCalendarDay())
          .catch((err) => announce(`Rule delete failed: ${err.message}`));
      };
      row.appendChild(toggle);
      row.appendChild(del);
      list.appendChild(row);
    });

    // ---- new-rule destination checklist (writable accounts only; REAL bridge shapes — see 6.3:
    // CalendarAccountSummary {id, accountEmail, scope, ...} + .calendars CalendarInfo[]) ----
    const destRows = [];
    const destsEl = panel.querySelector('#ruleNewDests');
    (calDay.accounts || []).filter((a) => (a.scope || '').toLowerCase() === 'readwrite').forEach((acct) => {
      (acct.calendars || []).forEach((cal) => {
        const row = { checked: false, accountId: acct.id, calendarId: cal.id };
        const div = document.createElement('div');
        div.className = 'calday-dest';
        div.innerHTML = `<div class="head"><input type="checkbox" aria-label="Use this destination">
          <span class="name">${escapeHtml(cal.displayName || 'Calendar')}</span>
          <span class="acct">${escapeHtml(acct.accountEmail || '')}</span></div>`;
        div.querySelector('input').onchange = (e) => { row.checked = e.target.checked; div.classList.toggle('on', row.checked); };
        destRows.push(row);
        destsEl.appendChild(div);
      });
    });
    if (!destRows.length) destsEl.innerHTML = '<p class="calday-empty">Connect a readwrite account first.</p>';

    panel.querySelector('#ruleNewSave').onclick = () => {
      const errEl = panel.querySelector('#ruleNewError');
      const payload = prefixRulePayload({
        prefix: panel.querySelector('#ruleNewPrefix').value,
        maskTitle: panel.querySelector('#ruleNewMask').value,
        destinations: destRows.filter((r) => r.checked),
      });
      if (typeof payload === 'string') { errEl.textContent = payload; errEl.hidden = false; return; }
      errEl.hidden = true;
      Bridge.call('savePrefixRule', JSON.stringify(payload))
        .then(() => { announce('Rule created'); loadCalendarDay(); })
        .catch((err) => { errEl.textContent = err.message; errEl.hidden = false; });
    };
  }

  // ---------------- Calendar v2: read-only Status popup (run state + Force-sync) ----------------
  // A minimal, READ-ONLY overview of the active sync pairs reached from the "Status" button in the
  // header. It surfaces, per active pair: the A->B route, synced + new counts, last-run and next-run
  // ETA, and a per-pair Force-sync (run-now) button. It deliberately does NOT add / edit / pause /
  // configure anything — that all lives behind the gear (the 'calendar' config sub-route). The popup
  // reuses the SAME shapes the pairs screen uses: pairViewModel for labels/state/ETA, the raw pair's
  // lastResult/lastRunUtc for counts, and runPairNow / syncPairRemote (via requestPairSync) for the
  // run-now bridge action — so a Force-sync here behaves identically to "Sync now" on the pairs card.

  // statusPopup holds the rendered body node + the sync-state unsubscribe handle so a completed
  // Force-sync can refresh the rows in place (spinner clears, counts / last-run update) without the
  // user reopening the popup. The popup body lives in document.body (openModal), OUTSIDE #view, so a
  // view rerender can never touch it — the only thing that repaints it is refreshStatusRows, driven
  // by the sync-state observer below (subscribed on open, unsubscribed on close).
  let statusPopup = null;

  // Build one read-only row for an ACTIVE pair. `vm` is the pairViewModel; `raw` is the underlying
  // live pair (for the per-run created/updated/skipped counts the view model folds into a total).
  function statusPairRow(vm, raw) {
    const lr = (raw && raw.lastResult) || null;
    const syncedCount = lr ? (lr.created + lr.updated + lr.skipped) : 0;  // events the last run touched/kept
    const newCount = lr ? (lr.created || 0) : 0;                          // events the last run created
    const lastRun = raw && raw.lastRunUtc
      ? new Date(raw.lastRunUtc).toLocaleString([], { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' })
      : 'never';
    // Next-run ETA: mid-sync has no meaningful countdown (em dash), otherwise mm:ss to the next
    // scheduled tick — same fmtMMSS the pairs card uses. fillStatusBody only feeds ACTIVE pairs here,
    // so pairViewModel.state is always 'ok'; the dot only toggles between 'sync' (busy) and 'ok'.
    const busy = !!vm.inFlight;
    const nextStr = busy ? '—' : fmtMMSS(vm.nextSync);
    const dotState = busy ? 'sync' : 'ok';

    const row = el('div', { class: 'calday-status-row glass glass--card' });

    // Route A -> B (read-only).
    row.append(el('div', { class: 'calday-status-route' },
      el('span', { class: 'status-dot', dataset: { state: dotState }, style: 'width:7px;height:7px' }),
      el('span', { class: 'calday-status-name', text: `${vm.src.svc} · ${vm.src.acct}` }),
      el('span', { class: 'calday-status-arrow', html: icon('arrowright', { size: 11, stroke: 1.8 }) }),
      el('span', { class: 'calday-status-name', text: `${vm.dst.svc} · ${vm.dst.acct}` })));

    // Stats as a label/value form: route endpoints + last-run counts + next ETA.
    const statLine = (label, value) => el('div', { class: 'calday-status-line' },
      el('span', { class: 'calday-status-line-lbl', text: label }),
      el('span', { class: 'calday-status-line-val num', text: String(value) }));
    row.append(el('div', { class: 'calday-status-form' },
      statLine('Source', `${vm.src.svc} · ${vm.src.acct}`),
      statLine('Destination', `${vm.dst.svc} · ${vm.dst.acct}`),
      statLine('New events', newCount),
      statLine('Synced', syncedCount),
      statLine('Last run', lastRun),
      statLine('Next', nextStr)));

    return row;
  }

  // Paint the popup rows into `container` from the CURRENT live pairs snapshot. Active pairs only
  // (paused/disabled are a config concern, not a run-state concern). Falls back to honest empty /
  // non-bridge messages. Used for both the initial body and the in-place refresh.
  function fillStatusBody(container) {
    if (!Bridge.available) {
      container.replaceChildren(el('p', { class: 'calday-empty', text: 'Sync status is available in the desktop app.' }));
      return;
    }
    const raws = (live.pairs || []).filter((p) => p && p.state === 'active');
    if (!raws.length) {
      container.replaceChildren(el('p', { class: 'calday-empty', text: 'No active sync pairs. Open the gear to add or resume a pair.' }));
      return;
    }
    const anyBusy = raws.some((raw) => !!pairViewModel(raw).inFlight);
    const forceAll = el('button', {
      class: 'btn primary calday-status-forceall', type: 'button', disabled: anyBusy,
      title: anyBusy ? 'Sync in progress…' : 'Force-sync all active pairs now',
      onclick: () => {
        if (anyBusy) return;
        raws.forEach((raw) => {
          const vm = pairViewModel(raw);
          if (vm.comOffline || vm.comUnclaimed || vm.inFlight) return;
          if (vm.comRemote) syncPairRemote(raw); else runPairNow(vm.id);
        });
      },
    },
      anyBusy
        ? el('span', { class: 'spinner', style: 'width:12px;height:12px;border-width:1.6px' })
        : el('span', { style: 'display:inline-flex', html: icon('sync', { size: 12, stroke: 1.8 }) }),
      el('span', { text: anyBusy ? 'Syncing…' : 'Force-sync' }));
    const children = [
      el('div', { class: 'calday-status-bar' }, forceAll),
      el('p', { class: 'calday-hint', style: 'padding:0 0 var(--s-2)', text: 'Read-only run state per pair. The gear adds, edits or pauses pairs.' }),
    ];
    raws.forEach((raw) => children.push(statusPairRow(pairViewModel(raw), raw)));
    container.replaceChildren(...children);
  }

  // Re-render the popup body in place (after a Force-sync starts/completes) without reopening it.
  function refreshStatusRows() {
    if (statusPopup && statusPopup.bodyEl) fillStatusBody(statusPopup.bodyEl);
  }

  function openStatusPopup() {
    const body = el('div', { class: 'calday-status' });
    fillStatusBody(body);
    // Subscribe to per-pair sync transitions so a Force-sync started from THIS popup (or a run that
    // begins/ends anywhere else) repaints the rows in place: spinner on at beginPairSync, spinner off
    // + fresh counts at endPairSync. This is what makes the run-now lifecycle reach the modal — a
    // view rerender cannot, because the body lives outside #view and the active view is 'calendar-day'.
    const unsubscribe = subscribeSyncState(refreshStatusRows);
    openModal({
      title: 'Sync status',
      body,
      onClose: () => { unsubscribe(); statusPopup = null; },
    });
    // Keep a handle to the rendered body node (for refreshStatusRows) + the unsubscribe (released on
    // close above). We do NOT retain the openModal { close } handle: the popup is only ever dismissed
    // by its own X / overlay / Escape, never programmatically, so storing it would be dead state.
    statusPopup = { bodyEl: body, unsubscribe };
  }

  // ======== registry ========
  // Info-arch: this day/week view IS the "Calendar" sidebar entry now — it owns the nav: block (and the
  // sidebar status dot). The pairs/accounts CONFIGURATION screen ('calendar') degrades to a sub-route
  // reached from the gear in this view's header (parent:'calendar-day', no nav of its own). Clicking
  // "Calendar" in the sidebar lands here (consume + trigger), config lives behind the gear.
  registry.register('calendar-day', {
    render: renderCalendarDay,
    soft: true,             // participa en rerenderInPlace (repaints sin animación de entrada)
    nav: { label: 'Calendar', icon: 'calendar', order: 2, section: 'modules' },
    statusDot: () => ctx.calendarStatusDot(),
    // header opcional: el shell pinta #vhead desde def.header(). Esta vista ya trae su PROPIO
    // header in-view (.calday-head, fiel al mock: Day/Week, ‹Today›, leyenda, +New). El título
    // viaja desde el nav label "Calendar"; la fecha viaja como meta en #vhead, FORMATEADA
    // ("Wed, June 13") igual que el banner in-view — nunca la ISO cruda.
    header: () => ({ title: 'Calendar', meta: formatDayLabel(calDay.date) }),
  });
}
