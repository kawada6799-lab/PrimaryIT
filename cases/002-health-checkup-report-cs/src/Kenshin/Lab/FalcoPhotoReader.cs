using System.Text.RegularExpressions;
using Kenshin.Config;
using Kenshin.Domain;
using Kenshin.Ocr;

namespace Kenshin.Lab;

/// <summary>写真から読んだ 1 項目。左右の写しの両方の読みと、確定してよいかの判定。</summary>
public sealed class PhotoItem
{
    public string Item { get; set; } = "";
    public string Kind { get; set; } = "数値";
    public string Category { get; set; } = "";
    public string Unit { get; set; } = "";
    public string RefText { get; set; } = "";
    /// <summary>左の写し（単位・基準値のある方）の読み</summary>
    public string Left { get; set; } = "";
    public float LeftScore { get; set; }
    /// <summary>右の写し（低値・基準値・高値の印のある方）の読み</summary>
    public string Right { get; set; } = "";
    public float RightScore { get; set; }
    /// <summary>採用する値（空なら読めていない）</summary>
    public string Value { get; set; } = "";
    /// <summary>ok＝左右一致で妥当、check＝人が見る、missing＝読めていない</summary>
    public string Status { get; set; } = "missing";
    public string Reason { get; set; } = "";
    /// <summary>左の写しの値の位置（元画像の画素）。確認画面の切り抜きに使う</summary>
    public float[]? Crop { get; set; }
    /// <summary>紙に印字された ↑↓（左の写し）</summary>
    public string Flag { get; set; } = "";
}

public sealed class PhotoReadResult
{
    public string FileName { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public string NameKana { get; set; } = "";
    public string Sex { get; set; } = "";
    public string PatientNo { get; set; } = "";
    public string RequestNo { get; set; } = "";
    public string Page { get; set; } = "";
    public string TakenAt { get; set; } = "";
    public string ReportedAt { get; set; } = "";
    public string BirthHint { get; set; } = "";
    public List<PhotoItem> Items { get; set; } = new();
    public List<string> Notes { get; set; } = new();
    public string Error { get; set; } = "";
    public int OkCount => Items.Count(i => i.Status == "ok");
    public int CheckCount => Items.Count(i => i.Status == "check");
}

/// <summary>
/// ファルコの検査報告書の写真を読む。報告書は左右に同じ値が 2 回印字されている（左：単位・基準値付き、右：低値/高値の印付き）ので、
/// 両方を読んで一致したものだけ自動で確定し、食い違い・読めない・桁が怪しいものは「要確認」にする。
/// 文字の読み取り自体は OcrEngine（PC 内の OCR）。ここは「どの文字が何の項目の値か」を決めるだけ。
/// </summary>
public static class FalcoPhotoReader
{
    static readonly Regex NumRe = new(@"^(\d+(?:\.\d+)?)\s*(以下|未満|以上|超)?$");
    static readonly Regex ReqRe = new(@"(\d{2}-\d{3}-\d{3})\s*[\(（]\s*(\d)\s*/\s*(\d)\s*[\)）]");
    // 「'26/11/ 2 09:59」の空白が落ちて「26/11/209:59」と読まれても、日と時刻を分けられるようにする（時刻は 2 桁:2 桁）
    static readonly Regex DateRe = new(@"[’'‘,，]?\s*(\d{2})\s*/\s*(\d{1,2})\s*/\s*(\d{1,2})(?:\s*\d{2}:\d{2})?(?!\d)");

    public static PhotoReadResult Read(List<OcrBox> boxes, int width, int height, Masters masters)
    {
        var r = new PhotoReadResult { Width = width, Height = height };
        if (boxes.Count == 0) { r.Error = "文字が読み取れませんでした（暗い・ぼけている・用紙が入っていない）"; return r; }
        // ---- 見出し「検査結果」（左右 2 つ）で列の位置を決める ----
        var heads = boxes.Where(b => IsHeader(b.Text)).OrderBy(b => b.X0).ToList();
        if (heads.Count == 0) { r.Error = "表の見出し（検査結果）が見つかりません。用紙全体が入るように撮ってください"; return r; }
        var hw = heads.Average(h => h.W); var hh = heads.Average(h => h.H);
        var tableTop = heads.Max(h => h.Y1);
        OcrBox left = heads[0], right = heads.Count > 1 ? heads[^1] : heads[0];
        if (heads.Count == 1)
        {
            // 片方しか読めなければ、もう片方を紙の比率から置く（左右の「検査結果」の間隔 ≒ 幅の 0.49）
            var dx = width * 0.489f;
            if (left.X0 < width * 0.5f) right = left with { X0 = left.X0 + dx, X1 = left.X1 + dx };
            else { right = left; left = left with { X0 = left.X0 - dx, X1 = left.X1 - dx }; }
            r.Notes.Add("見出しが片方しか読めなかったので、もう片方の位置は推定");
        }
        var split = (left.X1 + right.X0) / 2 + (right.X0 - left.X1) * 0.15f; // 左右の写しの境（右の写しの項目名の少し左）
        // ---- 上部（受診者の情報）----
        ReadHeader(boxes.Where(b => b.Y1 < tableTop - hh * 0.5f).ToList(), r, width);
        // ---- 行：表の中の箱を y でまとめる ----
        var body = boxes.Where(b => b.Cy > tableTop && !IsFooter(b.Text)).ToList(); // 中心が見出しより下の箱だけ（最初の行の背の高い箱を落とさない）
        var rowTol = hh * 0.55f;
        var rows = new List<List<OcrBox>>();
        foreach (var b in body.OrderBy(b => b.Cy))
        {
            if (rows.Count > 0 && Math.Abs(rows[^1].Average(x => x.Cy) - b.Cy) < rowTol) rows[^1].Add(b);
            else rows.Add(new List<OcrBox> { b });
        }
        // 列の範囲：値は見出しの x0−0.2w 〜 x1+0.85w（右寄せ）。項目名はそれより左。単位・基準値はそれより右
        (float a, float b) ValRange(OcrBox h) => (h.X0 - hw * 0.25f, h.X1 + hw * 0.9f);
        var lv = ValRange(left); var rv = ValRange(right);
        var items = new Dictionary<string, PhotoItem>();
        BloodItemMaster? pendingLdl = null; // 「LDLコレステロール」の次の行「計算法」に値がある
        foreach (var row in rows)
        {
            var cy = row.Average(x => x.Cy);
            foreach (var (side, h, vr) in new[] { (0, left, lv), (1, right, rv) })
            {
                var inSide = row.Where(b => side == 0 ? b.Cx < split : b.Cx >= split).OrderBy(b => b.X0).ToList();
                if (inSide.Count == 0) continue;
                var nameBoxes = inSide.Where(b => b.X1 <= vr.a + hw * 0.1f && !IsFlagMark(b.Text)).ToList();
                var valBoxes = inSide.Where(b => b.Cx >= vr.a && b.Cx <= vr.b && !IsFlagMark(b.Text)).ToList();
                var flag = inSide.Where(b => b.X1 <= vr.a && IsFlagMark(b.Text)).Select(b => NormFlag(b.Text)).FirstOrDefault() ?? "";
                // 矢印が値の箱にくっついて読まれることがある（「↑194」「个194」）
                var rawVal = string.Join("", valBoxes.Select(b => b.Text));
                if (flag == "" && Regex.IsMatch(rawVal, @"^[↑个†▲]")) flag = "↑"; else if (flag == "" && Regex.IsMatch(rawVal, @"^[↓▼]")) flag = "↓";
                var nameText = string.Join("", nameBoxes.Select(b => b.Text));
                BloodItemMaster? m = null;
                if (nameText.Length > 0) m = masters.MatchBloodItem(nameText) ?? Fuzzy(masters, nameText);
                if (m == null && nameText.Contains("計算法") && pendingLdl != null) m = pendingLdl;
                if (m != null && m.Item.StartsWith("LDL") && valBoxes.Count == 0) { pendingLdl = m; continue; }
                if (m == null) continue;
                if (!m.Item.StartsWith("LDL")) pendingLdl = null;
                var val = string.Join(" ", valBoxes.Select(b => b.Text));
                var score = valBoxes.Count == 0 ? 0 : valBoxes.Min(b => b.Score);
                if (!items.TryGetValue(m.Item, out var it))
                {
                    it = new PhotoItem { Item = m.Item, Kind = m.Kind, Category = m.Category, Unit = m.Unit, RefText = m.RefMale };
                    items[m.Item] = it;
                }
                var norm = NormalizeValue(val, m.Kind);
                if (side == 0)
                {
                    if (it.Left == "" && norm != "") { it.Left = norm; it.LeftScore = score; it.Flag = flag; if (valBoxes.Count > 0) it.Crop = new[] { valBoxes.Min(b => b.X0), valBoxes.Min(b => b.Y0), valBoxes.Max(b => b.X1), valBoxes.Max(b => b.Y1) }; }
                }
                else if (it.Right == "" && norm != "") { it.Right = norm; it.RightScore = score; }
            }
        }
        // ---- 判定 ----
        foreach (var it in items.Values)
        {
            Decide(it, masters);
            r.Items.Add(it);
        }
        r.Items = r.Items.OrderBy(i => masters.BloodItems.FindIndex(m => m.Item == i.Item)).ToList();
        if (r.Items.Count == 0) r.Error = "項目が 1 つも読めませんでした";
        return r;
    }

    static bool IsHeader(string t) { var n = Masters.NormalizeItemName(t); return n is "検査結果" or "検查結果" or "検査结果" or "検查结果"; }
    static bool IsFooter(string t) => t.Contains("ファルコ") || t.Contains("責任者") || t.Contains("ご報告") || t.Contains("FALCO") || t.Contains("名古屋");
    static bool IsFlagMark(string t) => t is "↑" or "↓" or "*" or "米" or "＊" or "H" or "L" or "▲" or "▼" or "†" or "t" or "+" ;
    static string NormFlag(string t) => t is "↑" or "▲" or "H" or "†" or "t" ? "↑" : t is "↓" or "▼" or "L" ? "↓" : "";

    /// <summary>名前の読み違い（検查→検査、单→単、値→值 など）を吸収して、最長共通部分列の比率で当てる。</summary>
    static BloodItemMaster? Fuzzy(Masters masters, string text)
    {
        var n = Masters.NormalizeItemName(Fix(text));
        if (n.Length < 2) return null;
        BloodItemMaster? best = null; double bs = 0;
        foreach (var m in masters.BloodItems)
        {
            foreach (var c in new[] { m.Item }.Concat(m.Aliases))
            {
                var cn = Masters.NormalizeItemName(c);
                if (cn.Length < 2) continue;
                var s = (double)Lcs(n, cn) / Math.Max(n.Length, cn.Length);
                if (s > bs) { bs = s; best = m; }
            }
        }
        return bs >= 0.6 ? best : null;
    }
    static string Fix(string s) => s.Replace('查', '査').Replace('单', '単').Replace('值', '値').Replace('测', '測').Replace('满', '満').Replace("ブミン", "ブミン").Replace('へ', 'ヘ').Replace('ぺ', 'ペ');
    static int Lcs(string a, string b)
    {
        var dp = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i < a.Length; i++) for (var j = 0; j < b.Length; j++)
            dp[i + 1, j + 1] = a[i] == b[j] ? dp[i, j] + 1 : Math.Max(dp[i, j + 1], dp[i + 1, j]);
        return dp[a.Length, b.Length];
    }

    /// <summary>読んだ文字を値の表記に揃える。数値：「0.05未満」「1.0」。定性：陰性／陽性／（−）／（+）。</summary>
    public static string NormalizeValue(string raw, string kind)
    {
        var t = Num.ToHalfWidth(raw).Replace(" ", "").Replace("　", "").Replace("未满", "未満").Replace("，", "").Replace(",", ".");
        if (t.Length == 0) return "";
        if (kind == "定性")
        {
            if (t.Contains("陰") || t.Contains("インセイ") || t.Contains("阴")) return "陰性";
            if (t.Contains("陽") || t.Contains("ヨウセイ") || t.Contains("阳")) return "陽性";
            var q = t.Replace("(", "").Replace(")", "").Replace("（", "").Replace("）", "");
            if (q is "+" or "＋" or "十") return "（+）";
            if (q is "-" or "−" or "一" or "ー" or "―" or "－") return "（−）";
            if (q is "±" or "+-" or "+−") return "（±）";
            return t;
        }
        // 数値：先頭の矢印（↑ が「个」「t」と読まれる）と、末尾にくっついた単位（%、mg/dL など）を外す
        t = Regex.Replace(t, @"^[↑↓个†tT▲▼\|lI]+(?=\d)", "");
        t = Regex.Replace(t, @"(?<=\d|以下|未満|以上)(%|％|mg/dL|g/dL|U/L|IU/mL|mEq/L|fL|pg|ng/mL|U/mL|μg/dL|ug/dL)$", "");
        // O→0、l/I→1 のような読み違いは数字の並びの中だけ直す
        t = t.Replace('O', '0').Replace('o', '0').Replace('l', '1').Replace('I', '1');
        var m = NumRe.Match(t);
        if (m.Success) return m.Groups[1].Value + m.Groups[2].Value;
        // 「陰性」などが数値項目の欄に来た（定性の行の読み違い）
        if (t.Contains("陰") || t.Contains("陽")) return t.Contains("陰") ? "陰性" : "陽性";
        return t;
    }

    static void Decide(PhotoItem it, Masters masters)
    {
        var m = masters.BloodItems.FirstOrDefault(x => x.Item == it.Item);
        var l = it.Left; var rr = it.Right;
        if (l == "" && rr == "") { it.Status = "missing"; it.Reason = "どちらの写しからも読めていない"; return; }
        if (it.Kind == "定性")
        {
            it.Value = l != "" ? l : rr;
            if (it.Value is "陽性" or "（+）" or "（±）") { it.Status = "check"; it.Reason = "陽性の読み。必ず紙と見比べる"; return; }
            if (l != "" && rr != "" && l == rr) { it.Status = "ok"; it.Reason = "左右一致"; return; }
            it.Status = "check"; it.Reason = l == "" || rr == "" ? "片方の写ししか読めていない" : $"左右が食い違う（{l} ／ {rr}）"; return;
        }
        var lok = NumRe.IsMatch(l); var rok = NumRe.IsMatch(rr);
        var pick = lok && rok ? (it.LeftScore >= it.RightScore ? l : rr) : lok ? l : rok ? rr : (l != "" ? l : rr);
        it.Value = pick;
        if (!NumRe.IsMatch(pick)) { it.Status = "check"; it.Reason = "数値として読めていない"; return; }
        var plausible = Plausible(pick, m?.RefMale ?? "");
        if (lok && rok && l == rr)
        {
            if (plausible) { it.Status = "ok"; it.Reason = "左右一致"; }
            else { it.Status = "check"; it.Reason = "左右一致だが基準値と桁が合わない（小数点の読み落としの可能性）"; }
            return;
        }
        it.Status = "check";
        it.Reason = lok && rok ? $"左右が食い違う（{l} ／ {rr}）" : "片方の写ししか読めていない";
        if (!plausible) it.Reason += "。基準値と桁が合わない";
    }

    /// <summary>基準値と比べて桁が妥当か（10 倍・1/10 を弾く）。基準値が無ければ妥当とみなす。</summary>
    public static bool Plausible(string value, string refText)
    {
        var v = Num.Parse(value); if (v == null) return false;
        var nums = Regex.Matches(Num.ToHalfWidth(refText), @"\d+(?:\.\d+)?").Select(x => double.Parse(x.Value, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        if (nums.Count == 0) return true;
        var lo = nums.Min(); var hi = nums.Max();
        if (nums.Count == 1 && (refText.Contains("以下") || refText.Contains("未満"))) lo = 0;      // 上限だけの基準値
        if (nums.Count == 1 && (refText.Contains("以上") || refText.Contains("超"))) hi = double.MaxValue; // 下限だけ
        return v <= hi * 3.5 && (lo <= 0 || v >= lo / 3.5);
    }

    static void ReadHeader(List<OcrBox> top, PhotoReadResult r, int width)
    {
        if (top.Count == 0) return;
        var hh = top.Average(b => b.H);
        var rows = new List<List<OcrBox>>();
        foreach (var b in top.OrderBy(b => b.Cy))
        {
            if (rows.Count > 0 && Math.Abs(rows[^1].Average(x => x.Cy) - b.Cy) < hh * 0.6f) rows[^1].Add(b);
            else rows.Add(new List<OcrBox> { b });
        }
        var nameFromSexRow = false;
        foreach (var row in rows)
        {
            var texts = row.OrderBy(b => b.X0).Select(b => (b, t: Num.ToHalfWidth(b.Text))).ToList();
            var line = string.Join(" ", texts.Select(x => x.t));
            var req = ReqRe.Match(line.Replace(" ", ""));
            if (req.Success && r.RequestNo == "") { r.RequestNo = req.Groups[1].Value; r.Page = $"{req.Groups[2].Value}/{req.Groups[3].Value}"; }
            if (line.Contains("男性") && r.Sex == "") r.Sex = "男"; else if (line.Contains("女性") && r.Sex == "") r.Sex = "女";
            // 氏名（カナ）：「男性/女性」のある行（無ければ「様」のある行）の、左半分にあるカナの箱。依頼元のクリニック名の行は除く
            var sexRow = line.Contains("男性") || line.Contains("女性");
            if ((r.NameKana == "" || (sexRow && !nameFromSexRow)) && (sexRow || line.Contains("様")) && !line.Contains("クリニック") && !line.Contains("医院") && !line.Contains("病院"))
            {
                var kana = texts.FirstOrDefault(x => x.b.Cx < width * 0.45f && Regex.IsMatch(x.t, @"^[ァ-ヶー゛゜”“\s　]{2,}$"));
                if (kana.b != null) { r.NameKana = Regex.Replace(kana.t.Replace("”", "゛").Replace("“", "゛"), @"[\s　]+", " ").Trim(); nameFromSexRow = sexRow; }
            }
            // 患者番号：生年月日（「生」）のある行の、左端の数字
            if (r.PatientNo == "" && line.Contains("生"))
            {
                var no = texts.FirstOrDefault(x => x.b.Cx < width * 0.3f && Regex.IsMatch(x.t, @"^\d{1,6}$"));
                if (no.b != null) r.PatientNo = no.t;
                var d = DateRe.Match(line); if (d.Success && r.BirthHint == "") r.BirthHint = d.Value.Trim();
            }
            if (line.Contains("採取日") || line.Contains("受付日") || line.Contains("報告日"))
            {
                var idx = line.IndexOf("採取日", StringComparison.Ordinal);
                var dm = idx >= 0 ? DateRe.Match(line, idx) : Match.Empty;
                if (dm.Success && r.TakenAt == "") r.TakenAt = ToDate(dm);
                var ri = line.IndexOf("報告日", StringComparison.Ordinal);
                var rm = ri >= 0 ? DateRe.Match(line, ri) : Match.Empty;
                if (rm.Success && r.ReportedAt == "") r.ReportedAt = ToDate(rm);
                if (r.TakenAt == "") { var am = DateRe.Match(line); if (am.Success) r.TakenAt = ToDate(am); }
            }
        }
    }
    static string ToDate(Match m) => $"20{m.Groups[1].Value}-{int.Parse(m.Groups[2].Value):D2}-{int.Parse(m.Groups[3].Value):D2}";
}
