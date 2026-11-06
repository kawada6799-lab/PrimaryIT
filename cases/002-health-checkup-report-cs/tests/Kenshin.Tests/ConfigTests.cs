using Xunit;
using Kenshin.Config;

namespace Kenshin.Tests;

public class ConfigTests
{
    [Fact]
    public void 設定の_パスの_バックスラッシュが_1つだと_日本語の_ヒント付きで_止まる()
    {
        var path = Path.Combine(Path.GetTempPath(), "kenshin-config-test-" + Guid.NewGuid() + ".json");
        File.WriteAllText(path, "{\n  \"listen\": { \"localPort\": 5080 },\n  \"backupDir\": \"C:\\Users\\kenshin\"\n}\n");
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => Json.Load<AppConfig>(path));
            Assert.Contains("3 行目", ex.Message);
            Assert.Contains("2 つ重ねて", ex.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void 設定の_パスは_スラッシュでも_2つ重ねでも_読める()
    {
        var path = Path.Combine(Path.GetTempPath(), "kenshin-config-test-" + Guid.NewGuid() + ".json");
        File.WriteAllText(path, "{ \"backupDir\": \"C:/kenshin-backup\", \"importDir\": \"C:\\\\kenshin\\\\取込\" }");
        try
        {
            var c = Json.Load<AppConfig>(path);
            Assert.Equal("C:/kenshin-backup", c.BackupDir);
            Assert.Equal("C:\\kenshin\\取込", c.ImportDir);
        }
        finally { File.Delete(path); }
    }
}
