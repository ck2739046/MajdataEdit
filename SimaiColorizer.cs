using ICSharpCode.AvalonEdit.Rendering;

namespace MajdataEdit;

/// <summary>
/// AvalonEdit 语法高亮着色器 — 复用 SyntaxHighlighter.TokenizeLine。
/// 在渲染层着色，不触碰文字模型，撤销/光标天然正常。
/// </summary>
public class SimaiColorizer : DocumentColorizingTransformer
{
    protected override void ColorizeLine(ICSharpCode.AvalonEdit.Document.DocumentLine line)
    {
        var text = CurrentContext.Document.GetText(line.Offset, line.Length);
        var tokens = SyntaxHighlighter.TokenizeLine(text);

        foreach (var tok in tokens)
        {
            var startOffset = line.Offset + tok.Start;
            var endOffset = startOffset + tok.Length;
            var brush = SyntaxHighlighter.BrushForType(tok.Type);
            ChangeLinePart(startOffset, endOffset, element =>
            {
                element.TextRunProperties.SetForegroundBrush(brush);
            });
        }
    }
}
