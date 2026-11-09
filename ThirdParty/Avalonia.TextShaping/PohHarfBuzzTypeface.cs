// Adapted from Avalonia 12.0.4. Copyright (c) AvaloniaUI OÜ. See LICENSE.
using Avalonia.Media;
using HarfBuzzSharp;

namespace MacExplorer.TextShaping
{
    internal sealed class PohHarfBuzzTypeface : ITextShaperTypeface
    {
        public PohHarfBuzzTypeface(GlyphTypeface glyphTypeface)
        {
            GlyphTypeface = glyphTypeface;

            HBFace = new Face(GetTable) { UnitsPerEm = glyphTypeface.Metrics.DesignEmHeight };

            HBFont = new Font(HBFace);

            HBFont.SetFunctionsOpenType();
        }

        public GlyphTypeface GlyphTypeface { get; }
        public Face HBFace { get; }
        public Font HBFont { get; }

        private Blob? GetTable(Face face, Tag tag)
        {
            if (!GlyphTypeface.PlatformTypeface.TryGetTable((uint)tag, out var table))
            {
                return null;
            }

            return PohFontTable.Create(table.Span);
        }

        public void Dispose()
        {
            HBFont.Dispose();
            HBFace.Dispose();
        }

    }
}
