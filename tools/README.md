# tools/

Developer helper scripts. None of these participate in the build, CI, or the
release pipeline — they are one-off utilities run by hand during development.

## capture-window.ps1

Captures only the running `ZyncMaster.App` window region (not the whole desktop)
to a PNG, for visual verification of the embedded WebView2 UI. Locates the window
through the running process.

```
powershell -NoProfile -File tools/capture-window.ps1 <output.png>
```

## make-icon.ps1

Regenerates the application icon PNG (rounded tile + two-arrow sync glyph in the
brand palette) at 256x256. Source for the window and tray icon when the artwork
needs to be rebuilt.

```
powershell -NoProfile -File tools/make-icon.ps1 <output.png>
```
