using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kenshin.Config;

public sealed class AppConfig
{
    public ListenConfig Listen { get; set; } = new();
    public string Pin { get; set; } = "1234";
    public string DataDir { get; set; } = "data";
    public string ImportDir { get; set; } = "取込";
    public string BackupDir { get; set; } = "";
    public bool NetworkGuard { get; set; } = true;
    public bool OpenBrowser { get; set; } = false; // exe からブラウザを起動するとウイルス対策のふるまい検知に引っかかりやすい。既定は run.bat が開く
    public VoiceConfig Voice { get; set; } = new();
    public ClinicConfig Clinic { get; set; } = new();

    /// <summary>設定ファイルのあるフォルダ（config\）。マスターもここから読む。</summary>
    [JsonIgnore] public string ConfigDir { get; set; } = "";
    /// <summary>exe のあるフォルダ。相対パスの基準。</summary>
    [JsonIgnore] public string BaseDir { get; set; } = "";

    public string Resolve(string relative) =>
        Path.IsPathRooted(relative) ? relative : Path.GetFullPath(Path.Combine(BaseDir, relative));

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    public static AppConfig Load(string baseDir)
    {
        var configDir = Path.Combine(baseDir, "config");
        var path = Path.Combine(configDir, "config.json");
        AppConfig cfg;
        if (File.Exists(path))
            cfg = Json.Load<AppConfig>(path);
        else
            cfg = new AppConfig();
        cfg.BaseDir = baseDir;
        cfg.ConfigDir = configDir;
        return cfg;
    }
}

public sealed class ListenConfig
{
    public int LocalPort { get; set; } = 5080;
    public string LanAddress { get; set; } = "";
    public int LanPort { get; set; } = 5443;
}

public sealed class VoiceConfig
{
    public bool Enabled { get; set; } = true;
    public string ModelPath { get; set; } = "models/ggml-small.bin";
    public string Language { get; set; } = "ja";
    public int Threads { get; set; } = 4;
}

public sealed class ClinicConfig
{
    public string Name { get; set; } = "";
    public string Doctor { get; set; } = "";
    public string OccupationalDoctor { get; set; } = "";
}
