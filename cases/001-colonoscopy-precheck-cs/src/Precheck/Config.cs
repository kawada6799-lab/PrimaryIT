using System.Text.Json;
using System.Text.Json.Serialization;

namespace Precheck;

public sealed class WakumyConfig
{
    public string LoginUrl { get; set; } = "https://hospital.wakumy.lyd.inc/a/admin-app/#/signIn";
    public string Id { get; set; } = "";
    public string Password { get; set; } = "";
    public bool Headless { get; set; } = true;
    public int TimeoutMs { get; set; } = 20000;
    /// <summary>使うブラウザ。msedge（Windows 標準の Edge）なら追加ダウンロード不要。</summary>
    public string Browser { get; set; } = "msedge";
}

public sealed class RulesConfig
{
    public List<string> ExamKeywords { get; set; } = new() { "大腸" };
    public List<string> ExamExcludeKeywords { get; set; } = new() { "連鎖用", "事前診察" };
    public List<string> PreExamKeywords { get; set; } = new() { "大腸カメラ事前診察" };
    public int WindowDays { get; set; } = 30;
    public List<string> ExamActiveStatuses { get; set; } = new() { "予約" };
    public List<string> PreExamOkStatuses { get; set; } = new() { "予約", "来院" };
    public int RecentVisitDays { get; set; } = 30;
}

public sealed class ScheduleConfig
{
    public string ExamTab { get; set; } = "内視鏡";
    public string PreExamTab { get; set; } = "外来診察";
    public int DaysAhead { get; set; } = 45;
    public int DaysBack { get; set; } = 30;
}

public sealed class NotifyConfig
{
    public string OutputDir { get; set; } = "結果";
    public bool OpenAfter { get; set; } = true;
    public bool Toast { get; set; } = true;
    public bool WriteEmpty { get; set; } = false;
}

public sealed class Config
{
    public WakumyConfig Wakumy { get; set; } = new();
    public RulesConfig Rules { get; set; } = new();
    public ScheduleConfig Schedule { get; set; } = new();
    public NotifyConfig Notify { get; set; } = new();
    public string StatePath { get; set; } = "state/processed.json";

    [JsonIgnore] public string BaseDir { get; set; } = ".";
    [JsonIgnore] public string StateFile => Resolve(StatePath);
    [JsonIgnore] public string OutputDirFull => Resolve(Notify.OutputDir);

    private string Resolve(string p) => Path.IsPathRooted(p) ? p : Path.Combine(BaseDir, p);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>config.json を読む。パスワードは 環境変数 → local/wakumy_password.txt → config の順。</summary>
    public static Config Load(string path)
    {
        var cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(path), JsonOpts) ?? new Config();
        cfg.BaseDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        cfg.Wakumy.Id = Environment.GetEnvironmentVariable("WAKUMY_ID") is { Length: > 0 } id ? id : cfg.Wakumy.Id;
        cfg.Wakumy.Password = ResolvePassword(cfg.BaseDir, cfg.Wakumy.Password);
        if (cfg.Wakumy.Id.Length == 0) throw new ConfigError("Wakumy の ID が config.json にありません。");
        if (cfg.Wakumy.Password.Length == 0)
            throw new ConfigError("Wakumy のパスワードが保存されていません。setup.bat を実行するか、precheck.exe --save-password を実行してください。");
        return cfg;
    }

    public static string PasswordFile(string baseDir) => Path.Combine(baseDir, "local", "wakumy_password.txt");

    public static string ResolvePassword(string baseDir, string fromConfig)
    {
        var env = Environment.GetEnvironmentVariable("WAKUMY_PASSWORD");
        if (!string.IsNullOrEmpty(env)) return env;
        var f = PasswordFile(baseDir);
        if (File.Exists(f)) return File.ReadAllText(f).Trim();
        return fromConfig;
    }

    public static string SavePassword(string baseDir, string password)
    {
        var f = PasswordFile(baseDir);
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        File.WriteAllText(f, password.Trim() + "\n");
        return f;
    }
}

public sealed class ConfigError : Exception
{
    public ConfigError(string message) : base(message) { }
}
