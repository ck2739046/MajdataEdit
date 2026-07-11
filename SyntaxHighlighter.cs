using System.Collections.Generic;
using System.Windows.Media;

namespace MajdataEdit;

public static class SyntaxHighlighter
{
    private static readonly SolidColorBrush DefaultBrush = new(Color.FromRgb(255,255,255));
    private static readonly SolidColorBrush CommentBrush = new(Color.FromRgb(106,153,85));
    private static readonly SolidColorBrush ParenBrush = new(Color.FromRgb(255,240,54));
    private static readonly SolidColorBrush BraceBrush = new(Color.FromRgb(218,112,214));
    private static readonly SolidColorBrush ModifierBrush = new(Color.FromRgb(216,129,100));
    private static readonly SolidColorBrush TapBrush = new(Color.FromRgb(156,220,254));
    private static readonly SolidColorBrush HoldBrush = new(Color.FromRgb(78,201,176));
    private static readonly SolidColorBrush SlideBrush = new(Color.FromRgb(220,220,170));
    private static readonly SolidColorBrush TouchBrush = new(Color.FromRgb(181,206,168));
    private static readonly SolidColorBrush PunctuationBrush = new(Color.FromRgb(255,255,255));

    public enum TokenType : byte
    {
        Default, Comment, ParenContent, BraceContent,
        NoteTap, NoteHold, NoteSlide, NoteTouch,
        Modifier, Punctuation
    }

    public struct Token
    {
        public int Start;
        public int Length;
        public TokenType Type;
        public Token(int start, int length, TokenType type)
        { Start = start; Length = length; Type = type; }
    }

    public static SolidColorBrush BrushForType(TokenType type) => type switch
    {
        TokenType.Comment => CommentBrush,
        TokenType.ParenContent => ParenBrush,
        TokenType.BraceContent => BraceBrush,
        TokenType.NoteTap => TapBrush,
        TokenType.NoteHold => HoldBrush,
        TokenType.NoteSlide => SlideBrush,
        TokenType.NoteTouch => TouchBrush,
        TokenType.Modifier => ModifierBrush,
        TokenType.Punctuation => PunctuationBrush,
        _ => DefaultBrush
    };

    public static List<Token> TokenizeLine(string line)
    {
        var result = new List<Token>();
        var len = line.Length;
        var i = 0;

        while (i < len)
        {
            var c = line[i];

            if (c == '|' && i + 1 < len && line[i + 1] == '|')
            {
                result.Add(new Token(i, len - i, TokenType.Comment));
                break;
            }

            if (c == '(')
            {
                result.Add(new Token(i, 1, TokenType.Punctuation));
                i++;
                var cs = i;
                while (i < len && line[i] != ')') i++;
                if (i > cs) result.Add(new Token(cs, i - cs, TokenType.ParenContent));
                if (i < len) { result.Add(new Token(i, 1, TokenType.Punctuation)); i++; }
                continue;
            }

            if (c == '{')
            {
                result.Add(new Token(i, 1, TokenType.Punctuation));
                i++;
                var cs = i;
                while (i < len && line[i] != '}') i++;
                if (i > cs) result.Add(new Token(cs, i - cs, TokenType.BraceContent));
                if (i < len) { result.Add(new Token(i, 1, TokenType.Punctuation)); i++; }
                continue;
            }

            if (c == ',' || c == ')' || c == '}')
            {
                result.Add(new Token(i, 1, TokenType.Punctuation));
                i++;
                continue;
            }

            if (IsNoteStart(c))
            {
                var ns = i;
                while (i < len && line[i] != ',')
                {
                    if (line[i] == '(' || line[i] == '{') break;
                    i++;
                }
                EmitNoteTokens(result, line, ns, i - ns);
                continue;
            }

            {
                var s = i;
                while (i < len && line[i] != ',' && line[i] != '(' && line[i] != '{' && line[i] != ')' && line[i] != '}' && !(line[i] == '|' && i + 1 < len && line[i + 1] == '|') && !IsNoteStart(line[i])) i++;
                result.Add(new Token(s, i - s, TokenType.Default));
            }
        }

        return result;
    }

    private static bool IsNoteStart(char c) => (c >= '1' && c <= '8') || (c >= 'A' && c <= 'E');
    private static bool IsSlideDirection(char c) => c is '-' or '^' or '<' or '>' or 'v' or 'V' or 'p' or 'q' or 's' or 'z' or 'w';
    private static bool IsModifier(char c) => c is 'b' or 'f' or 'x' or '$' or '!' or '?';
    private static bool IsNoteSeparator(char c) => c is '/' or '`';

    private static TokenType DetermineNoteType(string noteText)
    {
        var isTouch = noteText.Length > 0 && noteText[0] >= 'A' && noteText[0] <= 'E';
        if (isTouch || (noteText.Length > 0 && noteText[0] == 'C'))
            return TokenType.NoteTouch;
        foreach (var c in noteText) if (IsSlideDirection(c)) return TokenType.NoteSlide;
        if (noteText.Contains('h')) return TokenType.NoteHold;
        return TokenType.NoteTap;
    }

    private static void EmitNoteTokens(List<Token> result, string line, int start, int length)
    {
        // `/` 与 ` ` ` ` 是 simai 的「独立音符分隔符」(见 SimaiProcess 的 Split('/')/Split('`')),
        // 分隔符两侧是各自独立的音符,须分别按自身内容判色。
        var end = start + length;
        var segStart = start;

        for (var i = start; i < end; i++)
        {
            var ch = line[i];
            if (!IsNoteSeparator(ch)) continue;

            if (i > segStart) EmitNoteSegment(result, line, segStart, i - segStart);
            // 分隔符自身着色: `/` 维持 Punctuation, ` ` ` 维持 Modifier, 保持改动前外观。
            var sepType = ch == '/' ? TokenType.Punctuation : TokenType.Modifier;
            result.Add(new Token(i, 1, sepType));
            segStart = i + 1;
        }
        if (segStart < end) EmitNoteSegment(result, line, segStart, end - segStart);
    }

    private static void EmitNoteSegment(List<Token> result, string line, int start, int length)
    {
        // 一个段内不会再出现 `/` 或 ` ` ` `, 仅按本段内容判一次色;段内 `*`(同头滑条连接)与 modifier 仍逐字切分。
        var noteType = DetermineNoteType(line.Substring(start, length));
        var color = MapColor(noteType);
        var end = start + length;
        var runStart = start;

        for (var i = start; i < end; i++)
        {
            var ch = line[i];
            if (ch == '*')
            {
                if (i > runStart) result.Add(new Token(runStart, i - runStart, color));
                result.Add(new Token(i, 1, TokenType.Punctuation));
                runStart = i + 1;
                continue;
            }
            if (IsModifier(ch))
            {
                if (i > runStart) result.Add(new Token(runStart, i - runStart, color));
                result.Add(new Token(i, 1, TokenType.Modifier));
                runStart = i + 1;
                continue;
            }
        }
        if (runStart < end) result.Add(new Token(runStart, end - runStart, color));
    }

    private static TokenType MapColor(TokenType nt) => nt switch
    {
        TokenType.NoteTap or TokenType.NoteHold or TokenType.NoteSlide or TokenType.NoteTouch => nt,
        _ => TokenType.Default
    };
}
