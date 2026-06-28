# Plan — Desktop UI overhaul + native clipboard popup (v0.5.0)

Executor: Opus 4.8, effort high/xhigh. Execute phases 1→9 in order. Each phase has a gate and a
commit; a failed phase must not contaminate committed ones.

## 0. Rules
- Branch: `git switch -c ui-overhaul-0.5.0`. Never commit to `main`.
- Stage by explicit path. Never `git add -A`/`git add .`/`2>/dev/null`. Verify with
  `git diff --cached --name-only` before every commit.
- Identifiers and UI strings in English. Code comments in English.
- Never reference AI/Claude/Anthropic/Copilot or `Co-Authored-By` in commits, PRs, comments, README.
- Build/test:
  ```bash
  dotnet build -c Release --nologo
  dotnet test --nologo
  dotnet test tests/ZyncMaster.App.Tests/ZyncMaster.App.Tests.csproj -c Release --nologo
  dotnet test tests/ZyncMaster.Server.Tests/ZyncMaster.Server.Tests.csproj -c Release --nologo
  ```
- Gate per phase: 0 build errors, 0 failing tests. UI is JS vanilla in `ui/` (no build step); still
  run `dotnet build -c Release` (the App `.csproj` copies `ui/` → `Assets/ui`).
- Bridge action / engine method names marked "(verify name)" are placeholders — read the cited file and
  use the real name. Do not invent.
- Failure protocol: if an anchor is not found verbatim, or a build/test fails non-obviously, write
  `plans/reports/<NN>-<phase>-FAILED.md` (phase/step, expected, actual output verbatim, one bounded
  attempt, `git status` + `git diff --cached --name-only`, hypothesis), revert uncommitted changes of
  that phase, stop. Do not improvise architecture.

## 0.1 Targets
- App version files (bump in Phase 9 only): `src/ZyncMaster.App/ZyncMaster.App.csproj` (~19-22:
  `Version`/`AssemblyVersion`/`FileVersion`/`InformationalVersion`), `ui/js/app.js` (~323:
  `const VERSION`). Target `0.5.0`.
- Do not touch the Angular `zync-web`. Do not change global constants (`--r-pill`,
  `AppointmentIdNamespace`, color tokens) without explicit approval.

---

## Phase 1 — Native acrylic clipboard popup (no WebView2)

WebView2 cannot be transparent here (child HWND composites opaque; `MainWindow.axaml:28-31` already
abandoned transparency). Rebuild `ClipboardViewerWindow` as native Avalonia content with a DWM acrylic
system backdrop. Also fixes: default selection = first item, Enter pastes into the focused app, hover
trash overlay.

### 1.1 Read for real names
- `src/ZyncMaster.App/Bridge/EngineActions.cs` — methods behind bridge actions `getClipboardHistory`,
  `pasteClipboardEntry`, `deleteClipboardEntry`.
- `src/ZyncMaster.App/App.axaml.cs` — `EnsureClipboardViewer()` (~605-634),
  `OnClipboardItemReceived` (~476-600), access to `_engineHost.Actions`.
- `src/ZyncMaster.App/Platform/Clipboard/WindowsClipboardSink.cs` — `PasteIntoFocusedAsync(entry,
  targetWindow, ct)` (~46-76). Reuse as-is.
- `src/ZyncMaster.Engine/Clipboard/` — `ClipboardEntry` shape (id, kind, text/file/image, device, time).

### 1.2 Win32 DWM P/Invoke
File `src/ZyncMaster.App/Platform/Clipboard/Win32.cs`. Anchor:
```csharp
        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
```
Insert after it:
```csharp
        // DWM system backdrop (Windows 11 22621+). 38 = DWMWA_SYSTEMBACKDROP_TYPE; 3 = DWMSBT_TRANSIENTWINDOW.
        public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
        public const int DWMSBT_TRANSIENTWINDOW = 3;
        public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        public const int DWMWCP_ROUND = 2;

        [DllImport("dwmapi.dll", SetLastError = true)]
        public static extern int DwmSetWindowAttribute(
            IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);
```
Gate: `dotnet build -c Release --nologo`.

### 1.3 Row model
New file `src/ZyncMaster.App/Windows/ClipboardRow.cs`:
```csharp
using System;

namespace ZyncMaster.App.Windows;

public sealed class ClipboardRow
{
    public required string Id { get; init; }    // engine entry id
    public required string Kind { get; init; }  // "text" | "image" | "file"
    public required string Title { get; init; } // text preview, file name, or "Image"
    public required string Meta { get; init; }  // e.g. "DEVLAB2 · 1 min"
}
```
If `required` fails to compile, switch to a ctor with `ArgumentNullException` guards. Gate: build.

### 1.4 Rewrite the window

#### 1.4.1 XAML — replace `src/ZyncMaster.App/Windows/ClipboardViewerWindow.axaml` entirely:
```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        x:Class="ZyncMaster.App.Windows.ClipboardViewerWindow"
        Title="Zync Clipboard"
        Width="316" Height="420"
        WindowDecorations="None"
        ExtendClientAreaToDecorationsHint="True"
        Background="Transparent"
        TransparencyLevelHint="Transparent"
        CanResize="False"
        Topmost="True"
        ShowInTaskbar="False"
        WindowStartupLocation="CenterScreen">
  <Border x:Name="Card" CornerRadius="14" Padding="8" Background="#14000000">
    <DockPanel LastChildFill="True">
      <TextBlock x:Name="HeaderText" DockPanel.Dock="Top" Margin="6,4,6,8"
                 FontSize="11" Foreground="#9FB0CC" Text="CLIPBOARD" />
      <TextBlock x:Name="HintText" DockPanel.Dock="Bottom" Margin="6,8,6,2"
                 FontSize="10" Foreground="#7F8AA3" Text="↑↓ move · ↵ paste · Del remove · Esc close" />
      <ListBox x:Name="List" Background="Transparent" BorderThickness="0" Padding="0" />
    </DockPanel>
  </Border>
</Window>
```
Backdrop comes from DWM; `#14000000` is a faint contrast veil — do not raise it.

#### 1.4.2 Code-behind — `src/ZyncMaster.App/Windows/ClipboardViewerWindow.axaml.cs`

(a) Replace the constructor (~40-52):
```csharp
    public ClipboardViewerWindow()
    {
        InitializeComponent();

        // Esc closes (hides) the viewer. KeyDown on the window catches it before the WebView2 child in
        // the common case; the UI page also forwards Esc through the bridge (closeClipboardViewer) as a
        // belt-and-braces path when the WebView has keyboard focus.
        KeyDown += OnKeyDown;

        // Focus loss dismisses the popup, matching the OS quick-pickers (Win+V etc.). Guarded by
        // _suppressDeactivate so a programmatic hide during paste does not double-fire.
        Deactivated += OnDeactivated;
    }
```
with:
```csharp
    public event Action<ClipboardRow>? PasteRequested;
    public event Action<ClipboardRow>? DeleteRequested;

    public ClipboardViewerWindow()
    {
        InitializeComponent();
        var list = this.FindControl<ListBox>("List")!;
        list.DoubleTapped += (_, _) => RequestPasteSelected();
        KeyDown += OnKeyDown;
        Deactivated += OnDeactivated;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwnd == IntPtr.Zero) return;
            int backdrop = Platform.Clipboard.Win32.DWMSBT_TRANSIENTWINDOW;
            Platform.Clipboard.Win32.DwmSetWindowAttribute(
                hwnd, Platform.Clipboard.Win32.DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
            int corner = Platform.Clipboard.Win32.DWMWCP_ROUND;
            Platform.Clipboard.Win32.DwmSetWindowAttribute(
                hwnd, Platform.Clipboard.Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        }
        catch { /* old OS: keep the Card veil */ }
    }
```

(b) Replace `AttachWebHost` (~58-72) with:
```csharp
    // Replaces the list with a fresh snapshot; selection always resets to the first (newest) row.
    public void SetRows(System.Collections.Generic.IReadOnlyList<ClipboardRow> rows)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var list = this.FindControl<ListBox>("List")!;
            list.ItemTemplate = BuildRowTemplate();
            list.ItemsSource = rows;
            list.SelectedIndex = rows.Count > 0 ? 0 : -1;
            var h = this.FindControl<TextBlock>("HeaderText");
            if (h != null) h.Text = $"CLIPBOARD · {rows.Count} ITEM{(rows.Count == 1 ? "" : "S")}";
            if (rows.Count > 0) Dispatcher.UIThread.Post(() => list.ScrollIntoView(0));
        });
    }
```

(c) Add before `OnKeyDown`:
```csharp
    private Avalonia.Controls.Templates.IDataTemplate BuildRowTemplate()
    {
        return new Avalonia.Controls.Templates.FuncDataTemplate<ClipboardRow>((row, _) =>
        {
            var title = new TextBlock
            {
                Text = row.Title, Foreground = Avalonia.Media.Brushes.White, FontSize = 13,
                TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
            };
            var meta = new TextBlock
            {
                Text = row.Meta,
                Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#8AA0C0")),
                FontSize = 11,
            };
            var stack = new StackPanel { Spacing = 2, Children = { title, meta } };

            var trash = new Button
            {
                Content = "🗑", Padding = new Avalonia.Thickness(6, 2),
                Background = Avalonia.Media.Brushes.Transparent, BorderThickness = new Avalonia.Thickness(0),
                Opacity = 0, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            trash.Click += (_, e) => { e.Handled = true; DeleteRequested?.Invoke(row); };

            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Avalonia.Thickness(2),
            };
            Grid.SetColumn(stack, 0);
            Grid.SetColumn(trash, 1);
            grid.Children.Add(stack);
            grid.Children.Add(trash);

            var border = new Border
            {
                CornerRadius = new Avalonia.CornerRadius(8), Padding = new Avalonia.Thickness(10, 8),
                Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#14FFFFFF")),
                Child = grid,
            };
            border.PointerEntered += (_, _) => trash.Opacity = 1;
            border.PointerExited += (_, _) => trash.Opacity = 0;
            return border;
        });
    }

    private void RequestPasteSelected()
    {
        if (this.FindControl<ListBox>("List")?.SelectedItem is ClipboardRow row)
            PasteRequested?.Invoke(row);
    }

    private void RequestDeleteSelected()
    {
        if (this.FindControl<ListBox>("List")?.SelectedItem is ClipboardRow row)
            DeleteRequested?.Invoke(row);
    }
```

(d) Replace `OnKeyDown` (~144-151):
```csharp
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Dismiss();
        }
    }
```
with:
```csharp
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: e.Handled = true; Dismiss(); break;
            case Key.Enter:  e.Handled = true; RequestPasteSelected(); break;
            case Key.Delete: e.Handled = true; RequestDeleteSelected(); break;
        }
    }
```

(e) In `Open` (~114) replace `_webHost?.FocusContent();` with
`this.FindControl<ListBox>("List")?.Focus();`. Delete the field `private IWebHost? _webHost;` (~27)
and remove any remaining `_webHost` references.

Gate after 1.5.

### 1.5 Rewire composition — `App.axaml.cs`
In `EnsureClipboardViewer()` replace the `#if WIN_WEBVIEW2 ... #endif` body:
```csharp
#if WIN_WEBVIEW2
        if (OperatingSystem.IsWindows())
        {
            var opacity = _engineHost?.Settings.PastePanelOpacity ?? 70;
            var host = new WebView2WebHost(
                startPage: "clipboard-viewer.html",
                documentCreatedScript: WebView2WebHost.BuildPasteOpacityScript(opacity),
                transparentBackground: true);
            _clipboardViewerHost = host;
            if (_engineHost != null)
                _clipboardViewerBridge = new UiBridge(
                    new WebViewBridgeTransport((IBridgeTransport)host),
                    _engineHost.Actions);
            viewer.AttachWebHost(host);
        }
#endif
```
with:
```csharp
        if (_engineHost != null)
        {
            viewer.PasteRequested += async (row) =>
            {
                try { await _engineHost.Actions.PasteClipboardEntryAsync(row.Id, viewer.PriorForeground); } // (verify name; pass PriorForeground)
                catch { }
                finally { viewer.Dismiss(); }
            };
            viewer.DeleteRequested += async (row) =>
            {
                try { await _engineHost.Actions.DeleteClipboardEntryAsync(row.Id); } // (verify name)
                catch { }
                RefreshClipboardViewerRows(viewer);
            };
            RefreshClipboardViewerRows(viewer);
        }
```
The paste must target `viewer.PriorForeground` (already captured) so the synthetic Ctrl+V lands in the
right app. If `EngineActions` has no direct method, call `WindowsClipboardSink.PasteIntoFocusedAsync`
here after resolving the entry by id.

Add next to `EnsureClipboardViewer`:
```csharp
    private void RefreshClipboardViewerRows(ClipboardViewerWindow viewer)
    {
        if (_engineHost == null) return;
        try
        {
            var entries = _engineHost.Actions.GetClipboardHistory(); // (verify accessor)
            var rows = new System.Collections.Generic.List<ClipboardRow>();
            foreach (var en in entries)
                rows.Add(new ClipboardRow
                {
                    Id = en.Id.ToString(),
                    Kind = en.Kind,                // map from the real enum
                    Title = ClipboardRowTitle(en), // text preview (~80 chars) / file name / "Image"
                    Meta = ClipboardRowMeta(en),   // "{device} · {shortAge}"
                });
            viewer.SetRows(rows);
        }
        catch { }
    }
```
Implement `ClipboardRowTitle`/`ClipboardRowMeta` per the real `ClipboardEntry` shape.

### 1.6 Live updates
In `OnClipboardItemReceived` and the other handlers that mutate history (item published, deleted),
append:
```csharp
        if (_clipboardViewer != null)
            RefreshClipboardViewerRows(_clipboardViewer);
```

### 1.7 Cleanup
Remove now-unused `_clipboardViewerHost`/`_clipboardViewerBridge` fields if nothing else uses them.
Keep `WebView2WebHost.cs` (MainWindow uses it). Delete `ui/clipboard-viewer.html`,
`ui/js/clipboard-viewer.js`, and `.cb-viewer*` rules in `ui/css/clipboard.css` only if
`grep -rn "clipboard-viewer" ui/ src/` shows no other references; otherwise leave them.

### 1.8 Gate + commit
```bash
dotnet build -c Release --nologo
dotnet test tests/ZyncMaster.App.Tests/ZyncMaster.App.Tests.csproj -c Release --nologo
```
Manual check (human): hotkey opens an acrylic translucent rounded popup, first item selected, ↑↓
move, hover shows 🗑, Enter pastes into the focused app, Esc closes. If not acrylic, report with
`winver`; documented fallback (approval required): Card background `#CC0C0E16`.
```bash
git add src/ZyncMaster.App/Windows/ClipboardViewerWindow.axaml \
        src/ZyncMaster.App/Windows/ClipboardViewerWindow.axaml.cs \
        src/ZyncMaster.App/Windows/ClipboardRow.cs \
        src/ZyncMaster.App/Platform/Clipboard/Win32.cs \
        src/ZyncMaster.App/App.axaml.cs
git diff --cached --name-only
git commit -m "Rebuild the clipboard paste popup as a native acrylic window"
```

---

## Phase 2 — Status popup: form layout, single force-sync, status icon

File `ui/js/views/calendar-day.js` + `ui/css/calendar-day.css`.

### 2.1 Header button → icon
Anchor (line 125):
```javascript
      <button class="btn" id="calDayStatus" aria-label="Sync status">Status</button>
```
→
```javascript
      <button class="btn calday-statusicon" id="calDayStatus" aria-label="Sync status" title="Sync status"></button>
```
After line 128 (`...appendChild(iconEl('settings', 15, 1.7));`) add:
```javascript
    (() => {
      const raws = (live.pairs || []).filter((p) => p && p.state === 'active');
      const anyError = raws.some((p) => p.lastResult && p.lastResult.error);
      const btn = head.querySelector('#calDayStatus');
      btn.classList.toggle('warn', anyError);
      btn.appendChild(iconEl(anyError ? 'alert' : 'check', 15, 1.8));
    })();
```
Verify icon names `'check'`/`'alert'` exist in the icon set; use the real names if different.

### 2.2 Remove per-row force-sync
In `statusPairRow` delete the `forceBtn` block + its append (anchor ~677-707, from
`// Force-sync (run-now).` through `row.append(el('div', { class: 'calday-status-act' }, forceBtn));`)
leaving only:
```javascript
    return row;
```

### 2.3 Stats as a form
Anchor (~667-675, the `// Stats:` block) →
```javascript
    const statLine = (label, value) => el('div', { class: 'calday-status-line' },
      el('span', { class: 'calday-status-line-lbl', text: label }),
      el('span', { class: 'calday-status-line-val num', text: String(value) }));
    row.append(el('div', { class: 'calday-status-form' },
      statLine('Source', `${vm.src.svc} · ${vm.src.acct}`),
      statLine('Destination', `${vm.dst.svc} · ${vm.dst.acct}`),
      statLine('New events', newCount),
      statLine('Synced', syncedCount),
      statLine('Last run', lastRun),
      statLine('Next', nextStr)));
```

### 2.4 Single force-sync above the log
In `fillStatusBody`, anchor (~724-726):
```javascript
    const children = [el('p', { class: 'calday-hint', style: 'padding:0 0 var(--s-2)', text: 'Read-only overview. Use the gear to add, edit or pause pairs.' })];
    raws.forEach((raw) => children.push(statusPairRow(pairViewModel(raw), raw)));
    container.replaceChildren(...children);
```
→
```javascript
    const anyBusy = raws.some((raw) => !!pairViewModel(raw).inFlight);
    const forceAll = el('button', {
      class: 'btn primary calday-status-forceall', type: 'button', disabled: anyBusy,
      title: anyBusy ? 'Sync in progress…' : 'Force-sync all active pairs now',
      onclick: () => {
        if (anyBusy) return;
        raws.forEach((raw) => {
          const vm = pairViewModel(raw);
          if (vm.comOffline || vm.comUnclaimed || vm.inFlight) return;
          if (vm.comRemote) syncPairRemote(raw); else runPairNow(vm.id);
        });
      },
    },
      anyBusy
        ? el('span', { class: 'spinner', style: 'width:12px;height:12px;border-width:1.6px' })
        : el('span', { style: 'display:inline-flex', html: icon('sync', { size: 12, stroke: 1.8 }) }),
      el('span', { text: anyBusy ? 'Syncing…' : 'Force-sync' }));
    const children = [
      el('div', { class: 'calday-status-bar' }, forceAll),
      el('p', { class: 'calday-hint', style: 'padding:0 0 var(--s-2)', text: 'Read-only run state per pair. The gear adds, edits or pauses pairs.' }),
    ];
    raws.forEach((raw) => children.push(statusPairRow(pairViewModel(raw), raw)));
    container.replaceChildren(...children);
```

### 2.5 CSS
In `ui/css/calendar-day.css` delete `.calday-status-stats`, `.calday-status-stat`,
`.calday-status-force`, `.calday-status-act`. Append:
```css
.calday-status-bar { display: flex; justify-content: flex-end; padding: 0 0 var(--s-2); }
.calday-status-form { display: flex; flex-direction: column; gap: 4px; margin-top: var(--s-2); }
.calday-status-line { display: flex; justify-content: space-between; align-items: baseline; gap: var(--s-3); }
.calday-status-line-lbl { font-size: var(--t-micro); text-transform: uppercase; letter-spacing: .08em; color: var(--ink-3); }
.calday-status-line-val { color: var(--ink-1); text-align: right; }
.calday-statusicon.warn { color: var(--warn); border-color: var(--warn); }
```

### 2.6 Gate + commit
```bash
dotnet build -c Release --nologo
git add ui/js/views/calendar-day.js ui/css/calendar-day.css
git diff --cached --name-only
git commit -m "Reorganize the sync status popup into a form with a single force-sync"
```

---

## Phase 3 — Calendar: detail surfaces → modals, visible events, account selector, flat buttons

File `ui/js/views/calendar-day.js` + `ui/css/calendar-day.css` (+ `ui/js/views/calendar.js` for the
account screen if needed).

### 3.1 Remove right panel
Anchor (~131-141):
```javascript
    const body = document.createElement('div');
    body.className = 'calday';
    const gridWrap = document.createElement('div');
    gridWrap.className = 'calday-grid-wrap';
    body.appendChild(gridWrap);
    const panel = document.createElement('aside');
    panel.className = 'calday-panel';
    panel.id = 'calDayPanel';
    body.appendChild(panel);
    wrap.appendChild(body);
    root.appendChild(wrap);
```
→
```javascript
    const body = document.createElement('div');
    body.className = 'calday calday--full';
    const gridWrap = document.createElement('div');
    gridWrap.className = 'calday-grid-wrap';
    body.appendChild(gridWrap);
    wrap.appendChild(body);
    root.appendChild(wrap);
```
Delete the line `renderCalDayPanel(panel);` (~161).

### 3.2 Panel router → modal opener
Anchor `renderCalDayPanel` (~282-290) →
```javascript
  function openCalPanel(kind) {
    const body = document.createElement('div');
    body.className = 'calday-modal';
    calDay.panel = kind;
    const close = () => { calDay.panel = null; calDay.selected = null; };
    let title = 'Calendar';
    if (kind === 'replicate' && calDay.selected) { title = 'Replicate event'; renderReplicatePanel(body, close); }
    else if (kind === 'new-event') { title = 'New event'; renderNewEventPanel(body, close); }
    else if (kind === 'rules') { title = 'Prefix rules'; renderPrefixRulesPanel(body, close); }
    openModal({ title, body, onClose: close });
  }
```

### 3.3 Adapt the three render functions to `(panel, close)`
- `renderReplicatePanel(panel)` → `renderReplicatePanel(panel, close)`:
  - inner `function closePanel() {...}` (~394) → `function closePanel() { close(); }`
  - `#calDayManageRules` onclick (~370) → `() => { close(); openCalPanel('rules'); }`
- `renderNewEventPanel(panel)` → `renderNewEventPanel(panel, close)`:
  - delete `const close = () => { calDay.panel = null; rerenderInPlace(); };` (~499)
  - in the create `.then` (~518-525) replace `calDay.panel = null; ... rerenderInPlace();` with
    `close(); loadCalendarDay();`
- `renderPrefixRulesPanel(panel)` → `renderPrefixRulesPanel(panel, close)`:
  - `#calDayRulesClose` onclick (~557) → `() => { close(); if (calDay.selected) openCalPanel('replicate'); }`

### 3.4 Triggers
- `#calDayNew` onclick (~149) → `() => openCalPanel('new-event')`
- event button onclick in `buildCalDayColumn` (~234-238) →
  `() => { calDay.selected = { ev, account }; openCalPanel('replicate'); }`
- In `onCalDayKeydown` remove the `if (calDay.panel) {...}` block (~95-100); leave the function a no-op
  after the `state.view` guard (the modal owns Escape).

### 3.5 Account selector + visible events
Add state. Anchor (line 41 `rulesError: null,`) →
```javascript
    rulesError: null,
    hiddenAccounts: {},
```
In `renderCalDayGrid` replace the legend block (~170-180):
```javascript
    const accounts = (data && data.accounts) || [];
    if (legendEl) {
      legendEl.replaceChildren(...accounts.map((a, i) => {
        const s = document.createElement('span');
        const fresh = freshnessLabel(a.freshness); // "live" → null (no badge); "snapshot_unavailable" → badge
        s.innerHTML = `<span class="sw" data-acc="${i}"></span>${escapeHtml(a.email || 'Account')}`
          + (fresh ? `<span class="calday-fresh">${fresh}</span>` : '');
        s.querySelector('.sw').style.background = `var(--${{ az: 'azure', aq: 'aqua', cy: 'cyan' }[accountColorClass(i)]})`;
        return s;
      }));
    }
```
→
```javascript
    const allAccounts = (data && data.accounts) || [];
    const accounts = allAccounts.filter((a) => !calDay.hiddenAccounts[a.email]);
    if (legendEl) {
      legendEl.replaceChildren(...allAccounts.map((a, i) => {
        const off = !!calDay.hiddenAccounts[a.email];
        const s = document.createElement('button');
        s.type = 'button';
        s.className = 'calday-legend-chip' + (off ? ' off' : '');
        const fresh = freshnessLabel(a.freshness);
        s.innerHTML = `<span class="sw" data-acc="${i}"></span>${escapeHtml(a.email || 'Account')}`
          + (fresh ? `<span class="calday-fresh">${fresh}</span>` : '');
        s.querySelector('.sw').style.background = `var(--${{ az: 'azure', aq: 'aqua', cy: 'cyan' }[accountColorClass(i)]})`;
        s.onclick = () => { calDay.hiddenAccounts[a.email] = !off; rerenderInPlace(); };
        return s;
      }));
    }
```
Empty state. Anchor (~155-159) →
```javascript
    const data = calDay.days[calDay.date];
    const noAccounts = data && !(data.accounts || []).length;
    if (calDay.loading && !data) { gridWrap.innerHTML = '<p class="calday-empty">Loading the day…</p>'; }
    else if (calDay.error) { gridWrap.innerHTML = `<p class="calday-empty">${escapeHtml(calDay.error)}</p>`; }
    else if (noAccounts) {
      gridWrap.innerHTML = '<p class="calday-empty">No calendar accounts are visible here yet. '
        + 'Connect a calendar (gear → accounts) — your signed-in account can be connected in one click.</p>';
    }
    else if (calDay.mode === 'day') renderCalDayGrid(gridWrap, head.querySelector('#calDayLegend'), data, calDay.date, true);
    else renderCalWeekGrid(gridWrap, head.querySelector('#calDayLegend'));
```
Event identification on hover. Anchor (~232-233) →
```javascript
      btn.innerHTML = `<b>${escapeHtml(ev.title || '(no title)')}${link}${mark}</b>`
        + `<span class="num">${fmtRange(ev.start, ev.end)}</span>`;
      btn.title = `${ev.title || '(no title)'} · ${fmtRange(ev.start, ev.end)} · ${account.email || ''}`;
```

### 3.6 Flat buttons
Append to `ui/css/calendar-day.css` (do not change global `--r-pill`):
```css
.calday-head .btn { border-radius: var(--r-sm); }
.calday-head .btn.primary { border-radius: var(--r-sm); }
.calday-seg { border-radius: var(--r-sm); overflow: hidden; }
.calday-statusicon, .calday-gear { border-radius: var(--r-sm); }
```
If `--r-sm` is absent in `tokens.css`, use `8px`.

### 3.7 Gate + commit
```bash
dotnet build -c Release --nologo
git add ui/js/views/calendar-day.js ui/css/calendar-day.css
git diff --cached --name-only
git commit -m "Move calendar detail surfaces to modals and make events and accounts selectable"
```

---

## Phase 4 — One-click connect for the signed-in account's calendar

Identity and calendar stay separate (login requests `openid email profile` only and discards the
refresh token — `MicrosoftTokenService.cs:~53`; calendar is a distinct OAuth). No schema change. UI
offers to connect the signed-in email's calendar in one click.

### 4.1 Read
- `src/ZyncMaster.Server/Modules/Identity/` — `GET /api/identity/me`.
- `src/ZyncMaster.Server/Modules/Sync/PairEndpoints.cs` (~28-116) — `GET /api/accounts`
  (`accountEmail`/`scope`).
- `src/ZyncMaster.Server/Modules/Calendar/CalendarConnectEndpoints.cs` — graph connect start.
- `ui/js/views/calendar.js` — the bridge action that starts the calendar OAuth ("Add calendar
  account" wizard) and how the account list renders.

No new endpoint required. Optional UX: if the connect start accepts a `loginHint`, append
`&login_hint=<email>` to the authorize URL (1 line, no schema change); else omit it.

### 4.2 UI
In `ui/js/views/calendar.js`, before the account list, add and call:
```javascript
function maybeRenderConnectMyAccount(container) {
  const myEmail = (live.me && live.me.email || '').toLowerCase();
  if (!myEmail) return;
  const have = (live.calendarAccounts || calDay.accounts || []).some(
    (a) => (a.accountEmail || '').toLowerCase() === myEmail);
  if (have) return;
  const btn = el('button', { class: 'btn primary', type: 'button',
    text: `Connect the calendar for ${myEmail}`,
    onclick: () => Bridge.call('connectCalendarGraph', JSON.stringify({ scope: 'readwrite', loginHint: myEmail })) // (verify action name)
      .catch((err) => announce(`Connect failed: ${err.message}`)),
  });
  container.appendChild(el('div', { class: 'calday-connect-mine' }, btn));
}
```
Use the real connect bridge action (from `calendar.js`/`EngineActions.cs`); drop `loginHint` if
unsupported.

### 4.3 Gate + commit
```bash
dotnet build -c Release --nologo
dotnet test tests/ZyncMaster.Server.Tests/ZyncMaster.Server.Tests.csproj -c Release --nologo
```
If you added `login_hint`, add a `Server.Tests` assertion that the authorize URL includes it.
```bash
git add ui/js/views/calendar.js ui/js/views/calendar-day.js
# + CalendarConnectEndpoints.cs and its test only if server changed
git diff --cached --name-only
git commit -m "Offer a one-click connect for the signed-in account's calendar"
```

---

## Phase 5 — Home horizontal overflow

File `ui/css/shell.css`. Anchor (~193-196):
```css
  .board {
    display: grid;
    grid-template-columns: 1.2fr 1fr;
    gap: var(--s-5);
    align-items: start;
  }
```
→
```css
  .board {
    display: grid;
    grid-template-columns: minmax(0, 1.2fr) minmax(0, 1fr);
    gap: var(--s-5);
    align-items: start;
    min-width: 0;
  }
```
Add `min-width: 0;` to `.board-row`. Ensure `.board-row__title` has `min-width:0; overflow:hidden;
text-overflow:ellipsis; white-space:nowrap;`. In `ui/css/layout.css` ensure `.view { overflow-x:
hidden; }`.
```bash
dotnet build -c Release --nologo
git add ui/css/shell.css ui/css/layout.css
git diff --cached --name-only
git commit -m "Stop the home board from overflowing horizontally"
```

---

## Phase 6 — Devices: show signed-in identity + this device

File `ui/js/views/devices.js` (read it; find the bridge accessor for the current device). Add before
the roster:
```javascript
function renderIdentityCard() {
  const email = (live.me && live.me.email) || '—';
  const name = (live.me && live.me.displayName) || '';
  const dev = (live.thisDevice && live.thisDevice.name) || '—'; // (verify accessor)
  return el('section', { class: 'glass glass--card devices-id' },
    el('div', { class: 'devices-id-row' },
      el('span', { class: 'devices-id-lbl', text: 'Signed in as' }),
      el('span', { class: 'devices-id-val', text: name ? `${name} · ${email}` : email })),
    el('div', { class: 'devices-id-row' },
      el('span', { class: 'devices-id-lbl', text: 'This device' }),
      el('span', { class: 'devices-id-val', text: dev })));
}
```
Insert `renderIdentityCard()` first in the view's `root.append(...)`. Use the real current-device
accessor. Append CSS (to `ui/css/components.css` or the devices stylesheet):
```css
.devices-id { margin-bottom: var(--s-4); }
.devices-id-row { display: flex; justify-content: space-between; gap: var(--s-3); padding: 4px 0; }
.devices-id-lbl { color: var(--ink-3); font-size: var(--t-meta); }
.devices-id-val { color: var(--ink-1); }
```
```bash
dotnet build -c Release --nologo
git add ui/js/views/devices.js ui/css/components.css
git diff --cached --name-only
git commit -m "Show the signed-in identity and current device on the Devices screen"
```

---

## Phase 7 — Navigation consistency

`ui/js/app.js` (~1586-1638): Devices and Settings are already top-level. Verify: every nav item
resolves to an existing view; order Home, Calendar, Clipboard, Devices | Settings, About; no duplicate
paths to the same view. Fix only concrete breakage. If none, write
`plans/reports/07-nav-OK.md` ("no changes"). Commit only if changed:
```bash
git add ui/js/app.js
git commit -m "Tighten primary navigation consistency"
```

---

## Phase 8 — Optional visual polish (approval required)

Extend the flat/minimal azure look beyond the calendar header only if the human approves. Do not
change global tokens. Confirm no violet in `ui/css/tokens.css`; report any, do not change it. Keep
subtle border-glow, no hover color flicker, decorative overlays `pointer-events:none`. Commit only if
approved changes were made.

---

## Phase 9 — Version, tests, release, deploy

### 9.1 Bump to 0.5.0
`ZyncMaster.App.csproj` (~19-22): `Version`/`AssemblyVersion`/`FileVersion`/`InformationalVersion` →
`0.5.0`/`0.5.0.0`/`0.5.0.0`/`0.5.0`. `ui/js/app.js` (~323): `const VERSION = '0.5.0';`.

### 9.2 Full suite
```bash
dotnet build -c Release --nologo   # 0 errors, 0 warnings
dotnet test --nologo               # 0 failures
```
Remove/adjust tests that exercised the old WebView2 popup; document which in the commit. A failing
business-logic test is a bug to fix or report.

### 9.3 Commit + PR
```bash
git add src/ZyncMaster.App/ZyncMaster.App.csproj ui/js/app.js
git diff --cached --name-only
git commit -m "Bump app to 0.5.0"
git push -u origin ui-overhaul-0.5.0
gh pr create --base main --head ui-overhaul-0.5.0 \
  --title "UI overhaul: native clipboard popup, calendar modals, status form, home overflow" \
  --body "Native acrylic clipboard popup (no WebView2), calendar detail modals, status popup form with a single force-sync, one-click connect for the signed-in calendar, home overflow fix, devices identity card."
```
Do not auto-merge.

### 9.4 Release / deploy (after merge)
```bash
gh workflow run release-app.yml --ref main -f version=0.5.0
# server deploy only if Phase 4 changed the server:
gh workflow run deploy-syncmaster-vps.yml --ref main
curl -s -o /dev/null -w '%{http_code}\n' https://api.devlabperu.com/zync/health   # expect 200
```
Deploy snapshots and auto-rolls back on health failure.

### 9.5 Final report
Write `plans/reports/00-DONE.md`: phases completed, files per phase, `dotnet test` counts, any
deviations, any FAILED phases.

---

## Pitfalls
1. The popup must not be WebView2 (transparency impossible here). Phase 1 is native by design.
2. `git add` explicit paths only; verify `git diff --cached --name-only` before each commit.
3. Bridge/engine names tagged "(verify name)" are placeholders — use the real ones.
4. Do not change global constants/tokens without approval.
5. Do not touch the Angular `zync-web`.
6. Palette is azure/aqua/cyan; no violet; subtle animations only.
7. When in doubt, write a FAILED report and stop.
