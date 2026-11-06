using System.Text.Json;

namespace Kenshin.Config;

/// <summary>判定区分（150 異常なし など）。</summary>
public sealed record JudgmentCategory(string Code, string Name, int Rank, string Note);

/// <summary>判定コメント（H・C コード）。</summary>
public sealed record CommentMaster(string Code, string Category, string Text);

/// <summary>所見マスター（部位ごとのコードと、所見から決まる判定）。</summary>
public sealed record FindingMaster(string Section, string Code, string Text, string Judgment, string Note);

public sealed class Course
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Sections { get; set; } = new();
    public List<string> Required { get; set; } = new();
}

public sealed class CourseFile { public List<Course> Courses { get; set; } = new(); }

/// <summary>血液項目マスター（config/血液項目.csv）。ファルコの報告書の全項目を枠として持つ。</summary>
public sealed record BloodItemMaster(int Index, string Category, string Item, string Unit, string RefMale, string RefFemale, string Kind, string[] Aliases, string Note)
{
    public string RefFor(string? sex) => sex == "女" && RefFemale != "" ? RefFemale : RefMale;
    public bool IsQualitative => Kind == "定性";
}

/// <summary>config フォルダのマスター一式。</summary>
public sealed class Masters
{
    public List<JudgmentCategory> Categories { get; } = new();
    public List<CommentMaster> Comments { get; } = new();
    public List<FindingMaster> Findings { get; } = new();
    public List<Course> Courses { get; } = new();
    public List<BloodItemMaster> BloodItems { get; } = new();

    public JudgmentCategory? Category(string? code) => Categories.FirstOrDefault(c => c.Code == code);
    public string CategoryName(string? code) => Category(code)?.Name ?? (code ?? "");
    public int Rank(string? code) => Category(code)?.Rank ?? 0;
    public string? CategoryCodeByName(string name) => Categories.FirstOrDefault(c => c.Name == name)?.Code;
    public CommentMaster? Comment(string? code) => Comments.FirstOrDefault(c => c.Code == code);
    public FindingMaster? Finding(string section, string code) => Findings.FirstOrDefault(f => f.Section == section && f.Code == code);
    public Course? CourseOf(string? code) => Courses.FirstOrDefault(c => c.Code == code) ?? Courses.FirstOrDefault();

    /// <summary>項目名の比較用：全角→半角、空白・括弧を除き、小文字に。</summary>
    public static string NormalizeItemName(string s)
    {
        var t = Kenshin.Domain.Num.ToHalfWidth(s ?? "");
        var sb = new System.Text.StringBuilder(t.Length);
        foreach (var c in t) { if (char.IsWhiteSpace(c) || c is '(' or ')' or '（' or '）' or '　') continue; sb.Append(char.ToLowerInvariant(c)); }
        return sb.ToString();
    }

    /// <summary>取り込んだ項目名・手入力の項目名をマスターの項目に当てる。名前か別名に一致、無ければ名前を含むもの。当たらなければ null。</summary>
    public BloodItemMaster? MatchBloodItem(string name)
    {
        if (BloodItems.Count == 0 || string.IsNullOrWhiteSpace(name)) return null;
        var n = NormalizeItemName(name);
        foreach (var m in BloodItems)
            if (NormalizeItemName(m.Item) == n || m.Aliases.Any(a => NormalizeItemName(a) == n)) return m;
        foreach (var m in BloodItems)
        {
            var mn = NormalizeItemName(m.Item);
            if (mn.Length >= 3 && n.StartsWith(mn)) return m;
        }
        return null;
    }

    /// <summary>結果の項目名をマスターの名前に揃え、分野・並び・足りない単位と基準値を補う。マスターに無い項目はそのまま。</summary>
    public void Canonicalize(Kenshin.Domain.BloodResult b, string? sex)
    {
        var m = MatchBloodItem(b.Item);
        if (m == null) return;
        b.Item = m.Item; b.Category = m.Category; b.Sort = m.Index;
        if (b.Unit == "") b.Unit = m.Unit;
        // 定性項目（陰性／陽性）は報告書の「基準値」欄に読み仮名（インセイ）が入るので、基準値はマスターの表記にする
        if (b.RefText == "" || m.IsQualitative) { b.RefText = m.RefFor(sex); var (lo, hi) = Kenshin.Lab.FalcoPdfReader.ParseRange(b.RefText); b.RefLow = lo; b.RefHigh = hi; }
    }

    /// <summary>複数の判定区分のうち最も重いもの。無ければ null。</summary>
    public string? Heaviest(IEnumerable<string?> codes)
    {
        string? best = null; var bestRank = -1;
        foreach (var c in codes)
        {
            if (string.IsNullOrEmpty(c)) continue;
            var r = Rank(c);
            if (r > bestRank) { bestRank = r; best = c; }
        }
        return best;
    }

    public static Masters Load(string configDir)
    {
        var m = new Masters();
        foreach (var r in Csv.ReadFile(Path.Combine(configDir, "判定区分.csv")))
            m.Categories.Add(new JudgmentCategory(r["code"], r["name"], int.Parse(r["rank"]), r.GetValueOrDefault("note", "")));
        foreach (var r in Csv.ReadFile(Path.Combine(configDir, "判定コメント.csv")))
            m.Comments.Add(new CommentMaster(r["code"], r["category"], r["text"]));
        foreach (var r in Csv.ReadFile(Path.Combine(configDir, "所見マスター.csv")))
            m.Findings.Add(new FindingMaster(r["section"], r["code"], r["text"], r["judgment"], r.GetValueOrDefault("note", "")));
        var cf = Json.Load<CourseFile>(Path.Combine(configDir, "コース.json"));
        m.Courses.AddRange(cf.Courses);
        var bi = Path.Combine(configDir, "血液項目.csv");
        if (File.Exists(bi))
            foreach (var r in Csv.ReadFile(bi))
            {
                if (r["item"] == "") continue;
                var aliases = r.GetValueOrDefault("aliases", "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                m.BloodItems.Add(new BloodItemMaster(m.BloodItems.Count, r["category"], r["item"], r.GetValueOrDefault("unit", ""), r.GetValueOrDefault("ref_male", ""), r.GetValueOrDefault("ref_female", ""), r.GetValueOrDefault("kind", "数値"), aliases, r.GetValueOrDefault("note", "")));
            }
        return m;
    }
}
