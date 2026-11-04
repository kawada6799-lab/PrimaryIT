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

/// <summary>config フォルダのマスター一式。</summary>
public sealed class Masters
{
    public List<JudgmentCategory> Categories { get; } = new();
    public List<CommentMaster> Comments { get; } = new();
    public List<FindingMaster> Findings { get; } = new();
    public List<Course> Courses { get; } = new();

    public JudgmentCategory? Category(string? code) => Categories.FirstOrDefault(c => c.Code == code);
    public string CategoryName(string? code) => Category(code)?.Name ?? (code ?? "");
    public int Rank(string? code) => Category(code)?.Rank ?? 0;
    public string? CategoryCodeByName(string name) => Categories.FirstOrDefault(c => c.Name == name)?.Code;
    public CommentMaster? Comment(string? code) => Comments.FirstOrDefault(c => c.Code == code);
    public FindingMaster? Finding(string section, string code) => Findings.FirstOrDefault(f => f.Section == section && f.Code == code);
    public Course? CourseOf(string? code) => Courses.FirstOrDefault(c => c.Code == code) ?? Courses.FirstOrDefault();

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
        return m;
    }
}
