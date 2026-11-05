namespace Kenshin.Voice;

public interface IVoiceRecognizer : IDisposable
{
    bool Available { get; }
    string Status { get; }
    /// <summary>16kHz モノラルの float サンプルを文字にする。</summary>
    Task<string> RecognizeAsync(float[] samples16k, CancellationToken ct);
}

public sealed class NoVoiceRecognizer : IVoiceRecognizer
{
    public NoVoiceRecognizer(string status) { Status = status; }
    public bool Available => false;
    public string Status { get; }
    public Task<string> RecognizeAsync(float[] samples16k, CancellationToken ct) => throw new InvalidOperationException("音声認識は使えません：" + Status);
    public void Dispose() { }
}
