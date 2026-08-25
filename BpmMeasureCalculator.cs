using System.Globalization;
using System.Numerics;

namespace MajdataEdit;

/// <summary>
/// BPM 拍号计算器
/// 给定 inote 文本，从谱面开头按音符时值累积小节数；在每个 (BPM) 变化点记录该处拍号。
/// 算法用 BigInteger 分数精确计算，避免 {192} 等大除数 + 大量逗号累积导致整数溢出。
/// 本类为纯逻辑，无 UI 依赖；由编辑器 hover 处理器调用。
/// </summary>
public static class BpmMeasureCalculator
{
    /// <summary>
    /// 一个 BPM 变化点：字符跨度 [Start, End] 含括号两端（End 为 ')' 的索引），及该处累积拍号。
    /// </summary>
    public readonly struct BpmHit
    {
        public readonly int Start;   // '(' 的索引
        public readonly int End;     // ')' 的索引（含）
        public readonly Fraction Measure;

        public BpmHit(int start, int end, Fraction measure)
        {
            Start = start;
            End = end;
            Measure = measure;
        }
    }

    /// <summary>
    /// 精确分数。归约后 Den > 0 且 gcd(|Num|, Den) == 1。
    /// </summary>
    public readonly struct Fraction
    {
        public readonly BigInteger Num;
        public readonly BigInteger Den;

        public Fraction(BigInteger num, BigInteger den)
        {
            if (den == 0) den = 1; // 防御：理论上 currentDiv 不会为 0
            if (den < 0) { num = -num; den = -den; }
            var g = BigInteger.GreatestCommonDivisor(BigInteger.Abs(num), den);
            if (g > 1) { num /= g; den /= g; }
            Num = num;
            Den = den;
        }

        public static Fraction Zero => new(0, 1);

        public static Fraction operator +(in Fraction a, in Fraction b)
            => new(a.Num * b.Den + b.Num * a.Den, a.Den * b.Den);

        public BigInteger Whole => Num >= 0 ? Num / Den : BigInteger.Zero; // 谱面位置非负，取整除

        public bool IsZero => Num.IsZero;
    }

    /// <summary>
    /// 把累积小节数格式化为拍号字符串
    /// 整数或分母为 1/2/4 -> 小数 (8.0 / 3.25 / 3.5 / 3.75)；其它 -> 带分数 (3 + 1/8)。
    /// </summary>
    public static string FormatMeasure(Fraction pos)
    {
        var whole = pos.Whole;
        var frac = new Fraction(pos.Num - whole * pos.Den, pos.Den); // frac = pos - whole，非负

        if (frac.IsZero)
            return $"{whole}.0";

        if (frac.Den == 2 || frac.Den == 4)
        {
            // quarter = frac*4 的分子：1->"25", 2->"5", 3->"75"
            var quarter = (frac.Num * 4) / frac.Den;
            var map = quarter == 1 ? "25" : quarter == 2 ? "5" : quarter == 3 ? "75" : null;
            if (map != null)
                return $"{whole}.{map}";
        }

        return $"{whole} + {frac.Num}/{frac.Den}";
    }

    /// <summary>
    /// 判断括号内是否为 BPM 数值
    /// 排除 NaN/Infinity（非真实 BPM）。
    /// </summary>
    public static bool IsBpm(string inner)
    {
        if (string.IsNullOrWhiteSpace(inner)) return false;
        if (!double.TryParse(inner, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return false;
        return !double.IsNaN(v) && !double.IsInfinity(v);
    }

    /// <summary>
    /// 遍历整段 inote 文本，返回所有 (BPM) 变化点及其处累积拍号（含首个，首个拍号为 0）。
    /// 时值累积规则与 process_inote 完全一致：
    ///   ||     跳到行末，不计入时值
    ///   {N}    设置 currentDiv（默认 4）
    ///   ,      position += 1/currentDiv
    ///   (num)  记录拍号（position 不变）
    /// </summary>
    public static List<BpmHit> ComputeBpmMeasures(string text)
    {
        var hits = new List<BpmHit>();
        var position = Fraction.Zero;
        long currentDiv = 4;
        int i = 0;
        var n = text.Length;

        while (i < n)
        {
            var ch = text[i];

            // || ... 注释，到行末（不计入时值）
            if (ch == '|' && i + 1 < n && text[i + 1] == '|')
            {
                var lineEnd = text.IndexOf('\n', i);
                i = lineEnd == -1 ? n : lineEnd;
                continue;
            }

            // {N} 时值设置
            if (ch == '{')
            {
                var j = text.IndexOf('}', i);
                if (j != -1)
                {
                    var numStr = text.AsSpan(i + 1, j - i - 1).Trim();
                    if (long.TryParse(numStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dv) && dv != 0)
                        currentDiv = dv;
                    i = j + 1;
                    continue;
                }
            }

            // (BPM) 变化点
            if (ch == '(')
            {
                var j = text.IndexOf(')', i);
                if (j != -1)
                {
                    var inner = text.AsSpan(i + 1, j - i - 1).Trim().ToString();
                    if (IsBpm(inner))
                    {
                        hits.Add(new BpmHit(i, j, position));
                        i = j + 1;
                        continue;
                    }
                }
            }

            // 逗号 = 一个音符位置，累加 1/currentDiv 小节
            if (ch == ',')
            {
                position += new Fraction(1, currentDiv);
                i++;
                continue;
            }

            i++;
        }

        return hits;
    }

    public static Fraction ComputeMeasureAtOffset(string text, int offset)
    {
        var position = Fraction.Zero;
        long currentDiv = 4;
        var limit = Math.Clamp(offset, 0, text.Length);
        var i = 0;

        while (i < limit)
        {
            var ch = text[i];

            if (ch == '|' && i + 1 < text.Length && text[i + 1] == '|')
            {
                var lineEnd = text.IndexOf('\n', i);
                i = lineEnd == -1 ? limit : Math.Min(lineEnd, limit);
                continue;
            }

            if (ch == '{')
            {
                var j = text.IndexOf('}', i);
                if (j != -1 && j < limit)
                {
                    var numStr = text.AsSpan(i + 1, j - i - 1).Trim();
                    if (long.TryParse(numStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dv) && dv != 0)
                        currentDiv = dv;
                    i = j + 1;
                    continue;
                }
            }

            if (ch == '(')
            {
                var j = text.IndexOf(')', i);
                if (j != -1 && j < limit && IsBpm(text.AsSpan(i + 1, j - i - 1).Trim().ToString()))
                {
                    i = j + 1;
                    continue;
                }
            }

            if (ch == ',')
                position += new Fraction(1, currentDiv);

            i++;
        }

        return position;
    }

    /// <summary>
    /// 在文档 offset 处查找命中的 BPM 组：返回 [Start, End]（含两端括号）包含 offset 的 hit，否则 null。
    /// 用于鼠标 hover 定位光标是否在某个 (xxx) 上。
    /// </summary>
    public static BpmHit? FindBpmAt(string text, int offset)
    {
        if (offset < 0) return null;
        foreach (var hit in ComputeBpmMeasures(text))
            if (offset >= hit.Start && offset <= hit.End)
                return hit;
        return null;
    }
}
