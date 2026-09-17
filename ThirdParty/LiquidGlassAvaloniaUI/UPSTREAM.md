# LiquidGlassAvaloniaUI

- Source: https://github.com/KaranocaVe/LiquidGlassAvaloniaUI
- Commit: `cde864d6efebc5b32484eb055900308ef4f65754`
- License: MIT (see `LICENSE`)

The library sources and shaders are vendored because no LiquidGlassAvaloniaUI
package was available from NuGet when integrated. Demo projects are excluded.
The project file aligns Avalonia / Avalonia.Skia to 12.0.4 and SkiaSharp to
3.119.4, matching FKFinder, and packs the local copy of the upstream README.
Shaders are unchanged. The local backdrop provider refreshes snapshots in the same
frame when a subscribing surface changes bounds, theme, or render scale, preventing an
expanding sidebar from briefly sampling its previous layout. Background-only
updates retain the upstream capture cadence.

FKFinder uses `LiquidGlassSurface` for `SidebarSurface` in
`Views/ExplorerWorkspaceView.axaml`. The approved light appearance captures a
low-saturation soft-light backdrop; dark mode uses a neutral translucent surface
with lens refraction, edge highlights, and shadows disabled. The shared parameters
and backdrop are in `Controls/SidebarAppearance.cs` and are also linked by the Demo.
Sidebar action buttons use `LiquidGlassInteractiveSurface` around a transparent
content presenter, following the upstream demo's liquid-button composition.
Hover and keyboard-focus styling is scoped to `Views/FinderSidebarView.axaml`;
the upstream package does not provide a separate application interaction theme.
Full-window overlays (global search, super preview, modal scrim, dialogs, and the
Markdown editor) set `LiquidGlassBackdrop.IsExcludedFromCapture`: they paint above
the glass, and background-only changes use the throttled capture cadence, so without
the exclusion a closing overlay would replay its content inside the sidebar pane for
a frame. No application-wide theme or renderer defaults are installed.
