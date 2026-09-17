# Markdown core attribution

## Vex

Source: https://github.com/dotnet9/Vex

Reviewed commit: `f310641b21e70d6abb4bf9dab00632ee3b953564`.

MIT license: `LICENSE` in this directory. Copyright (c) 2026 码坊 CodeWF.

The following Mac Explorer files adapt the editor core, not the Vex application shell:

- `Services/Markdown/MarkdownSmartNewLine.cs` adapts `src/Vex/Modules/Workspace/Services/MarkdownSmartNewLine.cs`.
- `Services/Markdown/MarkdownEditing.cs` adapts the formatting, selection, link/image, and table operations in `MarkdownEditorMutationService.cs` and the action mappings in `MarkdownEditorActionService.cs` from the same Vex directory.

Local changes use AvaloniaEdit document mutations to preserve undo/redo, handle empty documents and offset zero, retain line delimiters, avoid ordered-list overflow, and run continuation through the editor indentation interface rather than intercepting IME Enter. The surrounding window, storage, conflict checking, search/replace, and host integration are specific to Mac Explorer.

Vex export/publishing, workspace/file management, settings, dependency-injection modules, and application theme were not copied.

## Shared renderer and editor dependencies

Renderer: https://github.com/dotnet9/CodeWF.Markdown

Reviewed compatible renderer commit: `e29c2a881262dce04b7f5f397de9d6fc1a8cf970`.
Its `Directory.Build.props` declares `12.0.4.3`; its `Directory.Packages.props` uses Avalonia `12.0.4` and AvaloniaEdit `12.0.0`.

Mac Explorer references `CodeWF.Markdown` and `CodeWF.Markdown.Themes` `12.0.4.3` and `Avalonia.AvaloniaEdit` `12.0.0`. Renderer source and Vex binaries are not vendored. These packages retain their upstream licenses and transitive dependencies. The host remains on .NET 10 / Avalonia 12.0.4.

AvaloniaEdit source: https://github.com/AvaloniaUI/AvaloniaEdit

## Icons

Source: https://github.com/microsoft/fluentui-system-icons

The Markdown editor toolbar, its view-mode switch, and the file-list "编辑" action use Fluent UI System Icons path data kept in `Assets/Icons.cs`. The Edit path came from `assets/Edit/SVG/ic_fluent_edit_24_regular.svg` (reviewed blob `a56089d881a48e12d86f30ecebe9f4dfd82c676b`).
Copyright (c) 2020 Microsoft Corporation. MIT license: `FluentIcons.LICENSE.txt`.
