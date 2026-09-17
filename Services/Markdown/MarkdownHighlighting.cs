using System.Xml;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;

namespace MacExplorer.Services.Markdown;

internal static class MarkdownHighlighting
{
    public static IHighlightingDefinition Create(bool dark)
    {
        var accent = dark ? "#82AAFF" : "#2457A7";
        var code = dark ? "#A6D5A8" : "#356B3A";
        var muted = dark ? "#B0B6C2" : "#677184";
        var xml = $$"""
            <SyntaxDefinition name="Markdown" extensions=".md" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
              <Color name="Heading" foreground="{{accent}}" fontWeight="bold"/>
              <Color name="Code" foreground="{{code}}"/>
              <Color name="Link" foreground="{{accent}}"/>
              <Color name="Quote" foreground="{{muted}}"/>
              <Color name="Strong" fontWeight="bold"/>
              <Color name="Emphasis" fontStyle="italic"/>
              <RuleSet>
                <Span color="Code" multiline="true"><Begin>^\s*```.*$</Begin><End>^\s*```\s*$</End></Span>
                <Span color="Code" multiline="true"><Begin>^\s*~~~.*$</Begin><End>^\s*~~~\s*$</End></Span>
                <Rule color="Heading">^\s{0,3}\#{1,6}\s.*$</Rule>
                <Rule color="Quote">^\s*&gt;.*$</Rule>
                <Span color="Code"><Begin>`</Begin><End>`</End></Span>
                <Rule color="Link">!?\[[^\]]*\]\([^)]+\)</Rule>
                <Rule color="Strong">\*\*[^*]+\*\*|__[^_]+__</Rule>
                <Rule color="Emphasis">\*[^*]+\*|_[^_]+_</Rule>
              </RuleSet>
            </SyntaxDefinition>
            """;
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }
}
