// toast.js — feedback visible para acciones (spec §4). Superficie SÓLIDA (--surface-solid
// + borde): los toasts NO entran en la excepción glass (spec §2.6). El aria-live global
// (announce) sigue siendo el canal para lectores de pantalla; esto es la señal visible.

let host = null;

export function showToast(message, opts = {}) {
  const kind = opts.kind === 'err' ? 'err' : opts.kind === 'warn' ? 'warn' : 'ok';
  if (!host || !host.isConnected) {
    host = document.createElement('div');
    host.className = 'toast-host';
    host.setAttribute('aria-hidden', 'true');
    document.body.append(host);
  }
  const t = document.createElement('div');
  t.className = `toast toast--${kind}`;
  t.textContent = message;
  host.append(t);
  while (host.children.length > 3) host.firstChild.remove();
  const ttl = opts.duration || 2600;
  setTimeout(() => t.classList.add('toast--out'), ttl);
  t.addEventListener('transitionend', () => t.remove(), { once: true });
  setTimeout(() => t.remove(), ttl + 600); // safety si transitionend no dispara
  return t;
}
