using System.Text;
using System.Text.RegularExpressions;

namespace Precheck;

/// <summary>Wakumy 画面の文字列を扱う小道具。</summary>
public static class TextUtil
{
    private static readonly Regex HeaderDate = new(@"(\d{4})年(\d{1,2})月(\d{1,2})日", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    /// <summary>全角英数・空白を半角に寄せ（NFKC）、前後の空白を落とす。</summary>
    public static string Normalize(string? text) => (text ?? "").Normalize(NormalizationForm.FormKC).Trim();

    /// <summary>氏名の比較用。全角/半角スペースや連続スペースの違いを吸収する。</summary>
    public static string NormalizeName(string? text) => Spaces.Replace(Normalize(text), " ").Trim();

    /// <summary>"2026年10月16日(金)" → 2026-10-16。無ければ null。</summary>
    public static DateOnly? ParseHeaderDate(string? text)
    {
        var m = HeaderDate.Match(Normalize(text));
        if (!m.Success) return null;
        return new DateOnly(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
    }

    public static bool ContainsAny(string text, IEnumerable<string> keywords)
    {
        var t = Normalize(text);
        return keywords.Any(k => t.Contains(Normalize(k), StringComparison.Ordinal));
    }
}
