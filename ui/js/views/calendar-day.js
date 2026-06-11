// ui/js/views/calendar-day.js — módulo Calendar v2: vista día/semana unificada + paneles
// (replicate / prefix-rules / new-event). Sigue el patrón de views/clipboard.js: un export
// registerCalendarDayView(ctx) que destructura ctx, define el estado/render/paneles como
// funciones módulo-locales, y registra la(s) vista(s) en el registry. NO toca app.js salvo el
// import + la llamada de boot. Los helpers PUROS viven en ../calendar-day.js (test node).
import {
  accountColorClass, clampBlock, localMinutes, replicateButtonLabel,
  maskedTitle, weekDates, shiftDate, freshnessLabel, replicateRequest, prefixRulePayload,
  // La tarea 9 AÑADE aquí, cuando exista, el export que crea:
  //   tarea 9 → newEventPayload.
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
    appendPrefixRulesSummary(panel);
  }

  // ---------------- Calendar v2: Replicate panel (task 7) ----------------
  function renderReplicatePanel(panel) {
    const { ev, account } = calDay.selected;
    panel.innerHTML = `
      <header><h2>Replicate event</h2>
        <button class="x btn" id="calDayPanelClose" aria-label="Close">✕</button></header>
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
    panel.querySelector('#calDayPanelClose').onclick = closePanel;
    panel.querySelector('#calDayCancelReplicate').onclick = closePanel;
    panel.querySelector('#calDayManageRules').onclick = () => { calDay.panel = 'rules'; rerenderInPlace(); };
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

    function closePanel() { calDay.panel = null; calDay.selected = null; rerenderInPlace(); }
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

  // Temporary stub so the module loads before task 9 lands its panel. Task 9 REPLACES it;
  // it may not survive past task 9.
  function renderNewEventPanel(p) { p.innerHTML = ''; }

  // ---------------- Calendar v2: Prefix rules panel (task 8) ----------------
  function renderPrefixRulesPanel(panel) {
    panel.innerHTML = `
      <header><h2>Prefix rules</h2>
        <button class="x btn" id="calDayRulesClose" aria-label="Close">✕</button></header>
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

    panel.querySelector('#calDayRulesClose').onclick = () => { calDay.panel = calDay.selected ? 'replicate' : null; rerenderInPlace(); };

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
