// ui/js/views/calendar-day.js — módulo Calendar v2: vista día/semana unificada + paneles
// (replicate / prefix-rules / new-event). Sigue el patrón de views/clipboard.js: un export
// registerCalendarDayView(ctx) que destructura ctx, define el estado/render/paneles como
// funciones módulo-locales, y registra la(s) vista(s) en el registry. NO toca app.js salvo el
// import + la llamada de boot. Los helpers PUROS viven en ../calendar-day.js (test node).
import {
  accountColorClass, clampBlock, localMinutes, replicateButtonLabel,
  maskedTitle, weekDates, shiftDate, freshnessLabel,
  // Las tareas 7/8/9 AÑADEN aquí, a medida que existan, los exports que cada una crea:
  //   tarea 7 → replicateRequest, tarea 8 → prefixRulePayload, tarea 9 → newEventPayload.
  // NO importarlos antes de que existan: un import de un export inexistente rompe el módulo
  // ES (node --check/--test fallan). Cada tarea extiende esta línea cuando crea su helper.
} from '../calendar-day.js';

export function registerCalendarDayView(ctx) {
  const { Bridge, state, navigate, rerenderInPlace, announce, registry } = ctx;

  // ---------------- Calendar v2: unified day/week view state ----------------
  function todayIso() {
    const d = new Date();
    return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
  }
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

  function renderCalendarDay(root) {
    const wrap = document.createElement('section');
    wrap.className = 'view-calday';

    const head = document.createElement('div');
    head.className = 'calday-head';
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
      <button class="btn primary" id="calDayNew">+ New event</button>`;
    wrap.appendChild(head);

    const body = document.createElement('div');
    body.className = 'calday';
    const gridWrap = document.createElement('div');
    gridWrap.className = 'calday-grid-wrap';
    body.appendChild(gridWrap);
    const panel = document.createElement('aside');
    panel.className = 'calday-panel';
    panel.id = 'calDayPanel';
    body.appendChild(panel);
    wrap.appendChild(body);
    root.appendChild(wrap);

    head.querySelector('#calDayLabel').textContent = calDay.date;
    head.querySelector('#calDayModeDay').onclick = () => { calDay.mode = 'day'; loadCalendarDay(); rerenderInPlace(); };
    head.querySelector('#calDayModeWeek').onclick = () => { calDay.mode = 'week'; loadCalendarDay(); rerenderInPlace(); };
    head.querySelector('#calDayPrev').onclick = () => { calDay.date = shiftDate(calDay.date, calDay.mode === 'week' ? -7 : -1); loadCalendarDay(); rerenderInPlace(); };
    head.querySelector('#calDayNext').onclick = () => { calDay.date = shiftDate(calDay.date, calDay.mode === 'week' ? 7 : 1); loadCalendarDay(); rerenderInPlace(); };
    head.querySelector('#calDayToday').onclick = () => { calDay.date = todayIso(); loadCalendarDay(); rerenderInPlace(); };
    head.querySelector('#calDayNew').onclick = () => { calDay.panel = 'new-event'; rerenderInPlace(); };

    const data = calDay.days[calDay.date];
    if (calDay.loading && !data) { gridWrap.innerHTML = '<p class="calday-empty">Loading the day…</p>'; }
    else if (calDay.error) { gridWrap.innerHTML = `<p class="calday-empty">${escapeHtml(calDay.error)}</p>`; }
    else if (calDay.mode === 'day') renderCalDayGrid(gridWrap, head.querySelector('#calDayLegend'), data);
    else renderCalWeekGrid(gridWrap, head.querySelector('#calDayLegend'));

    renderCalDayPanel(panel);
    if (!data && !calDay.loading) loadCalendarDay();
  }

  // One day: a column per ACCOUNT inside one hour grid (the mock's layered day view).
  function renderCalDayGrid(container, legendEl, data) {
    const accounts = (data && data.accounts) || [];
    if (legendEl) {
      legendEl.replaceChildren(...accounts.map((a, i) => {
        const s = document.createElement('span');
        const fresh = freshnessLabel(a.freshness); // "live" → null (no badge); "snapshot_unavailable" → badge
        s.innerHTML = `<span class="sw" data-acc="${i}"></span>${escapeHtml(a.email || 'Account')}`
          + (fresh ? `<span class="calday-fresh">${fresh}</span>` : '');
        s.querySelector('.sw').style.background = `var(--${{ az: 'azure', aq: 'aqua', cy: 'cyan' }[accountColorClass(i)]})`;
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
    accounts.forEach((account, i) => cols.appendChild(buildCalDayColumn(account, i)));
    grid.appendChild(cols);
    container.replaceChildren(grid);
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
      btn.onclick = () => {
        calDay.selected = { ev, account };
        calDay.panel = 'replicate';
        rerenderInPlace();
      };
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
    days.forEach((d) => {
      const cell = document.createElement('div');
      const title = document.createElement('div');
      title.className = 'num';
      title.style.cssText = 'font-size:var(--t-micro);color:var(--ink-3);margin-bottom:4px;';
      title.textContent = d;
      cell.appendChild(title);
      renderCalDayGrid(cell, d === calDay.date ? legendEl : null, calDay.days[d]);
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

  // Panel router: replicate (task 7) / rules (task 7-8) / new-event (task 9) / zero state.
  function renderCalDayPanel(panel) {
    panel.replaceChildren();
    if (calDay.panel === 'replicate' && calDay.selected) return renderReplicatePanel(panel);
    if (calDay.panel === 'new-event') return renderNewEventPanel(panel);
    if (calDay.panel === 'rules') return renderPrefixRulesPanel(panel);
    panel.innerHTML = '<p class="calday-empty">Select an event to replicate it, or create a new one.</p>';
    appendPrefixRulesSummary(panel); // task 7 adds this; until then a no-op stub
  }

  // Temporary stubs so the module loads before tasks 7-9 land their panels. Each of those
  // tasks REPLACES its stub; none of these may survive past task 9.
  function renderReplicatePanel(p) { p.innerHTML = '<p class="calday-empty">Replicate — task 7</p>'; }
  function renderNewEventPanel(p) { p.innerHTML = ''; }
  function renderPrefixRulesPanel(p) { p.innerHTML = ''; }
  function appendPrefixRulesSummary() {}

  // ======== registry ========
  registry.register('calendar-day', {
    render: renderCalendarDay,
    soft: true,             // participa en rerenderInPlace (repaints sin animación de entrada)
    parent: 'calendar',     // sub-ruta de Calendar: el sidebar resalta "Calendar" mientras está
                            // abierta (registry.activeNavId), sin entrada de nav propia
    // header opcional: el shell pinta #vhead desde def.header(). Esta vista ya trae su PROPIO
    // header in-view (.calday-head, fiel al mock: Day/Week, ‹Today›, leyenda, +New). El título
    // cae al label de nav del parent "Calendar"; la fecha viaja como meta en #vhead.
    header: () => ({ title: 'Calendar', meta: calDay.date }),
  });
}
