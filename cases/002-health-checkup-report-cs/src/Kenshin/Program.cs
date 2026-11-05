namespace Kenshin;

public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            return Web.Host.Run(args);
        }
        catch (Exception ex)
        {
            // 起動に失敗したときの理由を画面とファイルの両方に残す（黒い画面がすぐ閉じても読めるように）
            var msg = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} 起動に失敗しました: {ex}";
            Console.Error.WriteLine(msg);
            try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "kenshin.log"), msg + Environment.NewLine); } catch { }
            Console.Error.WriteLine("よくある原因: config\\config.json の書き間違い（JSON の形が崩れている）、ポート 5080 が使用中、フォルダが読み取り専用。");
            return 1;
        }
    }
}
