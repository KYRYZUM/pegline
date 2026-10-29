# Pegline roadmap

Goal: a Windows-native counterpart of every user-facing feature in ClipBoardLine.
`[x]` done · `[~]` partly done · `[ ]` to do

## Phase 1 — Foundation and shelves
- [x] .NET 10 solution split into Core / App / Tests, CI
- [x] SQLite Library store (transactions + undo), content-addressed media store
- [x] Clipboard listener: text / link / image (PNG, DIB), concealed and history-excluded copies, Pegline's own copies; image files copied in Explorer, copies with text next to the image (browsers), 32-bit DIBs with an empty alpha channel
- [x] Duplicate prevention by text identity
- [x] History length per kind, saved records kept, expired copies moved to Trash
- [x] Shelf navigation state machine (13 behavior tests)
- [x] Keymap: remapping, disabling, default aliases
- [x] Shelf window: sliding panel, sagging ropes, pegs, numbered cards, paging, theme / card size groundwork
- [x] Copy (`Enter` closes, `Ctrl+C` keeps the shelves open), `Ctrl+Enter` pastes into the previous app, pin, take down + undo (32 steps), move to Trash
- [x] Shelf search (`Shift+S`), show / hide rows, pinned only, select mode
- [x] Global shortcuts, tray, single instance, welcome notification
- [x] Turkish + English interface (the other seven languages arrived in Phase 9)

## Phase 2 — Preview and Actions
- [x] Borderless preview with Space (image / text / link), arrow keys move between cards in place
- [x] Image zoom (100–400%, anchored at the pointer), drag to pan, double-click to reset
- [x] Text editing (`E`, saves automatically, `Ctrl+Enter` finishes), open a link in the browser (`Ctrl+O`)
- [x] Actions palette (`Ctrl+K`): context-aware, searchable with or without Turkish accents, shows shortcuts and why an action is unavailable; unfinished actions appear as "Coming soon"
- [x] From the palette: save to Library, remove the cards to the left / right, open the file / show it in Explorer, copy the latest image, clear unsaved copies, add to / remove from a folder, remove a tag

## Phase 3 — Library window
- [x] Grid / list (virtualized, 20,000 records), sidebar (My library, Favorites, Unorganized, Recent copies, Trash), folders (one level of subfolders), tags, counts
- [x] Auto-saving details pane (name, content, note), remove from folder / tag, new note (`Ctrl+N`), create / rename / remove folders, rename / remove tags
- [x] Kind filters (`Ctrl+0..3`), sorting (newest / oldest / title / last copied / best match), date filter, accent-insensitive search, keyboard behavior tests
- [x] Trash: `S` / `Delete` move items there recoverably, `Backspace` / `Ctrl+Z` undo, keys never delete permanently in the Trash view; restore, delete permanently, empty Trash, confirmation before clearing unsaved copies
- [x] Preview in the Library (`Space`), Actions palette (scope, view, sort, folder commands), context menu; opened with `L` from the shelves, from the tray and with a global shortcut
- [x] Domain and multi-tag filter interface (the Phase 7 filter panel)

## Phase 4 — Organizing and transfer
- [x] Pin to folder (`Shift+P`, recent folders, `Ctrl+N`, pin without a folder), Name and pin (`Alt+P`, one by one for a batch, skips items deleted meanwhile), add / create tags (`Ctrl+Shift+T`); sibling folder names are unique
- [x] Export (`Ctrl+Shift+E`, " (2)" on a name clash, never overwrites, counts failures), drag and drop (images as files; text / links as .txt / .url files when the drag starts with `Ctrl`; the whole selection), drag onto a sidebar folder in the Library
- [x] Paste target check (UI Automation: refuses password fields and changed targets; reads only identity and capabilities)
- [x] Live acceptance: drag-and-drop targets and pasting into real apps tried by hand (0.1.0 test round; fixes in 0.1.1)

## Phase 5 — Image markup and crop
- [x] Pen, highlighter, rectangle, ellipse, arrow, line, text (typed in place, double-click to edit); select / move / resize; 8 colors, 4 stroke widths, fill, 4 text sizes; `Shift` for straight lines / squares
- [x] Non-destructive crop (handles, moving, reset; `C` opens straight into crop), 60 steps of undo / redo, `Ctrl+S`, `Ctrl+Enter` / `Esc` save and finish, marks stored as editable vectors, full-resolution PNG
- [x] In the preview, `Ctrl+Z` / `Ctrl+Shift+Z` step through this session's edits; zoom (100–400%) and `Space`+drag panning
- [x] Live acceptance: end-to-end markup with mouse and keyboard tried by hand (style panel fixed in 0.1.1)

## Phase 6 — Capture sources and OCR
- [x] Screen capture (`Ctrl+Alt+A`, tray): the display under the pointer, full resolution, opens straight into crop
- [x] The Windows Screenshots folder (even when relocated) and up to three watched folders (Settings › Sources); a screenshot that also lands on the clipboard becomes one record
- [x] Local OCR (Windows.Media.Ocr; English or the profile languages when Turkish is unavailable), background queue, edited images are read again, copy image text (`Ctrl+Shift+C`, also hung on the Texts shelf), OCR text in shelf and Library search
- [x] New copies bring the shelves down for a moment (never in front of a full-screen app or game unless allowed)
- [x] Live acceptance: `Ctrl+Alt+A`, OCR and watched folders tried by hand

## Phase 7 — Visual search
- [x] MobileCLIP-S2 ONNX (Microsoft.ML.OnnxRuntime, CPU): pinned export (size + SHA-256 per file), CLIP BPE tokenizer, the reference letterbox + tiling policy and match thresholds; checks: red circle / blue square ranking, cosines within ±0.01 of the reference
- [x] Background indexing: one core, low priority, waits while a full-screen app is in front, unloads the model when idle; ~80 ms per image; vectors of edited / deleted images are dropped, stale results are never shown
- [x] Combined / text-only / visual-only search in the Library (ranked within the scope, text matches first under "Best match"), filter panel: date, domain, tags, pinned; kind and folder from the existing chips / sidebar
- [x] Live acceptance: visual search in a real Library tried by hand (filter panel fixed in 0.1.1)

## Phase 8 — Links and recordings
- [x] Link enrichment: title, site, author, thumbnail (Open Graph / `<title>`, YouTube oEmbed); public HTTPS only, every resolved address and redirect is checked again (no DNS rebinding), size / time limits, links carrying tokens, signatures or credentials are never fetched; at most 2 requests at once, failures retried after an hour; "Refresh link preview"; can be turned off
- [x] MP4/MOV recordings: copy from Explorer, drop on the Library, the Snipping Tool's "Screen Recordings" folder and watched folders (waits until writing finishes); poster frame and length; playback in the preview (`K`, seek bar)
- [x] Trim video (Windows.Media.Editing): the original never changes, the trimmed part is a new file; `I` / `O` set start / end, "Use the whole video" goes back; copy / drag / export hand over the trimmed video under its name
- [x] "View recent copies" button when My library is empty
- [x] Live acceptance: playback and the trim window tried by hand with a real screen recording

## Phase 9 — Settings and personalization
- [x] Settings window (`Ctrl+,`, tray, Actions palette, the button on the shelves): General, History, Sources, Keyboard shortcuts, Updates, About; every control writes through the same settings path as the tray, and opening the window writes nothing
- [x] Shortcut recorder: global shortcuts are released while recording, Ctrl/Alt/Win required, warnings for clashes with each other and with other apps in Windows; navigation shortcuts cannot clash within one place (shelves / Library / preview / markup / text editing); turn off, back to default, reset all; a ready-made `Ctrl+Alt+L` button for the Library
- [x] Soft colors / neutral glass and card size apply to the shelves at once; visible shelves (at least one stays on); sounds (short tones generated in code for hanging and taking down); start at sign-in (the user's Run key, read back from Windows, installed app only)
- [x] Bringing the shelves down: when the pointer rests at the top edge for 350 ms (off by default; never while a button is held or with the taskbar at the top), a 2.5 s peek when something new hangs (does not take the keyboard, stays while the pointer is over it, a click turns it into ordinary shelves); over full-screen apps only when allowed
- [x] 9 languages (en, tr, es, fr, pt-BR, az, zh-Hans, ja, ko) and the system language (pt → pt-BR, zh-CN/SG → zh-Hans); every string in every language, placeholders checked by tests; changing the language offers "Reopen Pegline"
- [x] Live acceptance: Settings window, shortcut recorder and top-edge reveal tried by hand

## Phase 10 — Distribution
- [x] Installer (Velopack): `PeglineApp-win-Setup.exe`, portable zip, full and delta update packages; no administrator rights; the install folder (`PeglineApp`) is separate from the Library folder (`Pegline`), uninstalling never touches the Library and removes the sign-in entry
- [x] Updates from GitHub Releases: a check at launch and every hour (can be turned off), notification and tray menu, download and install only when the user chooses, open edits saved before installing; a DPAPI-encrypted GitHub token for the private repository; "Check for updates" / "Install update" in the Actions palette
- [x] Release workflow on `v*` tags (tests, models, delta, publish); local `scripts/pack.ps1`
- [x] Logo: app / installer icon (dark rounded tile), transparent tray icon, logo in Settings and on the installer splash
- [ ] Code-signing certificate (unsigned for now; SmartScreen warning)
- [x] Live acceptance: install and uninstall tried (0.1.0); in-app updates 0.1.0 → 0.1.1 → 0.1.2 confirmed

## 0.1.1 — Fixes from the first hands-on round
- [x] Crisp text on the shelves: the shadow is drawn apart from the card's content, text and link cards hang straight (only images hang askew)
- [x] Cards fit the screen width (no more nine-card limit), follow the card size and spread evenly along the rope; rows share the same columns
- [x] A row shown again resizes the window once more after layout (no half-hidden row)
- [x] `Ctrl`+click selects cards that are not next to each other and of different kinds (image + link) together
- [x] Text / link cards dragged with `Ctrl` held land in Explorer as .txt / .url files (Explorer read `Alt` as "create shortcut" and refused the drop)
- [x] Markup style panel: stroke width, fill and text size in one panel that stays open (the old menu closed at once on a focus change)
- [x] Copy image text also hangs the text on the Texts shelf
- [x] The Library filter panel stays in place when the toolbar refreshes; the chosen date / option chip uses the accent color
- [x] "When something new is copied": bring the shelves down for every copy (default) / for images only / never; the old on-off setting is carried over once
- [x] Library and Settings buttons at the top right of the shelves; the logo in Settings is scaled in high quality; the Updates texts are fully translated

## 0.1.2 — License
- [x] Pegline © 2026 Enes Sarı, all rights reserved (`LICENSE`, About, file properties)
- [x] Test files copied from another project removed; the same behaviors are covered by Pegline's own tests (shelves, Library keyboard, capture rules, history cleanup)
- [x] Third-party notices list only the components the app actually uses
- [x] The visual search models are downloaded from this repository's own `models-mobileclip-s2-v1` release

## 0.1.3 — Open source
- [x] MIT License, © 2026 Enes Sarı (`LICENSE`, About)
- [x] Code signing policy with team roles and privacy policy (`CODE_SIGNING_POLICY.md`), Code of Conduct (Contributor Covenant 2.1)
- [x] Public repository
- [~] Free code signing for open source projects: application sent (2026-10-29), waiting for the answer; then signed releases

## 0.1.4 — Smoother shelves
- [x] The shelves are prepared off-screen while Pegline starts: the first opening no longer pauses (about 350 ms → 35 ms)
- [x] The slide starts once the panel's first frame is drawn and moves the panel as one cached picture, so ropes, pegs and card shadows are not redrawn for every frame
- [x] The window is resized only when its size really changes, never in the middle of a slide
- [x] Repository renamed to `pegline-clipboard-manager`; update checks and model downloads use the new address
