using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kenshin.Config;

public static class Json
{
    /// <summary>「_」で始まるキー（_comment など）を取り除いてから型に変換する。設定ファイルに説明を書けるようにするため。</summary>
    public static T Load<T>(string path) where T : new()
    {
        var node = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
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
