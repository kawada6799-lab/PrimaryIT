using System.Text.Json;

namespace Kenshin.Config;

/// <summary>判定基準.json。数値の境界はすべてここ。</summary>
public sealed class Criteria
{
    public Dictionary<string, Dictionary<string, string>> SectionLabels { get; set; } = new();
    public BmiCriteria Bmi { get; set; } = new();
    public WaistCriteria Waist { get; set; } = new();
    public BloodPressureCriteria BloodPressure { get; set; } = new();
    public UrineCriteria Urine { get; set; } = new();
    public VisionCriteria Vision { get; set; } = new();
    public CodeOnly Hearing { get; set; } = new();
    public CodeOnly Stool { get; set; } = new();
    public LungCriteria LungFunction { get; set; } = new();
    public BloodCriteria Blood { get; set; } = new();
    public MetabolicCriteria Metabolic { get; set; } = new();
    public OverallCriteria Overall { get; set; } = new();

    public static Criteria Load(string configDir)
    {
        var path = Path.Combine(configDir, "判定基準.json");
        return Json.Load<Criteria>(path);
    }

    /// <summary>結果のお知らせに印字する区分名（計測の異常なし → 正常範囲 など）。</summary>
    public string Label(Masters masters, string section, string? code)
    {
        if (code == null) return "";
        if (SectionLabels.TryGetValue(section, out var map) && map.TryGetValue(code, out var label)) return label;
        return masters.CategoryName(code);
    }
}

public sealed class Tier { public double? Min { get; set; } public double? Max { get; set; } public string Code { get; set; } = ""; }
public sealed class BmiCriteria { public List<Tier> Tiers { get; set; } = new(); public double StandardWeightBmi { get; set; } = 22.0; }
public sealed class WaistCriteria { public double Male { get; set; } = 85; public double Female { get; set; } = 90; public string Code { get; set; } = "180"; }
public sealed class BpTier { public int? SysMax { get; set; } public int? DiaMax { get; set; } public string Code { get; set; } = ""; }
public sealed class BloodPressureCriteria { public string Use { get; set; } = "second"; public List<BpTier> Tiers { get; set; } = new(); public string OnMedicationCode { get; set; } = ""; }
public sealed class UrineCriteria
{
    public Dictionary<string, string> Sugar { get; set; } = new();
    public Dictionary<string, string> Protein { get; set; } = new();
    public Dictionary<string, string> Blood { get; set; } = new();
    public Dictionary<string, string> Urobilinogen { get; set; } = new();
}
public sealed class VisionCriteria { public double NormalMin { get; set; } = 0.7; public string Code { get; set; } = "160"; }
public sealed class CodeOnly { public string Code { get; set; } = "180"; }
public sealed class LungCriteria { public double Fev1PercentMin { get; set; } = 70; public double VcPercentMin { get; set; } = 80; public string Code { get; set; } = "180"; }
public sealed class SevereRule { public string Item { get; set; } = ""; public double? Min { get; set; } public double? Max { get; set; } public string Code { get; set; } = ""; }
public sealed class BloodCriteria
{
    public string OutOfRangeCode { get; set; } = "180";
    public List<SevereRule> Severe { get; set; } = new();
    public Dictionary<string, List<string>> Categories { get; set; } = new();
    public Dictionary<string, string> CategoryOverride { get; set; } = new();
    public List<string> JudgedCategories { get; set; } = new();

    public string CategoryOf(string itemName)
    {
        foreach (var (cat, keys) in Categories)
        {
            if (cat.StartsWith("_")) continue;
            foreach (var k in keys)
                if (ContainsKey(itemName, k)) return cat;
        }
        return "その他";
    }

    /// <summary>英字のキー（LD、AST など）は単語として一致させる（「LD」が「LDL」に当たらないように）。日本語のキーは部分一致。</summary>
    public static bool ContainsKey(string item, string key)
    {
        var idx = item.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        while (idx >= 0)
        {
            var asciiKey = key.All(c => c < 128);
            if (!asciiKey) return true;
            var before = idx == 0 ? ' ' : item[idx - 1];
            var after = idx + key.Length >= item.Length ? ' ' : item[idx + key.Length];
            // 英数字が隣接していなければ単語として一致（日本語の文字は区切りとみなす：「CRP定量」は CRP に一致）
            if (!IsAsciiWord(before) && !IsAsciiWord(after)) return true;
            idx = item.IndexOf(key, idx + 1, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }
    static bool IsAsciiWord(char c) => c < 128 && char.IsLetterOrDigit(c);
}
public sealed class MetabolicCriteria
{
    public WaistOnly Waist { get; set; } = new();
    public BpOnly Bp { get; set; } = new();
    public LipidOnly Lipid { get; set; } = new();
    public GlucoseOnly Glucose { get; set; } = new();
    public Dictionary<string, string> Labels { get; set; } = new();
    public Dictionary<string, string> Comments { get; set; } = new();
    public sealed class WaistOnly { public double Male { get; set; } = 85; public double Female { get; set; } = 90; }
    public sealed class BpOnly { public int Sys { get; set; } = 130; public int Dia { get; set; } = 85; }
    public sealed class LipidOnly { public double TgMin { get; set; } = 150; public double HdlMax { get; set; } = 40; }
    public sealed class GlucoseOnly { public double FbsMin { get; set; } = 110; public double Hba1cMin { get; set; } = 6.0; }
}
public sealed class CRule { public string When { get; set; } = ""; public string Code { get; set; } = ""; }
public sealed class OverallCriteria
{
    public Dictionary<string, string> HDefault { get; set; } = new();
    public string CDefault { get; set; } = "C006";
    public List<CRule> CRules { get; set; } = new();
}
