using Kenshin.Domain;
using Microsoft.Data.Sqlite;

namespace Kenshin.Data;

/// <summary>SQLite（SQLCipher で暗号化）1 ファイル。起動パスワードがそのまま鍵。</summary>
public sealed class Db : IDisposable
{
    readonly SqliteConnection _conn;
    public string Path { get; }

    static Db() { SQLitePCL.Batteries_V2.Init(); }

    Db(SqliteConnection conn, string path) { _conn = conn; Path = path; }

    /// <summary>開く。鍵が違えば例外（SqliteException "file is not a database"）。</summary>
    public static Db Open(string path, string password)
    {
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        // Pooling=false：接続を閉じたらファイルも閉じる（プールに残ると Windows でファイルを消せない・複製できない）。接続は 1 本を持ち続けるのでプールは不要
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Password = password, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
        var conn = new SqliteConnection(cs);
        try { conn.Open(); }
        catch { conn.Dispose(); throw; } // 開けなかった接続を放置すると、ネイティブのハンドルがファイルを掴んだままになる（Windows）
        var db = new Db(conn, path);
        try
        {
            db.Exec("PRAGMA journal_mode=WAL;");
            db.Exec("SELECT count(*) FROM sqlite_master;"); // 鍵の確認
            db.CreateSchema();
        }
        catch
        {
            db.Dispose(); // 鍵違いのときも、接続プールにファイルを掴ませたままにしない（Windows でファイルを消せなくなる）
            throw;
        }
        return db;
    }

    public static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

    void CreateSchema()
    {
        Exec(@"
CREATE TABLE IF NOT EXISTS patients(id INTEGER PRIMARY KEY, falco_id TEXT DEFAULT '', chart_no TEXT DEFAULT '', employee_no TEXT DEFAULT '',
  name TEXT NOT NULL, kana TEXT DEFAULT '', sex TEXT DEFAULT '', birth_date TEXT DEFAULT '', office TEXT DEFAULT '', dept TEXT DEFAULT '', created_at TEXT);
CREATE TABLE IF NOT EXISTS exams(id INTEGER PRIMARY KEY, patient_id INTEGER NOT NULL, exam_date TEXT NOT NULL, course TEXT DEFAULT '',
  status TEXT DEFAULT '入力中', confirmed_at TEXT, created_at TEXT, updated_at TEXT);
CREATE INDEX IF NOT EXISTS ix_exams_date ON exams(exam_date);
CREATE INDEX IF NOT EXISTS ix_exams_patient ON exams(patient_id);
CREATE TABLE IF NOT EXISTS exam_values(exam_id INTEGER NOT NULL, key TEXT NOT NULL, value TEXT DEFAULT '', PRIMARY KEY(exam_id, key));
CREATE TABLE IF NOT EXISTS judgments(exam_id INTEGER NOT NULL, section TEXT NOT NULL, code TEXT NOT NULL, manual INTEGER DEFAULT 0, updated_at TEXT, PRIMARY KEY(exam_id, section));
CREATE TABLE IF NOT EXISTS findings(id INTEGER PRIMARY KEY, exam_id INTEGER NOT NULL, section TEXT NOT NULL, code TEXT DEFAULT '', text TEXT DEFAULT '', sort INTEGER DEFAULT 0);
CREATE TABLE IF NOT EXISTS blood_results(id INTEGER PRIMARY KEY, exam_id INTEGER NOT NULL, item TEXT NOT NULL, value TEXT DEFAULT '', unit TEXT DEFAULT '',
  ref_text TEXT DEFAULT '', ref_low REAL, ref_high REAL, flag TEXT DEFAULT '', category TEXT DEFAULT '', sort INTEGER DEFAULT 0, source_file TEXT DEFAULT '', taken_at TEXT DEFAULT '');
CREATE INDEX IF NOT EXISTS ix_blood_exam ON blood_results(exam_id);
CREATE TABLE IF NOT EXISTS lab_files(id INTEGER PRIMARY KEY, file_name TEXT NOT NULL UNIQUE, taken_at TEXT DEFAULT '', patient_hint TEXT DEFAULT '',
  exam_id INTEGER, status TEXT DEFAULT '', imported_at TEXT, item_count INTEGER DEFAULT 0);
CREATE TABLE IF NOT EXISTS events(id INTEGER PRIMARY KEY, ts TEXT NOT NULL, kind TEXT NOT NULL, exam_id INTEGER, detail TEXT DEFAULT '');
CREATE TABLE IF NOT EXISTS issued_pdfs(id INTEGER PRIMARY KEY, exam_id INTEGER NOT NULL, kind TEXT NOT NULL, path TEXT NOT NULL, created_at TEXT);
");
    }

    // ---------- 低レベル ----------
    public void Exec(string sql, params (string, object?)[] ps)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
    long Insert(string sql, params (string, object?)[] ps)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql + "; SELECT last_insert_rowid();";
        foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        return (long)cmd.ExecuteScalar()!;
    }
    List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string, object?)[] ps)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }
    static string S(SqliteDataReader r, string col) => r.IsDBNull(r.GetOrdinal(col)) ? "" : r.GetString(r.GetOrdinal(col));
    static long L(SqliteDataReader r, string col) => r.GetInt64(r.GetOrdinal(col));
    static long? LN(SqliteDataReader r, string col) => r.IsDBNull(r.GetOrdinal(col)) ? null : r.GetInt64(r.GetOrdinal(col));
    static double? DN(SqliteDataReader r, string col) => r.IsDBNull(r.GetOrdinal(col)) ? null : r.GetDouble(r.GetOrdinal(col));

    public IDisposable Transaction() => new Tx(_conn);
    sealed class Tx : IDisposable
    {
        readonly SqliteTransaction _t; bool _done;
        public Tx(SqliteConnection c) { _t = c.BeginTransaction(); }
        public void Commit() { _t.Commit(); _done = true; }
        public void Dispose() { if (!_done) { try { _t.Commit(); } catch { _t.Rollback(); } } _t.Dispose(); }
    }

    // ---------- 受診者 ----------
    static Patient MapPatient(SqliteDataReader r) => new()
    {
        Id = L(r, "id"), FalcoId = S(r, "falco_id"), ChartNo = S(r, "chart_no"), EmployeeNo = S(r, "employee_no"), Name = S(r, "name"),
        Kana = S(r, "kana"), Sex = S(r, "sex"), BirthDate = S(r, "birth_date"), Office = S(r, "office"), Dept = S(r, "dept"), CreatedAt = S(r, "created_at"),
    };
    const string PatientCols = "id, falco_id, chart_no, employee_no, name, kana, sex, birth_date, office, dept, created_at";
    public Patient? GetPatient(long id) => Query($"SELECT {PatientCols} FROM patients WHERE id=@id", MapPatient, ("@id", id)).FirstOrDefault();
    public List<Patient> SearchPatients(string q, int limit = 50)
    {
        q = q?.Trim() ?? "";
        if (q == "") return Query($"SELECT {PatientCols} FROM patients ORDER BY id DESC LIMIT @l", MapPatient, ("@l", limit));
        var like = "%" + q + "%";
        return Query($"SELECT {PatientCols} FROM patients WHERE name LIKE @q OR kana LIKE @q OR falco_id LIKE @q OR chart_no LIKE @q OR employee_no LIKE @q ORDER BY id DESC LIMIT @l",
            MapPatient, ("@q", like), ("@l", limit));
    }
    public List<Patient> FindPatientsByIds(string falcoId, string chartNo)
    {
        var list = new List<Patient>();
        if (!string.IsNullOrWhiteSpace(falcoId)) list.AddRange(Query($"SELECT {PatientCols} FROM patients WHERE falco_id=@v", MapPatient, ("@v", falcoId.Trim())));
        if (!string.IsNullOrWhiteSpace(chartNo)) list.AddRange(Query($"SELECT {PatientCols} FROM patients WHERE chart_no=@v", MapPatient, ("@v", chartNo.Trim())));
        return list.DistinctBy(p => p.Id).ToList();
    }
    /// <summary>カナで探す（空白・濁点の揺れを吸収）。写真の読み取りの突合用。</summary>
    public List<Patient> FindPatientsByKana(string kana)
    {
        var k = Domain.Num.ToHalfWidth(kana).Replace(" ", "").Replace("　", "").Replace("゛", "").Replace("゜", "");
        if (k == "") return new();
        return Query($"SELECT {PatientCols} FROM patients", MapPatient).Where(p => Domain.Num.ToHalfWidth(p.Kana).Replace(" ", "").Replace("　", "") == k).ToList();
    }
    public List<Patient> FindPatientsByNameBirth(string name, string birth) =>
        Query($"SELECT {PatientCols} FROM patients WHERE (name=@n OR kana=@n OR REPLACE(name,' ','')=@n2) AND (@b='' OR birth_date=@b)", MapPatient,
            ("@n", name.Trim()), ("@n2", name.Replace(" ", "").Replace("　", "")), ("@b", birth));
    public long SavePatient(Patient p)
    {
        if (p.Id == 0)
        {
            p.CreatedAt = Now();
            p.Id = Insert("INSERT INTO patients(falco_id, chart_no, employee_no, name, kana, sex, birth_date, office, dept, created_at) VALUES(@f,@c,@e,@n,@k,@s,@b,@o,@d,@t)",
                ("@f", p.FalcoId), ("@c", p.ChartNo), ("@e", p.EmployeeNo), ("@n", p.Name), ("@k", p.Kana), ("@s", p.Sex), ("@b", p.BirthDate), ("@o", p.Office), ("@d", p.Dept), ("@t", p.CreatedAt));
        }
        else
            Exec("UPDATE patients SET falco_id=@f, chart_no=@c, employee_no=@e, name=@n, kana=@k, sex=@s, birth_date=@b, office=@o, dept=@d WHERE id=@id",
                ("@f", p.FalcoId), ("@c", p.ChartNo), ("@e", p.EmployeeNo), ("@n", p.Name), ("@k", p.Kana), ("@s", p.Sex), ("@b", p.BirthDate), ("@o", p.Office), ("@d", p.Dept), ("@id", p.Id));
        return p.Id;
    }

    // ---------- 健診回 ----------
    static Exam MapExam(SqliteDataReader r) => new()
    {
        Id = L(r, "id"), PatientId = L(r, "patient_id"), ExamDate = S(r, "exam_date"), Course = S(r, "course"), Status = S(r, "status"),
        ConfirmedAt = r.IsDBNull(r.GetOrdinal("confirmed_at")) ? null : S(r, "confirmed_at"), CreatedAt = S(r, "created_at"), UpdatedAt = S(r, "updated_at"),
    };
    const string ExamCols = "id, patient_id, exam_date, course, status, confirmed_at, created_at, updated_at";
    public Exam? GetExam(long id) => Query($"SELECT {ExamCols} FROM exams WHERE id=@id", MapExam, ("@id", id)).FirstOrDefault();
    public List<Exam> ExamsOn(string date) => Query($"SELECT {ExamCols} FROM exams WHERE exam_date=@d ORDER BY id", MapExam, ("@d", date));
    public List<Exam> ExamsOfPatient(long patientId) => Query($"SELECT {ExamCols} FROM exams WHERE patient_id=@p ORDER BY exam_date DESC, id DESC", MapExam, ("@p", patientId));
    public List<Exam> PendingExams(int days = 90)
    {
        var since = DateTime.Today.AddDays(-days).ToString("yyyy-MM-dd");
        return Query($"SELECT {ExamCols} FROM exams WHERE exam_date>=@s AND status<>@m ORDER BY exam_date, id", MapExam, ("@s", since), ("@m", ExamStatus.Mailed));
    }
    public List<Exam> ExamsBetween(string from, string to) => Query($"SELECT {ExamCols} FROM exams WHERE exam_date BETWEEN @f AND @t ORDER BY exam_date, id", MapExam, ("@f", from), ("@t", to));
    public long SaveExam(Exam e)
    {
        e.UpdatedAt = Now();
        if (e.Id == 0)
        {
            e.CreatedAt = e.UpdatedAt;
            e.Id = Insert("INSERT INTO exams(patient_id, exam_date, course, status, confirmed_at, created_at, updated_at) VALUES(@p,@d,@c,@s,@cf,@ca,@ua)",
                ("@p", e.PatientId), ("@d", e.ExamDate), ("@c", e.Course), ("@s", e.Status), ("@cf", e.ConfirmedAt), ("@ca", e.CreatedAt), ("@ua", e.UpdatedAt));
        }
        else
            Exec("UPDATE exams SET patient_id=@p, exam_date=@d, course=@c, status=@s, confirmed_at=@cf, updated_at=@ua WHERE id=@id",
                ("@p", e.PatientId), ("@d", e.ExamDate), ("@c", e.Course), ("@s", e.Status), ("@cf", e.ConfirmedAt), ("@ua", e.UpdatedAt), ("@id", e.Id));
        return e.Id;
    }
    public void TouchExam(long examId) => Exec("UPDATE exams SET updated_at=@u WHERE id=@id", ("@u", Now()), ("@id", examId));

    // ---------- 入力値 ----------
    public Dictionary<string, string> GetValues(long examId)
    {
        var d = new Dictionary<string, string>();
        foreach (var (k, v) in Query("SELECT key, value FROM exam_values WHERE exam_id=@e", r => (S(r, "key"), S(r, "value")), ("@e", examId))) d[k] = v;
        return d;
    }
    public void SetValue(long examId, string key, string value)
    {
        if (string.IsNullOrEmpty(value)) Exec("DELETE FROM exam_values WHERE exam_id=@e AND key=@k", ("@e", examId), ("@k", key));
        else Exec("INSERT INTO exam_values(exam_id, key, value) VALUES(@e,@k,@v) ON CONFLICT(exam_id, key) DO UPDATE SET value=excluded.value", ("@e", examId), ("@k", key), ("@v", value));
        TouchExam(examId);
    }

    // ---------- 判定 ----------
    public List<Judgment> GetJudgments(long examId) => Query("SELECT exam_id, section, code, manual, updated_at FROM judgments WHERE exam_id=@e",
        r => new Judgment { ExamId = L(r, "exam_id"), Section = S(r, "section"), Code = S(r, "code"), Manual = L(r, "manual") == 1, UpdatedAt = S(r, "updated_at") }, ("@e", examId));
    public void ReplaceJudgments(long examId, IEnumerable<Judgment> js)
    {
        using var tx = Transaction();
        Exec("DELETE FROM judgments WHERE exam_id=@e", ("@e", examId));
        foreach (var j in js)
            Exec("INSERT INTO judgments(exam_id, section, code, manual, updated_at) VALUES(@e,@s,@c,@m,@u)", ("@e", examId), ("@s", j.Section), ("@c", j.Code), ("@m", j.Manual ? 1 : 0), ("@u", j.UpdatedAt));
    }
    public void SetJudgment(long examId, string section, string? code, bool manual)
    {
        if (string.IsNullOrEmpty(code)) Exec("DELETE FROM judgments WHERE exam_id=@e AND section=@s", ("@e", examId), ("@s", section));
        else Exec("INSERT INTO judgments(exam_id, section, code, manual, updated_at) VALUES(@e,@s,@c,@m,@u) ON CONFLICT(exam_id, section) DO UPDATE SET code=excluded.code, manual=excluded.manual, updated_at=excluded.updated_at",
            ("@e", examId), ("@s", section), ("@c", code), ("@m", manual ? 1 : 0), ("@u", Now()));
        TouchExam(examId);
    }

    // ---------- 所見 ----------
    public List<Finding> GetFindings(long examId) => Query("SELECT id, exam_id, section, code, text, sort FROM findings WHERE exam_id=@e ORDER BY section, sort, id",
        r => new Finding { Id = L(r, "id"), ExamId = L(r, "exam_id"), Section = S(r, "section"), Code = S(r, "code"), Text = S(r, "text"), Sort = (int)L(r, "sort") }, ("@e", examId));
    public long AddFinding(Finding f)
    {
        f.Id = Insert("INSERT INTO findings(exam_id, section, code, text, sort) VALUES(@e,@s,@c,@t,@o)", ("@e", f.ExamId), ("@s", f.Section), ("@c", f.Code), ("@t", f.Text), ("@o", f.Sort));
        TouchExam(f.ExamId); return f.Id;
    }
    public void DeleteFinding(long examId, long findingId) { Exec("DELETE FROM findings WHERE id=@id AND exam_id=@e", ("@id", findingId), ("@e", examId)); TouchExam(examId); }

    // ---------- 血液 ----------
    public List<BloodResult> GetBlood(long examId) => Query("SELECT * FROM blood_results WHERE exam_id=@e ORDER BY sort, id", MapBlood, ("@e", examId));
    static BloodResult MapBlood(SqliteDataReader r) => new()
    {
        Id = L(r, "id"), ExamId = L(r, "exam_id"), Item = S(r, "item"), Value = S(r, "value"), Unit = S(r, "unit"), RefText = S(r, "ref_text"), RefLow = DN(r, "ref_low"), RefHigh = DN(r, "ref_high"),
        Flag = S(r, "flag"), Category = S(r, "category"), Sort = (int)L(r, "sort"), SourceFile = S(r, "source_file"), TakenAt = S(r, "taken_at"),
    };
    public void ReplaceBlood(long examId, IEnumerable<BloodResult> rows)
    {
        using var tx = Transaction();
        Exec("DELETE FROM blood_results WHERE exam_id=@e", ("@e", examId));
        var i = 0;
        foreach (var b in rows)
            Exec("INSERT INTO blood_results(exam_id, item, value, unit, ref_text, ref_low, ref_high, flag, category, sort, source_file, taken_at) VALUES(@e,@i,@v,@u,@rt,@rl,@rh,@f,@c,@s,@sf,@ta)",
                ("@e", examId), ("@i", b.Item), ("@v", b.Value), ("@u", b.Unit), ("@rt", b.RefText), ("@rl", b.RefLow), ("@rh", b.RefHigh), ("@f", b.Flag), ("@c", b.Category), ("@s", b.Sort == 0 ? i++ : b.Sort), ("@sf", b.SourceFile), ("@ta", b.TakenAt));
        TouchExam(examId);
    }
    public void UpsertBloodItem(long examId, BloodResult b)
    {
        var existing = Query("SELECT id FROM blood_results WHERE exam_id=@e AND item=@i", r => L(r, "id"), ("@e", examId), ("@i", b.Item)).FirstOrDefault();
        if (existing != 0)
            Exec("UPDATE blood_results SET value=@v, unit=@u, ref_text=@rt, ref_low=@rl, ref_high=@rh, flag=@f, category=@c WHERE id=@id",
                ("@v", b.Value), ("@u", b.Unit), ("@rt", b.RefText), ("@rl", b.RefLow), ("@rh", b.RefHigh), ("@f", b.Flag), ("@c", b.Category), ("@id", existing));
        else
            Exec("INSERT INTO blood_results(exam_id, item, value, unit, ref_text, ref_low, ref_high, flag, category, sort, source_file, taken_at) VALUES(@e,@i,@v,@u,@rt,@rl,@rh,@f,@c,@s,@sf,@ta)",
                ("@e", examId), ("@i", b.Item), ("@v", b.Value), ("@u", b.Unit), ("@rt", b.RefText), ("@rl", b.RefLow), ("@rh", b.RefHigh), ("@f", b.Flag), ("@c", b.Category), ("@s", 999), ("@sf", b.SourceFile != "" ? b.SourceFile : "手入力"), ("@ta", b.TakenAt));
        TouchExam(examId);
    }
    public void DeleteBloodItem(long examId, long id) { Exec("DELETE FROM blood_results WHERE id=@id AND exam_id=@e", ("@id", id), ("@e", examId)); TouchExam(examId); }

    // ---------- 取込ファイル ----------
    public List<LabFile> GetLabFiles() => Query("SELECT * FROM lab_files ORDER BY id DESC",
        r => new LabFile { Id = L(r, "id"), FileName = S(r, "file_name"), TakenAt = S(r, "taken_at"), PatientHint = S(r, "patient_hint"), ExamId = LN(r, "exam_id"), Status = S(r, "status"), ImportedAt = S(r, "imported_at"), ItemCount = (int)L(r, "item_count") });
    public LabFile? GetLabFile(string fileName) => Query("SELECT * FROM lab_files WHERE file_name=@f",
        r => new LabFile { Id = L(r, "id"), FileName = S(r, "file_name"), TakenAt = S(r, "taken_at"), PatientHint = S(r, "patient_hint"), ExamId = LN(r, "exam_id"), Status = S(r, "status"), ImportedAt = S(r, "imported_at"), ItemCount = (int)L(r, "item_count") }, ("@f", fileName)).FirstOrDefault();
    public void SaveLabFile(LabFile f)
    {
        Exec("INSERT INTO lab_files(file_name, taken_at, patient_hint, exam_id, status, imported_at, item_count) VALUES(@f,@t,@p,@e,@s,@i,@n) ON CONFLICT(file_name) DO UPDATE SET taken_at=excluded.taken_at, patient_hint=excluded.patient_hint, exam_id=excluded.exam_id, status=excluded.status, imported_at=excluded.imported_at, item_count=excluded.item_count",
            ("@f", f.FileName), ("@t", f.TakenAt), ("@p", f.PatientHint), ("@e", f.ExamId), ("@s", f.Status), ("@i", f.ImportedAt), ("@n", f.ItemCount));
    }

    // ---------- 記録 ----------
    public void Log(string kind, long? examId, string detail = "") =>
        Exec("INSERT INTO events(ts, kind, exam_id, detail) VALUES(@t,@k,@e,@d)", ("@t", Now()), ("@k", kind), ("@e", examId), ("@d", detail));
    public List<EventRecord> RecentEvents(int limit = 200) => Query("SELECT * FROM events ORDER BY id DESC LIMIT @l",
        r => new EventRecord { Id = L(r, "id"), Ts = S(r, "ts"), Kind = S(r, "kind"), ExamId = LN(r, "exam_id"), Detail = S(r, "detail") }, ("@l", limit));
    public void AddIssuedPdf(long examId, string kind, string path) =>
        Exec("INSERT INTO issued_pdfs(exam_id, kind, path, created_at) VALUES(@e,@k,@p,@t)", ("@e", examId), ("@k", kind), ("@p", path), ("@t", Now()));
    public List<IssuedPdf> IssuedPdfs(long examId) => Query("SELECT * FROM issued_pdfs WHERE exam_id=@e ORDER BY id DESC",
        r => new IssuedPdf { Id = L(r, "id"), ExamId = L(r, "exam_id"), Kind = S(r, "kind"), Path = S(r, "path"), CreatedAt = S(r, "created_at") }, ("@e", examId));

    // ---------- バックアップ（暗号化されたまま 1 ファイルに書き出す）----------
    public void BackupTo(string destPath)
    {
        var dir = System.IO.Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(destPath)) File.Delete(destPath);
        Exec("VACUUM INTO @p", ("@p", destPath));
    }

    /// <summary>閉じる。接続プールも空にして、ファイルをすぐ消したり複製したりできるようにする。</summary>
    public void Dispose() { _conn.Dispose(); }
}
