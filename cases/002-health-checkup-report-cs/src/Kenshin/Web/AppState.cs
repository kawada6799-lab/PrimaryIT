using System.Security.Cryptography;
using Kenshin.Config;
using Kenshin.Data;
using Kenshin.Judge;

namespace Kenshin.Web;

/// <summary>起動中に 1 つだけある状態。DB は起動パスワードが入るまで null。</summary>
public sealed class AppState : IDisposable
{
    public AppConfig Config { get; }
    public Masters Masters { get; }
    public Criteria Criteria { get; }
    public JudgeEngine Engine { get; }
    public NetworkGuard Guard { get; }
    public Voice.IVoiceRecognizer Voice { get; set; } = new Voice.NoVoiceRecognizer("未初期化");
    public byte[] CookieKey { get; } = RandomNumberGenerator.GetBytes(32);
    public readonly object Sync = new();

    Db? _db;
    public Db? Db => _db;
    public bool Unlocked => _db != null;
    public string DataDir => Config.Resolve(Config.DataDir);
    public string DbPath => Path.Combine(DataDir, "kenshin.db");
    public string PdfDir => Path.Combine(DataDir, "pdf");
    public string ImportDir => Config.Resolve(Config.ImportDir);

    public AppState(AppConfig config, Masters masters, Criteria criteria, NetworkGuard guard)
    {
        Config = config; Masters = masters; Criteria = criteria; Guard = guard;
        Engine = new JudgeEngine(criteria, masters);
    }

    /// <summary>起動パスワードで DB を開く。鍵が違えば false。</summary>
    public bool Unlock(string password)
    {
        lock (Sync)
        {
            if (_db != null) return true;
            try
            {
                var db = Data.Db.Open(DbPath, password);
                _db = db;
                Directory.CreateDirectory(PdfDir);
                Directory.CreateDirectory(ImportDir);
                db.Log("起動", null, "");
                return true;
            }
            catch (Exception) { return false; }
        }
    }

    public Db RequireDb() => _db ?? throw new InvalidOperationException("DB が開いていません");

    /// <summary>終了時：設定があればバックアップ先へ 1 ファイルを上書きコピー（暗号化されたまま）。</summary>
    public string? BackupOnExit()
    {
        var db = _db; if (db == null) return null;
        if (string.IsNullOrWhiteSpace(Config.BackupDir)) return null;
        try
        {
            var dir = Config.Resolve(Config.BackupDir);
            var dest = Path.Combine(dir, "kenshin.db");
            lock (Sync) { db.Exec("SELECT count(*) FROM patients"); db.BackupTo(dest); }
            return dest;
        }
        catch (Exception ex) { Console.Error.WriteLine("バックアップに失敗: " + ex.Message); return null; }
    }

    public void Dispose()
    {
        lock (Sync) { try { _db?.Log("終了", null, ""); } catch { } _db?.Dispose(); _db = null; }
        Voice.Dispose();
    }
}
