// status-model.js — deriva los status dots del sidebar (spec §3.1) y las filas de
// "Sync health" del tablero today (spec §3.3) desde el snapshot real. DOM-free.
//
// Shapes de entrada (los que ya manejan loadPairs / getClipboardDevices en app.js):
//   pair    — { id, name, state:'active'|'paused'|'disabled', lastRunUtc, intervalMin,
//               lastResult:{ created,updated,deleted,skipped,failed }, pinnedDeviceOnline }
//   device  — { id, name, online:boolean, isThis:boolean, settings:{ send, receive, … } }

const t = (iso) => {
  if (!iso) return null;
  const d = new Date(iso);
  return isNaN(d) ? null : d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
};

export function calendarDot(pairs, loaded) {
  if (!loaded) return null;
  const list = (pairs || []).filter(Boolean);
  if (!list.length) return null;
  if (list.some((p) => p.state === 'disabled')) return { state: 'err', title: 'A sync pair is broken' };
  if (list.some((p) => (p.lastResult && (p.lastResult.failed || 0) > 0) || p.pinnedDeviceOnline === false)) {
    return { state: 'warn', title: 'Last run had problems' };
  }
  const last = list.map((p) => t(p.lastRunUtc)).filter(Boolean)[0];
  return { state: 'ok', title: last ? `Last run OK · ${last}` : 'All pairs OK' };
}

export function clipboardDot(device) {
  const s = (device && device.settings) || null;
  if (!s || (!s.send && !s.receive)) return { state: 'off', title: 'Setup pending' };
  const dirs = [s.send && 'send', s.receive && 'receive'].filter(Boolean).join(' + ');
  return { state: 'ok', title: `Clipboard ${dirs} on` };
}

export function devicesDot(devices) {
  const list = (devices || []).filter(Boolean);
  if (!list.length) return null;
  const offline = list.find((d) => !d.isThis && d.online === false);
  if (offline) return { state: 'warn', title: `${offline.name || 'A device'} offline` };
  return null;
}

export function healthRows(pairs, devices) {
  const rows = (pairs || []).filter(Boolean).map((p) => {
    const lr = p.lastResult || null;
    const failed = lr ? (lr.failed || 0) : 0;
    const state = p.state === 'disabled' ? 'err'
      : (failed > 0 || p.pinnedDeviceOnline === false) ? 'warn' : 'ok';
    return {
      kind: 'pair',
      id: p.id,
      name: p.name || 'Sync pair',
      state,
      lastRun: t(p.lastRunUtc),
      changes: lr ? (lr.created || 0) + (lr.updated || 0) + (lr.deleted || 0) : null,
      intervalMin: p.intervalMin || null,
    };
  });
  const list = (devices || []).filter(Boolean);
  if (list.length) {
    const offline = list.filter((d) => !d.isThis && d.online === false);
    rows.push({
      kind: 'devices',
      id: 'devices',
      name: `Devices (${list.length} paired)`,
      state: offline.length ? 'warn' : 'ok',
      detail: offline.length ? `${offline[0].name || 'A device'} offline` : 'All devices online',
    });
  }
  return rows;
}
