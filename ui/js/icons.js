// icons.js — Zync Master line icon set. 24px grid, currentColor strokes.
// Ported from the design handoff (icons.jsx) to framework-free vanilla JS.
// Each entry returns a static SVG markup string. All markup here is
// author-controlled (no user/external input ever flows in), so assigning these
// strings via innerHTML carries no injection risk. Any dynamic text in the app
// is written with textContent instead.
//
// icon(name, { size, stroke, cls }) -> svg string
// logoSvg({ size, mono }) -> the brand mark svg string
// hydrateIcons(root) -> replaces [data-icon] placeholders with their SVG.

// Inner markup for the 24x24 line icons. Wrapped by icon() below.
const STROKE_PATHS = {
  home:
    '<path d="M4 11l8-6.5L20 11"/><path d="M5.5 10v8.5h13V10"/><path d="M10 18.5v-4.5h4v4.5"/>',
  settings:
    '<circle cx="12" cy="12" r="2.6"/><path d="M19.4 14a1.2 1.2 0 0 0 .24 1.32l.04.04a1.5 1.5 0 1 1-2.12 2.12l-.04-.04a1.2 1.2 0 0 0-1.32-.24 1.2 1.2 0 0 0-.72 1.1v.12a1.5 1.5 0 0 1-3 0v-.07a1.2 1.2 0 0 0-.78-1.1 1.2 1.2 0 0 0-1.32.24l-.04.04a1.5 1.5 0 1 1-2.12-2.12l.04-.04a1.2 1.2 0 0 0 .24-1.32 1.2 1.2 0 0 0-1.1-.72H6a1.5 1.5 0 0 1 0-3h.07a1.2 1.2 0 0 0 1.1-.78 1.2 1.2 0 0 0-.24-1.32l-.04-.04a1.5 1.5 0 1 1 2.12-2.12l.04.04a1.2 1.2 0 0 0 1.32.24h.06a1.2 1.2 0 0 0 .72-1.1V6a1.5 1.5 0 0 1 3 0v.07a1.2 1.2 0 0 0 .72 1.1 1.2 1.2 0 0 0 1.32-.24l.04-.04a1.5 1.5 0 1 1 2.12 2.12l-.04.04a1.2 1.2 0 0 0-.24 1.32v.06a1.2 1.2 0 0 0 1.1.72H18a1.5 1.5 0 0 1 0 3h-.07a1.2 1.2 0 0 0-1.1.72z"/>',
  link:
    '<path d="M10 14a4 4 0 0 0 5.66 0l3-3a4 4 0 0 0-5.66-5.66l-1.5 1.5"/><path d="M14 10a4 4 0 0 0-5.66 0l-3 3a4 4 0 0 0 5.66 5.66l1.5-1.5"/>',
  sync:
    '<path d="M3 12a9 9 0 0 1 15.5-6.3M21 4v5h-5"/><path d="M21 12a9 9 0 0 1-15.5 6.3M3 20v-5h5"/>',
  check:
    '<path d="M5 12.5l4.5 4.5L19 7.5"/>',
  plus:
    '<path d="M12 5v14M5 12h14"/>',
  close:
    '<path d="M6 6l12 12M18 6L6 18"/>',
  minimize:
    '<path d="M5 12h14"/>',
  maximize:
    '<rect x="5.5" y="5.5" width="13" height="13" rx="1"/>',
  pin:
    '<path d="M12 17v5"/><path d="M9 3h6l-1 5 3 3v2H7v-2l3-3-1-5z"/>',
  calendar:
    '<rect x="4" y="5.5" width="16" height="14" rx="2.5"/><path d="M4 10h16M9 3.5v3M15 3.5v3"/>',
  clock:
    '<circle cx="12" cy="12" r="8"/><path d="M12 7.5V12l3 2"/>',
  bolt:
    '<path d="M13 3 L5 14 h6 l-2 7 L18 10 h-6 z"/>',
  wifi:
    '<path d="M5 9.5a11 11 0 0 1 14 0"/><path d="M8 12.5a7 7 0 0 1 8 0"/><path d="M11 15.5a3 3 0 0 1 2 0"/><circle cx="12" cy="18" r="0.6" fill="currentColor" stroke="none"/>',
  wifioff:
    '<path d="M3 3l18 18"/><path d="M8 12.5a7 7 0 0 1 6.5-1.9"/><path d="M5 9.5a11 11 0 0 1 6-3"/><circle cx="12" cy="18" r="0.6" fill="currentColor" stroke="none"/>',
  alert:
    '<path d="M12 4 L22 20 H2 z"/><path d="M12 10v4M12 17v.5"/>',
  pause:
    '<path d="M9 5v14M15 5v14"/>',
  copy:
    '<rect x="8" y="8" width="12" height="12" rx="2"/><path d="M16 8V5a1 1 0 0 0-1-1H5a1 1 0 0 0-1 1v10a1 1 0 0 0 1 1h3"/>',
  arrowright:
    '<path d="M5 12h14M13 6l6 6-6 6"/>',
  chevronleft:
    '<path d="M14.5 6 L8.5 12 L14.5 18"/>',
  chevrondown:
    '<path d="M6 9.5 L12 15.5 L18 9.5"/>',
  clipboard:
    '<rect x="5" y="5" width="14" height="16" rx="2.5"/><rect x="8.5" y="2.5" width="7" height="4" rx="1.2" fill="currentColor" stroke="none" opacity="0.18"/><rect x="8.5" y="2.5" width="7" height="4" rx="1.2"/><path d="M8.5 12h7M8.5 16h5"/>',
  folder:
    '<path d="M3.5 7.5a1.5 1.5 0 0 1 1.5-1.5h4.4l2 2.5H19a1.5 1.5 0 0 1 1.5 1.5V18a1.5 1.5 0 0 1-1.5 1.5H5A1.5 1.5 0 0 1 3.5 18V7.5z"/>',
  bookmark:
    '<path d="M6.5 4.5h11v16l-5.5-4-5.5 4z"/>',
  note:
    '<path d="M5 5h11l3 3v11H5z"/><path d="M9 12h6M9 16h4"/>',
  tab:
    '<path d="M3.5 9.5h6l2-3h9v11.5h-17z"/>',
  repeat:
    '<path d="M4 7h13l-2-2M20 17H7l2 2"/>',
  activity:
    '<path d="M3 12h4l3-7 4 14 3-7h4"/>',
  sparkle:
    '<path d="M12 4v6M12 14v6M4 12h6M14 12h6"/>',
};

// The brand mark — two interlocking arcs forming a sync loop. A gradient version
// for the in-app UI; a `mono` variant (currentColor) for menu-bar / icon use.
// Returns full <svg> markup. Unique gradient ids per call avoid collisions when
// several logos render at once.
let logoSeq = 0;
export function logoSvg({ size = 18, mono = false } = {}) {
  if (mono) {
    return (
      `<svg viewBox="0 0 24 24" width="${size}" height="${size}" fill="none" aria-hidden="true">` +
      '<rect x="1.5" y="1.5" width="21" height="21" rx="6" fill="currentColor"/>' +
      '<path d="M6.5 11.5 A 5.5 5.5 0 0 1 16.5 9.2" stroke="currentColor" stroke-width="2.1" stroke-linecap="round" fill="none"/>' +
      '<path d="M15.2 7.6 L17 9.2 L15.6 11" stroke="currentColor" stroke-width="2.1" stroke-linecap="round" stroke-linejoin="round" fill="none"/>' +
      '<path d="M17.5 12.5 A 5.5 5.5 0 0 1 7.5 14.8" stroke="currentColor" stroke-width="2.1" stroke-linecap="round" fill="none"/>' +
      '<path d="M8.8 16.4 L7 14.8 L8.4 13" stroke="currentColor" stroke-width="2.1" stroke-linecap="round" stroke-linejoin="round" fill="none"/>' +
      '</svg>'
    );
  }
  const g1 = `sm-g1-${logoSeq}`;
  const g2 = `sm-g2-${logoSeq}`;
  logoSeq += 1;
  return (
    `<svg viewBox="0 0 24 24" width="${size}" height="${size}" fill="none" aria-hidden="true">` +
    '<defs>' +
    `<linearGradient id="${g1}" x1="2" y1="4" x2="22" y2="20" gradientUnits="userSpaceOnUse">` +
    '<stop offset="0" stop-color="#7aa6ff"/><stop offset="1" stop-color="#3766d8"/></linearGradient>' +
    `<linearGradient id="${g2}" x1="22" y1="4" x2="2" y2="22" gradientUnits="userSpaceOnUse">` +
    '<stop offset="0" stop-color="#ee9476"/><stop offset="1" stop-color="#c95a38"/></linearGradient>' +
    '</defs>' +
    '<rect x="1.5" y="1.5" width="21" height="21" rx="6" fill="rgba(255,255,255,0.04)" stroke="rgba(255,255,255,0.10)" stroke-width="0.8"/>' +
    `<path d="M6.5 11.5 A 5.5 5.5 0 0 1 16.5 9.2" stroke="url(#${g1})" stroke-width="2.1" stroke-linecap="round" fill="none"/>` +
    `<path d="M15.2 7.6 L17 9.2 L15.6 11" stroke="url(#${g1})" stroke-width="2.1" stroke-linecap="round" stroke-linejoin="round" fill="none"/>` +
    `<path d="M17.5 12.5 A 5.5 5.5 0 0 1 7.5 14.8" stroke="url(#${g2})" stroke-width="2.1" stroke-linecap="round" fill="none"/>` +
    `<path d="M8.8 16.4 L7 14.8 L8.4 13" stroke="url(#${g2})" stroke-width="2.1" stroke-linecap="round" stroke-linejoin="round" fill="none"/>` +
    '</svg>'
  );
}

// icon(name, opts) — returns the SVG markup string for a named line icon.
// Names are case-insensitive. Unknown names return an empty string.
export function icon(name, { size = 18, stroke = 1.6, cls = '' } = {}) {
  const key = String(name).toLowerCase();
  if (key === 'logo') return logoSvg({ size });
  const inner = STROKE_PATHS[key];
  if (!inner) return '';
  return (
    `<svg viewBox="0 0 24 24" width="${size}" height="${size}" fill="none" ` +
    `stroke="currentColor" stroke-width="${stroke}" stroke-linecap="round" ` +
    `stroke-linejoin="round"${cls ? ` class="${cls}"` : ''} aria-hidden="true">${inner}</svg>`
  );
}

// hydrateIcons — replace any element carrying data-icon="name" with its SVG.
// Optional data-icon-size and data-icon-stroke attributes tune the render.
export function hydrateIcons(root = document) {
  root.querySelectorAll('[data-icon]').forEach((elm) => {
    const name = elm.getAttribute('data-icon');
    const size = elm.getAttribute('data-icon-size');
    const stroke = elm.getAttribute('data-icon-stroke');
    const opts = {};
    if (size) opts.size = Number(size);
    if (stroke) opts.stroke = Number(stroke);
    elm.innerHTML = icon(name, opts);
  });
}
