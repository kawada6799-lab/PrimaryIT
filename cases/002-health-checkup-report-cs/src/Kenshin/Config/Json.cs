using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kenshin.Config;

public static class Json
{
    /// <summary>「_」で始まるキー（_comment など）を取り除いてから型に変換する。設定ファイルに説明を書けるようにするため。</summary>
    public static T Load<T>(string path) where T : new()
    {
        JsonNode? node;
        try { node = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException ex)
        {
            // よくある書き間違いを日本語で示す（黒い画面に英語のスタックトレースだけ出ても直せないので）
            var hint = ex.Message.Contains("invalid escapable character")
                ? "パスの \\ は 2 つ重ねて \\\\ と書くか、/ にしてください（例 \"C:/kenshin-backup\" または \"C:\\\\kenshin-backup\"）"
                : "JSON の形が崩れています（カンマ・引用符・波かっこを確認）";
            throw new InvalidOperationException($"{Path.GetFileName(path)} の {ex.LineNumber + 1} 行目を読めません。{hint}", ex);
        }
        if (node == null) return new T();
        Strip(node);
        return node.Deserialize<T>(AppConfig.JsonOptions) ?? new T();
    }

    static void Strip(JsonNode node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(kv => kv.Key).Where(k => k.StartsWith('_')).ToList()) o.Remove(key);
                foreach (var kv in o) if (kv.Value != null) Strip(kv.Value);
                break;
            case JsonArray a:
                foreach (var item in a) if (item != null) Strip(item);
                break;
        }
    }
}
