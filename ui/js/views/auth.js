// views/auth.js — sign-in gates + the legacy pairing walkthrough. The web-panel sign-in card
// (renderSignIn) and the desktop identity gate (renderIdentitySignIn) are NOT registry views: the
// gates in rerender() (app.js) call them directly, so they are re-exposed via ctx.gates. The
// session/login machinery (refreshIdentity, startMicrosoftLogin, …) stays in app.js (boot/gates
// own it) and reaches these screens through ctx. Extraído VERBATIM de app.js.

export function registerAuthViews(ctx) {
  const {
    el, iconEl, icon, Bridge, state, identityAuth, logoSvg, microsoftLogo,
    navigate, rerender, announce, registry,
    startMicrosoftLogin, retryMicrosoftLogin, cancelAppLogin, startMagicLinkLogin,
  } = ctx;

  // ---------------- Screen: Sign in (web panel gate) ----------------
  // Shown only in web mode when /api/me reported 401. The button starts the server's OAuth
  // flow; after the callback signs in the cookie and redirects to returnTo=/, the panel reloads
  // authenticated and the gate clears. Styled with the existing glass tokens.
  function renderSignIn(root) {
    const card = el('div', { class: 'glass glass--card pair-card', style: 'margin-top:14px' },
      el('div', { class: 'about-logo', style: 'margin:4px auto 0', html: logoSvg({ size: 56 }) }),
      el('div', { class: 'pair-title', text: 'Sign in to Zync Master' }),
      el('div', { class: 'pair-sub', text: 'Connect your Microsoft account to manage your calendar sync pairs from the web panel.' }),
      el('button', { class: 'btn btn--primary ms-signin', style: 'align-self:stretch',
        onclick: () => { window.location.href = '/connect?returnTo=/'; } },
        el('span', { class: 'ms-signin__logo', html: microsoftLogo({ size: 18 }) }),
        el('span', { class: 'ms-signin__label', text: 'Sign in with Microsoft' })));
    root.append(card);
  }

  // renderIdentitySignIn — the desktop App identity gate. Liquid Glass card with the Microsoft
  // 4-square button and an email magic-link form. States: idle, loading (login in flight), error,
  // and the "magic-link sent" confirmation. Decorative glow lives on the buttons' own
  // border-light; nothing here moves the hit area or flickers on hover.
  function renderIdentitySignIn(root) {
    // Magic-link sent confirmation — replaces the form until the round-trip completes. The user
    // still has to click the emailed link (which opens the browser), so we also nudge them to
    // continue in the browser and give a Cancel/Back path (cancelLogin) for the closed-tab case.
    if (identityAuth.magicLinkSent) {
      const card = el('div', { class: 'glass glass--card pair-card identity-card', style: 'margin-top:14px' },
        el('div', { class: 'about-logo', style: 'margin:4px auto 0', html: logoSvg({ size: 56 }) }),
        el('div', { class: 'pair-title', text: 'Check your email' }),
        el('div', { class: 'pair-sub' },
          'We sent a sign-in link to ',
          el('b', { style: 'color:var(--ink-1)', text: identityAuth.magicLinkEmail || 'your inbox' }),
          '. Open it on this device to finish signing in — this screen updates automatically.'),
        el('div', { class: 'identity-waiting' },
          el('span', { class: 'spinner', style: 'width:14px;height:14px;border-color:var(--azure-edge);border-top-color:var(--azure)' }),
          el('span', { class: 'identity-waiting__txt', text: 'Continue in your browser to finish signing in…' })),
        el('button', { class: 'btn btn--ghost', text: 'Cancel and use a different email',
          onclick: () => cancelAppLogin() }),
      );
      root.append(card);
      return;
    }

    // Login in flight (Microsoft, or magic-link request before it lands) — the host has opened the
    // system browser and is waiting on the loopback callback. Replace the form with a clear
    // "continue in your browser" panel plus a Cancel button so a closed tab is recoverable without
    // restarting the App (cancelLogin frees the port and resets the attempt).
    if (identityAuth.loading) {
      const card = el('div', { class: 'glass glass--card pair-card identity-card', style: 'margin-top:14px' },
        el('div', { class: 'about-logo', style: 'margin:4px auto 0', html: logoSvg({ size: 56 }) }),
        el('div', { class: 'pair-title', text: 'Continue in your browser' }),
        el('div', { class: 'pair-sub', text: 'We opened your browser to finish signing in. Come back here once you are done — this screen updates on its own.' }),
        el('div', { class: 'identity-waiting' },
          el('span', { class: 'spinner', style: 'width:14px;height:14px;border-color:var(--azure-edge);border-top-color:var(--azure)' }),
          el('span', { class: 'identity-waiting__txt', text: 'Waiting for the browser…' })));

      // After a while with no callback, hint at the common corporate-network cause: a firewall/proxy
      // that resets the FIRST OAuth connection (ERR_CONNECTION_RESET). Retrying usually works because
      // the proxy session is then established. The retry button frees the port and starts a fresh attempt
      // in one click, so the user does not have to Cancel and re-open the form.
      if (identityAuth.slowHint) {
        card.append(el('div', { class: 'identity-error identity-error--warn', role: 'alert', style: 'margin-top:2px' },
          iconEl('alert', 13, 1.8),
          el('span', { text: 'Taking longer than usual. If your browser shows “ERR_CONNECTION_RESET” (common on work/corporate networks), just try again — the second attempt usually goes through.' })));
        card.append(el('button', { class: 'btn btn--primary', style: 'align-self:stretch',
          onclick: () => retryMicrosoftLogin() },
          iconEl('sync', 14, 1.8), el('span', { text: 'Cancel and try again' })));
      }

      card.append(el('button', { class: 'btn btn--ghost', text: 'Cancel', onclick: () => cancelAppLogin() }));
      root.append(card);
      return;
    }

    const loading = identityAuth.loading;
    const card = el('div', { class: 'glass glass--card pair-card identity-card', style: 'margin-top:14px' });
    card.append(
      el('div', { class: 'about-logo', style: 'margin:4px auto 0', html: logoSvg({ size: 56 }) }),
      el('div', { class: 'pair-title', text: 'Sign in to Zync Master' }),
      el('div', { class: 'pair-sub', text: 'Sign in to mirror your calendars across your accounts and devices.' }),
    );

    // Error banner (login failure). Calm, no flicker; cleared on the next attempt.
    if (identityAuth.error) {
      card.append(el('div', { class: 'identity-error', role: 'alert' },
        iconEl('alert', 13, 1.8), el('span', { text: identityAuth.error })));
    }

    // Microsoft — official 4-square logo + label, on the primary glass button.
    const msBtn = el('button', { class: 'btn btn--primary ms-signin', style: 'align-self:stretch',
      disabled: loading, onclick: () => startMicrosoftLogin() },
      loading
        ? el('span', { class: 'spinner', style: 'width:16px;height:16px' })
        : el('span', { class: 'ms-signin__logo', html: microsoftLogo({ size: 18 }) }),
      el('span', { class: 'ms-signin__label', text: loading ? 'Signing in…' : 'Sign in with Microsoft' }));
    card.append(msBtn);

    // Divider between the providers.
    card.append(el('div', { class: 'identity-divider' },
      el('span', { class: 'identity-divider__line' }),
      el('span', { class: 'identity-divider__txt', text: 'or' }),
      el('span', { class: 'identity-divider__line' })));

    // Magic-link — email input + send button.
    const emailInput = el('input', {
      class: 'field-input identity-email', type: 'email', inputmode: 'email',
      autocomplete: 'email', placeholder: 'you@example.com', 'aria-label': 'Email address',
    });
    const submit = () => startMagicLinkLogin(emailInput.value);
    emailInput.addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); submit(); } });
    emailInput.disabled = loading;
    const sendBtn = el('button', { class: 'btn identity-email__send', disabled: loading, onclick: submit },
      iconEl('arrowright', 14, 1.8), el('span', { text: 'Sign in with email' }));
    card.append(el('div', { class: 'identity-email-row' }, emailInput, sendBtn));
    card.append(el('div', { class: 'identity-foot', text: 'We will email you a one-time sign-in link. No password needed.' }));

    root.append(card);
  }

  // ---------------- Screen: Pairing (MOCK-ONLY) ----------------
  // Legacy manual pairing-by-key walkthrough. A device now registers as part of the identity
  // sign-in, so this screen is unreachable in every real transport: navigate() bounces 'pairing' to
  // Settings whenever Bridge.available. It survives only in the standalone mock (file://) demo, so
  // the data below — the demo name, the fabricated link, the timer that auto-advances — is fixed
  // mock content that never runs against a real host.
  const pairing = { step: 0, name: '', timer: null };

  function renderPairing(root) {
    const labels = ['Name', 'Approve', 'Done'];
    const stepper = el('div', { class: 'stepper' });
    labels.forEach((lab, i) => {
      const st = i < pairing.step ? 'done' : i === pairing.step ? 'active' : null;
      stepper.append(el('div', { class: 'stepper__dot', dataset: st ? { state: st } : {}, html: i < pairing.step ? icon('check', { size: 11, stroke: 2.4 }) : '' }, i < pairing.step ? '' : String(i + 1)));
      if (i < labels.length - 1) stepper.append(el('div', { class: 'stepper__line', dataset: st ? { state: st } : {} }));
    });
    root.append(el('div', { class: 'glass glass--card', style: 'margin-top:6px;padding:0' }, stepper));

    if (pairing.step === 0) {
      // Mock-only demo name for the standalone walkthrough (this screen never renders with a bridge).
      if (!pairing.name) pairing.name = "Daniel's MacBook";
      const nameInput = el('input', { class: 'field-input', value: pairing.name, placeholder: 'Name this device', style: 'width:100%;height:36px;margin-top:4px' });
      nameInput.addEventListener('input', () => { pairing.name = nameInput.value; });
      root.append(el('div', { class: 'glass glass--card pair-card', style: 'margin-top:14px' },
        el('div', { style: 'width:56px;height:56px;margin:4px auto 0;border-radius:14px;display:grid;place-items:center;background:var(--azure-soft);color:var(--azure);border:1px solid var(--azure-edge)', html: icon('link', { size: 26, stroke: 1.6 }) }),
        el('div', { class: 'pair-title', text: 'Name this device' }),
        el('div', { class: 'pair-sub', text: 'So you can recognise it from other devices in your account.' }),
        nameInput,
        el('div', { style: 'display:flex;gap:10px;align-self:stretch' },
          el('button', { class: 'btn btn--ghost', style: 'flex:none', text: 'Cancel', onclick: () => { pairing.step = 0; navigate('home'); } }),
          el('button', { class: 'btn btn--primary', style: 'flex:1', onclick: () => { pairing.step = 1; rerender(); } },
            el('span', { text: 'Continue' }), iconEl('arrowright', 14, 1.8)))));
      clearTimeout(pairing.timer);
    } else if (pairing.step === 1) {
      root.append(el('div', { class: 'glass glass--card pair-card', style: 'margin-top:14px' },
        el('div', { style: 'width:56px;height:56px;margin:4px auto 0;border-radius:50%;display:grid;place-items:center;background:var(--terra-soft);color:var(--terra);border:1px solid var(--terra-edge);position:relative' },
          el('span', { class: 'spinner', style: 'width:26px;height:26px;border-width:2px;border-color:var(--terra-edge);border-top-color:var(--terra)' })),
        el('div', { class: 'pair-title', text: 'Approve in your browser' }),
        el('div', { class: 'pair-sub' }, 'We opened a sign-in page. Approve ', el('b', { style: 'color:var(--ink-1)', text: pairing.name }), ' there to finish pairing.'),
        el('button', { class: 'pair-link', onclick: copyPairLink }, iconEl('copy', 13, 1.6), el('span', { text: 'zyncmaster.app/pair/8h3-4f2a' })),
        el('button', { class: 'btn btn--ghost', text: 'Cancel', onclick: () => { pairing.step = 0; rerender(); } })));
      clearTimeout(pairing.timer);
      pairing.timer = setTimeout(() => {
        if (state.view === 'pairing' && pairing.step === 1) {
          pairing.step = 2; announce('Device paired successfully.'); rerender();
        }
      }, 2400);
    } else if (pairing.step === 2) {
      root.append(el('div', { class: 'glass glass--card pair-card', style: 'margin-top:14px' },
        el('div', { class: 'big-check', html: icon('check', { size: 28, stroke: 2.6 }) }),
        el('div', { class: 'pair-title', text: 'Paired!' }),
        el('div', { class: 'pair-sub' }, el('b', { style: 'color:var(--ink-1)', text: pairing.name }), ' is now mirroring your calendar. First sync starts in a moment.'),
        el('button', { class: 'btn btn--primary', style: 'align-self:stretch', text: 'Open dashboard', onclick: () => { state.sync = 'ok'; pairing.step = 0; navigate('home'); } })));
    }
  }
  async function copyPairLink() {
    try { await navigator.clipboard.writeText('https://zyncmaster.app/pair/8h3-4f2a'); } catch (_) {}
  }

  registry.register('pairing', { render: renderPairing });

  // renderSignIn / renderIdentitySignIn are NOT registry views — the gates in rerender() call them
  // directly, so re-expose them on ctx.
  ctx.gates = { renderSignIn, renderIdentitySignIn };
}
