// views/devices.js — roster de devices: presencia + rename del propio device. Antes vivía
// disperso en Settings y Clipboard settings (spec §4). El rename del device actual se muda
// AQUÍ desde accountAndDeviceSection: el bloque name-field + indicador + el flujo live-check
// (renderNameCheck / scheduleDeviceNameCheck / saveDeviceName), que solo Devices consume tras
// el corte de la Tarea 12.
//
// Desviación documentada del spec §4: el spec pide «presencia, lease, pinning, rename, unpair»;
// esta vista entrega presencia + rename. Lease/pinning/unpair quedan para cuando el roster del
// server exponga esos datos POR DEVICE (hoy ni el bridge ni la UI actual los exponen — el
// pinning vive a nivel PAIR, no device, y no hay acción de unpair).

export function registerDevicesViews(ctx) {
  const { el, icon, Bridge, live, identityAuth, settings, registry, loadClipboardDevices, devicesStatusDot } = ctx;

  // ---------------- Device-name live availability (✓/✗) ----------------
  // Tracks the latest known availability so saveDeviceName can refuse a name we already know is
  // taken without a round-trip. { name, state } where state is 'available' | 'taken' | 'invalid'.
  const deviceNameCheck = { name: null, state: null };
  let deviceNameCheckTimer = null;
  let deviceNameCheckSeq = 0;

  const DEVICE_NAME_DEBOUNCE_MS = 400;
  const DEVICE_NAME_MAX = 100;

  // Paints the indicator span: ✓ (available, aqua/ok), ✗ (taken/invalid, err), a subtle dot while
  // checking, or nothing when the field is empty / unchanged. `hint` (the row hint span) carries a
  // short message for the taken/invalid states.
  function renderNameCheck(indicator, hint, st) {
    if (!indicator) return;
    indicator.innerHTML = '';
    indicator.className = 'name-check';
    const setHint = (text, color) => { if (hint) { hint.textContent = text; hint.style.color = color || ''; } };

    if (st === 'available') {
      indicator.classList.add('name-check--ok');
      indicator.innerHTML = icon('check', { size: 15, stroke: 2.4 });
      setHint('', '');
    } else if (st === 'taken') {
      indicator.classList.add('name-check--err');
      indicator.innerHTML = icon('close', { size: 15, stroke: 2.4 });
      setHint('Name already used', 'var(--err)');
    } else if (st === 'invalid') {
      indicator.classList.add('name-check--err');
      indicator.innerHTML = icon('close', { size: 15, stroke: 2.4 });
      setHint(`Use 1–${DEVICE_NAME_MAX} characters`, 'var(--err)');
    } else if (st === 'checking') {
      indicator.classList.add('name-check--busy');
      indicator.innerHTML = '<span class="name-check__dot"></span>';
      setHint('', '');
    } else {
      setHint('', '');
    }
  }

  // Debounced live check as the user types: ~400ms after the last keystroke, ask the host whether
  // the trimmed name is free (excluding this device). Mock has no bridge, so the caller never wires
  // this in mock mode. Empty / unchanged names clear the indicator; over-long names flag invalid
  // without a round-trip.
  function scheduleDeviceNameCheck(input, indicator, hint) {
    if (!Bridge.available || !input) return;
    const name = (input.value || '').trim();

    if (deviceNameCheckTimer) { clearTimeout(deviceNameCheckTimer); deviceNameCheckTimer = null; }

    // Empty, or unchanged from the device's current name → no indicator (nothing to validate).
    if (!name || (live.device && live.device.name === name)) {
      deviceNameCheck.name = name; deviceNameCheck.state = null;
      renderNameCheck(indicator, hint, null);
      return;
    }
    if (name.length > DEVICE_NAME_MAX) {
      deviceNameCheck.name = name; deviceNameCheck.state = 'invalid';
      renderNameCheck(indicator, hint, 'invalid');
      return;
    }

    renderNameCheck(indicator, hint, 'checking');
    const seq = ++deviceNameCheckSeq;
    deviceNameCheckTimer = setTimeout(() => {
      Bridge.call('checkDeviceName', JSON.stringify({ name }))
        .then((r) => {
          if (seq !== deviceNameCheckSeq) return; // a newer keystroke superseded this check
          const st = (r && r.available) ? 'available' : 'taken';
          deviceNameCheck.name = name; deviceNameCheck.state = st;
          renderNameCheck(indicator, hint, st);
        })
        .catch(() => {
          if (seq !== deviceNameCheckSeq) return;
          deviceNameCheck.name = name; deviceNameCheck.state = null;
          renderNameCheck(indicator, hint, null); // a transient failure: clear, don't block save
        });
    }, DEVICE_NAME_DEBOUNCE_MS);
  }

  // saveDeviceName — renames the current device in place (hot rename) through the host. Shows inline
  // feedback next to the input: "Saving…", then "Saved" or an error. A no-op when the name is
  // unchanged or blank. On success live.device is updated so a later render keeps showing the real
  // name. If the name is known-taken (or the server returns name_taken) the rename is refused with
  // an inline ✗.
  function saveDeviceName(input, feedback, indicator) {
    if (!Bridge.available || !input) return;
    const name = (input.value || '').trim();
    const setMsg = (text, color) => { if (feedback) { feedback.textContent = text; feedback.style.color = color || ''; } };

    if (!name) { setMsg('Name cannot be empty', 'var(--err)'); return; }
    if (live.device && live.device.name === name) { setMsg(''); return; }
    if (name.length > DEVICE_NAME_MAX) {
      renderNameCheck(indicator, feedback, 'invalid');
      return;
    }
    // Already known to be taken (from the live check): don't even try to rename.
    if (deviceNameCheck.name === name && deviceNameCheck.state === 'taken') {
      renderNameCheck(indicator, feedback, 'taken');
      return;
    }

    setMsg('Saving…', 'var(--ink-2)');
    Bridge.call('renameDevice', JSON.stringify({ name }))
      .then((d) => {
        const saved = (d && d.name) || name;
        live.device = Object.assign({}, live.device, { name: saved });
        settings.deviceName = saved;
        input.value = saved;
        deviceNameCheck.name = saved; deviceNameCheck.state = null;
        renderNameCheck(indicator, feedback, null);
        setMsg('Saved', 'var(--ok)');
        setTimeout(() => { if (feedback) feedback.textContent = ''; }, 2000);
      })
      .catch((err) => {
        const msg = (err && err.message) ? err.message : '';
        // The server rejects a duplicate with a 409 carrying "name_taken"; show the inline ✗ instead
        // of a generic error so it reads like the live indicator.
        if (/name_taken/i.test(msg)) {
          deviceNameCheck.name = name; deviceNameCheck.state = 'taken';
          renderNameCheck(indicator, feedback, 'taken');
          return;
        }
        setMsg(msg || 'Could not rename', 'var(--err)');
      });
  }

  // renameField — the live device-name input + ✓/✗ indicator + feedback line, integrated under the
  // "this device" card. Moved VERBATIM from accountAndDeviceSection (spec §4).
  function renameField() {
    const currentName = (live.device && live.device.name) || settings.deviceName || '';
    const nameInput = el('input', { class: 'field-input', value: currentName, placeholder: 'Name this device' });
    const feedback = el('span', { class: 'cfg-row__hint', style: 'margin-left:10px' });
    const indicator = el('span', { class: 'name-check' });
    const inputWrap = el('div', { class: 'name-field' }, nameInput, indicator);

    nameInput.addEventListener('input', () => {
      settings.deviceName = nameInput.value;
      scheduleDeviceNameCheck(nameInput, indicator, feedback);
    });
    nameInput.addEventListener('change', () => saveDeviceName(nameInput, feedback, indicator));

    return el('div', { class: 'device-card__rename' },
      el('div', { class: 'device-card__rename-lbl' },
        document.createTextNode('Device name'), feedback),
      inputWrap);
  }

  function deviceCard(dev) {
    const isThis = !!dev.isThis;
    return el('div', { class: 'glass glass--card device-card' },
      el('span', { class: `side-dot side-dot--${dev.online ? 'ok' : 'off'}`, title: dev.online ? 'online' : 'offline' }),
      el('div', { class: 'device-card__body' },
        el('div', { class: 'device-card__name', text: dev.name || 'Device' }),
        el('div', { class: 'board-row__meta', text: `${dev.online ? 'online' : 'offline'}${isThis ? ' · this device' : ''}` }),
        (isThis && Bridge.available) ? renameField() : null),
      isThis ? el('span', { class: 'chip chip--info', text: 'this device' }) : null);
  }

  // Identity + this-device summary. Sign-in (identityAuth.me) is a separate grant from the device
  // roster, so surface both: who the user is signed in as, and which device this is. The current
  // device name comes from the roster's isThis entry, falling back to the live device / local config.
  function renderIdentityCard() {
    const me = (Bridge.desktopApp && identityAuth && identityAuth.me) || null;
    const email = (me && me.email) || '—';
    const name = (me && me.displayName) || '';
    const roster = (live.clipboardDevices && live.clipboardDevices.devices) || [];
    const thisDev = roster.find((d) => d.isThis);
    const dev = (thisDev && thisDev.name) || (live.device && live.device.name) || settings.deviceName || '—';
    return el('section', { class: 'glass glass--card devices-id' },
      el('div', { class: 'devices-id-row' },
        el('span', { class: 'devices-id-lbl', text: 'Signed in as' }),
        el('span', { class: 'devices-id-val', text: name ? `${name} · ${email}` : email })),
      el('div', { class: 'devices-id-row' },
        el('span', { class: 'devices-id-lbl', text: 'This device' }),
        el('span', { class: 'devices-id-val', text: dev })));
  }

  function renderDevices(root) {
    if (Bridge.desktopApp) loadClipboardDevices('devices');
    root.append(renderIdentityCard());
    const devices = (live.clipboardDevices && live.clipboardDevices.devices) || [];
    const list = el('div', { class: 'device-list' });
    if (!devices.length) {
      list.append(el('div', { class: 'empty' },
        el('div', { class: 'empty__title', text: 'No paired devices yet' }),
        el('div', { class: 'empty__sub', text: 'Devices appear here when you sign in to SyncMaster on them.' })));
    } else {
      devices.forEach((d) => list.append(deviceCard(d)));
    }
    root.append(list);
  }

  registry.register('devices', {
    render: renderDevices, soft: true,
    // hidden solo en web panel (NO !Bridge.available): en demo sin Bridge la vista debe verse con
    // su empty-state — ver la nota del predicado hidden en 4.7 paso 5; el smoke de 12.5 navega a
    // Devices en demo.
    nav: { label: 'Devices', icon: 'device', order: 90, section: 'system', hidden: () => Bridge.webPanel },
    statusDot: () => devicesStatusDot(),
  });
}
