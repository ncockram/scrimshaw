# Scrimshaw — Requirements

| | |
|---|---|
| Status | **v1.0: delivered.** Every Must and Should requirement is built and verified (§9). |
| Date | 2026-10-17 (v0.4 added FR-D8 after the Phase 2 real-video tests) |
| Source | `brainstorm.md` (initial brainstorming conversation); review comments on v0.1 and v0.2 |

## 1. Purpose

Scrimshaw is a Windows desktop app that:

1. Takes screenshots of a chosen screen source at a set interval and keeps only the ones that are different from the last one saved.
2. Lets the user go through the saved images and draw one or more bounding boxes on each.
3. Builds a PDF made of every boxed region, cropped from its source image.

**Main scenario.** The user wants to turn a **sheet-music video** into a printable PDF score. In these videos, the score is shown on screen while the music plays, and the picture changes mainly at **page turns**. The user:

1. creates a workspace for the video
2. sets the capture target and interval
3. starts capturing
4. plays the YouTube video
5. stops capturing when the video ends
6. goes through the captured pages and draws boxes around the music, usually one box per page or one per system
7. generates the PDF, with the crops stacked down each page like a printed score

## 2. Scope

**In scope (v1):** Windows desktop app with capture, de-duplication, annotation and PDF export, all working on a local workspace folder.

**Out of scope (v1):**
- macOS and Linux
- OCR and optical music recognition (OMR)
- video file input (only the live screen is captured)
- cloud sync
- multiple users working on one workspace
- several videos in one workspace
- automatic content detection (boxes are drawn by hand)
- continuously scrolling score videos (page-turn videos only)

## 3. Definitions

| Term | Meaning |
|---|---|
| Workspace | A folder on disk for **one video**. It holds that video's settings, captured images, annotations and exported PDFs. |
| Capture target | The source being captured: the whole desktop, one monitor, one window, or a webpage (see FR-C2). |
| Frame | One captured image that was kept after de-duplication. |
| Page turn | The video's displayed score changing to new content. This can be the full page or part of it (e.g. the top half updates first). |
| Bounding box (box) | A rectangle on a frame, stored in the frame's pixel coordinates, marking a region to export. |
| Interval | The time between capture attempts. |

## 4. Functional requirements

Priority: **M** = must have (v1), **S** = should have (v1 if feasible), **C** = could have (later).

### 4.1 Workspace

| ID | Requirement | Pri |
|---|---|---|
| FR-W1 | The user can create a new workspace by choosing an empty folder, or open an existing one. | M |
| FR-W2 | A workspace stores its settings (target, interval, dedup settings, image format, export options), its frames, and per-frame metadata (sequence number, capture timestamp, hash, boxes). | M |
| FR-W3 | Reopening a workspace restores all frames and boxes exactly as they were left. | M |
| FR-W4 | The app remembers recently opened workspaces. | S |
| FR-W5 | Workspace state stays out of the image files themselves; images remain plain PNG or JPEG files. | M |

### 4.2 Capture

| ID | Requirement | Pri |
|---|---|---|
| FR-C1 | The user can set the capture interval in seconds, with fractions allowed (e.g. 0.5, 1, 2). The minimum supported interval is 0.25 s. The default is 1 s. | M |
| FR-C2 | The user can pick the capture target: **(a)** the whole desktop (all monitors), **(b)** one monitor, **(c)** one application window, or **(d)** a webpage, which v1 handles by capturing the browser window. | M (a–d) |
| FR-C3 | The user can start and stop capturing. Stopping finishes any pending writes before the session is reported as complete. | M |
| FR-C4 | Captures happen on time no matter what the UI is doing. Drift should stay within ±10% of the interval on average. | M |
| FR-C5 | Frames are saved in the workspace's image format and numbered in order (`frame_00001.png`, …). **PNG** (lossless) is the default. **JPEG** is available with a quality setting (default 90). The format is chosen per workspace before capture starts. | M |
| FR-C6 | While capturing, the app shows elapsed time, the number of capture attempts, the number of frames kept, and the number discarded. | M |
| FR-C7 | The mouse cursor is left out of captures by default (configurable). | S |
| FR-C8 | Window capture still works when the target window is partly covered by other windows. | S |
| FR-C9 | A global hotkey starts and stops capture, so the user doesn't have to leave the video. | S |
| FR-C10 | If the target window is closed or a monitor is disconnected during capture, capture stops cleanly, the frames already captured are kept, and the user is told. | M |
| FR-C11 | Starting capture again in a workspace that already has frames (e.g. resuming an interrupted capture of the same video) adds to them; numbering continues from the last frame. | S |
| FR-C12 | The user can limit capture (or just the dedup comparison) to a sub-region of the target. This handles video player controls and progress bars that change while the content doesn't. *Deferred to a later version.* | C |

### 4.3 De-duplication

| ID | Requirement | Pri |
|---|---|---|
| FR-D1 | Each new capture is compared with the **last saved** frame. If it is effectively the same, it is thrown away and never written to disk. | M |
| FR-D2 | Similarity uses a perceptual measure that tolerates compression and anti-aliasing noise, not an exact byte comparison. Comparison is done on the raw captured pixels, before PNG or JPEG encoding. | M |
| FR-D3 | The similarity threshold can be changed by the user (slider or number) and is stored in the workspace. | M |
| FR-D4 | Each frame's hash is stored in its metadata so it never has to be computed again. | M |
| FR-D5 | Every page turn is kept at the default threshold, including **partial** page turns where only part of the page (e.g. the top half) changes. | M |
| FR-D6 | **Settle detection.** A changed frame is only saved once it has stayed the same for a set number of captures (default 1, meaning two matching captures in a row). This skips half-finished frames from page-turn animations, fades and slides. It can be turned off. | M |
| FR-D7 | Small local changes are **not** treated as new frames at the default threshold. The target videos have no moving elements between page turns, so in practice this covers compression flicker, the mouse cursor passing over, and YouTube's controls briefly appearing. | M |
| FR-D8 | **Keep the cleanest version of each page.** If a page is saved while something briefly covers part of it (for example, YouTube's controls and title, which appear for a few seconds when playback starts or the mouse moves), the saved image is replaced by the version of that page that stays on screen longest. It can be turned off. *(Added after Phase 2: without it, the first page of both V1 and V2 was saved with the controls showing.)* | M |

### 4.4 Review and annotation

| ID | Requirement | Pri |
|---|---|---|
| FR-A1 | The user can browse all frames in the workspace as a thumbnail strip or grid. | M |
| FR-A2 | Choosing a frame opens it in an editor at full resolution, with zoom-to-fit, zoom in/out and pan. | M |
| FR-A3 | The user can draw a box by click-and-drag. A frame can have any number of boxes. | M |
| FR-A4 | The user can select, move, resize (with handles) and delete existing boxes. | M |
| FR-A5 | Each box can have an optional text label. Labels become PDF bookmarks (FR-P10). | S |
| FR-A6 | Box changes are saved straight to the workspace, so a crash loses at most the edit in progress. | M |
| FR-A7 | Keyboard navigation: next and previous frame, delete the selected box, and undo/redo within a frame. | S |
| FR-A8 | Thumbnails show which frames have boxes (e.g. a badge with the count). | S |
| FR-A9 | The user can delete a frame, e.g. a leftover duplicate or a mid-transition frame. Deleted frames are left out of export. | S |
| FR-A10 | Box coordinates are stored in the image's native pixels, so they don't depend on display DPI or zoom. | M |
| FR-A11 | **Reuse boxes.** The user can copy the boxes from the previous frame onto the current one, or apply the current frame's boxes to all following frames that have no boxes yet. Score videos usually show the music in the same place on every page. Copied boxes can be edited on each frame afterwards. Labels are not copied. | S |

### 4.5 PDF export

| ID | Requirement | Pri |
|---|---|---|
| FR-P1 | The user can generate a PDF of every box from every frame that hasn't been deleted. | M |
| FR-P2 | Order: by frame sequence, then by the order the boxes were created within a frame. | M |
| FR-P3 | Each box's region is cropped from its full-resolution source frame in memory. No crop files are left in the workspace. | M |
| FR-P4 | **Layout.** The default is **stacked**: crops are placed top to bottom, **aligned to the left margin**, with a configurable gap (default 0.25 in / 6 mm), and flow onto a new page when the next crop won't fit. A crop is never split across pages. Option: **one box per page**. | M / S |
| FR-P5 | Optional caption under each crop: frame number, capture time and box label. Off by default. | S |
| FR-P6 | Page size options (Letter or A4), portrait or landscape (default portrait), and margins (default 0.5 in / 12.7 mm). | S |
| FR-P7 | **Scaling.** Each crop keeps its aspect ratio. The default is **shared scale**: every crop in the export uses one scale factor, worked out so the widest crop fits the content width, which keeps the staves the same size throughout. A crop that is still taller than the content area at that scale is shrunk to fit, and only that crop. Option: **fit width**, where each crop is scaled to the content width on its own. Either way, crops are not enlarged past a limit the user can set (default 2× a base print size of 150 source pixels per inch). | M |
| FR-P8 | The PDF is written to `exports/` in the workspace with a timestamped name. The user can open it or the folder straight after export. | M |
| FR-P9 | If no boxes exist, export is blocked and the user is told why. | M |
| FR-P10 | **Bookmarks.** Every labelled box adds an entry to the PDF's bookmark panel (outline). The entry points to the page and position where that crop appears. Entries are listed in export order. Boxes without labels get no bookmark. | M |
| FR-P11 | Print clean-up options: convert to grayscale, and whiten off-white backgrounds. | C |
| FR-P12 | Horizontal alignment option for crops (left, centre, right). *Later version; v1 is left-aligned.* | C |

## 5. Non-functional requirements

| ID | Requirement |
|---|---|
| NFR-1 **Platform** | Windows 10 version 2004 (build 19041) or later, and Windows 11, on x64. ARM64 is a "could have". |
| NFR-2 **DPI** | The app is per-monitor-DPI aware. Captures are taken at native pixel resolution on every monitor, including mixed-DPI setups. |
| NFR-3 **Performance** | At a 0.5 s interval on a 4K monitor, capture, hashing and image writing keep up without a growing backlog. While capturing, the app uses less than 15% CPU on a typical 4-core machine. |
| NFR-4 **Responsiveness** | The UI stays responsive while capturing and exporting. Exporting 500 boxes finishes in under 60 s. |
| NFR-5 **Durability** | Metadata writes are atomic (write to a temp file, then replace), so a crash or power loss never leaves a corrupt workspace. |
| NFR-6 **Scale** | Handles workspaces of at least 5,000 frames. That is roughly a 1-hour video at 0.5 s with little de-duplication. The thumbnail view stays smooth (virtualized). |
| NFR-7 **Distribution** | Ships as a self-contained single-file exe (no separate .NET install). No admin rights needed. |
| NFR-8 **Privacy** | Everything stays local. No network calls and no telemetry. |
| NFR-9 **Licensing** | Third-party libraries must allow personal and small-business use. Any commercial-license limits must be written down. |

## 6. Constraints and assumptions

*All confirmed on 2026-10-16.*

- **A1 — Stack.** The stack is C# / .NET with WPF and the Windows.Graphics.Capture API. The brainstorm recommended this and preferred it over Python + PySide6 because it captures covered windows and DPI-scaled screens more reliably. *Checked against this development machine; see §2 of the implementation plan.*
- **A2 — .NET version.** The plan targets **.NET 10 (LTS, supported until 2028-11-14)**. Both .NET 8 and .NET 9 reach end of support on 2026-11-10. The .NET 10 SDK is not yet installed on the development machine (see the implementation plan, §2).
- **A3 — Webpages.** A "webpage" target is captured as its browser window. Capturing a whole scrolling page or a headless browser is out of scope.
- **A4 — Pause gaps.** Video playback is controlled by the user outside the app. Scrimshaw doesn't detect when a video pauses or ends.
- **A5 — Protected content.** DRM-protected video (e.g. some streaming services) may capture as black frames. That is a platform limit, not a defect. Normal YouTube playback is not affected.
- **A6 — Capture border.** On Windows 10, the yellow border Windows draws around captured content can't be turned off. This is accepted. The border doesn't appear in the captured frames.
- **A7 — Player chrome.** In v1, the user is expected to keep the mouse off the video, or use theatre or fullscreen mode, so YouTube's controls stay hidden. The region-of-interest feature (FR-C12) is deferred.
- **A8 — Source resolution.** Print quality depends on the video's resolution and the size of the player. A 1080p fullscreen capture of a full page gives roughly 200 DPI on Letter or A4, which prints acceptably. Smaller players give worse results.

## 7. Decisions log

| # | Question | Decision |
|---|---|---|
| 1 | Typical content | **Sheet music.** The usual change is a whole page turn. → FR-D5, FR-D6, FR-D7, FR-A11, FR-P4 |
| 2 | Region of interest for player chrome in v1? | **Later version.** → FR-C12 stays C; A7 |
| 3 | Yellow capture border on Windows 10 acceptable? | **Yes.** → A6 |
| 4 | Box labels in the PDF | **Bookmarks.** → FR-P10 |
| 5 | Sessions per workspace | **One workspace per video.** → Definitions, FR-C11, scope |
| 6 | Image formats | **PNG and JPEG.** → FR-C5, FR-W5 |
| — | PDF layout default (v0.1 review note) | **Stacked, several boxes per page.** → FR-P4. The N-up grid option has been dropped. |
| 7 | Moving elements between page turns? | **None.** → FR-D7; region of interest stays deferred |
| 8 | Scrolling scores in v1? | **No; page-turn videos only.** → Scope |
| 9 | Staff size in the PDF | **One shared scale by default.** → FR-P7 |
| — | Crop alignment (v0.2 review note) | **Left-aligned.** Other alignments later. → FR-P4, FR-P12 |
| — | Constraints and assumptions | **All confirmed.** → §6 |
| — | Player controls on saved pages (Phase 2 finding) | **Keep the version shown longest.** → FR-D8 |

## 8. Open questions

None on the product requirements. The development process is described in §11 of the implementation plan.

## 9. v1.0 delivery status

Every **Must** and **Should** requirement is delivered. The **Could** items are deliberately deferred. "Verified" says how each was proven; the test suite has 180 headless tests plus desktop tests.

| Requirement | Status | Verified by |
|---|---|---|
| FR-W1–W3, W5 | Done | Workspace and manifest unit tests; UI tests reopen workspaces |
| FR-W4 Recent workspaces | Done (Should) | View-model tests; Recent ▾ menu |
| FR-C1, C3–C6 | Done | Capture-session tests on a fake clock; desktop tests (±7 ms average drift) |
| FR-C2 Targets | Done: (a) whole desktop, (b) monitor, (c) window, (d) webpage via the browser window | Stitching unit tests on this machine's 3-monitor layout; Phase 0 monitor and desktop capture; desktop window tests; real-video acceptance |
| FR-C7 Cursor left out | Done (Should) | Desktop test (0.00% change excluded, visible when included) |
| FR-C8 Covered window | Done (Should) | Phase 0 (F13) |
| FR-C9 Global hotkey | Done (Should) | Real-video acceptance stops with Ctrl+Alt+S |
| FR-C10 Target lost | Done | Unit and desktop tests |
| FR-C11 Resume | Done (Should) | Capture-session test |
| FR-C12 Region of interest | **Deferred (Could)** | — |
| FR-D1–D8 | Done | Unit tests; replay of 5 synthetic and 4 real labelled recordings, all passing; desktop tests per turn style |
| FR-A1–A4, A6, A10 | Done | Editor and view-model tests; UI tests with the real mouse |
| FR-A5, A7, A8, A9, A11 | Done (Should) | Same; undo goes beyond "within a frame" (one history, and "apply to following" undoes in one step) |
| FR-P1–P3, P7–P10 | Done | 13 golden PDF tests (PdfPig); visual review of real exports |
| FR-P4–P6 | Done (stacked and one per page; Letter/A4; portrait/landscape; margins and gap) | Golden tests; Export tab UI test |
| FR-P5 Captions | Done (Should) | Golden test |
| FR-P11 Print clean-up, FR-P12 Alignment | **Deferred (Could)** | — |
| NFR-1 Platform | Done for Windows 11 x64. Windows 10 2004+ checked at the API level only (no Windows 10 machine available). ARM64 (Could) not built. | Platform analyser as a build error; guarded Windows 11-only call |
| NFR-2 DPI | Done | 125% + 100% monitors; native-resolution captures; UI screenshots at both |
| NFR-3 Performance | Done | 4K benchmark (detection 21.5 ms, PNG 113 ms); about 1% CPU at 1080p |
| NFR-4 Responsiveness | Done | 500 crops exported in 6.7 s; capture and export off the UI thread |
| NFR-5 Durability | Done | Atomic writes; crash-recovery tests; kill-after-edit UI test; repair of damaged files |
| NFR-6 Scale | Done | 5,000-frame test (open, review, edit, apply to all, undo, save, export layout) runs in under 3 s in total |
| NFR-7 Distribution | Done | Single self-contained `Scrimshaw.exe`; UI tests run against it from a clean folder |
| NFR-8 Privacy | Done | No network code (checked) |
| NFR-9 Licensing | Done | All shipped components MIT; PDFsharp pinned below the AGPL versions; `THIRD-PARTY-NOTICES.txt` |

## 10. Adding new requirements

New feature requests go through the same steps as v1.0:

1. **Record:** add the request to §11, "Requests after v1.0", with a short description, why it's wanted, and the date. Nothing is built yet.
2. **Refine:** together, agree its scope, its priority (M/S/C for the release it targets) and its answers to any open questions. Then give it a requirement ID in the right §4 table (the next free number, e.g. FR-C13 or FR-P13; IDs are never reused) and a row in the decisions log (§7) if it changed or overrode an earlier decision.
3. **Plan:** add a phase, or a step within one, to the implementation plan: what gets built, how it's verified (headless first; desktop tests by arrangement), and its exit criteria.
4. **Build and verify,** then update §9 of this document, the user guide and the README.

## 11. Requests after v1.0

*None yet.* Candidates already on file are the deferred Could items: FR-C12 region of interest, FR-P11 print clean-up, FR-P12 alignment, and ARM64. The implementation plan (§13) lists further ideas.
