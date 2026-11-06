using System.Text.Json;
using Kenshin.Data;
using Kenshin.Domain;
using Kenshin.Web;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Kenshin.Lab;

/// <summary>
/// 報告書の写真の取り込み。写真は取込フォルダに置かれ（携帯から送られる・PC で選ぶ）、PC の中の OCR で読み、
/// 読み取り結果は写真の隣に「.ocr.json」で残す（同じ写真を何度も読まない）。保存は人が確認画面で OK したあと。
/// </summary>
public sealed class PhotoImporter
{
    readonly AppState _s;
    readonly ExamService _svc;
    public PhotoImporter(AppState s, ExamService svc) { _s = s; _svc = svc; }
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true };
    public static readonly string[] ImageExts = { ".jpg", ".jpeg", ".png" };
    public static bool IsImage(string path) => ImageExts.Contains(Path.GetExtension(path).ToLowerInvariant());
    public static string SidecarPath(string imagePath) => imagePath + ".ocr.json";
    public static string SheetSidecarPath(string imagePath) => imagePath + ".sheet.json";

    /// <summary>写真が入力シート（四隅の印がある）か。</summary>
    public bool IsSheet(string fileName)
    {
        var path = SafePath(fileName);
        if (File.Exists(SheetSidecarPath(path))) return true;
        if (File.Exists(SidecarPath(path))) return false;
        using var img = Image.Load<Rgb24>(path); img.Mutate(c => c.AutoOrient());
        return SheetReader.FindMarkers(img) != null;
    }

    /// <summary>入力シートの読み取り（無ければ読む）。</summary>
    public SheetReadResult ReadSheet(string fileName, bool force = false, long? examIdHint = null)
    {
        var path = SafePath(fileName);
        var side = SheetSidecarPath(path);
        if (!force && File.Exists(side))
        {
            try { var cached = JsonSerializer.Deserialize<SheetReadResult>(File.ReadAllText(side), Json); if (cached != null && (examIdHint == null || cached.ExamId == examIdHint || cached.ExamId != 0)) return cached; } catch { }
        }
        SheetReadResult r;
        lock (_s.OcrSync)
        {
            using var img = Image.Load<Rgb24>(path);
            img.Mutate(c => c.AutoOrient());
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var hint = examIdHint != null ? _svc.Context(examIdHint.Value) : null;
            r = SheetReader.Read(img, _s.Ocr, _s.Masters, hint, id => _svc.Context(id));
            r.FileName = fileName;
            if (r.ExamId == 0 && examIdHint != null) r.ExamId = examIdHint.Value;
            r.Notes.Add($"読み取り {sw.ElapsedMilliseconds / 1000.0:F1} 秒");
        }
        try { File.WriteAllText(side, JsonSerializer.Serialize(r, Json)); } catch { }
        return r;
    }

    /// <summary>確認済みの値と所見を健診回に入れる。写真は「済」へ。</summary>
    public (bool ok, string message, int count) ApplySheet(string fileName, long examId, Dictionary<string, string> values, List<(string section, string code)> findings)
    {
        var path = SafePath(fileName);
        var db = _s.RequireDb();
        var exam = db.GetExam(examId); if (exam == null) return (false, "健診回がありません", 0);
        if (exam.Status is ExamStatus.Confirmed or ExamStatus.Mailed) return (false, "確定済みの健診回には取り込めません", 0);
        var n = 0;
        foreach (var (k, v) in values) { if (string.IsNullOrWhiteSpace(k)) continue; db.SetValue(examId, k, (v ?? "").Trim()); n++; }
        var existing = db.GetFindings(examId);
        foreach (var (sec, code) in findings)
        {
            if (existing.Any(f => f.Section == sec && f.Code == code)) continue;
            var fm = _s.Masters.Finding(sec, code); if (fm == null) continue;
            db.AddFinding(new Finding { ExamId = examId, Section = sec, Code = code, Text = fm.Text, Sort = existing.Count(f => f.Section == sec) });
            if (string.IsNullOrEmpty(db.GetValues(examId).GetValueOrDefault(Keys.SectionDate(sec)))) db.SetValue(examId, Keys.SectionDate(sec), exam.ExamDate);
            n++;
        }
        db.SaveLabFile(new LabFile { FileName = fileName, TakenAt = exam.ExamDate, PatientHint = "入力シート", ExamId = examId, Status = "取込済", ImportedAt = Db.Now(), ItemCount = n });
        db.Log("入力シートを写真から取り込み", examId, $"{fileName}（{n} 件、人が確認済み）");
        _svc.Recompute(examId);
        MoveDone(path, SheetSidecarPath(path));
        return (true, "取り込みました", n);
    }

    void MoveDone(params string[] files)
    {
        var done = Path.Combine(_s.ImportDir, "済"); Directory.CreateDirectory(done);
        foreach (var f in files)
        {
            if (!File.Exists(f)) continue;
            var dest = Path.Combine(done, Path.GetFileName(f));
            try { if (File.Exists(dest)) File.Delete(dest); File.Move(f, dest); } catch { }
        }
    }

    /// <summary>写真を取込フォルダに保存して読む。戻りは保存したファイル名。</summary>
    public string Save(Stream content, string originalName)
    {
        Directory.CreateDirectory(_s.ImportDir);
        var ext = Path.GetExtension(originalName).ToLowerInvariant();
        if (!ImageExts.Contains(ext)) ext = ".jpg";
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string name; var n = 0;
        do { name = $"写真-{stamp}{(n == 0 ? "" : "-" + n)}{ext}"; n++; } while (File.Exists(Path.Combine(_s.ImportDir, name)));
        var path = Path.Combine(_s.ImportDir, name);
        using (var img = Image.Load<Rgb24>(content))
        {
            // 向き（EXIF）を正し、長辺 3000 まで縮める（読み取りに十分で、保存も軽い）
            img.Mutate(c => c.AutoOrient());
            if (Math.Max(img.Width, img.Height) > 3000) { var sc = 3000.0 / Math.Max(img.Width, img.Height); img.Mutate(c => c.Resize((int)(img.Width * sc), (int)(img.Height * sc))); }
            img.Save(path, new JpegEncoder { Quality = 90 });
        }
        return name;
    }

    /// <summary>読み取り結果（無ければ読む）。OCR が無効なら Error 付きで返す。</summary>
    public PhotoReadResult Read(string fileName, bool force = false)
    {
        var path = SafePath(fileName);
        var side = SidecarPath(path);
        if (!force && File.Exists(side))
        {
            try { var cached = JsonSerializer.Deserialize<PhotoReadResult>(File.ReadAllText(side), Json); if (cached != null) return cached; } catch { }
        }
        var eng = _s.Ocr;
        if (eng == null) return new PhotoReadResult { FileName = fileName, Error = "写真の読み取りは使えません：" + _s.OcrStatus };
        PhotoReadResult r;
        lock (_s.OcrSync)
        {
            using var img = Image.Load<Rgb24>(path);
            img.Mutate(c => c.AutoOrient());
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var boxes = eng.Recognize(img);
            r = FalcoPhotoReader.Read(boxes, img.Width, img.Height, _s.Masters);
            r.FileName = fileName;
            r.Notes.Add($"読み取り {sw.ElapsedMilliseconds / 1000.0:F1} 秒、文字の箱 {boxes.Count}");
        }
        try { File.WriteAllText(side, JsonSerializer.Serialize(r, Json)); } catch { }
        return r;
    }

    /// <summary>確認画面用：項目ごとの値の切り抜き（JPEG を base64）。</summary>
    public Dictionary<string, string> Crops(string fileName, PhotoReadResult r)
    {
        var path = SafePath(fileName);
        var crops = new Dictionary<string, string>();
        using var img = Image.Load<Rgb24>(path);
        img.Mutate(c => c.AutoOrient());
        foreach (var it in r.Items)
        {
            if (it.Crop == null || it.Crop.Length < 4) continue;
            var pad = (int)((it.Crop[3] - it.Crop[1]) * 0.35);
            var x0 = Math.Max(0, (int)it.Crop[0] - pad * 2); var y0 = Math.Max(0, (int)it.Crop[1] - pad);
            var x1 = Math.Min(img.Width, (int)it.Crop[2] + pad); var y1 = Math.Min(img.Height, (int)it.Crop[3] + pad);
            if (x1 - x0 < 4 || y1 - y0 < 4) continue;
            using var c = img.Clone(ctx => ctx.Crop(new Rectangle(x0, y0, x1 - x0, y1 - y0)));
            var h = 44.0; var w = Math.Max(1, (int)(c.Width * h / c.Height));
            c.Mutate(ctx => ctx.Resize(w, (int)h));
            using var ms = new MemoryStream();
            c.Save(ms, new JpegEncoder { Quality = 80 });
            crops[it.Item] = "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray());
        }
        return crops;
    }

    /// <summary>表示用の画像（長辺 1400 に縮小）。</summary>
    public byte[] Preview(string fileName)
    {
        using var img = Image.Load<Rgb24>(SafePath(fileName));
        img.Mutate(c => c.AutoOrient());
        if (Math.Max(img.Width, img.Height) > 1400) { var sc = 1400.0 / Math.Max(img.Width, img.Height); img.Mutate(c => c.Resize((int)(img.Width * sc), (int)(img.Height * sc))); }
        using var ms = new MemoryStream(); img.Save(ms, new JpegEncoder { Quality = 80 }); return ms.ToArray();
    }

    public sealed record ConfirmedItem(string Item, string Value);

    /// <summary>確認済みの値を健診回に入れる（同じ項目は上書き、他の項目は残す＝2 枚目の写真を足せる）。写真は「済」へ。</summary>
    public (bool ok, string message, int count) Apply(string fileName, long examId, List<ConfirmedItem> items, string takenAt)
    {
        var path = SafePath(fileName);
        var db = _s.RequireDb();
        var exam = db.GetExam(examId); if (exam == null) return (false, "健診回がありません", 0);
        if (exam.Status is ExamStatus.Confirmed or ExamStatus.Mailed) return (false, "確定済みの健診回には取り込めません", 0);
        var sex = db.GetPatient(exam.PatientId)?.Sex;
        var n = 0;
        foreach (var ci in items)
        {
            var value = (ci.Value ?? "").Trim();
            if (string.IsNullOrWhiteSpace(ci.Item) || value == "") continue;
            var b = new BloodResult { Item = ci.Item.Trim(), Value = value, TakenAt = takenAt != "" ? takenAt : exam.ExamDate, SourceFile = fileName };
            b.Category = _s.Criteria.Blood.CategoryOf(b.Item);
            _s.Masters.Canonicalize(b, sex);
            if (b.NumericValue is { } v) { if (b.RefHigh != null && v > b.RefHigh) b.Flag = "↑"; else if (b.RefLow != null && v < b.RefLow) b.Flag = "↓"; }
            db.UpsertBloodItem(examId, b); n++;
        }
        db.SaveLabFile(new LabFile { FileName = fileName, TakenAt = takenAt, PatientHint = "写真", ExamId = examId, Status = "取込済", ImportedAt = Db.Now(), ItemCount = n });
        db.Log("採血結果を写真から取り込み", examId, $"{fileName}（{n} 項目、人が確認済み）");
        _svc.Recompute(examId);
        try
        {
            var done = Path.Combine(_s.ImportDir, "済"); Directory.CreateDirectory(done);
            foreach (var f in new[] { path, SidecarPath(path) })
            {
                if (!File.Exists(f)) continue;
                var dest = Path.Combine(done, Path.GetFileName(f));
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(f, dest);
            }
        }
        catch (Exception ex) { return (true, "取り込みましたが、写真を「済」へ移せませんでした: " + ex.Message, n); }
        return (true, "取り込みました", n);
    }

    string SafePath(string fileName)
    {
        var name = Path.GetFileName(fileName);
        var path = Path.Combine(_s.ImportDir, name);
        if (!File.Exists(path)) throw new FileNotFoundException("ファイルがありません: " + name);
        return path;
    }
}
