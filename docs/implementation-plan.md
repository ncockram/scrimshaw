# Scrimshaw — Implementation Plan

| | |
|---|---|
| Status | v1.2 (Phase 1 complete) |
| Date | 2026-10-16 |
| Implements | [requirements.md](requirements.md) v0.3 |

**Changes since v0.2:**
- Development environment surveyed and the stack confirmed against it (§2).
- Claude is now the only developer, so the plan explains how each phase is verified without a human at the keyboard (§3). This adds a synthetic score player and UI automation.
- Human time estimates removed. Each phase now has deliverables, verification steps and a checkpoint with you.
- PDF layout updated to left-aligned crops and one shared scale (§6.4).
- Development agreements (setup, git, desktop-test coordination, real-video links) are in §11.

## 1. Technology choices (confirmed)

| Concern | Choice | Notes |
|---|---|---|
| Language / runtime | C# 14 / **.NET 10 (LTS)** | The SDK has to be installed first (§2.2). |
| UI | **WPF** using MVVM (`CommunityToolkit.Mvvm`) | Builds with the .NET SDK alone; Visual Studio isn't needed. |
| Capture | **Windows.Graphics.Capture (WGC)** | Target framework moniker: `net10.0-windows10.0.26100.0` with `SupportedOSPlatformVersion` 10.0.19041.0. Compiling against the newer SDK is needed for `IsBorderRequired`; the minimum version makes the platform analyzer flag any unguarded Windows 11-only call (R8). The WinRT projections come from NuGet, so no separate Windows SDK install is needed. |
| D3D device for WGC | **Vortice.Direct3D11** (MIT) | Only needed to create the `IDirect3DDevice` that WGC requires. |
| Pixel readback | D3D11 GPU copy on frame arrival, then a staging texture read when the timer ticks | Chosen in the spike: the frame pool never stalls, and nothing is read back to the CPU until a tick. |
| Image encode / crop | WPF imaging (WIC): `PngBitmapEncoder`, `JpegBitmapEncoder`, `CroppedBitmap` | Built in. |
| Change detection | Written in-house: changed-area measure, settle state machine, 64-bit dHash | §6.2 |
| PDF | **PDFsharp 6.2.4** (MIT), pinned (decision F2, §12) | Bookmarks (outlines) and simple absolute positioning. PDFsharp 6.5+ is AGPL-3.0; don't upgrade past 6.2.x. |
| Persistence | `System.Text.Json` (source-generated), atomic replace | §5 |
| Tests | xUnit; **UglyToad.PdfPig** (Apache-2.0) to read PDFs back; **FlaUI.UIA3** (MIT) for UI automation | §3 |
| Packaging | `dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true` | |

**Why not Python + PySide6?** Python 3.13 is installed, but none of the Qt or capture libraries are. More importantly, the stack's main advantage is still WGC's reliable window capture. That matters more on this machine than usual, because of its hybrid GPU (§2.1). Python stays available for one-off scripts, but nothing in the product depends on it.

## 2. Development environment

### 2.1 What's on this machine (surveyed 2026-10-16)

| Item | Found | Impact |
|---|---|---|
| OS | Windows 11, build 26200 (25H2), x64 | WGC fully available, including turning off the capture border. |
| .NET SDKs | 7.0.302, 8.0.412, **9.0.300** | **No .NET 10.** .NET 8 and 9 both reach end of support on **2026-11-10**, in less than a month. → Install .NET 10 SDK 10.0.402 (latest; LTS until 2028-11-14). |
| Desktop runtimes | WindowsDesktop 6–9 | Not needed at runtime, because the app publishes self-contained. |
| Visual Studio | Community 2022 (17.14) | Not needed: all building and testing is done from the `dotnet` command line. VS 2022 doesn't support .NET 10 projects, so to open the solution in an IDE, use VS Code (C# Dev Kit) or Visual Studio 2026. |
| Windows SDK | Not installed under `Windows Kits\10\Include` | Not needed (see the WGC row in §1). |
| Git | 2.37.1 | Works. Repository set up in Phase 0 (§11). No `gh` CLI, and none is needed because there's no remote. |
| Package manager / network | winget 1.29; nuget.org reachable | NuGet restore and SDK install both possible. |
| Python / Node | 3.13 / 20.10 | Available for scripts; not part of the product. |
| CPU / GPU | Ryzen 7 5800H; **AMD Radeon integrated + NVIDIA RTX 3050 Ti** (hybrid laptop) | Hybrid GPUs are the classic case where DXGI Desktop Duplication breaks (it must run on the adapter that drives each monitor). WGC goes through the Windows compositor and avoids this, which supports choosing WGC. The Phase 0 spike checks browser capture on this setup specifically. |
| Displays | 3 monitors: laptop panel 1920×1080 at **125%** (primary), plus two external 1920×1080 at 100% | A ready-made **mixed-DPI, multi-monitor** test setup for NFR-2 and FR-C2(a). There's no 4K monitor, so the 4K part of NFR-3 is checked with a synthetic 3840×2160 frame benchmark instead. |
| Disk | About 86 GB free on C: | Plenty. |

### 2.2 Setup steps (Phase 0)

1. Install the .NET 10 SDK. Recommended: `winget install Microsoft.DotNet.SDK.10`. This installs machine-wide next to the existing SDKs and needs **one UAC approval from you**. *(Alternative with no admin rights: a per-user install with `dotnet-install.ps1`, which is more fiddly with PATH.)*
2. `git init`, plus `.gitignore` and `.gitattributes`.
3. `global.json` pinning SDK `10.0.402` with `rollForward: latestFeature`.
4. `Directory.Build.props`: nullable on, warnings as errors, analyzers, `LangVersion latest`.
5. Smoke test: a WPF hello-world and the WGC console spike both build and run from the command line.

## 3. How work is verified (Claude as sole developer)

I can't watch the screen the way a person can, but I can view images and PDFs, run any command, and drive the desktop through UI Automation. Every phase is built so its results can be checked that way.

| Technique | Used for |
|---|---|
| **Headless unit tests** (`dotnet test`) | All of Core: manifest, change detection, layout math, export planning, plus the view-models (MVVM keeps logic out of XAML). This is the bulk of the testing. |
| **Viewing outputs directly** | I open captured PNG/JPEG frames, rendered UI screenshots and exported PDFs, and inspect them visually: crop correctness, layout, mid-turn frames, DPI scaling. |
| **Synthetic score player** (`tools/Scrimshaw.TestPlayer`) | A small WPF app that shows **generated** score pages (staves, notes, a large page number), turned on a script. Turn styles: cut, half-page (top, then bottom), cross-fade, slide. It writes a ground-truth log (page ID and the time each page became stable) and reference PNGs of each page. This gives repeatable end-to-end capture tests without YouTube or copyrighted material. |
| **Replay harness** (`tools/Scrimshaw.Harness`, CLI) | Feeds recorded frame sequences through `ChangeDetector` offline. It matches saved frames to reference pages and reports pages missed, extra frames and mid-turn frames saved. Used to tune the defaults. |
| **UI automation** (FlaUI, `tests/Scrimshaw.App.UiTests`) | Drives the real app: create a workspace, start and stop capture, draw boxes, export. It takes window screenshots for me to review. It **moves the real mouse and opens windows**, so these tests are coordinated with you first (§11). |
| **Real-video acceptance** | Captures of actual YouTube sheet-music videos, opened by me in your default browser after checking with you (§11). Used in Phases 0, 2 and 5. |

**Checkpoints with you.** At the end of Phases 0, 3, 4 and 5 I stop and report what was built, how it was verified, and anything needing your decision. Phases 1 and 2 roll straight on unless something blocks them.

## 4. Solution structure

```
scrimshaw/
├─ docs/
├─ src/
│  ├─ Scrimshaw.Core/          # No UI. Workspace, manifest I/O, change detection, export planning and layout
│  ├─ Scrimshaw.Imaging/       # WIC (WPF imaging) PNG/JPEG codec implementing Core's IImageCodec
│  ├─ Scrimshaw.Capture/       # WGC wrapper, target enumeration, capture loop
│  ├─ Scrimshaw.Export/        # PDFsharp rendering, cropping, bookmarks
│  └─ Scrimshaw.App/           # WPF shell, views, view-models
├─ tools/
│  ├─ Scrimshaw.TestPlayer/    # Synthetic score player (ground truth for capture tests)
│  └─ Scrimshaw.Harness/       # CLI: replay (score detection against labels), synth (labelled synthetic recordings), render-pdf
├─ tests/
│  ├─ Scrimshaw.Core.Tests/
│  ├─ Scrimshaw.Export.Tests/
│  ├─ Scrimshaw.Capture.Tests/ # Interactive desktop: WGC against TestPlayer
│  └─ Scrimshaw.App.UiTests/   # Interactive desktop: FlaUI end-to-end
├─ samples/                    # Recorded sequences (synthetic + real) with ground-truth labels; large files git-ignored
├─ global.json
├─ Directory.Build.props
└─ Scrimshaw.sln
```

Dependency direction: `App → Capture, Export → Imaging → Core`. Tools and tests may reference anything. Core uses no WPF or WinRT types. Pixel buffers cross project boundaries as `RawImage { int Width, Height, Stride; byte[] Bgra; }`.

Tests that need an interactive desktop are marked `[Trait("Category","Desktop")]` and left out of the default run: `dotnet test --solution Scrimshaw.slnx --filter-not-trait "Category=Desktop"`. (xunit v3 runs on Microsoft.Testing.Platform, opted into in `global.json`; the old `--filter` syntax doesn't apply.)

## 5. Workspace format

One workspace holds one video.

```
<workspace>/
├─ scrimshaw.json        # settings: target descriptor, interval, change-detection settings, image format, export options, schema version
├─ manifest.json         # frames + boxes
├─ images/
│  ├─ frame_00001.png    # or .jpg, depending on the workspace's image format
│  └─ …
├─ thumbs/               # cache, safe to delete and rebuild
└─ exports/
   └─ scrimshaw_2026-10-16_1530.pdf
```

`manifest.json`:

```json
{
  "schemaVersion": 1,
  "nextSequence": 43,
  "frames": [
    {
      "seq": 1,
      "file": "images/frame_00001.png",
      "capturedAt": "2026-10-16T15:02:11.512Z",
      "width": 1920, "height": 1080,
      "dhash": "a3f09c1b7e22d410",
      "deleted": false,
      "boxes": [
        { "id": "b1", "x": 412, "y": 18, "w": 1096, "h": 1040, "label": "Mvt. I – Allegro", "order": 0 }
      ]
    }
  ]
}
```

- **Image format:** `"imageFormat": "png" | "jpeg"`, `"jpegQuality": 90`. It is fixed once the workspace has frames.
- **One manifest file.** Sheet-music sessions have tens to hundreds of frames, so the file stays tiny.
- **Writes:** serialize to `manifest.json.tmp`, flush, then `File.Replace` it over the old file. Writes are debounced (about 250 ms) and go through one `ManifestStore` that holds a single lock.
- **Recovery:** on load, any image in `images/` that has no manifest entry is added back in.
- **Fallback:** SQLite behind the same `IManifestStore` interface, only if NFR-6 profiling calls for it.

## 6. Key components

### 6.1 Capture pipeline (`Scrimshaw.Capture`)

```
PeriodicTimer (interval) ──► FrameGrabber.GrabLatest() ──► RawImage
                                                             │
                                       ChangeDetector.Process(img)   [Core]
                                             │ Save(img)            │ Discard / Pending → counters
                                             ▼
                          Channel<RawImage> (bounded, e.g. 8)
                                             │
                          FrameWriter (background): PNG/JPEG encode → images/ → ManifestStore.Add
```

- **`CaptureTargetProvider`** lists monitors (`EnumDisplayMonitors`) and capturable windows (`EnumWindows`, keeping visible, non-cloaked, titled, top-level windows). It builds a `GraphicsCaptureItem` through `IGraphicsCaptureItemInterop.CreateForMonitor` / `CreateForWindow`. The target is saved as a descriptor (monitor device name; or window process name + title) so it can be found again on reopen.
- **Whole desktop:** one WGC session per monitor, drawn into a single virtual-screen bitmap at each monitor's position in physical pixels. This setup's three monitors, with mixed DPI and negative coordinates (the external monitors sit above and to the left of the primary), are a direct test case.
- **`FrameGrabber`:** a `Direct3D11CaptureFramePool.CreateFreeThreaded` with buffer size 1 that always keeps the most recent frame. Each timer tick reads that frame. It sets `IsCursorCaptureEnabled = false` (FR-C7) and `IsBorderRequired = false` where supported.
- **Timing:** `PeriodicTimer` on a dedicated task. Each tick's timestamp and drift are recorded (FR-C4).
- **Backpressure:** if the writer channel is full, drop the newest frame and log a warning.
- **Failure:** `GraphicsCaptureItem.Closed` ends the session cleanly (FR-C10).

### 6.2 Change detection (`Scrimshaw.Core`)

The target videos have no moving elements between page turns. So every capture is either *the same page* (apart from noise), *mid-turn*, or *a new page*.

**Changed-area measure (`AreaChange`)**
1. Downscale the capture to a grayscale grid of 160×90 cells (aspect ratio kept), averaging the pixels in each cell.
2. A cell counts as changed if its brightness differs from the same cell in the reference frame by more than a **pixel tolerance** (default 24 of 255). This absorbs compression noise and anti-aliasing (FR-D2).
3. `changedFraction` = changed cells ÷ total cells.
4. Two captures are **different** if `changedFraction` ≥ the **area threshold** (FR-D3, default **3%**, lowered from 8% after the Phase 0 measurements, F15). A full page turn measured 19–23% when capturing the browser window, and stable frames measured 0.00%.

**Settle detection (`ChangeDetector`, FR-D6)**

```
Stable:   capture vs lastSaved  → different? → Pending(candidate = capture, count = 0)
                                → same?      → discard
Pending:  capture vs candidate  → same?      → count++; if count ≥ settleCount → Save(capture), lastSaved = capture → Stable
                                → different? → candidate = capture, count = 0 (still changing)
          (capture vs lastSaved "same" again → back to Stable: it was a blip)
```

- **"Same" for settling uses a stricter test than "different" for page turns** (F16). At tolerance 24, a fading-out ghost of the old page doesn't register as change. So the settle check uses `settleTolerance` 8 and requires `changedFraction` < 1%. A frame still mid-fade (7.3% at tolerance 8) therefore doesn't count as settled.
- With the default `settleCount = 1` at a 1 s interval, a page must be shown for about 2 s to be captured.
- Mid-animation frames never settle, so they are never saved.
- The grids for `lastSaved` and the candidate are kept in memory. On resume (FR-C11), the `lastSaved` grid is rebuilt from the last image on disk.
- A **64-bit dHash** is stored for each saved frame (FR-D4). It's used for near-duplicate hints in review, not for deciding what to keep.
- Every capture's decision and `changedFraction` are reported to the capture UI, which shows a live readout to help with setting the threshold.

### 6.3 Annotation UI (`Scrimshaw.App`)

- **Layout:** a capture panel (target picker, interval, threshold with the live readout, image format, start/stop, counters), a virtualized thumbnail strip with a `thumbs/` cache, and the editor.
- **Editor:** an `Image` inside a `Canvas` with zoom and pan transforms. Boxes are `Rectangle`s bound to `BoxViewModel`s. A resize adorner shows 8 handles.
- **Coordinates:** boxes are stored in **image pixels**, and only the rendering transform converts to screen space (FR-A10). Bitmaps are loaded with DPI normalized to 96.
- **Reusing boxes (FR-A11):** *Copy from previous* (`Ctrl+Shift+V`), and *Apply to following* (`Ctrl+Shift+A`), which fills later frames that have no boxes, reports how many it changed, and can be undone as one step. The intended workflow is: box page 1, apply to following, then step through with `→` and adjust.
- **Undo/redo:** a command stack per frame, plus a workspace-level stack for "apply to following".
- **Saving:** every finished edit calls `ManifestStore.UpdateFrame` (debounced atomic write, FR-A6).
- **Automation:** every interactive control gets an `AutomationProperties.AutomationId`, so FlaUI tests can find it reliably.
- **Shortcuts:** `←/→` previous/next frame, `Del` delete box, `Ctrl+Z/Y`, `Ctrl+D` delete frame, `Ctrl+Shift+V`, `Ctrl+Shift+A`.

### 6.4 PDF export (`Scrimshaw.Export`)

1. **`ExportPlanner`** (Core): ordered `(frame, box)` pairs for frames that aren't deleted, sorted by `seq` then by `box.order` (FR-P2).
2. **`StackedLayoutEngine`** (Core, pure math in points):
   - The content area is the page size minus margins (FR-P6).
   - `baseScale` maps 1 source pixel to 1/150 in. The enlargement cap is `maxUpscale × baseScale` (default 2×).
   - **Shared scale (default, FR-P7):** `s = min(contentWidth / maxCropWidth, maxUpscale × baseScale)`, used for every crop. Only a crop that is taller than `contentHeight` at `s` gets its own smaller scale `contentHeight / cropH`.
   - **Fit width (option):** each crop's scale is `min(contentWidth / cropW, contentHeight / cropH, maxUpscale × baseScale)`.
   - **Flow (FR-P4):** `x = left margin` (left-aligned), and y moves down by `height + gap`. If the next crop doesn't fit in the space left, start a new page; a crop is never split. *One per page* = always break after each crop.
3. **`PdfExporter`** (PDFsharp):
   - Loads each source image once and crops all its boxes in memory.
   - Embeds PNG sources losslessly, and JPEG sources as JPEG at the source quality.
   - Draws the optional caption below each crop (FR-P5).
   - **Bookmarks (FR-P10):** one top-level outline entry for each labelled box, pointing to the crop's page and its top position.
   - Runs in the background, reports progress, can be cancelled, and writes `exports/scrimshaw_<timestamp>.pdf` (FR-P8).

## 7. Phases

Each phase lists what gets built, how I verify it, and when it's done.

### Phase 0 — Environment and technical spike
**Build**
- Setup steps in §2.2.
- WGC console spike: capture (a) one monitor, (b) a browser window, (c) all three monitors stitched, every 1 s, saved as PNGs.
- PDFsharp spike: 2 pages, 3 images, 2 bookmarks with positions.
- First version of `TestPlayer` (cut turns only).

**Verify**
- I view the captured PNGs to check:
  - the laptop monitor captures at its full native 1920×1080, not at its 125%-scaled size
  - the stitched desktop lines up correctly
  - the cursor is left out
  - the border is off
  - a covered window still captures
- **Browser test on this hybrid GPU:** hardware-accelerated YouTube content is not black.
- Record timing drift and CPU use.
- Check the PDFsharp output with PdfPig (bookmark titles and targets) and by viewing it.

**Done when** a findings note is added to this doc and WGC + PDFsharp are confirmed (or the fallbacks are chosen). **→ Checkpoint with you.**

### Phase 1 — Core and workspace
**Build**
- Solution skeleton (§4).
- `Workspace`, `ManifestStore`, settings, recovery.
- `AreaChange`, `ChangeDetector`, `DHash`, `ExportPlanner`, `StackedLayoutEngine`.
- `Harness` replay command.

**Verify**
- Unit tests:
  - manifest round-trip and recovery from a simulated crash
  - the settle state machine on synthetic sequences (cut, half turn, cross-fade, blip, noise)
  - layout: shared vs fit width, the oversize-crop exception, page breaks, no splits, left alignment, enlargement cap
- The harness runs on TestPlayer recordings.

**Done when** the headless tests pass cleanly and the harness reports 0 missed pages on synthetic sequences.

**Status: complete (2026-10-16).**
- 71 unit tests pass.
- Harness `replay` passes on synthetic cut, half, fade, slide and mixed sequences (1920×1080, ±4 noise, cursor blips), and on the real V3 recording: every page saved once, no transition frames saved.
- With settle detection off, V3 saves its mid-fade frame and misses page 2, so the settle check is necessary.
- Changes from the plan: a `Scrimshaw.Imaging` project was added (keeps WPF out of Core). The namespace is `Scrimshaw.Core.Workspaces`, so it doesn't clash with the `Workspace` class. The dHash has a 1-level dead-zone so flat areas stay stable under noise.
- The Capture, Export and App projects are created in their own phases.

### Phase 2 — Capture engine and capture UI
**Build**
- `CaptureTargetProvider`, `FrameGrabber`, capture loop, `FrameWriter` (PNG/JPEG), stitching for "all monitors", global hotkey.
- WPF shell: create/open workspace, capture panel with live readout.
- TestPlayer gains half-page, cross-fade and slide turns.

**Verify**
- Desktop tests: capture TestPlayer running scripted 30-page sequences in each turn style, at 0.5 s and 1 s.
- The harness compares saved frames to the ground truth.
- 4K benchmark (synthetic 3840×2160 frames through detection and encoding) for NFR-3.
- Tune the defaults; **target: 0 missed pages, at most 1 extra frame per 10 turns, 0 mid-turn frames saved.**
- Real videos V1–V3 (§11), with V3 testing the cross-fade turns.

**Done when** the targets above are met, drift stays within ±10%, and closing the target window mid-capture stops cleanly and keeps the frames.

### Phase 3 — Review and annotation
**Build**
- Thumbnail strip, editor (zoom and pan, draw, select, move, resize, delete, labels), undo/redo, keyboard navigation, delete frame, badges, copy from previous, apply to following.

**Verify**
- View-model unit tests (commands, undo stacks, coordinate conversions at several zoom levels and DPIs).
- FlaUI run: open a captured workspace, draw a box, apply it to following frames, adjust one, relabel. Screenshots for me to review.
- Kill the process mid-session, reopen, and confirm no finished edits are lost.

**Done when** the tests pass and the screenshots show the boxes in the right places at 100% and 125% DPI. **→ Checkpoint: you try the capture and annotation workflow yourself.**

### Phase 4 — PDF export
**Build**
- `PdfExporter`: stacked (default) and one-per-page layouts, shared scale and fit width, captions, page size, orientation and margins, bookmarks, progress and cancel, Open PDF / Open folder.

**Verify**
- Golden tests with PdfPig: page count, image order, no image crossing a margin or page edge, left alignment, equal scale across crops in shared mode, bookmark titles and target pages.
- I view the exported PDFs page by page.

**Done when** a full TestPlayer session goes all the way to a correct PDF. **→ Checkpoint: you review a PDF made from a real video, and ideally print one page.**

### Phase 5 — Hardening and packaging
**Build**
- Error handling: target lost, disk full, workspace locked, corrupt manifest (rebuild from images).
- Recent workspaces, remembering window position, an icon, and an About box with licenses.
- Single-file self-contained publish.

**Verify**
- Error paths covered by tests where they can be.
- The published exe runs from a fresh folder on this machine, using the bundled runtime rather than the installed SDK.
- A full real-video run from start to finish.
- *Not available here:* a clean Windows 10 (build 19041) machine. Windows 10 support is checked by reviewing the API calls and runtime OS checks, because no Windows 10 machine is available (§11).

**Done when** v1.0 is built and the main scenario works on a real video. **→ Final checkpoint.**

## 8. Testing strategy

| Level | What | Where it runs |
|---|---|---|
| Unit | Core logic, layout math, view-models | Headless; every build |
| Replay | Change detection compared with ground truth (synthetic + real recordings) | Headless; whenever detection code or defaults change |
| Capture integration | WGC against TestPlayer | Interactive desktop; coordinated (§11) |
| Export golden | Fixture workspace → PDF → PdfPig checks + visual review | Headless |
| UI end-to-end | FlaUI workflows + screenshots | Interactive desktop; coordinated (§11) |
| Real-video acceptance | YouTube sheet-music videos V1–V3 | Phases 0, 2, 5; coordinated (§11) |

## 9. Risks

| # | Risk | Impact | Mitigation |
|---|---|---|---|
| R1 | ~~Black frames from hardware-accelerated browser content (especially on hybrid GPUs)~~ **Retired: Phase 0 captured YouTube in Chrome cleanly on this hybrid GPU (F12).** | Main scenario broken | Phase 0 tests this on this exact machine. If it fails: force the browser onto one GPU in Windows graphics settings, or turn off browser hardware acceleration, and document the workaround. DXGI Desktop Duplication is a weaker fallback on hybrid GPUs. |
| R2 | A page turn is missed (a partial turn below the threshold, or a page shown for less than the settle time) | Missing music in the PDF | Low default threshold, tuned against ground truth; live readout; settle count can be set to 0. |
| R3 | YouTube's controls or the mouse cause extra frames | A few extra frames to delete | Low impact now that there are no moving elements. Leave the cursor out; follow A7. Deleting frames is quick (FR-A9). |
| R4 | Mixed-DPI coordinates or crops come out wrong | Wrong PDF content | Pixel-space boxes; tested on this machine's 125% + 100% monitors. |
| R5 | PDFsharp gaps on .NET 10 (outlines, images) | Export rework | Phase 0 spike; export kept behind `IPdfExporter`. |
| R6 | Low-resolution source gives blurry prints | Poor output | A8; show the source resolution in the capture panel. |
| R7 | UI automation clashes with you using the PC at the same time | Flaky tests, or interrupting your work | Desktop tests are kept separate and only run when agreed (§11). Most logic is tested headlessly. |
| R8 | Windows 10 compatibility can't be tested on this machine | A Windows 10 bug found late | Use only APIs available in build 19041; check the OS version at runtime before using Windows 11-only features (border control). |

## 10. Decisions

| Decision | Status |
|---|---|
| .NET 10 + WPF + WGC | Confirmed against this machine (§2) |
| PDFsharp for PDF | Proposed; confirmed by the Phase 0 spike |
| Changed-area measure + settle detection | Proposed; defaults set in Phase 2 |
| Shared-scale, left-aligned stacked layout | Confirmed (requirements v0.3) |
| Headless-first testing with a synthetic score player and on-demand desktop tests | Proposed (§3) |

## 11. Development agreements (answered 2026-10-16)

| # | Topic | Agreement |
|---|---|---|
| 1 | .NET 10 SDK | Install with `winget install Microsoft.DotNet.SDK.10` (approved). |
| 2 | Git | `git init` approved. Commit at the end of each phase and at sensible points within a phase. No remote. |
| 3 | Desktop tests | **Coordinate first.** Before any run that captures the screen, opens windows or moves the mouse (capture integration, UI automation, real-video captures), I ask whether the machine is free and wait for your go-ahead. I say when the run is finished. Headless work goes ahead without asking. |
| 4 | Real videos | Open in your default browser (covered by the coordination rule in row 3). Links: **V1** https://www.youtube.com/watch?v=atYihjjTgAk · **V2** https://www.youtube.com/watch?v=LyZIyDVehzU · **V3** https://www.youtube.com/watch?v=Ay8Vauav4yA (animated / cross-fade page turns; the main test of settle detection). |
| 5 | Windows 10 | No Windows 10 machine is available. Windows 10 support is checked only at the API level (R8). |

## 12. Phase 0 findings

| # | Finding | Action |
|---|---|---|
| F1 | .NET SDK 10.0.402 installed with winget. The repository is set up (`global.json`, `Directory.Build.props`, central package versions in `Directory.Packages.props`). | Done |
| F2 | **PDFsharp licence change.** PDFsharp **6.5.0 is AGPL-3.0** and refuses to run without a licence (`PdfSharpLicenseException`). **6.2.4** is the last MIT release. | **Decided: stay on 6.2.4 (MIT)** (2026-10-16). |
| F3 | PDFsharp 6.2.4 on .NET 10: PNG crops are embedded at full resolution and placed to within 0.1 pt of the plan. Stacked flow, page breaks and the shared scale were all checked with PdfPig and visually. | Confirmed |
| F4 | PDFsharp writes outline `Top` as-is in PDF coordinates, which measure from the bottom of the page. The exporter must set `Top = pageHeight − y`. | Built into the §6.4 design |
| F5 | PDFsharp brings in `System.Security.Cryptography.Pkcs` 8.0.1, which has known vulnerabilities. On `net10.0-windows` the SDK prunes it, because the framework supplies a fixed version. A central transitive pin covers any plain `net10.0` project. | Done |
| F6 | The tool I'd normally use to view PDFs can't render them here (poppler isn't installed). A small renderer built on the built-in `Windows.Data.Pdf` turns PDF pages into PNGs that I can view. | It becomes a `Harness render-pdf` command |
| F7 | `IsBorderRequired` needs SDK 10.0.20348 or later at compile time. | TFM changed (§1) |
| F8 | Monitor enumeration in a per-monitor-DPI-aware process returns physical bounds: laptop 1920×1080 at 125%, plus two 1920×1080 at 100% at negative coordinates. | Confirmed |
| F9 | Window enumeration includes the NVIDIA overlay (a full-screen invisible tool window). | Filter tool windows and transparent windows out of the target picker |

| F10 | **Monitor capture:** the laptop panel (125%) captures at its native 1920×1080. Ticks land within ±8 ms of a 1 s interval. Readback 4–6 ms, PNG encode 40–120 ms at 1080p, CPU 0.4–1.1% of 16 cores. | Confirmed; NFR-3 4K benchmark still due in Phase 2 |
| F11 | **All monitors:** the stitched image (3840×2173, including negative coordinates) is laid out correctly. WGC only sends frames when content changes (an idle monitor sent 1 frame in 4 s), so the grabber must keep the latest frame, as designed. | Confirmed |
| F12 | **Hybrid GPU and YouTube:** hardware-accelerated video in Chrome captures cleanly, with no black frames. | R1 retired |
| F13 | **Covered window:** a window covered by another app still captures cleanly, while monitor capture shows the window on top. No capture border appears in any frame on Windows 11. | Confirmed |
| F14 | **Cursor exclusion** couldn't be observed, because the mouse was idle. | Test in Phase 2 by moving the cursor over a TestPlayer window programmatically |
| F15 | **Change detection on V3** (tolerance 24): page turns measured 18.8% and 21.1% in window capture (the video is about half the window, and notation is mostly white space). Stable pages measured 0.00%, and Chrome losing focus measured 0.00%. 8% would be too close to a half-page turn. | Default area threshold lowered to **3%** (§6.2) |
| F16 | **Cross-fade:** a mid-fade frame with the old page ghosted was captured. At tolerance 24 it looks the same as the clean page that follows (0.04%); at tolerance 8 it differs by 7.3%. | Settle check uses a stricter tolerance (§6.2) |
| F17 | **End of video:** YouTube shows an animated end screen and then autoplays the next video. These are real changes and will be captured. | Help text: turn off autoplay and stop capture at the end; end-screen frames can be deleted in review (FR-A9) |
| F18 | Window titles change when YouTube moves to another video. | During a session, track the target by window handle; the title is only used to find the window again when a workspace is reopened |
| F19 | A test that used Notepad brought up your own saved notes in a monitor capture. | Capture deleted. Tests use only Scrimshaw's own windows (TestPlayer) from now on |

**Phase 0 result: GO.** WGC and PDFsharp 6.2.4 are confirmed. Moved to Phase 2: checking cursor exclusion (F14) and the 4K benchmark.
