using Kenshin.Config;

namespace Kenshin.Tests;

/// <summary>リポジトリの config フォルダを使うテスト共通の土台。患者データはすべて架空。</summary>
public static class TestEnv
{
    public static string ConfigDir
    {
        get
        {
            var dir = AppContext.BaseDirectory;
            for (var i = 0; i < 8 && dir != null; i++)
            {
                var c = Path.Combine(dir, "config", "判定基準.json");
                if (File.Exists(c)) return Path.Combine(dir, "config");
                dir = Path.GetDirectoryName(dir);
            }
            throw new InvalidOperationException("config フォルダが見つかりません");
        }
    }
    /// <summary>案件のフォルダ（Kenshin.sln のある所）。モデルなど config 以外を探すときに使う。</summary>
    public static string CaseDir
    {
        get
        {
            var dir = AppContext.BaseDirectory;
            for (var i = 0; i < 10 && dir != null; i++)
            {
                if (File.Exists(Path.Combine(dir, "Kenshin.sln"))) return dir;
                dir = Path.GetDirectoryName(dir);
            }
            throw new InvalidOperationException("Kenshin.sln が見つかりません");
        }
    }
    /// <summary>OCR のモデル（models/ocr）。無ければ null（OCR のテストは飛ばす）。</summary>
    public static string? OcrModelDir
    {
        get { var d = Path.Combine(CaseDir, "models", "ocr"); return File.Exists(Kenshin.Ocr.OcrEngine.DetPath(d)) ? d : null; }
    }
    public static Masters Masters() => Kenshin.Config.Masters.Load(ConfigDir);
    public static Criteria Criteria() => Kenshin.Config.Criteria.Load(ConfigDir);
    public static string TempFile(string ext) => Path.Combine(Path.GetTempPath(), "kenshin-test-" + Guid.NewGuid().ToString("N") + ext);
}
