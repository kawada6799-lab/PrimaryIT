using System.Text.Json;
using Kenshin.Lab;
using Kenshin.Ocr;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Kenshin.Tests;

/// <summary>写真の読み取り（行の組み立て・左右の突合・要確認の判定）。OCR 自体はモデルがあるときだけ、架空の報告書画像で確かめる。</summary>
public class PhotoReaderTests
{
    // ファルコの紙と同じ並びの「読み取った文字の箱」を作る（座標は 1512×2016 の写真に合わせた架空の値）
    static List<OcrBox> Boxes(params (string left, string val, string valR, string unit, string refText)[] rows)
    {
        var b = new List<OcrBox>
        {
            new(689, 81, 886, 113, "00-625-123(1/2)", 1), new(141, 190, 372, 225, "ケンシン　タロウ", 0.9f), new(533, 188, 570, 220, "様", 1), new(602, 192, 654, 227, "男性", 1),
            new(140, 229, 207, 263, "0001", 1), new(470, 230, 621, 267, "74/5/1生", 1), new(639, 232, 702, 265, "52才", 1),
            new(147, 341, 433, 376, "受付日，26/11/2 報告日", 0.9f), new(427, 342, 571, 376, "'26/11/4", 0.9f), new(563, 342, 638, 374, "採取日", 1), new(659, 345, 866, 377, "26/11/ 2 09:59", 1),
            new(173, 459, 296, 493, "検查项目", 0.9f), new(403, 458, 507, 494, "検查結果", 1), new(597, 458, 663, 493, "单位", 0.8f), new(709, 459, 837, 493, "基準值", 1),
            new(945, 461, 1068, 495, "検查项目", 0.9f), new(1142, 461, 1246, 497, "検查結果", 1), new(1327, 459, 1379, 489, "基準值", 1),
        };
        var y = 493f;
        foreach (var r in rows)
        {
            b.Add(new OcrBox(133, y + 3, 133 + r.left.Length * 20, y + 39, r.left, 1));
            if (r.val != "") b.Add(new OcrBox(560 - r.val.Length * 17, y, 560, y + 39, r.val, 1));
            if (r.unit != "") b.Add(new OcrBox(577, y + 6, 686, y + 42, r.unit, 1));
            if (r.refText != "") b.Add(new OcrBox(730, y + 7, 830, y + 40, r.refText, 1));
            b.Add(new OcrBox(913, y + 8, 913 + r.left.Length * 18, y + 43, r.left, 1));
            if (r.valR != "") b.Add(new OcrBox(1270 - r.valR.Length * 17, y + 7, 1270, y + 46, r.valR, 1));
            y += 39;
        }
        return b;
    }

    [Fact]
    public void 左右一致は確定_食い違い_片方_陽性_桁違いは要確認()
    {
        var boxes = Boxes(
            ("総ビリルビン", "0.8", "0.8", "mg/dL", "0.2~1.2"),
            ("総蛋白", "6.8", "6.3", "g/dL", "6.5~8.3"),          // 食い違い
            ("アルブミン", "4.1", "", "g/dL", "3.8~5.3"),           // 片方だけ
            ("AST (GOT)", "22", "22", "U/L", "8~38"),
            ("ヘモグロビン量", "151", "151", "g/dL", "13.5~17.6"),   // 小数点の読み落とし（桁違い）
            ("CRP定量", "0.05以下", "0.05以下", "mg/dL", "0.30以下"), // 上限だけの基準値：妥当
            ("HBs抗原 精密測定", "陰性インセイ", "陰性　インセイ", "", ""),
            ("RPR 定性", "(+)", "(+)", "", "(-)"),                  // 陽性は必ず要確認
            ("中性脂肪（TG)", "↑194", "194", "mg/dL", "30~149"));   // 矢印がくっついた
        var r = FalcoPhotoReader.Read(boxes, 1512, 2016, TestEnv.Masters());
        Assert.Equal("", r.Error);
        Assert.Equal("ケンシン タロウ", r.NameKana); Assert.Equal("男", r.Sex); Assert.Equal("0001", r.PatientNo);
        Assert.Equal("1974-05-01", r.Birth); Assert.Equal(52, r.Age);
        Assert.Equal("00-625-123", r.RequestNo); Assert.Equal("1/2", r.Page); Assert.Equal("2026-11-02", r.TakenAt); Assert.Equal("2026-11-04", r.ReportedAt);
        var d = r.Items.ToDictionary(i => i.Item);
        Assert.Equal(("ok", "0.8"), (d["総ビリルビン"].Status, d["総ビリルビン"].Value));
        Assert.Equal("check", d["総蛋白"].Status); Assert.Contains("食い違う", d["総蛋白"].Reason);
        Assert.Equal("check", d["アルブミン"].Status); Assert.Equal("4.1", d["アルブミン"].Value);
        Assert.Equal(("ok", "22"), (d["AST（GOT）"].Status, d["AST（GOT）"].Value));
        Assert.Equal("check", d["ヘモグロビン量"].Status); Assert.Contains("桁", d["ヘモグロビン量"].Reason);
        Assert.Equal(("ok", "0.05以下"), (d["CRP定量"].Status, d["CRP定量"].Value));
        Assert.Equal(("ok", "陰性"), (d["HBs抗原（精密測定）"].Status, d["HBs抗原（精密測定）"].Value));
        Assert.Equal(("check", "（+）"), (d["RPR定性"].Status, d["RPR定性"].Value));
        Assert.Equal(("ok", "194", "↑"), (d["中性脂肪（TG）"].Status, d["中性脂肪（TG）"].Value, d["中性脂肪（TG）"].Flag));
        Assert.NotNull(d["総ビリルビン"].Crop);
    }

    [Fact]
    public void LDLは次の行の計算法に値がある()
    {
        var boxes = Boxes(("LDLコレステロール", "", "", "", ""), ("計算法", "126", "126", "mg/dL", "70~139"), ("総コレステロール", "232", "232", "mg/dL", "130~219"));
        var r = FalcoPhotoReader.Read(boxes, 1512, 2016, TestEnv.Masters());
        var d = r.Items.ToDictionary(i => i.Item);
        Assert.Equal(("ok", "126"), (d["LDLコレステロール（計算法）"].Status, d["LDLコレステロール（計算法）"].Value));
        Assert.Equal("232", d["総コレステロール"].Value);
    }

    [Fact]
    public void 見出しが無ければエラー()
    {
        var r = FalcoPhotoReader.Read(new List<OcrBox> { new(10, 10, 100, 40, "なにか", 1) }, 1000, 1000, TestEnv.Masters());
        Assert.Contains("見出し", r.Error);
        Assert.Contains("読み取れません", FalcoPhotoReader.Read(new(), 1000, 1000, TestEnv.Masters()).Error);
    }

    [Theory]
    [InlineData("45.9%", "数値", "45.9")]
    [InlineData("个3.0", "数値", "3.0")]
    [InlineData("0.05未满", "数値", "0.05未満")]
    [InlineData("1O8", "数値", "108")]
    [InlineData("陰性　インセイ", "定性", "陰性")]
    [InlineData("(－)", "定性", "（−）")]
    [InlineData("(+)", "定性", "（+）")]
    public void 値の表記を揃える(string raw, string kind, string expected) => Assert.Equal(expected, FalcoPhotoReader.NormalizeValue(raw, kind));

    [Theory]
    [InlineData("0.6", "2.5以下", true)]
    [InlineData("151", "13.5~17.6", false)]
    [InlineData("15.1", "13.5~17.6", true)]
    [InlineData("71.0", "60.0以上", true)]
    [InlineData("3", "37以下", true)]
    public void 基準値との桁の妥当性(string v, string refText, bool ok) => Assert.Equal(ok, FalcoPhotoReader.Plausible(v, refText));

    [Fact]
    public void 架空の報告書画像をOCRで読む()
    {
        var dir = TestEnv.OcrModelDir; if (dir == null) return; // モデルが無い環境では飛ばす
        var png = Path.Combine(TestEnv.CaseDir, "tests", "Kenshin.Tests", "samples", "falco_sample.png");
        var truth = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(TestEnv.CaseDir, "tests", "Kenshin.Tests", "samples", "falco_sample.json")))!;
        using var eng = OcrEngine.TryLoad(dir)!;
        using var img = Image.Load<Rgb24>(png);
        var boxes = eng.Recognize(img);
        var r = FalcoPhotoReader.Read(boxes, img.Width, img.Height, TestEnv.Masters());
        Assert.Equal("", r.Error);
        Assert.Equal("ケンシンタロウ", r.NameKana.Replace(" ", "")); Assert.Equal("0001", r.PatientNo); Assert.Equal("2026-11-02", r.TakenAt);
        var d = r.Items.ToDictionary(i => i.Item);
        var wrong = truth.Where(t => d.TryGetValue(t.Key, out var it) && it.Status == "ok" && it.Value != t.Value).Select(t => $"{t.Key}: 正 {t.Value} 読 {d[t.Key].Value}").ToList();
        Assert.True(wrong.Count == 0, "自動確定で誤り: " + string.Join(", ", wrong));
        var okCount = truth.Count(t => d.TryGetValue(t.Key, out var it) && it.Status == "ok" && it.Value == t.Value);
        Assert.True(okCount >= truth.Count * 0.8, $"自動確定 {okCount}/{truth.Count}（未検出 {string.Join(",", truth.Keys.Where(k => !d.ContainsKey(k)))}）");
    }
}
