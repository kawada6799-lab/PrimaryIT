using System.Text.Json;
using System.Text.Json.Nodes;

namespace Precheck;

/// <summary>通知済み（患者×検査日）を記録する小さな JSON ファイル。Python 版と同じ形式。</summary>
public sealed class State
{
    private readonly string _path;
    private readonly JsonObject _data;

    public State(string path)
    {
        _path = path;
        _data = File.Exists(path)
            ? (JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject())
            : new JsonObject();
        _data["processed_notifications"] ??= new JsonObject();
        _data["notified_findings"] ??= new JsonObject();
    }

    private JsonObject Notified => (JsonObject)_data["notified_findings"]!;

    public bool IsNotified(string key) => NotifiedCount(key) > 0;

    /// <summary>これまでに知らせた回数。旧形式（日時の文字列だけ）は 1 回とみなす。</summary>
    public int NotifiedCount(string key)
    {
        if (!Notified.TryGetPropertyValue(key, out var v) || v is null) return 0;
        if (v is JsonObject o && o.TryGetPropertyValue("count", out var c) && c is not null) return c.GetValue<int>();
        return 1;
    }

    public void MarkNotified(string key)
    {
        var n = NotifiedCount(key) + 1;
        Notified[key] = new JsonObject { ["count"] = n, ["last"] = DateTime.Now.ToString("s") };
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, _data.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
        File.Move(tmp, _path, overwrite: true);
    }
}
