// views/settings.js — Settings hub: identity + this-device app prefs (Run at startup),
// navigable Calendar / Clipboard / Devices rows, Appearance, and About. The calendar accounts
// and the device roster/rename now live in their own modules (Calendar / Devices); this hub
// links to them. Extraído VERBATIM de app.js — solo el device-name row salió a views/devices.js.

export function registerSettingsViews(ctx) {
  const {
    el, iconEl, Bridge, state, live, settings, navigate, softRepaint,
    cfgSection, cfgRow, navRow, loadAccounts, loadCalendars, logoSvg,
    pushConfig, applyTheme, storedTheme, VERSION, signOutApp, signOutWeb, registry,
  } = ctx;

  // toggle(get, set) — a .toggle bound to a settings getter/setter that persists on flip via
  // pushConfig. Keyboard + mouse both flip it.
  function toggle(get, set) {
    const t = el('div', { class: 'toggle', role: 'switch', 'aria-checked': String(get()), tabindex: '0' });
    const flip = () => { const v = !get(); set(v); t.setAttribute('aria-checked', String(v)); pushConfig(); };
    t.addEventListener('click', flip);
    t.addEventListener('keydown', (e) => { if (e.key === ' ' || e.key === 'Enter') { e.preventDefault(); flip(); } });
    return t;
  }

  // ensureConfigData — coalesced first-load for the Settings hub. Fires every lazy fetch the hub
  // needs (device, auto-start, connected accounts + their calendars) in one batch and triggers a
  // SINGLE softRepaint when the whole batch settles, instead of each fetch repainting on its own.
  // That stops the first open of Settings from flickering as each promise lands. Guarded so the batch
  // runs at most once per session (configDataLoaded); nothing here re-fetches data already cached.
  let configDataLoaded = false;
  let configDataLoading = false;
  function ensureConfigData() {
    if (!Bridge.available || configDataLoaded || configDataLoading) return;
    configDataLoading = true;

    const jobs = [];

    // Device record (desktop App). Cached on live.device.
    if (live.device === null && !live.deviceLoading) {
      live.deviceLoading = true;
      jobs.push(Bridge.call('getDevice')
        .then((d) => { live.device = d || {}; if (d && d.name) settings.deviceName = d.name; })
        .catch(() => { live.device = {}; })
        .finally(() => { live.deviceLoading = false; }));
    }

    // Auto-start preference (desktop App). Cached on live.autoStart.
    if (live.autoStart === null) {
      jobs.push(Bridge.call('getAutoStart')
        .then((r) => { live.autoStart = !!(r && r.enabled); })
        .catch(() => {}));
    }

    // Connected accounts + each account's calendars (desktop App only; the web panel has none).
    if (Bridge.desktopApp && live.accounts === null) {
      jobs.push(loadAccounts().then((list) => Promise.all(
        (list || []).map((acc) => (live.calendars[acc.accountRef] ? Promise.resolve() : loadCalendars(acc.accountRef)))
      )));
    } else if (Bridge.desktopApp) {
      (live.accounts || []).forEach((acc) => { if (!live.calendars[acc.accountRef]) jobs.push(loadCalendars(acc.accountRef)); });
    }

    Promise.all(jobs).finally(() => {
      configDataLoaded = true;
      configDataLoading = false;
      if (state.view === 'config') softRepaint();
    });
  }

  // resetSessionState — clear every per-session cache the Settings hub relies on so the next sign-in
  // (possibly a DIFFERENT identity on the same machine without restarting the App) reloads fresh data
  // instead of showing the previous user's device name, auto-start toggle and account count. Without
  // this, ensureConfigData()'s once-per-session guard (configDataLoaded) would short-circuit and the
  // hub would render stale values. Called on sign-out (via ctx, by signOutApp in app.js).
  function resetSessionState() {
    configDataLoaded = false;
    configDataLoading = false;
    live.device = null;
    live.deviceLoading = false;
    live.autoStart = null;
    live.accounts = null;
    live.calendars = {};
    live.me = null;
    live.clipboardDevices = null;
    live.clipboardDevicesLoading = false;
    live.clipboardHistory = null;
    live.clipboardHistoryLoading = false;
    // El estado de vista módulo-local (accordion abierto, overlay/filter) vive en
    // views/clipboard.js; ese módulo expone su reset en ctx.
    if (ctx.resetClipboardViewState) ctx.resetClipboardViewState();
  }
  // Re-exposed so signOutApp (app.js) can clear the session caches this hub owns.
  ctx.resetSessionState = resetSessionState;

  // ---------------- Screen: Settings hub ----------------
  // The hub separates the three concepts the user kept conflating: IDENTITY (who signed in),
  // DEVICE (this machine), and CALENDAR ACCOUNTS (OAuth grants). Identity lives in the
  // "Account & device" card; everything calendar-shaped, the device roster and About sit behind
  // navigable rows that open their own module.
  function renderConfig(root) {
    // Web panel: keep the lean panel-appropriate hub (identity + appearance + about). It has no
    // device and no desktop Calendar module, so it never shows those.
    if (Bridge.webPanel) {
      const email = (live.me && live.me.email) || 'Signed in';
      const signOutBtn = el('button', { class: 'btn btn--ghost', style: 'color:var(--err)', text: 'Sign out', onclick: () => signOutWeb() });
      root.append(cfgSection('Account',
        cfgRow('Signed in as', el('div', { class: 'cfg-row__hint', text: email }), signOutBtn)));
      root.append(appearanceSection());
      root.append(aboutSection());
      return;
    }

    // Coalesced first-load: kick every lazy fetch this hub needs (device, auto-start, accounts +
    // their calendars) in ONE batch and repaint ONCE when the batch settles, instead of each fetch
    // firing its own softRepaint (which made the first open of Settings flicker repeatedly). The
    // section builders below only READ from `live`; they no longer trigger their own loads.
    ensureConfigData();

    // Account & device — identity (who) + this device app prefs, consolidated into one card. The
    // device NAME / roster moved to the Devices module (linked below).
    root.append(accountAndDeviceSection());

    // Calendar — navigable module. Desktop App only: renderCalendarSettings bounces back to the hub
    // unless Bridge.desktopApp, so showing the row in mock/web would be a dead-end (a flash that
    // navigates and immediately returns). Show a live summary value (account count) when known.
    if (Bridge.desktopApp) {
      const accounts = live.accounts || [];
      let calValue = '';
      if (live.accounts === null) calValue = '';
      else if (accounts.length === 0) calValue = 'Not connected';
      else calValue = `${accounts.length} ${accounts.length === 1 ? 'account' : 'accounts'}`;
      root.append(el('div', { class: 'glass glass--card config-section' },
        navRow({ label: 'Calendar accounts', sublabel: 'Connected calendar accounts', value: calValue, onClick: () => navigate('calendar-settings') })));

      // Devices — navigable module (roster + rename). Shows a live device-count summary once the
      // device list has loaded; blank until then (never a fabricated number).
      const devs = live.clipboardDevices;
      let devValue = '';
      if (devs && Array.isArray(devs.devices)) {
        const n = devs.devices.length;
        devValue = `${n} ${n === 1 ? 'device' : 'devices'}`;
      }
      root.append(el('div', { class: 'glass glass--card config-section' },
        navRow({ label: 'Devices', sublabel: 'Presence and device name', value: devValue, onClick: () => navigate('devices') })));

      // Clipboard — navigable module (desktop App only).
      root.append(el('div', { class: 'glass glass--card config-section' },
        navRow({ label: 'Clipboard', sublabel: 'Sync across your devices', value: '', onClick: () => navigate('clipboard-settings') })));
    }

    // Appearance.
    root.append(appearanceSection());

    // About — same nav-row component so the chevron aligns.
    root.append(aboutSection());
  }

  // accountAndDeviceSection — identity row, plus (desktop App only) the device-only "Run at startup"
  // app preference. The device-name rename row moved to the Devices module. Honest across
  // transports: mock seeds demo strings, the real desktop App reads identity live.
  function accountAndDeviceSection() {
    const rows = [];

    // Identity row: name (displayName||email) on top, a "Signed in" chip + email/plan hint below,
    // and Sign out on the right. This single row IS the session representation.
    const identityRow = (primary, email, plan) => {
      const hint = el('div', { class: 'cfg-row__hint identity-hint' });
      hint.append(el('span', { class: 'chip chip--ok identity-signed', style: 'margin-right:6px' }, iconEl('check', 9, 2.4), 'Signed in'));
      if (email) hint.append(el('span', { class: 'identity-hint__email', text: email }));
      if (plan) hint.append(el('span', { class: 'chip chip--ok', style: 'margin-left:8px;height:18px;font-size:9.5px', text: String(plan) }));
      const signOutBtn = el('button', { class: 'btn btn--ghost', style: 'color:var(--err)', text: 'Sign out', onclick: () => signOutApp() });
      return cfgRow(primary, hint, signOutBtn);
    };

    if (Bridge.desktopApp && ctx.identityAuth.signedIn) {
      const me = ctx.identityAuth.me || {};
      rows.push(identityRow(me.displayName || me.email || 'Signed in', (me.displayName && me.email) ? me.email : '', me.plan));
    } else if (!Bridge.available) {
      // Mock-only walkthrough identity.
      rows.push(identityRow('Daniel López', 'daniel@outlook.com', null));
    }

    // Run at startup — a device/app preference (NOT calendar), so it lives here. Native/loopback
    // only; backed by the host auto-start manager.
    if (Bridge.available) {
      const startupToggle = toggle(
        () => (live.autoStart !== null) ? live.autoStart : settings.startup,
        (v) => { settings.startup = v; live.autoStart = v; Bridge.call('setAutoStart', v ? 'true' : 'false').catch(() => {}); });
      // (auto-start is fetched by the coalesced ensureConfigData() batch — read-only here.)
      rows.push(cfgRow('Run at startup', el('div', { class: 'cfg-row__hint', text: 'Launch Zync Master when you sign in' }), startupToggle));
    } else {
      rows.push(cfgRow('Run at startup', el('div', { class: 'cfg-row__hint', text: 'Launch Zync Master when you sign in' }),
        toggle(() => settings.startup, (v) => { settings.startup = v; })));
    }

    return cfgSection('Account & device', ...rows);
  }

  // appearanceSection — Dark / Light / Auto segmented. The theme handler updates aria-pressed on the
  // buttons directly (no full rerender) so toggling the theme never flickers the screen.
  function appearanceSection() {
    const seg = el('div', { class: 'segmented' });
    const buttons = [];
    ['Dark', 'Light', 'Auto'].forEach((opt) => {
      const val = opt.toLowerCase();
      const b = el('button', { class: 'segmented__item', 'aria-pressed': String(storedTheme() === val), text: opt,
        onclick: () => {
          applyTheme(val);
          pushConfig();
          buttons.forEach((btn) => btn.setAttribute('aria-pressed', String(btn.dataset.val === val)));
        } });
      b.dataset.val = val;
      buttons.push(b);
      seg.append(b);
    });
    return cfgSection('Appearance',
      cfgRow('Theme', el('div', { class: 'cfg-row__hint', text: 'Auto follows your system' }), seg));
  }

  // aboutSection — the navigable About row (nav-row component, so the chevron is centred and the
  // version sits to its left). live.appVersion is populated by the getAppVersion bridge call at
  // boot (desktop App) and falls back to the hardcoded VERSION constant (web panel / not yet set).
  function aboutSection() {
    const displayVersion = live.appVersion || VERSION;
    return el('div', { class: 'glass glass--card config-section' },
      navRow({ label: 'About Zync Master', sublabel: 'Version, credits, links', value: displayVersion, onClick: () => navigate('about') }));
  }

  // ---------------- Screen: About ----------------
  // External destinations. The landing is the product's website/source; release notes ("What's
  // new") live on the GitHub Releases page. Rendered as real <a target="_blank"> links.
  const ABOUT_WEBSITE_URL = 'https://zyncmaster.azurewebsites.net';
  const ABOUT_RELEASES_URL = 'https://github.com/zezelazo/ZyncMaster/releases';
  // Company site (DevLab-Pe), distinct from the product landing above.
  const ABOUT_COMPANY_URL = 'https://devlabperu.com';

  function renderAbout(root) {
    root.append(ctx.viewHeader('About', { onBack: () => navigate('config') }));
    const link = (ic, label, href) => el('a', {
      class: 'about-link', href, target: '_blank', rel: 'noopener noreferrer',
    }, iconEl(ic, 13, 1.6), label);

    // The open-source notices live in a file bundled next to the desktop exe; opening it is a
    // desktop-only bridge action (openLicenses) that hands the file to the system's default viewer.
    const links = el('div', { class: 'about-links' },
      link('link', 'Website', ABOUT_WEBSITE_URL),
      link('sparkle', "What's new", ABOUT_RELEASES_URL));
    if (Bridge.desktopApp) {
      links.append(el('button', {
        class: 'about-link', type: 'button',
        style: 'grid-column:1 / -1',
        onclick: () => { Bridge.call('openLicenses').catch(() => {}); },
      }, iconEl('note', 13, 1.6), 'Open-source notices'));
    }

    root.append(el('div', { class: 'glass glass--card about-card' },
      el('div', { class: 'about-logo', html: logoSvg({ size: 64 }) }),
      el('div', { class: 'about-name', text: 'Zync Master' }),
      // Sourced from the VERSION const — single source in the UI. No build number.
      el('div', { class: 'about-version num', text: `VERSION ${live.appVersion || VERSION} · BETA` }),
      el('div', { class: 'about-tag', text: 'A quiet desktop utility for mirroring calendars across Microsoft, Google and iCloud accounts. Past events are never touched.' }),
      links,
    ));
    root.append(el('div', { class: 'glass glass--card about-credits' },
      el('div', { class: 'about-credits__hd', text: 'Made by DevLab-Pe' }),
      el('div', { class: 'about-credits__txt', text: 'For people who are tired of keeping things in sync across their devices — PCs, Macs and phones — by hand, over and over.' }),
      el('a', { class: 'about-credits__link', href: ABOUT_COMPANY_URL, target: '_blank', rel: 'noopener noreferrer' },
        iconEl('link', 12, 1.6), el('span', { text: 'devlabperu.com' })),
      el('div', { class: 'about-sys', text: '© 2026 DevLab-Pe · still in beta' }),
    ));
  }

  registry.register('config', {
    render: renderConfig, soft: true,
    nav: { label: 'Settings', icon: 'settings', order: 100, section: 'system' },
  });
  registry.register('about', { render: renderAbout, parent: 'config' });
}
