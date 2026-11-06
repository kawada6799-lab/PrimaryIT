using System.Text.Json;

namespace Kenshin.Config;

/// <summary>判定区分（150 異常なし など）。Section が空なら共通の区分。計測（肥満度）・視力は項目ごとの個別コードを持ち、Overall が総合判定に数えるときの共通区分。</summary>
public sealed record JudgmentCategory(string Code, string Name, int Rank, string Note, string Section = "", string Overall = "");

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
    /// <summary>胸部X線の組み合わせコードの部品（kind：位置／部位／所見）。コード＝位置 1 文字＋部位 1 文字＋所見 2 文字（例 A001＝右肺野異常陰影）</summary>
    public Dictionary<string, List<(string code, string name)>> XrayParts { get; } = new();

    /// <summary>共通の区分（section 無し）。</summary>
    public JudgmentCategory? Category(string? code) => Categories.FirstOrDefault(c => c.Code == code && c.Section == "");
    /// <summary>項目の区分：その項目の個別コードがあればそれ、無ければ共通。</summary>
    public JudgmentCategory? Category(string section, string? code) => Categories.FirstOrDefault(c => c.Code == code && c.Section == section) ?? Category(code);
    public string CategoryName(string? code) => Category(code)?.Name ?? (code ?? "");
    /// <summary>画面・帳票に出す区分名（計測 150 → 正常範囲、視力 145 → 眼鏡あり）。</summary>
    public string Label(string section, string? code) => code == null ? "" : Category(section, code)?.Name ?? code;
    /// <summary>総合判定に数えるときの共通区分（計測 170 太り過ぎ → 180 要経過観察 など）。</summary>
    public string OverallCode(string section, string code) { var c = Category(section, code); return c != null && c.Section != "" && c.Overall != "" ? c.Overall : code; }
    public int Rank(string? code) => Category(code)?.Rank ?? 0;
    public int Rank(string section, string? code) => code == null ? 0 : Rank(OverallCode(section, code));
    /// <summary>その項目で選べる区分：個別コードがあればそれと、個別コードに無い番号の共通区分。</summary>
    public List<JudgmentCategory> CodesFor(string section)
    {
        var own = Categories.Where(c => c.Section == section).ToList();
        if (own.Count == 0) return Categories.Where(c => c.Section == "").ToList();
        return own.Concat(Categories.Where(c => c.Section == "" && own.All(o => o.Code != c.Code))).ToList();
    }
    public string? CategoryCodeByName(string name) => Categories.FirstOrDefault(c => c.Name == name && c.Section == "")?.Code;
    public CommentMaster? Comment(string? code) => Comments.FirstOrDefault(c => c.Code == code);
    public FindingMaster? Finding(string section, string code)
    {
        var f = Findings.FirstOrDefault(x => x.Section == section && x.Code == code);
        if (f != null || section != Domain.Sections.ChestXray || XrayParts.Count == 0) return f;
        // 胸部X線の組み合わせコード
        var c = Domain.Num.ToHalfWidth(code ?? "").ToUpperInvariant();
        if (c.Length != 4) return null;
        var pos = XrayParts.GetValueOrDefault("位置")?.FirstOrDefault(p => p.code == c[..1]);
        var part = XrayParts.GetValueOrDefault("部位")?.FirstOrDefault(p => p.code == c[1..2]);
        var find = XrayParts.GetValueOrDefault("所見")?.FirstOrDefault(p => p.code == c[2..]);
        if (pos == null || part == null || find == null || pos.Value.code == null || part.Value.code == null || find.Value.code == null) return null;
        return new FindingMaster(section, c, pos.Value.name + part.Value.name + find.Value.name, "", "組み合わせ");
    }
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
            m.Categories.Add(new JudgmentCategory(r["code"], r["name"], int.TryParse(r.GetValueOrDefault("rank", ""), out var rk) ? rk : 0, r.GetValueOrDefault("note", ""), r.GetValueOrDefault("section", ""), r.GetValueOrDefault("overall", "")));
        foreach (var r in Csv.ReadFile(Path.Combine(configDir, "判定コメント.csv")))
            m.Comments.Add(new CommentMaster(r["code"], r["category"], r["text"]));
        foreach (var r in Csv.ReadFile(Path.Combine(configDir, "所見マスター.csv")))
            m.Findings.Add(new FindingMaster(r["section"], r["code"], r["text"], r["judgment"], r.GetValueOrDefault("note", "")));
        var cf = Json.Load<CourseFile>(Path.Combine(configDir, "コース.json"));
        m.Courses.AddRange(cf.Courses);
        var xp = Path.Combine(configDir, "胸部X線所見部品.csv");
        if (File.Exists(xp))
            foreach (var r in Csv.ReadFile(xp))
            {
                if (!m.XrayParts.TryGetValue(r["kind"], out var lst)) m.XrayParts[r["kind"]] = lst = new();
                lst.Add((r["code"], r["name"]));
            }
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
