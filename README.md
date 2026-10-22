# dsh-screenshot

English | [中文](README.zh.md)

The built-in screenshot tool for DeepSeek Harness on Windows: one click opens a full-screen
region selector in about 0.2 s, with annotations, a colour picker and edge snapping.
Confirming puts the image straight on the clipboard. The exe also works standalone.

![Overview](docs/screenshots/overview-zh.png)

## Features

| | |
|---|---|
| One click, ~0.2 s to the overlay | resident warm process, no startup wait |
| Drag to select | window auto-detect, live size readout |
| Annotations | pen, line, arrow, rectangle, ellipse, text; `Ctrl+Z` undo |
| Colour | three widths, swatch palette, solid fill |
| Magnifier | coordinates and hex under the pointer, press `C` to copy the colour |
| Edge snapping | selection, handles and text stay inside the screen |
| Right click cancels | without leaking the desktop context menu |
| Follows the UI language | Chinese DSH → Chinese tool, English DSH → English tool |
| Light on the machine | ~6 MB idle, ~0.5 % CPU, exits when idle |

![Toolbar](docs/screenshots/toolbar.png)

## Install

Requires Windows 10/11 and DeepSeek Harness. No administrator rights, no extra runtime
(`.NET Framework 4.0` ships with Windows).

1. Put the repository under your plugins directory, e.g. `D:\dsh-home\plugins\dsh-screenshot`.

2. Add the bundle and a link entry to your DSH profile
   (`<DSH_HOME>/profiles/<name>/package.json`):

   ```json
   {
     "dsh": {
       "profile": {
         "bundles": ["@deepseek-ai/dsh-base", "@deepseek-ai/dsh-web-app", "dsh-screenshot"]
       }
     },
     "dependencies": {
       "dsh-screenshot": "link:D:/dsh-home/plugins/dsh-screenshot"
     }
   }
   ```

3. Restart DSH; this button appears on the right of the composer:

   ![Screenshot button](docs/screenshots/button.png)

## Shortcuts

| Action | Effect |
|---|---|
| Click the button or drag | Start a capture |
| Single click | Mark the window under the pointer |
| Drag a handle | Resize the selection (magnifier stays on) |
| `Ctrl+Z` | Undo the last annotation |
| `C` | Copy the colour under the pointer (`#RRGGBB`) |
| `Enter` or `✓` | Copy and exit |
| `Esc`, right click or `✕` | Cancel |

## Privacy

No network requests, no telemetry, no uploads: the image only goes to the clipboard. The tool
never captures the screen until you ask it to, exits after three idle minutes and follows DSH
out of memory. Set `DSH_SNIP_DEBUG=1` to write `%TEMP%\dsh-text.log`; nothing is written by default.

## Implementation

The plugin host keeps one resident `SnipTool.exe` and wakes it over a named pipe
(`\\.\pipe\dsh-snip-show`), waiting for an explicit acknowledgement so a dead process is
detected instead of silently ignored. Theme and UI language travel with each request. The
overlay is drawn by hand: a double-buffered bitmap for the dimmed desktop and bright selection,
a layered window for the toolbar, a small window for the magnifier.

```
bin/SnipTool.exe      prebuilt native tool
src/SnipTool.cs       all C# source, single file
lib/index.js          plugin host: wake, theme, language, routes
lib/client.js         web button and menu
cordis.patch.yml      plugin registration
```

| Metric | Measured |
|---|---|
| Warm wake | 170–200 ms |
| First wake after warm-up | 330–390 ms |
| Cold start | ≈ 1.1 s |
| Idle working set | ≈ 6 MB |

Waking retries four times; if the resident process never acknowledges, a host-side watchdog
kills only processes older than eight seconds and starts a fresh one. Every timer and mouse-hook
callback checks for disposal, so a closing session cannot throw "cannot access a disposed object".

## Limitations

Verified on a single display at 150 % scaling; mixed-DPI and multi-monitor setups are untested.
Windows only. The UI language is read from the `locale` entry in the DSH profile, so a future
layout change there needs a matching update here. Annotations can only be undone one by one.

## Development

```powershell
csc /nologo /target:winexe /optimize+ /out:bin\SnipTool.exe `
    /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll src\SnipTool.cs
```

## License

MIT
