# Scrimshaw — notes for Claude

Windows desktop app (.NET 10, WPF, Windows.Graphics.Capture, PDFsharp 6.2.4) that turns sheet-music videos into PDF scores. **v1.0 is delivered.** Start with `docs/requirements.md` §9–11 (status, how to add requests, request list) and `docs/implementation-plan.md` §13 (next steps).

## Commands

```
dotnet build Scrimshaw.slnx
dotnet test --solution Scrimshaw.slnx --filter-not-trait "Category=Desktop"     # headless; run freely
dotnet test --project tests/Scrimshaw.Capture.Tests --filter-trait "Category=Desktop"   # desktop: coordinate first
dotnet test --project tests/Scrimshaw.App.UiTests                                # desktop: coordinate first
dotnet publish src/Scrimshaw.App -p:PublishProfile=win-x64                       # → publish/win-x64/Scrimshaw.exe
dotnet run --project tools/Scrimshaw.Harness -- <replay|synth|record|bench|contact|export|render-pdf|icon> ...
```

Tests use xunit v3 on Microsoft.Testing.Platform (opted in via `global.json`): filter with `--filter-trait` / `--filter-not-trait` / `--filter-method`, not `--filter`.

## Working agreements

- **Desktop runs need the user's go-ahead.** Anything that captures the screen, opens windows, moves the mouse or plays a video: ask first, say when it's done. Never use the user's own apps (e.g. Notepad) as test props; use TestPlayer or Scrimshaw's own windows, and prefer window capture to monitor capture.
- Commit at the end of each phase and at sensible points within one. No remote.
- Stop for a user checkpoint at the end of each phase that changes behaviour.
- New features follow requirements §10: record → refine (IDs, priority) → plan → build and verify → update the docs (requirements §9, user guide, README).

## Conventions and gotchas

- Warnings are errors, with analysers on. Fix them rather than suppressing, unless there's a stated reason.
- Core has no WPF/WinRT; pixels cross projects as `RawImage`. Box coordinates are always frame pixels.
- Processes that use `CaptureTargets` must be per-monitor DPI aware (the app and Harness have manifests; test hosts set it in a module initializer). Otherwise windows land partly off-screen and capture black (F20).
- JSON (source-generated): optional properties use `set`, not `init`, or missing fields are read as null/0 (F27).
- **Don't upgrade PDFsharp past 6.2.x** (6.5+ is AGPL, F2). PDF outline `Top` is measured from the page bottom (F4).
- WPF: two-way bindings to read-only properties crash (F28); give every radio-button pair a `GroupName` (F32); never call `Close()` synchronously inside `OnClosing` (F31).
- UI tests run serially (one collection); use `SCRIMSHAW_EXE` to test the published exe. Launch the app via `AppExe.StartInfo()`, which sets `SCRIMSHAW_SETTINGS_PATH` to a throwaway file, so tests never touch the user's Recent list or window position (F35). Collapsed elements and `Border`s aren't in the UIA tree (F36, F38): put automation IDs on controls or text.
- Change detection is tuned against `samples/` (labels committed, frames git-ignored). After touching detection, run `replay` on every sample; all must PASS.
