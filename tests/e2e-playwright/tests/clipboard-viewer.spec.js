import { test, expect } from '@playwright/test';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

// Browser UI e2e for the clipboard paste OVERLAY (the served ui/clipboard-viewer.html). This page
// is the frameless window the desktop App pops up on the global viewer hotkey; in production it
// loads through WebView2 and talks the SHARED BRIDGE CONTRACT over window.chrome.webview. There is
// no server in the loop for the viewer, so this suite is fully self-contained:
//
//   * it serves ui/clipboard-viewer.html + its CSS/JS straight from disk via route interception
//     (no running Server needed, unlike panel.spec.js), and
//   * it injects a MOCK window.chrome.webview BEFORE the module loads, so clipboard-viewer.js picks
//     its "native" transport and we can answer getClipboardDevices / getClipboardHistory and record
//     pasteClipboardEntry / closeClipboardViewer — exercising the REAL render + navigation code.
//
// What it asserts (Plan 3 Task 7):
//   - rich render shows the filter segmented control + per-row meta line,
//   - ArrowDown/Up move the selection,
//   - PageDown jumps the selection to the first item of the next group (item #11),
//   - filtering to "Img" hides the text rows,
//   - a very long URL row never overflows its row (scrollWidth <= clientWidth),
//   - mini density hides the filters / meta / help bar,
//   - Enter calls the mock paste with the SELECTED item's id.

const here = path.dirname(fileURLToPath(import.meta.url));
const uiRoot = path.resolve(here, '../../../ui'); // tests/e2e-playwright/tests -> repo/ui

const CONTENT_TYPES = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.svg': 'image/svg+xml',
};

// A fixed history: 25 text items + 5 image items so groups of 10 and the image filter are testable.
// Item 0 carries an absurdly long unbroken URL to prove rows never overflow horizontally.
function buildHistory() {
  const items = [];
  const now = Date.now();
  const longUrl =
    'https://example.com/' + 'segment-'.repeat(40) +
    '?token=' + 'A'.repeat(400) + '&ref=overflow-guard-check';
  items.push({
    id: 'long-0',
    type: 'Text',
    text: longUrl,
    imagePreviewDataUri: null,
    sizeBytes: null,
    createdUtc: new Date(now).toISOString(),
    originDeviceId: 'dev-1',
    originDeviceName: 'Desk',
  });
  for (let i = 1; i < 25; i++) {
    items.push({
      id: `t-${i}`,
      type: 'Text',
      text: `Text snippet number ${i} that is reasonably short`,
      imagePreviewDataUri: null,
      sizeBytes: null,
      createdUtc: new Date(now - i * 60_000).toISOString(),
      originDeviceId: 'dev-1',
      originDeviceName: i % 2 ? 'Desk' : 'Laptop',
    });
  }
  for (let i = 0; i < 5; i++) {
    items.push({
      id: `img-${i}`,
      type: 'Image',
      text: null,
      imagePreviewDataUri: null, // exercise the typed-tile fallback (no DIB->PNG)
      sizeBytes: 12_345 + i * 1000,
      createdUtc: new Date(now - (30 + i) * 60_000).toISOString(),
      originDeviceId: 'dev-2',
      originDeviceName: 'Phone',
    });
  }
  return items;
}

// The mock bridge: a window.chrome.webview that answers the SHARED BRIDGE CONTRACT actions exactly
// the way the App's WebView2 host does — a reply is {correlationId, ok:true, payload:<JSON string>}.
// density ('rich'|'mini') selects what getClipboardDevices reports for THIS device. Paste/close
// calls are recorded on window.__calls for assertions.
function makeInitScript(historyJson, density) {
  return `(() => {
    const HISTORY = ${historyJson};
    const DENSITY = ${JSON.stringify(density)};
    const listeners = [];
    window.__calls = { paste: [], close: 0 };
    const reply = (correlationId, value) => {
      const msg = JSON.stringify({ correlationId, ok: true, payload: JSON.stringify(value) });
      // Mirror WebView2: deliver as a message event with string data, async (next tick).
      setTimeout(() => listeners.forEach((cb) => cb({ data: msg })), 0);
    };
    const devices = {
      thisDeviceId: 'dev-1',
      devices: [{
        id: 'dev-1', name: 'Desk', online: true, isThis: true,
        settings: { autoSync: true, send: true, receive: true, viewerHotkey: 'Ctrl+Win+Q', density: DENSITY, showHints: true },
      }],
    };
    window.chrome = window.chrome || {};
    window.chrome.webview = {
      addEventListener: (type, cb) => { if (type === 'message') listeners.push(cb); },
      removeEventListener: (type, cb) => {
        if (type === 'message') { const i = listeners.indexOf(cb); if (i >= 0) listeners.splice(i, 1); }
      },
      postMessage: (raw) => {
        let m; try { m = JSON.parse(raw); } catch (_) { return; }
        const { action, correlationId, payload } = m;
        if (action === 'getClipboardHistory') return reply(correlationId, HISTORY);
        if (action === 'getClipboardDevices') return reply(correlationId, devices);
        if (action === 'pasteClipboardEntry') {
          // payload is the id, stringified by the viewer's Bridge.call (String(id)).
          let id = payload; try { id = JSON.parse(payload); } catch (_) {}
          window.__calls.paste.push(id);
          return reply(correlationId, { status: 'ok' });
        }
        if (action === 'closeClipboardViewer') { window.__calls.close++; return reply(correlationId, null); }
        return reply(correlationId, null);
      },
    };
  })();`;
}

// Serve ui/clipboard-viewer.html + its referenced assets from disk. The page is loaded from a
// stable https origin so the viewer's transport never falls into loopback mode by mistake.
async function gotoViewer(page, { density }) {
  await page.addInitScript(makeInitScript(JSON.stringify(buildHistory()), density));

  await page.route('https://zyncmaster.test/**', async (route) => {
    const url = new URL(route.request().url());
    let rel = url.pathname.replace(/^\//, '');
    if (rel === '' ) rel = 'clipboard-viewer.html';
    const abs = path.join(uiRoot, rel);
    try {
      const body = await readFile(abs);
      const ext = path.extname(abs).toLowerCase();
      await route.fulfill({ status: 200, contentType: CONTENT_TYPES[ext] || 'application/octet-stream', body });
    } catch (_) {
      await route.fulfill({ status: 404, body: 'not found' });
    }
  });

  await page.goto('https://zyncmaster.test/clipboard-viewer.html');
  // The card renders after getClipboardDevices/History resolve; wait for the first row.
  await page.locator('.cb-viewer .cb-row').first().waitFor();
}

test.describe('Clipboard viewer overlay', () => {
  test('rich: shows the filter control and a per-row meta line', async ({ page }) => {
    await gotoViewer(page, { density: 'rich' });

    await expect(page.locator('.cb-viewer')).toBeVisible();
    await expect(page.locator('.cb-viewer--mini')).toHaveCount(0);
    // Filter segmented control is present (rich only).
    await expect(page.locator('.cb-filter')).toBeVisible();
    await expect(page.locator('.cb-filter .cb-filter__item')).toHaveCount(4);
    // Rows carry a meta line in rich.
    await expect(page.locator('.cb-row .cb-meta').first()).toBeVisible();
    // Help bar shows (showHints = true).
    await expect(page.locator('.cb-help')).toBeVisible();
  });

  test('rich: ArrowDown / ArrowUp move the selection', async ({ page }) => {
    await gotoViewer(page, { density: 'rich' });

    // Item 0 starts selected.
    await expect(page.locator('.cb-row').nth(0)).toHaveClass(/is-selected/);

    await page.keyboard.press('ArrowDown');
    await expect(page.locator('.cb-row').nth(1)).toHaveClass(/is-selected/);
    await expect(page.locator('.cb-row').nth(0)).not.toHaveClass(/is-selected/);

    await page.keyboard.press('ArrowDown');
    await expect(page.locator('.cb-row').nth(2)).toHaveClass(/is-selected/);

    await page.keyboard.press('ArrowUp');
    await expect(page.locator('.cb-row').nth(1)).toHaveClass(/is-selected/);
  });

  test('rich: PageDown jumps the selection to the first item of the next group (#11)', async ({ page }) => {
    await gotoViewer(page, { density: 'rich' });

    // Selection starts at index 0 (the first group). PageDown -> first item of the next group = index 10.
    await page.keyboard.press('PageDown');
    const selected = page.locator('.cb-row.is-selected');
    await expect(selected).toHaveAttribute('data-index', '10');
  });

  test('rich: filtering to Img hides the text rows', async ({ page }) => {
    await gotoViewer(page, { density: 'rich' });

    const totalRows = await page.locator('.cb-row').count();
    expect(totalRows).toBeGreaterThan(5);

    // Click the "Img" filter (label "Img" in the segmented control).
    await page.getByRole('button', { name: 'Img', exact: true }).click();

    // Only image rows remain (the fixture has 5 images, all carrying a size).
    const rows = page.locator('.cb-row');
    await expect(rows).toHaveCount(5);
    await expect(page.locator('.cb-row--img')).toHaveCount(5);
  });

  test('rich: a long URL row does not overflow horizontally', async ({ page }) => {
    await gotoViewer(page, { density: 'rich' });

    // The first row holds the very long URL; its title must be clipped, not widen the row.
    const metrics = await page.locator('.cb-row').first().evaluate((el) => {
      const title = el.querySelector('.cb-title');
      return {
        rowScroll: el.scrollWidth, rowClient: el.clientWidth,
        titleScroll: title ? title.scrollWidth : 0, titleClient: title ? title.clientWidth : 0,
      };
    });
    // The row never exceeds its own box; the title is allowed to be ellipsized but the layout holds.
    expect(metrics.rowScroll).toBeLessThanOrEqual(metrics.rowClient);
  });

  test('mini: hides the filters, the meta line and the help bar', async ({ page }) => {
    await gotoViewer(page, { density: 'mini' });

    await expect(page.locator('.cb-viewer--mini')).toBeVisible();
    await expect(page.locator('.cb-filter')).toHaveCount(0);
    await expect(page.locator('.cb-row .cb-meta')).toHaveCount(0);
    await expect(page.locator('.cb-help')).toHaveCount(0);
  });

  test('applyNewItem: re-anchors selection against the FILTERED list under an active filter', async ({ page }) => {
    await gotoViewer(page, { density: 'rich' });

    // Import the pure helper straight from the loaded module and exercise it in the page context.
    // Scenario: an Img filter is active; the visible list is the 5 images and sel points at the 2nd
    // visible image. A new TEXT item arrives. The selected image must stay highlighted: sel must
    // re-map to the same image's index within the NEW filtered (image-only) list, not jump to a raw
    // index that the render would mis-apply to the filtered list.
    const result = await page.evaluate(async () => {
      const mod = await import('/clipboard-viewer.js');
      const items = [
        { id: 'i1', type: 'Image', sizeBytes: 1 },
        { id: 't1', type: 'Text', text: 'a' },
        { id: 'i2', type: 'Image', sizeBytes: 2 },
        { id: 't2', type: 'Text', text: 'b' },
        { id: 'i3', type: 'Image', sizeBytes: 3 },
      ];
      // Visible (image filter) = [i1, i2, i3]; sel=1 selects i2.
      const state = { items, filter: 'image', density: 'rich', sel: 1 };
      const next = mod.applyNewItem(state, { id: 't3', type: 'Text', text: 'c' });
      const nextVisible = next.items.filter((it) => mod.filterMatch(it, 'image'));
      return {
        sel: next.sel,
        selectedVisibleId: nextVisible[next.sel] ? nextVisible[next.sel].id : null,
        topId: next.items[0] ? next.items[0].id : null,
      };
    });

    // The new text item went to the top of the raw list, but the highlighted IMAGE is unchanged.
    expect(result.topId).toBe('t3');
    expect(result.selectedVisibleId).toBe('i2');
    expect(result.sel).toBe(1);
  });

  test('rich: Enter pastes the selected item id', async ({ page }) => {
    await gotoViewer(page, { density: 'rich' });

    // Move down two so a non-default item is selected (index 2), confirm the highlight, then paste.
    await page.keyboard.press('ArrowDown');
    await page.keyboard.press('ArrowDown');
    await expect(page.locator('.cb-row.is-selected')).toHaveAttribute('data-index', '2');

    await page.keyboard.press('Enter');

    // The mock recorded a paste; the id is the third item (index 2) in newest-first order = 't-2'.
    await expect.poll(() => page.evaluate(() => window.__calls.paste.length)).toBeGreaterThan(0);
    const pasted = await page.evaluate(() => window.__calls.paste);
    expect(pasted[0]).toBe('t-2');
    // And the viewer asked to close after the paste.
    await expect.poll(() => page.evaluate(() => window.__calls.close)).toBeGreaterThan(0);
  });
});
