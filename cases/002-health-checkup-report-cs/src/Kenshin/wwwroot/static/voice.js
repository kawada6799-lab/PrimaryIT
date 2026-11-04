// 音声入力（限定語彙）。マイクの音を 16kHz のモノラル float32 にして /api/voice へ送る。外部には送らない。
// 結果の数値・命令語は app.js の applyVoice() が欄に入れる。
window.Voice = (() => {
  let ctx = null, stream = null, source = null, proc = null, chunks = [], recording = false, onDone = null;
  const TARGET = 16000;
  function downsample(buf, from) {
    if (from === TARGET) return buf;
    const ratio = from / TARGET, n = Math.floor(buf.length / ratio), out = new Float32Array(n);
    for (let i = 0; i < n; i++) { const s = Math.floor(i * ratio), e = Math.floor((i + 1) * ratio); let sum = 0, c = 0; for (let j = s; j < e && j < buf.length; j++) { sum += buf[j]; c++; } out[i] = c ? sum / c : 0; }
    return out;
  }
  async function start(done) {
    if (recording) return;
    if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) throw new Error('この接続ではマイクが使えません。https のアドレスで開いてください。');
    stream = await navigator.mediaDevices.getUserMedia({ audio: { channelCount: 1, noiseSuppression: true, echoCancellation: true } });
    ctx = new (window.AudioContext || window.webkitAudioContext)();
    source = ctx.createMediaStreamSource(stream);
    proc = ctx.createScriptProcessor(4096, 1, 1);
    chunks = []; onDone = done; recording = true;
    proc.onaudioprocess = e => { if (recording) chunks.push(downsample(new Float32Array(e.inputBuffer.getChannelData(0)), ctx.sampleRate)); };
    source.connect(proc); proc.connect(ctx.destination);
  }
  async function stop() {
    if (!recording) return;
    recording = false;
    try { proc.disconnect(); source.disconnect(); stream.getTracks().forEach(t => t.stop()); await ctx.close(); } catch (e) { }
    const total = chunks.reduce((a, c) => a + c.length, 0), all = new Float32Array(total);
    let o = 0; for (const c of chunks) { all.set(c, o); o += c.length; }
    chunks = [];
    if (all.length < TARGET * 0.3) { if (onDone) onDone({ error: '短すぎます。ボタンを押している間に話してください。' }); return; }
    try {
      const r = await fetch('/api/voice', { method: 'POST', headers: { 'content-type': 'application/octet-stream' }, body: all.buffer });
      const j = await r.json();
      if (onDone) onDone(j);
    } catch (e) { if (onDone) onDone({ error: String(e) }); }
  }
  return { start, stop, get recording() { return recording; } };
})();
