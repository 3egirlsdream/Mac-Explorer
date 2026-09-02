# Finder interaction and preview fixes

This source package keeps the existing Avalonia UI and service architecture while adding:

- Browser/Finder-style tabs above the file toolbar.
  - `+` or Command-T opens a new tab at the current local folder.
  - Each tab has its own path, back/forward history, search results, view state and selection.
  - Clicking a tab switches without rebuilding its file-list model; Command-W closes the
    active tab (or the window when only one tab remains).
  - Control-Tab and Control-Shift-Tab cycle through tabs in either direction.
  - Closing a tab disposes its scoped background work and event subscriptions.
- A layout picker beside the new-tab button provides 12 simultaneous-pane layouts.
  - Supports one to four panes: horizontal/vertical splits, rows, columns, a 2×2 grid,
    and large-primary-pane arrangements with two or three secondary panes.
  - Every pane is backed by an existing tab, so it keeps independent location,
    navigation history, search, view mode and selection state.
  - Clicking anywhere in a pane makes it active; the main sidebar, toolbar, search,
    status bar and info panel then operate on that pane.
  - Choosing a layout with more panes automatically creates the missing tabs at the
    current local folder. Closing tabs automatically reduces the layout when needed.
- Rubber-band selection from empty file-list space in list, grouped-list and grid views.
  - Plain drag replaces selection.
  - Command/Control drag toggles entries relative to the selection at mouse-down.
  - Shift drag adds entries to the existing selection.
  - Edge dragging scrolls the active file view.
  - Dragging from a file entry still uses the existing `MacNativeFileDrag` / Avalonia file-drag path.
- Folder drops onto the pinned-folders area in the sidebar.
  - Only local folders are accepted.
  - Duplicate pins are ignored by the existing persistent service.
  - Dropping on an existing pin reorders it and persists `sort_order` transactionally.
- Quick Look thumbnails for PDF, text, Markdown, source, web, data and Office/iWork/OpenDocument files.
  - The UI now asks the existing `IThumbnailService` for every eligible local file instead of images only.
  - Existing memory/disk caching and macOS `qlmanage` generation are unchanged.
  - A bundled `QuickLookThumbnailing` helper is now the primary generator; `qlmanage`
    remains as fallback. This avoids failures caused by launching `qlmanage` from some
    macOS/Xcode environments.

## Runtime interaction corrections

- File-list marquee handlers are registered with `handledEventsToo`, because Avalonia's
  inner `ListBox` consumes pointer events over its empty background.
- Marquee geometry is tracked in scroll-content coordinates, so selection does not
  drift when the view scrolls. Bounds for virtualized entries are retained while the
  gesture is active, allowing the rectangle to grow and shrink predictably.
- A 60 Hz edge-scroll timer continues scrolling while the pointer is held near the
  top or bottom edge, with speed based on pointer distance from the edge.
- The full press/move/release gesture is observed at the `FileListView` root using
  tunnel routing. This prevents macOS pointer capture from turning an intended drag
  into a plain background click after the inner control reroutes move events.
- Selection hit testing no longer depends exclusively on materialized item templates.
  Uniform logical bounds are generated for virtualized list, grouped-list and icon-grid
  items, then replaced by measured bounds as controls appear. Automated mouse tests
  cover both list and grid empty-area drags and assert that multiple files are selected.
- Icon-grid cards now have a real 20-point horizontal gap. The transparent part of an
  outer list cell is treated as Finder canvas rather than as the file itself, so a drag
  beginning between icons starts marquee selection instead of becoming a click.
- Grid clicking and selection highlighting are limited to the 72-point icon tile and
  the compact filename label, matching Finder more closely; the rest of the 120-point
  cell remains marquee canvas. Photoshop PSD/PSB files are routed through Quick Look.
- The grid ListBox container itself never paints hover, selected or focus backgrounds;
  this removes Avalonia's second full-cell blue highlight behind the compact targets.
- In list view, only the visible icon and column text are item drag/click targets.
  Empty space within any row is canvas, so marquee selection can begin even when rows
  fill the viewport; the existing full-row selection appearance is unchanged.
- List marquee hit testing uses the separate visible hit regions (icon, name, date,
  size and type) instead of a full-row geometry surface. In list view, blank space
  inside a row is a canvas but the row's vertical center is still selectable, so a
  drag can begin at the arrow-column whitespace and sweep down across rows. Virtualized list/grid fallback
  bounds are kept in the same scroll-content coordinate system and rebuilt as the
  scroll offset changes. List fallback rows derive their pitch from realized
  ListBoxItem positions (rather than the shorter text hit-target height), eliminating
  cumulative vertical drift near the bottom of large folders and selection of
  neighboring rows. An item is selected only when the center of a visible region is
  inside the marquee, so grazing an adjacent file at an edge does not select it.
- During a marquee gesture, delayed `SelectionChanged` events from nested virtualized
  ListBoxes are ignored. The final marquee result is synchronized once on release,
  so an earlier selection cannot be added back to a new ordinary (non-Cmd/Shift)
  selection.
- Right-clicking an entry that is already part of a marquee multi-selection now keeps
  the complete selection for the context menu. Right-clicking an unselected entry
  still changes the context target to that entry, while a background right-click
  clears the selection as in Finder. A selection snapshot/guard remains active until
  the next real pointer interaction, covering deferred `ListBox.SelectionChanged`
  callbacks that otherwise cleared list-mode multi-selection after mouse-up.
- Marquee bounds are collected only from the currently visible presentation host.
  Avalonia keeps recycled list and grid containers in the visual tree during a
  view-mode switch; hidden containers are excluded so a list-to-grid change cannot
  make the icon marquee select stale rows (or vice versa).
- Inline rename continues to work for the clipped list name target as well as the
  grid name target; the editor replaces either a panel child or a decorator child.
- The Quick Look thumbnail helper is now a UI-free ImageIO process and no longer
  imports or initializes AppKit. Large folders can generate previews without helper
  launches repeatedly registering as foreground/Dock application activity.
- Local folders now use the generic folder artwork supplied by the installed macOS
  CoreTypes bundle—the same system artwork Finder uses. The former custom yellow SVG
  remains only as a non-macOS or missing-resource fallback.
- Hidden macOS migration, Time Machine and APFS support mounts (including
  `.migration-timemachine`) are filtered out of sidebar Locations. No volume is
  deleted or unmounted; normal user-named external and backup drives remain visible.
- Long list-view filenames are clipped and ellipsized inside the effective Name
  column. Their pointer hit area is constrained with the same live column width, so
  it cannot invisibly cover the Date column and steal a marquee start gesture.
- The whole sidebar is now a valid folder pin drop surface instead of only the tiny
  empty pinned-items control. Existing pinned rows remain reorder targets.
- The macOS Quick Look helper has been run against real TXT and PDF inputs and produced
  valid 512×512 PNG thumbnails for both.
- The 12-choice pane layout picker now follows the compact Finder-style reference:
  a four-column by three-row card grid, proper vector pane diagrams, complete Chinese
  labels, theme-aware colors, hover feedback and a blue current-layout indicator.
- Global Quick Search is available from **⌘K**, **⌘⇧F**, or a quick **D, D** outside
  text inputs. It searches the app-wide FTS index, sidebar favourites and recent
  folders without running a new disk crawl for each keystroke; use arrow keys and
  Return to reveal a file or enter a folder in the active tab. Global search now
  reuses the complete Mac Explorer search pipeline, including indexed OCR text from
  images and AI-recognized text tags. Return is handled at
  the window level while the search palette is visible, so it also works when the
  query text box has keyboard focus.
- The global search range is saved as **Current Folder** or **This Mac**. It also
  supports **Custom Folders**, where multiple user-selected
  directories are persisted and searched together. On macOS, the native folder
  picker allows multiple folders in one invocation, and the adjacent **＋** button
  can add more later without replacing the existing list. Selecting a file in the result
  list requests the existing Quick Look thumbnail service and shows a side preview;
  its small, medium and large sizes are also saved between launches.
- Search locations are now maintained in **设置 → 位置**, matching the Finder-style
  locations panel. There are no implicit locations now: only folders explicitly
  selected by the user are listed and searched. Extra folders can be added with
  **＋** and removed with **−**. Paths automatically inserted by an earlier build
  are removed during migration, while user-selected folders are preserved.
- Appending a new location now materializes the existing list before persistence,
  so adding a second or later folder no longer replaces the previous one. Profiles
  created by earlier builds keep their user folders without adding default paths.
- Global search applies the same visibility settings as file views. When hidden
  system files, dot files or dot folders are enabled, matching entries are removed
  from direct, indexed, recursive and AI-tag search results; ordinary files inside a
  hidden folder are also excluded.
- Selecting a folder in global search now fills the preview pane with its immediate
  children (folders first, then name order), while respecting the same hidden-file
  settings. Files in that list request the existing thumbnail service on demand,
  so PDF, TXT, Markdown and Office items can show thumbnails while folders retain
  system icons. A single click selects and previews; Return or double-click opens
  the selected result through the normal launcher path, including PDFs.
- Global search is index-first: after the local FTS/OCR/AI query succeeds it does
  not recursively walk the entire startup disk for every keystroke. The original
  recursive fallback remains available to ordinary in-folder search when its index
  is unavailable.
- Space now opens an in-window **Super Preview** for the active selection. A
  selected folder or archive shows its immediate contents without opening the
  folder or extracting the archive. Double-clicking a folder, archive, or nested
  archive entry pushes a preview breadcrumb and continues browsing; files inside
  archives are extracted only to a private temporary preview directory. Images,
  PDF, PSD/PSB, Office documents, text and common video files reuse the existing
  thumbnail/Quick Look pipeline, and Escape or Space closes the preview without
  changing the active tab's navigation.

## Build

The macOS entry point waits for an active CoreVideo display link before starting
Avalonia. This avoids the known `CVDisplayLink` `-6661` startup abort that can occur
when launching immediately after display sleep/wake or while Xcode is reconfiguring
the display session.

The repository pins .NET SDK 10.0.201 in `global.json`.

### Build from Xcode

Open `MacExplorer.xcodeproj`, select the shared `MacExplorer` scheme and choose
**Product > Build** (or press Command-B). Xcode's Debug/Release selection is passed
to the existing .NET/macOS bundle pipeline. If .NET 10.0.201 is not installed, the
first build downloads it into the source folder's `.dotnet` directory automatically.
After a successful Debug build, **Product > Run** launches the generated app.

```sh
dotnet restore
dotnet build -c Debug
```

On macOS, the build creates:

```text
bin/Debug/net10.0/osx-arm64/Mac Explorer.app
```

If Command Line Tools and the active macOS SDK do not match, select the full Xcode toolchain first:

```sh
export DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer
dotnet build -c Debug
```

## Verification performed for this package

- `dotnet restore`: passed.
- macOS arm64 Debug build: passed, including Swift helper, Objective-C++ native drag library, ad-hoc signing and `.app` bundle creation.
- Xcode Debug build from `MacExplorer.xcodeproj`: passed and produced a runnable app
  with a valid `CFBundleExecutable` and ad-hoc signature.
- The test project compiles successfully with 0 errors. Running the complete test
  runner is environment-dependent; in the restricted verification sandbox its local
  IPC pipe is denied, so execution could not be completed there.

The upstream dependency audit reports existing advisories for DotNetZip 1.16.0, SSH.NET 2025.0.0 and transitive System.Drawing.Common 4.7.0. They were not changed as part of this compatibility-focused fix.
