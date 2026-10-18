# Scrimshaw

**v1.1**: turns sheet-music videos into printable PDF scores. It captures each page of a video as it plays, lets you box the music, and exports the boxes as a PDF stacked like a printed score.

- **Capture** a window, a monitor or the whole desktop at a set interval. Unchanged frames, mid-animation frames and player-control overlays are left out automatically.
  - A live **preview** of the chosen source before you start.
  - An optional **music area**: only changes inside it count as page turns, so a performer video or progress bar beside the score is ignored.
  - Capture **stops by itself** when the browser moves on to the next video.
- **Review** the captured pages and draw boxes, with "apply to following pages", labels, and undo/redo.
- **Export** a PDF: stacked or one per page, at one shared staff size, with bookmarks from the labels.

See the **[user guide](docs/user-guide.md)** for how to use it.

## Running it

Windows 10 version 2004 (build 19041) or later, or Windows 11, on x64. The published `Scrimshaw.exe` is self-contained: no .NET install or admin rights are needed.

```
dotnet publish src/Scrimshaw.App -p:PublishProfile=win-x64     # → publish/win-x64/Scrimshaw.exe
```

## Building and testing

You need the .NET 10 SDK (`global.json` pins 10.0.402 or a later feature band).

```
dotnet build Scrimshaw.slnx
dotnet test --solution Scrimshaw.slnx --filter-not-trait "Category=Desktop"   # 240 headless tests, safe anywhere
```

Desktop tests capture the screen, open windows and move the mouse, so only run them when the machine is free:

```
dotnet test --project tests/Scrimshaw.Capture.Tests --filter-trait "Category=Desktop"   # capture via the TestPlayer
dotnet test --project tests/Scrimshaw.App.UiTests                                       # UI automation (FlaUI)
```

## Layout

| Path | What's there |
|---|---|
| `src/Scrimshaw.Core` | Workspace and manifest storage, change detection, box editing, export planning and layout. No UI. |
| `src/Scrimshaw.Imaging` | PNG/JPEG codec and thumbnails (WIC) |
| `src/Scrimshaw.Capture` | Windows.Graphics.Capture sources, target enumeration, the capture loop |
| `src/Scrimshaw.Export` | PDF export (PDFsharp 6.2.x) |
| `src/Scrimshaw.App` | The WPF app |
| `tools/Scrimshaw.Harness` | Developer CLI: replay and score detection, synthetic and real recordings, benchmarks, export, PDF-to-PNG, contact sheets, icon |
| `tools/Scrimshaw.TestPlayer` | Synthetic score player used by the desktop tests |
| `samples/` | Labelled recordings (frames are git-ignored; labels are committed) |
| `docs/` | [Requirements](docs/requirements.md) (with the delivery status and how to add requests), [implementation plan](docs/implementation-plan.md) (with findings from each phase and next steps), [user guide](docs/user-guide.md) |

## Licences

Scrimshaw's dependencies are listed with their licence texts in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt). **PDFsharp is pinned to 6.2.x**: version 6.5 and later is AGPL-3.0 (implementation plan, finding F2).
