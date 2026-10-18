# Scrimshaw user guide

Scrimshaw turns a sheet-music video into a printable PDF score. While the video plays, it saves a picture of each new page. You then draw boxes around the music and export the boxes as a PDF.

**You need:** Windows 10 (version 2004 or later) or Windows 11, 64-bit. Just run `Scrimshaw.exe`; nothing needs installing.

---

## 1. Create a workspace

Each video gets its own **workspace**: a folder that holds the captured pages, your boxes and the exported PDFs.

1. Click **New workspace…**.
2. Choose or create an **empty** folder, for example `Documents\Scores\Cinema Paradiso`.

To come back to a video later, click **Open workspace…** or pick it from **Recent ▾**.

## 2. Capture the video

1. Open the video in your browser and switch to **full screen** (press **F** on YouTube). Use the highest video quality: the sharper the video, the sharper the printed music.
2. **Turn off autoplay**, so the next video doesn't start and get captured too.
3. In Scrimshaw's **Capture** tab, choose the browser window under **What to capture**. Click **Refresh** if it isn't listed.
4. Check the **preview** on the right: it shows what will be captured, live, with an orange "PREVIEW · not saved" badge. Nothing is saved while previewing.
5. Optionally, **mark the music area** (see below).
6. Click **Start capture**, or press **Ctrl+Alt+S** from any app.
7. Play the video. **Keep the mouse off the video**, so the player's controls stay hidden.
8. When the video ends, capture **stops by itself** as soon as YouTube moves on to another video (the window's title changes). Otherwise press **Ctrl+Alt+S** again, or click **Stop capture**.

While it captures, **Pages saved** goes up at each page turn, and the latest saved page appears on the right in place of the preview. After capture, click **Show preview** to see the live picture again.

### Mark the music area (optional)

If something on screen keeps moving besides the music (a video of the performer, a progress bar, a ticking clock), mark the part of the picture that holds the score:

1. On the preview, **drag a rectangle** around the music. Everything outside it is dimmed, and the area gets a green **Music area** outline.
2. Only changes **inside** the area now count as page turns. Pages are still saved in full.
3. Tick **Save only this area** to save just the marked part instead.

To change it, drag again; **Clear area** goes back to the whole picture. The area is kept with the workspace and can't change while capture is running. It's stored as a proportion of the picture, so it still fits if the window is resized.

**Settings, if you need them:**
- **Seconds between captures:** the default of 1 second suits almost every score video. Use 0.5 if pages turn very quickly.
- **New page when at least …% changes:** the default of 3% catches every page turn, including half-page turns. The line underneath shows how much the picture changed at the last capture. If the same page keeps being saved again, raise the threshold a little.
- **PNG or JPEG:** PNG is perfect quality, and JPEG uses less disk space. This can't be changed once a workspace has pages.
- **Stop when the window's title changes** (on by default, windows only): stops capture when the next video starts. A notification count such as "(3)" at the start of the title is ignored. Untick it if your browser changes the title for other reasons.

**Good to know:**
- Scrimshaw waits for a page to **stop changing** before saving it, so fades and page-curl animations aren't saved half-finished.
- If the player's controls were showing when a page was saved, Scrimshaw swaps in the clean version once it has been on screen longer.
- Keep the browser window **fully on screen**. Any part that's off the edge comes out black.
- You can start capture again in the same workspace (for example, after an interruption). New pages are added after the existing ones.
- The status bar always says why capture stopped (you stopped it, the title changed, or the window closed).
- **After an automatic stop, check the last page or two.** YouTube shows its "Up next" screen, and sometimes the next video's first page, a moment before the title changes, so these can be saved. Delete them in Review with **Ctrl+D**.

## 3. Review and box the music

Open the **Review and box** tab. The pages are listed on the left, and a number badge shows how many boxes each page has.

1. On the first page, **drag a box** around the music. Drag a box to move it, or drag its corners and edges to resize it.
2. Optionally, type a **label** (such as "Mvt. I") and press **Enter**. Labels become bookmarks in the PDF.
3. Click **Apply boxes to following frames**. Every later page that has no boxes yet gets the same boxes, without labels.
4. Step through the pages with **→** and adjust any page that's laid out differently.
5. Delete pages you don't want, such as YouTube's end screen, with **Ctrl+D**. Deleted pages stay in the list, greyed out, and aren't exported. Press Ctrl+D again to restore one.

| Keys | Action |
|---|---|
| ← / → | Previous / next page |
| Delete | Delete the selected box |
| Ctrl+Z / Ctrl+Y | Undo / redo (including "apply to following", in one step) |
| Ctrl+Shift+V | Copy boxes from the previous page |
| Ctrl+Shift+A | Apply boxes to the following pages |
| Ctrl+D | Delete or restore the page |
| Enter | Edit the selected box's label |
| F · mouse wheel · right-drag | Fit to window · zoom · pan |

Everything is saved as you go; there's no Save button.

## 4. Export the PDF

Open the **Export PDF** tab. The preview shows exactly how the pages will come out.

- **Stacked** (the default) places the boxes one under another, like a printed score. **One crop per page** gives each box its own page.
- **Same scale for every crop** (the default) keeps the staves the same size throughout. **Each crop fills the page width** makes every box as wide as possible.
- Choose **Letter** or **A4**, portrait or landscape, and the margins and gap (inches for Letter, millimetres for A4).
- **Captions** add the page number, capture time and label under each box.

Click **Export PDF**, then **Open PDF** or **Open folder**. Exports are saved in the workspace's `exports` folder with the date and time in the name, so nothing is overwritten.

---

## Troubleshooting

| Problem | What to do |
|---|---|
| The browser window isn't in the list | Click **Refresh**. Minimised windows can't be captured, so restore the window first. |
| Captured pages are black | The video is copy-protected (some streaming services block screen capture). Ordinary YouTube videos aren't affected. |
| A yellow border appears around the window during capture (Windows 10) | Windows 10 shows this while capturing and it can't be turned off. It isn't saved in the pages. |
| "Ctrl+Alt+S is in use by another app" | Another program owns that shortcut. Use the Start and Stop buttons instead. |
| The same page is saved several times | Raise **New page when at least …%** a little, and keep the mouse off the video. If something else on screen moves, **mark the music area**. Delete the extras with Ctrl+D. |
| Nothing is being saved, and **Settling** stays at "yes" | Something on screen never stops moving (e.g. a performer video beside the score). **Mark the music area** around just the score. |
| The preview says "Nothing to preview: is the window minimised?" | Restore the window. Minimised windows can't be captured or previewed. |
| Capture stopped by itself partway through | The window's title changed (the status bar shows the new title). If the same video was still playing, untick **Stop when the window's title changes** and start again: new pages are added after the existing ones. |
| A page turn was missed | Lower the threshold, or use a 0.5-second interval for videos that turn pages quickly. |
| "This workspace is already open in another Scrimshaw window" | Switch to that window, or close it first. |
| "… is damaged and can't be read" | Choose **Yes** to repair. Your captured pages are kept, and the damaged file is kept beside it as a backup. |
| Not enough disk space | Free up space, or create the workspace on another drive. JPEG uses less space than PNG. |
| Scrimshaw reported a problem | Details are saved in `%LOCALAPPDATA%\Scrimshaw\logs` (**About → Open error logs folder**). |
