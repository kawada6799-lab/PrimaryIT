using Kenshin.Config;

namespace Kenshin.Voice;

/// <summary>Whisper（whisper.cpp）をプログラム内で動かす。モデルファイルは models\ に置く。外部には何も送らない。</summary>
public sealed class WhisperRecognizer : IVoiceRecognizer
{
    readonly Whisper.net.WhisperFactory _factory;
    readonly Whisper.net.WhisperProcessor _proc;
    readonly SemaphoreSlim _gate = new(1, 1);
    public bool Available => true;
    public string Status { get; }

    WhisperRecognizer(string modelPath, string language, int threads)
    {
        _factory = Whisper.net.WhisperFactory.FromPath(modelPath);
        _proc = _factory.CreateBuilder().WithLanguage(language).WithThreads(Math.Max(1, threads)).WithNoContext().WithSingleSegment().Build();
        Status = "有効（" + Path.GetFileName(modelPath) + "）";
    }

    public static IVoiceRecognizer Create(AppConfig config, string baseDir)
    {
        if (!config.Voice.Enabled) return new NoVoiceRecognizer("設定で無効");
        var path = config.Resolve(config.Voice.ModelPath);
        if (!File.Exists(path)) return new NoVoiceRecognizer("モデルが無い（" + path + "）。README のダウンロード手順を参照");
        try { return new WhisperRecognizer(path, config.Voice.Language, config.Voice.Threads); }
        catch (Exception ex) { return new NoVoiceRecognizer("初期化に失敗: " + ex.Message); }
    }

    public async Task<string> RecognizeAsync(float[] samples16k, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var sb = new System.Text.StringBuilder();
            await foreach (var seg in _proc.ProcessAsync(samples16k, ct)) sb.Append(seg.Text);
            return sb.ToString().Trim();
        }
        finally { _gate.Release(); }
    }

    public void Dispose() { _proc.Dispose(); _factory.Dispose(); }
}
