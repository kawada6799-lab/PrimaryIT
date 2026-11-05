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
    public static Masters Masters() => Kenshin.Config.Masters.Load(ConfigDir);
    public static Criteria Criteria() => Kenshin.Config.Criteria.Load(ConfigDir);
    public static string TempFile(string ext) => Path.Combine(Path.GetTempPath(), "kenshin-test-" + Guid.NewGuid().ToString("N") + ext);
}
