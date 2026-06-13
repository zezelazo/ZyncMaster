// registry.js — registro de vistas compartido por el router, el sidebar y el command
// palette. DOM-free a propósito: testeable en node con assert puro (tests/js-unit).
//
// Una entrada por vista:
//   render(root)   — pinta la vista dentro del contenedor (#view). Obligatorio.
//   soft           — true: participa en rerenderInPlace (repaints sin animación de entrada).
//   parent         — id de la entrada del sidebar que queda activa cuando esta sub-ruta
//                    está abierta (p. ej. 'add-pair' -> 'calendar').
//   nav            — { label, icon, order, section: 'modules'|'system', hidden() } o ausente.
//                    El sidebar se construye SOLO de aquí: una entrada existe cuando su
//                    módulo existe — nunca placeholders "Soon" (spec §3.1, principio 5).
//   statusDot      — () => ({ state:'ok'|'warn'|'err'|'off', title }) | null. Dot del sidebar.
//   header         — () => ({ title, meta }) | null. Título del view header (--t-title).

export function createRegistry() {
  const views = new Map();

  return {
    register(id, def) {
      if (!id || typeof id !== 'string') throw new Error('view id required');
      if (!def || typeof def.render !== 'function') throw new Error(`view "${id}" needs a render(root) function`);
      if (views.has(id)) throw new Error(`view "${id}" already registered`);
      views.set(id, {
        id,
        render: def.render,
        soft: def.soft === true,
        parent: def.parent || null,
        nav: def.nav || null,
        statusDot: typeof def.statusDot === 'function' ? def.statusDot : null,
        header: typeof def.header === 'function' ? def.header : null,
      });
    },
    get(id) { return views.get(id) || null; },
    has(id) { return views.has(id); },
    ids() { return [...views.keys()]; },
    // La entrada del sidebar que debe marcarse activa para la vista `viewId`. Sube por la cadena de
    // `parent` hasta el ANCESTRO más cercano con `nav` (no solo un nivel): así una sub-ruta cuyo
    // padre es a su vez una sub-ruta sin nav —p. ej. add-pair -> calendar (sin nav) -> calendar-day
    // (con nav)— resuelve igualmente a la entrada del sidebar. Guarda contra ciclos con un set visto.
    activeNavId(viewId) {
      const seen = new Set();
      let v = views.get(viewId);
      while (v && !seen.has(v.id)) {
        if (v.nav) return v.id;
        seen.add(v.id);
        v = v.parent ? views.get(v.parent) : null;
      }
      return null;
    },
    navItems() {
      return [...views.values()]
        .filter((v) => v.nav && !(typeof v.nav.hidden === 'function' && v.nav.hidden()))
        .sort((a, b) => (a.nav.order || 0) - (b.nav.order || 0));
    },
  };
}
