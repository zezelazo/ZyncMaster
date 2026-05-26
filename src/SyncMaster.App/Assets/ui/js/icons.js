// icons.js — custom line icon set. 24px grid, 1.5px stroke, rounded joins,
// stroke=currentColor so each icon inherits the surrounding ink/vibrancy.
// Each entry is the inner SVG markup; icon() wraps it in a sized <svg>.
// All markup here is static and author-controlled (no user input), so building
// these SVG strings and assigning them via innerHTML carries no injection risk.

const PATHS = {
  sync:
    '<path d="M21 12a9 9 0 1 1-2.64-6.36"/><path d="M21 4v5h-5"/>',
  calendar:
    '<rect x="3" y="5" width="18" height="16" rx="3"/><path d="M3 9h18M8 3v4M16 3v4"/>',
  gear:
    '<circle cx="12" cy="12" r="3.2"/><path d="M12 2.5v2.4M12 19.1v2.4M4.2 4.2l1.7 1.7M18.1 18.1l1.7 1.7M2.5 12h2.4M19.1 12h2.4M4.2 19.8l1.7-1.7M18.1 5.9l1.7-1.7"/>',
  monitor:
    '<rect x="3" y="4" width="18" height="13" rx="2.5"/><path d="M8 21h8M12 17v4"/>',
  link:
    '<path d="M9.5 14.5l5-5"/><path d="M7 12.5l-1.6 1.6a3.5 3.5 0 0 0 5 5l1.6-1.6"/><path d="M17 11.5l1.6-1.6a3.5 3.5 0 0 0-5-5L12 6.5"/>',
  check:
    '<path d="M5 12.5l4.5 4.5L19 6"/>',
  alert:
    '<path d="M12 3.2L1.8 20.5h20.4z"/><path d="M12 9.5v4.5M12 17.4h.01"/>',
  pause:
    '<rect x="6.5" y="5" width="3.3" height="14" rx="1.2"/><rect x="14.2" y="5" width="3.3" height="14" rx="1.2"/>',
  chevron:
    '<path d="M9 6l6 6-6 6"/>',
  close:
    '<path d="M6 6l12 12M18 6L6 18"/>',
  plus:
    '<path d="M12 5v14M5 12h14"/>',
  home:
    '<path d="M4 11l8-7 8 7"/><path d="M6 10v9a1 1 0 0 0 1 1h10a1 1 0 0 0 1-1v-9"/>',
  bolt:
    '<path d="M13 2L4 14h7l-1 8 9-12h-7z"/>',
  copy:
    '<rect x="9" y="9" width="11" height="11" rx="2.5"/><path d="M5 15V5a2 2 0 0 1 2-2h8"/>',
  wifi:
    '<path d="M2 8.5a16 16 0 0 1 20 0M5 12a11 11 0 0 1 14 0M8.5 15.5a6 6 0 0 1 7 0"/><path d="M12 19h.01"/>',
};

export function icon(name, { size = 24, stroke = 1.5, cls = '' } = {}) {
  const inner = PATHS[name];
  if (!inner) return '';
  return (
    `<svg viewBox="0 0 24 24" width="${size}" height="${size}" fill="none" ` +
    `stroke="currentColor" stroke-width="${stroke}" stroke-linecap="round" ` +
    `stroke-linejoin="round" class="${cls}" aria-hidden="true">${inner}</svg>`
  );
}

// Hydrate any element carrying data-icon="name" with its SVG.
export function hydrateIcons(root = document) {
  root.querySelectorAll('[data-icon]').forEach((el) => {
    const name = el.getAttribute('data-icon');
    const size = el.getAttribute('data-icon-size');
    el.innerHTML = icon(name, size ? { size: Number(size) } : {});
  });
}
