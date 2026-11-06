using Kenshin.Reports;
using Xunit;
using Xunit.Abstractions;

namespace Kenshin.Tests;

public class NoticeDebugTests
{
    readonly ITestOutputHelper _o;
    public NoticeDebugTests(ITestOutputHelper o) { _o = o; }

    [Theory]
    [InlineData(0)]
    public void ページごとに生成(int page)
    {
        if (Environment.GetEnvironmentVariable("KENSHIN_NOTICE_DEBUG") != "1") return;
        var (state, svc, examId, _) = ReportTestsFacade.Setup();
        NoticeDocument.DebugOnlyPage = page >= 1200 ? 12 : page; NoticeDocument.DebugMaxGroups = page >= 1200 ? page - 1200 : 0;
        try
        {
            var task = Task.Run(() => ReportService.Render(state, svc, examId, "notice"));
            Assert.True(task.Wait(TimeSpan.FromSeconds(40)), $"page {page} が 40 秒で終わらない");
            _o.WriteLine($"page {page}: {task.Result.Length} bytes");
        }
        finally { NoticeDocument.DebugOnlyPage = 0; NoticeDocument.DebugMaxGroups = 0; }
    }
}
