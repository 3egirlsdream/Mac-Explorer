# Avalonia 12.0.4 font-table allocation backport

`PohHarfBuzzTextShaper.cs` and `PohHarfBuzzTypeface.cs` derive from AvaloniaUI
12.0.4, commit `a8dd6417fd8918570edefdbecd92d16ac7620069`, under the MIT license
in `LICENSE`:

- https://github.com/AvaloniaUI/Avalonia/blob/12.0.4/src/HarfBuzz/Avalonia.HarfBuzz/HarfBuzzTextShaper.cs
- https://github.com/AvaloniaUI/Avalonia/blob/12.0.4/src/HarfBuzz/Avalonia.HarfBuzz/HarfBuzzTypeface.cs

The shaping algorithm is preserved. Names are scoped to Mac Explorer, an unused
local is removed, and font-table Blob creation uses `PohFontTable`: copy the
table once into .NET's pinned object heap, retain it for the Blob lifetime, and
release its GC handle when HarfBuzz releases the Blob. This avoids long-lived
pins in the movable small object heap. Table creation failure also releases the
handle. No font-table bytes, file paths, or text are logged.

The adapter project alone references the implementation of Avalonia.Base to
implement the backend interfaces hidden by its reference assembly. Both the
package dependencies and runtime installation are pinned to 12.0.4. On macOS,
`App.Initialize` installs the adapter before XAML is loaded, when the current
backend is the matching original HarfBuzz implementation. Other backends and
versions are left intact and the numeric installation result is logged.

On an Avalonia upgrade, compare the two upstream source files and backend
interfaces, remove this backport if the allocation issue is fixed upstream, and
rerun the native shaping equivalence and real-directory memory regressions.
