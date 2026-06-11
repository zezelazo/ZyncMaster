// board-model.js — deriva las filas del tablero "today" (spec §3.3) desde datos reales.
// DOM-free. Las filas de Sync health vienen de status-model.healthRows (no se duplican aquí).

const fmtTime = (iso) => {
  const d = new Date(iso);
  return isNaN(d) ? '' : d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
};

// payload de GET /api/calendar/day (Calendar v2): { events: [{ start, end, subject,
// calendarName?, accountRef? }] }. Mientras el endpoint no exista, el caller cae en
// catch y pasa null -> [] -> "No upcoming events today" (vacío honesto, cero píxeles muertos).
export function eventRows(payload, now = new Date(), max = 6) {
  const list = (payload && Array.isArray(payload.events)) ? payload.events : [];
  return list
    .filter((e) => e && e.end && new Date(e.end) >= now)
    .slice(0, max)
    .map((e) => ({
      time: `${fmtTime(e.start)} – ${fmtTime(e.end)}`,
      title: e.subject || '(no subject)',
      source: e.calendarName || e.accountRef || '',
    }));
}

// items de getClipboardHistory ({ id, type, text, createdUtc, sizeBytes, originDeviceName }).
// Misma normalización de tipo/título que la vista de clipboard (clipNormalizeType/clipTitleOf).
export function clipboardRows(items, max = 5) {
  const list = Array.isArray(items) ? items : [];
  return list.slice(0, max).map((it) => {
    const t = (it && it.type ? String(it.type) : '').toLowerCase();
    const kind = t === 'image' || t === 'file' ? t : 'text';
    let title;
    if (kind === 'text') {
      title = it.text == null
        ? 'Waiting for key from your other device'
        : (String(it.text).replace(/\s+/g, ' ').trim() || '(empty)');
    } else {
      title = it.text || (kind === 'image' ? 'Image' : 'File');
    }
    return { id: it.id, kind, title, device: it.originDeviceName || '', createdUtc: it.createdUtc || '' };
  });
}
