using Kenshin.Web;

namespace Kenshin.Voice;

public static class VoiceEndpoints
{
    public static void Map(WebApplication app, AppState state)
    {
        app.MapGet("/api/voice/status", () => Results.Json(new { available = state.Voice.Available, status = state.Voice.Status }));
        app.MapPost("/api/voice/parse", (Dictionary<string, string> r) => Results.Json(NumberParser.Parse(r.GetValueOrDefault("text", ""))));
        // 本文：16kHz モノラル float32 リトルエンディアンの生データ（ブラウザの Web Audio で作る）
        app.MapPost("/api/voice", async (HttpRequest req, CancellationToken ct) =>
        {
            if (!state.Voice.Available) return Results.Json(new { error = "音声認識は使えません：" + state.Voice.Status }, statusCode: 503);
            using var ms = new MemoryStream();
            await req.Body.CopyToAsync(ms, ct);
            var bytes = ms.ToArray();
            if (bytes.Length < 16000 * 4 / 4) return Results.BadRequest(new { error = "音声が短すぎます" });
            var samples = new float[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 4);
            if (samples.Length > 16000 * 30) return Results.BadRequest(new { error = "30 秒以内にしてください" });
            try
            {
                var text = await state.Voice.RecognizeAsync(samples, ct);
                return Results.Json(new { text, parsed = NumberParser.Parse(text) });
            }
            catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 500); }
        });
    }
}
