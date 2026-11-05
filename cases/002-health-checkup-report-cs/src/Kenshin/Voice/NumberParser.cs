using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Kenshin.Voice;

/// <summary>
/// 音声認識の文字列から、数値と少数の命令語だけを取り出す（限定語彙）。
/// 「142 の 91」「ひゃくよんじゅうに」「百四十二」「いってんに」「なし」「プラス」「つぎ」など。
/// </summary>
public static class NumberParser
{
    public sealed record Parsed(string Text, List<double> Numbers, List<string> Commands, string Normalized);

    static readonly (string word, string cmd)[] CommandWords =
    {
        ("つぎ", "next"), ("次", "next"), ("もどる", "back"), ("戻る", "back"), ("けす", "clear"), ("消す", "clear"), ("クリア", "clear"), ("とりけし", "clear"), ("取り消し", "clear"),
        ("なし", "なし"), ("無し", "なし"), ("あり", "あり"), ("有り", "あり"),
        ("プラマイ", "±"), ("ぷらまい", "±"), ("プラスマイナス", "±"), ("プラス", "+"), ("ぷらす", "+"), ("マイナス", "−"), ("まいなす", "−"),
        ("せいじょう", "正常"), ("正常", "正常"), ("いんせい", "−"), ("陰性", "−"), ("ようせい", "+"), ("陽性", "+"),
        ("ふくよう", "服用"), ("服用", "服用"), ("ちょくせつ", "直接"), ("直接", "直接"), ("かんせつ", "間接"), ("間接", "間接"),
    };

    static readonly Dictionary<string, int> Digits = new()
    {
        ["れい"] = 0, ["ぜろ"] = 0, ["ゼロ"] = 0, ["零"] = 0, ["〇"] = 0, ["○"] = 0,
        ["いち"] = 1, ["いっ"] = 1, ["一"] = 1, ["壱"] = 1,
        ["に"] = 2, ["二"] = 2, ["さん"] = 3, ["三"] = 3, ["し"] = 4, ["よん"] = 4, ["よ"] = 4, ["四"] = 4,
        ["ご"] = 5, ["五"] = 5, ["ろく"] = 6, ["ろっ"] = 6, ["六"] = 6, ["なな"] = 7, ["しち"] = 7, ["七"] = 7,
        ["はち"] = 8, ["はっ"] = 8, ["八"] = 8, ["きゅう"] = 9, ["く"] = 9, ["九"] = 9,
    };
    static readonly Dictionary<string, int> Units = new()
    {
        ["じゅう"] = 10, ["じゅっ"] = 10, ["十"] = 10, ["ひゃく"] = 100, ["びゃく"] = 100, ["ぴゃく"] = 100, ["百"] = 100, ["せん"] = 1000, ["ぜん"] = 1000, ["千"] = 1000,
    };

    public static Parsed Parse(string text)
    {
        var t = (text ?? "").Trim();
        var norm = Normalize(t);
        var commands = new List<string>();
        foreach (var (word, cmd) in CommandWords.OrderByDescending(c => c.word.Length))
            if (norm.Contains(word)) { commands.Add(cmd); norm = norm.Replace(word, " "); }
        var numbers = new List<double>();
        // 1) アラビア数字
        var work = new StringBuilder();
        foreach (Match m in Regex.Matches(norm, @"\d+(?:[.．]\d+)?"))
            if (double.TryParse(m.Value.Replace('．', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) numbers.Add(v);
        var rest = Regex.Replace(norm, @"\d+(?:[.．]\d+)?", " ");
        // 2) 漢数字・かな数字（「てん」で小数）
        foreach (var chunk in Regex.Split(rest, @"[\s、,。の/／ノ]+"))
        {
            if (chunk.Length == 0) continue;
            var v = KanaNumber(chunk);
            if (v != null) numbers.Add(v.Value);
        }
        return new Parsed(t, numbers, commands.Distinct().ToList(), norm);
    }

    /// <summary>カタカナ→ひらがな、全角→半角、句読点整理。</summary>
    public static string Normalize(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in Domain.Num.ToHalfWidth(s))
        {
            if (ch >= 'ァ' && ch <= 'ヶ') sb.Append((char)(ch - 0x60)); // カタカナ → ひらがな
            else if (ch == 'ー' || ch == '〜') sb.Append(ch);
            else sb.Append(ch);
        }
        // 命令語はカタカナのまま照合したいものもあるので、両方用意する代わりに、ひらがな化した文字列で照合する
        return sb.ToString().Replace("ぷらすまいなす", "ぷらまい").Replace("くりあ", "クリア");
    }

    /// <summary>「ひゃくよんじゅうに」「百四十二」「いってんに」「さんてんご」→ 数値。読めなければ null。</summary>
    public static double? KanaNumber(string chunk)
    {
        var s = chunk.Replace("ー", "");
        if (s.Length == 0) return null;
        // 小数点
        var parts = Regex.Split(s, "てん|点|ポイント|ぽいんと");
        if (parts.Length > 2) return null;
        var ip = IntPart(parts[0]);
        if (ip == null && parts[0].Length > 0) return null;
        if (parts.Length == 1) return ip;
        var frac = parts[1];
        var fsb = new StringBuilder();
        var i = 0;
        while (i < frac.Length)
        {
            var matched = false;
            foreach (var (k, v) in Digits.OrderByDescending(d => d.Key.Length))
                if (frac.Substring(i).StartsWith(k)) { fsb.Append(v); i += k.Length; matched = true; break; }
            if (!matched) return null;
        }
        if (fsb.Length == 0) return ip;
        return double.Parse((ip ?? 0) + "." + fsb, CultureInfo.InvariantCulture);
    }

    static int? IntPart(string s)
    {
        if (s.Length == 0) return 0;
        var total = 0; var current = 0; var i = 0; var any = false;
        while (i < s.Length)
        {
            var matched = false;
            foreach (var (k, v) in Units.OrderByDescending(u => u.Key.Length))
            {
                if (s.Substring(i).StartsWith(k)) { total += (current == 0 ? 1 : current) * v; current = 0; i += k.Length; matched = true; any = true; break; }
            }
            if (matched) continue;
            foreach (var (k, v) in Digits.OrderByDescending(d => d.Key.Length))
            {
                if (s.Substring(i).StartsWith(k)) { current = current * 10 + v; i += k.Length; matched = true; any = true; break; }
            }
            if (!matched) return null;
        }
        return any ? total + current : null;
    }
}
