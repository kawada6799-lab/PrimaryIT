using Kenshin.Data;
using Kenshin.Domain;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kenshin.Tests;

public class DbTests
{
    [Fact]
    public void 暗号化DBの作成と鍵の検証と基本操作()
    {
        var path = TestEnv.TempFile(".db");
        try
        {
            long examId;
            using (var db = Db.Open(path, "pass-1"))
            {
                var pid = db.SavePatient(new Patient { Name = "健診 太郎", Kana = "ケンシン タロウ", Sex = "男", BirthDate = "1974-05-01", FalcoId = "0001" });
                examId = db.SaveExam(new Exam { PatientId = pid, ExamDate = "2026-11-04", Course = "企業健診" });
                db.SetValue(examId, Keys.Height, "170.2");
                db.SetValue(examId, Keys.Weight, "74.5");
                db.SetValue(examId, Keys.Weight, ""); // 空は削除
                db.ReplaceJudgments(examId, new[] { new Judgment { ExamId = examId, Section = Sections.Measure, Code = "180", UpdatedAt = Db.Now() } });
                db.SetJudgment(examId, Sections.Bp, "150", true);
                db.AddFinding(new Finding { ExamId = examId, Section = Sections.Gastroscopy, Code = "V014", Text = "胃ポリープ" });
                db.ReplaceBlood(examId, new[] { new BloodResult { Item = "AST", Value = "27", Unit = "U/L", RefLow = 8, RefHigh = 38 } });
                db.Log("テスト", examId, "記録");
                Assert.Single(db.ExamsOn("2026-11-04"));
                Assert.Single(db.SearchPatients("太郎"));
                Assert.Single(db.FindPatientsByIds("0001", ""));
            }
            using (var db = Db.Open(path, "pass-1"))
            {
                var v = db.GetValues(examId);
                Assert.Equal("170.2", v[Keys.Height]);
                Assert.False(v.ContainsKey(Keys.Weight));
                var js = db.GetJudgments(examId);
                Assert.Equal(2, js.Count);
                Assert.True(js.First(j => j.Section == Sections.Bp).Manual);
                Assert.Single(db.GetFindings(examId));
                Assert.Single(db.GetBlood(examId));
                Assert.Single(db.RecentEvents());
                var backup = TestEnv.TempFile(".bak");
                db.BackupTo(backup);
                Assert.True(new FileInfo(backup).Length > 0);
                using (var b = Db.Open(backup, "pass-1")) Assert.Single(b.GetFindings(examId)); // 閉じてから消す（Windows はロックが残る）
                SqliteConnection.ClearAllPools();
                File.Delete(backup);
            }
            Assert.ThrowsAny<SqliteException>(() => { using var bad = Db.Open(path, "wrong"); });
            // 暗号化されていること：先頭に SQLite のヘッダ文字列が無い
            var head = new byte[16];
            using (var fs = File.OpenRead(path)) fs.Read(head, 0, 16);
            Assert.NotEqual("SQLite format 3\0", System.Text.Encoding.ASCII.GetString(head));
        }
        finally { SqliteConnection.ClearAllPools(); foreach (var f in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(f)) File.Delete(f); }
    }
}
