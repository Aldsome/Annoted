# Changelog

## v0.1.1-beta

- **Waveform playback fix** — single-click now seeks instantly (no double-click-interval delay); double-click reliably plays from the exact clicked position and no longer pauses right after starting. Click vs. drag-scrub are cleanly separated.
- **Distribution** — ships as framework-dependent win-x64 only (no portable SDK bundle). Requires the .NET Desktop Runtime 8.0+, preinstalled on most Win10/11; if missing, install from https://dotnet.microsoft.com/download/dotnet/8.0.

## v0.1.0-beta

First beta after the WinForms → modern WPF desktop rework. Highlights:

- **Modern UI / theme system** — global light/dark/system theming with accent color; centralized control styles (buttons, inputs, menus, sliders, scrollbars, context menus, tooltips) that all controls inherit automatically; instant theme/accent updates.
- **Dictation & recording workflow** — Whisper-based dictation with live ghost-text preview; shared audio-reactive visualizer (ball for dictation, crescent for recording); microphone selection; background model download with progress.
- **Waveform preview** — themed waveform, click-to-seek, smooth red playhead, single-click seek/pause and double-click seek/play, preview volume (persisted, playback-only).
- **Memoirs / audio preview** — sortable list (date/name/duration), single-click select / double-click open, right-click and batch actions (rename/export/move/delete), filename sync on note save.
- **Autosave / dirty-state fixes** — restored tabs are no longer falsely marked dirty; no false "save changes" prompts on launch; real unsaved edits still prompt.
- **Stability cleanup** — global crash logging, theme/resource scoping fixes, dictation artifact removal (`[BLANK_AUDIO]`).
