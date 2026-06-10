using System.Windows;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using ICSharpCode.AvalonEdit.Rendering;

namespace MajdataEdit;

/// <summary>
/// 自定义行距生成器 — 在每行末尾注入一个 TextEmbeddedObject，
/// 通过 TextEmbeddedObjectMetrics 直接控制元素对行高的贡献。
/// 元素放在行尾：自动换行（WordWrap）产生的折行不会包含此元素，
/// 只有每个逻辑行的最后一个视觉行获得额外高度，效果即为仅在行间加间距。
/// </summary>
public class LineSpacingGenerator : VisualLineElementGenerator
{
    private readonly double _multiplier;

    public LineSpacingGenerator(double multiplier = 1.0)
    {
        _multiplier = multiplier >= 1.0 ? multiplier : 1.0;
    }

    public override int GetFirstInterestedOffset(int startOffset)
    {
        var line = CurrentContext.VisualLine.LastDocumentLine;
        int lineEnd = line.Offset + line.Length;
        // 在行尾注注入元素：仅每个逻辑行末尾有一个 spacer，
        // wrap 产生的折行不包含 spacer，行间距只出现在真实行之间。
        return startOffset <= lineEnd ? lineEnd : -1;
    }

    public override VisualLineElement ConstructElement(int offset)
    {
        return new SpacerElement(_multiplier);
    }

    private sealed class SpacerElement : VisualLineElement
    {
        private readonly double _multiplier;

        public SpacerElement(double multiplier) : base(visualLength: 1, documentLength: 0)
        {
            _multiplier = multiplier;
        }

        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
        {
            return new SpacerTextRun(this, _multiplier);
        }

        public override bool CanSplit => false;
    }

    private sealed class SpacerTextRun : TextEmbeddedObject
    {
        private readonly SpacerElement _element;
        private readonly double _multiplier;

        public SpacerTextRun(SpacerElement element, double multiplier)
        {
            _element = element;
            _multiplier = multiplier;
        }

        public override LineBreakCondition BreakBefore => LineBreakCondition.BreakDesired;
        public override LineBreakCondition BreakAfter => LineBreakCondition.BreakDesired;
        public override bool HasFixedSize => true;
        public override CharacterBufferReference CharacterBufferReference => new CharacterBufferReference();
        public override int Length => _element.VisualLength;
        public override TextRunProperties Properties => _element.TextRunProperties;

        public override TextEmbeddedObjectMetrics Format(double remainingParagraphWidth)
        {
            var props = _element.TextRunProperties;
            double fontSize = props.FontRenderingEmSize;
            double lineHeight = props.Typeface.FontFamily.LineSpacing * fontSize;
            double baseline = props.Typeface.FontFamily.Baseline * fontSize;
            return new TextEmbeddedObjectMetrics(
                width: 0,
                height: lineHeight * _multiplier,
                baseline: baseline);
        }

        public override Rect ComputeBoundingBox(bool rightToLeft, bool sideways)
        {
            return new Rect(0, 0, 0, 0);
        }

        public override void Draw(DrawingContext drawingContext, Point origin, bool rightToLeft, bool sideways)
        {
            // 不可见元素，不做绘制
        }
    }
}
