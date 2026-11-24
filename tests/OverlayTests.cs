using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;

internal static class OverlayTests
{
    private static int _passed;

    private static void Assert(bool cond, string message)
    {
        if (!cond) throw new Exception("ASSERT FAILED: " + message);
        _passed++;
    }

    [STAThread]
    public static int Main(string[] args)
    {
        TestFreeRangeAfterModel();
        TestQuotaWindowAndAlerts();
        TestWindowTitleSessionMatch();
        TestUiaQuotaParser();
        TestNarrowPriorityTrim();
        TestDualSlotQuotaRoundTrip();
        Console.WriteLine("C# OverlayTests passed: " + _passed + " assertions.");
        return 0;
    }

    private static void TestFreeRangeAfterModel()
    {
        // Mirrors real Antigravity 2.0 UIA geometry:
        // Input row: X=874..1633, Model button: X=904..1047, Permission button: X=1452..1567
        var row = new Rect(874, 1080, 759, 28);
        var model = new Rect(904, 1080, 143, 28);
        var others = new[]
        {
            new Rect(876, 1080, 29, 28),
            new Rect(1452, 1080, 115, 28),
            new Rect(1568, 1080, 29, 28),
            new Rect(1602, 1080, 29, 28)
        };
        Rect free = OverlayLayout.FreeRangeAfterModel(row, model, others, 1.0);
        Assert(!free.IsEmpty, "FreeRangeAfterModel should locate gap between model and permission button");
        Assert(free.Left >= 1052 && free.Right <= 1447, "Gap should respect 6px control margins");
        Assert(free.Width >= 385, "Gap width should be ~393px");

        Rect placed = OverlayLayout.PlaceInRange(free.Left, free.Right, free.Top, 300, 24, 1.0);
        Assert(!placed.IsEmpty && placed.Width == 300, "Strip should fit inside gap");
        Rect tooWide = OverlayLayout.PlaceInRange(free.Left, free.Right, free.Top, 500, 24, 1.0);
        Assert(tooWide.IsEmpty, "Oversized strip should refuse placement");
    }

    private static void TestQuotaWindowAndAlerts()
    {
        var now = new DateTime(2026, 11, 24, 3, 0, 0, DateTimeKind.Utc);
        var q5hWarn = QuotaEvaluator.Evaluate("claude", 9.5, now.AddHours(3), now);
        Assert(q5hWarn.WindowKind == "5h", "3h reset must classify as 5h window");
        Assert(q5hWarn.Alert == QuotaAlertLevel.Warn5h, "9.5% in 5h must warn");

        var q5hOk = QuotaEvaluator.Evaluate("gemini", 25.0, now.AddHours(3), now);
        Assert(q5hOk.WindowKind == "5h" && q5hOk.Alert == QuotaAlertLevel.None, "25% in 5h should be normal");

        var q7dCrit = QuotaEvaluator.Evaluate("gemini", 4.0, now.AddDays(3), now);
        Assert(q7dCrit.WindowKind == "7d", ">6h reset must classify as 7d window");
        Assert(q7dCrit.Alert == QuotaAlertLevel.Critical7d, "4% in 7d must trigger critical alert");

        var qPassed = QuotaEvaluator.Evaluate("gemini", 0.0, now.AddMinutes(-5), now);
        Assert(qPassed.Alert == QuotaAlertLevel.ResetPassed && qPassed.CountdownText == "已重置", "Past reset must display 已重置");
    }

    private static void TestWindowTitleSessionMatch()
    {
        var sessions = new Dictionary<string, SessionStats>(StringComparer.OrdinalIgnoreCase)
        {
            { "id-1", new SessionStats { ConversationId = "id-1", Title = "Antigravity Usage Monitor Development Ideas", LastModifiedUtc = DateTime.UtcNow } },
            { "id-2", new SessionStats { ConversationId = "id-2", Title = "Other Task", LastModifiedUtc = DateTime.UtcNow.AddMinutes(-10) } }
        };
        var matched = MonitorSnapshot.MatchSessionByWindowTitle("Antigravity - 电脑 - Antigravity Usage... (WSL: Ubuntu)", sessions);
        Assert(matched != null && matched.ConversationId == "id-1", "Window title prefix should match active session");
        Assert(AnchorFinder.ExtractModelName("Select model, current: Gemini 4 Neon High") == "Gemini 4 Neon High", "Should extract model name from UIA button label");
    }

    private static void TestUiaQuotaParser()
    {
        var now = new DateTime(2026, 11, 24, 3, 25, 0, DateTimeKind.Utc);
        // Exact real Antigravity 2.0 Chinese "模型额度" modal lines
        var lines = new[]
        {
            "模型额度",
            "启用 AI 额度超量使用",
            "编排模型",
            "Gemini 4 Neon Extra High",
            "Gemini 模型",
            "剩余每周限制",
            "6 天 16 小时后刷新",
            "70%",
            "剩余五小时限制",
            "2 小时 43 分钟后刷新",
            "5%",
            "Claude 和 GPT 模型",
            "剩余每周限制",
            "6 天 22 小时后刷新",
            "56%",
            "剩余五小时限制",
            "3 小时 25 分钟后刷新",
            "12%"
        };
        PoolQuotaState g, c;
        bool ok = UiaQuotaParser.TryParseFromLines(lines, now, out g, out c);
        Assert(ok && g != null && c != null, "UIA parser should extract both pools from real Chinese modal");
        Assert(g.FiveHour != null && Math.Abs(g.FiveHour.RemainingPct - 5.0) < 0.01 && g.FiveHour.Alert == QuotaAlertLevel.Warn5h, "Gemini 5h should be 5% warn");
        Assert(g.Weekly != null && Math.Abs(g.Weekly.RemainingPct - 70.0) < 0.01 && g.Weekly.WindowKind == "7d", "Gemini 7d should be 70%");
        Assert(c.FiveHour != null && Math.Abs(c.FiveHour.RemainingPct - 12.0) < 0.01 && c.FiveHour.WindowKind == "5h", "Claude 5h should be 12%");
        Assert(c.Weekly != null && Math.Abs(c.Weekly.RemainingPct - 56.0) < 0.01 && c.Weekly.WindowKind == "7d", "Claude 7d should be 56%");

        // Also verify real model-menu "使用量" popover (including 100% Claude pool without reset line)
        var popoverLines = new[]
        {
            "使用量",
            "Gemini 模型",
            "周限额",
            "99% 剩余",
            "6 天 16 小时 后重置",
            "5 小时限额",
            "99% 剩余",
            "2 小时 31 分钟 后重置",
            "Claude 和 GPT 模型",
            "周限额",
            "100% 剩余",
            "5 小时限额",
            "100% 剩余"
        };
        PoolQuotaState pg, pc;
        bool popOk = UiaQuotaParser.TryParseFromLines(popoverLines, now, out pg, out pc);
        Assert(popOk && pg != null && pc != null, "Model-menu 使用量 popover should extract both Gemini and 100% Claude pools");
        Assert(pg.FiveHour != null && Math.Abs(pg.FiveHour.RemainingPct - 99.0) < 0.01, "Popover Gemini 5h should be 99%");
        Assert(pc.FiveHour != null && Math.Abs(pc.FiveHour.RemainingPct - 100.0) < 0.01 && pc.FiveHour.CountdownText == "满额", "Popover Claude 5h 100% should show 满额");
        Assert(pc.Weekly != null && Math.Abs(pc.Weekly.RemainingPct - 100.0) < 0.01, "Popover Claude 7d should be 100%");

        // Verify modal where Claude pool omits reset-time lines (e.g. 48% / 0%)
        var omittedResetLines = new[]
        {
            "模型额度",
            "Gemini 模型",
            "剩余每周限制",
            "6 天 16 小时后刷新",
            "91%",
            "剩余五小时限制",
            "2 小时 18 分钟后刷新",
            "71%",
            "Claude 和 GPT 模型",
            "剩余每周限制",
            "48%",
            "剩余五小时限制",
            "0%"
        };
        PoolQuotaState og, oc;
        bool omOk = UiaQuotaParser.TryParseFromLines(omittedResetLines, now, out og, out oc);
        Assert(omOk && og != null && oc != null, "Modal with omitted reset lines on Claude pool must still parse both pools");
        Assert(oc.Weekly != null && Math.Abs(oc.Weekly.RemainingPct - 48.0) < 0.01, "Claude weekly should be 48%");
        Assert(oc.FiveHour != null && Math.Abs(oc.FiveHour.RemainingPct - 0.0) < 0.01, "Claude 5h should be 0%");

        // Verify React split-span nodes (["85", "%", "剩余"], ["6 天 16 小时", "后重置"], ["Claude", "和 GPT 模型"])
        var splitSpanLines = new[]
        {
            "使用量",
            "Gemini", "模型",
            "周限额",
            "85", "%", "剩余",
            "6 天 16 小时", "后重置",
            "5 小时限额",
            "42%", "剩余",
            "2 小时 2 分钟", "后重置",
            "Claude", "和 GPT 模型",
            "周限额",
            "48%", "剩余",
            "6 天 23 小时", "后重置",
            "5 小时限额",
            "0%", "剩余",
            "4 小时 32 分钟", "后重置"
        };
        PoolQuotaState sg, sc;
        bool splitOk = UiaQuotaParser.TryParseFromLines(splitSpanLines, now, out sg, out sc);
        Assert(splitOk && sg != null && sc != null, "Split-span Usage popover lines must merge and parse both pools");
        Assert(sg.FiveHour != null && Math.Abs(sg.FiveHour.RemainingPct - 42.0) < 0.01 && sg.FiveHour.ResetAtUtc.HasValue, "Split Gemini 5h should be 42% with reset");
        Assert(sc.Weekly != null && Math.Abs(sc.Weekly.RemainingPct - 48.0) < 0.01 && sc.Weekly.ResetAtUtc.HasValue, "Split Claude 7d should be 48% with reset");
        Assert(sc.FiveHour != null && Math.Abs(sc.FiveHour.RemainingPct - 0.0) < 0.01 && sc.FiveHour.ResetAtUtc.HasValue, "Split Claude 5h should be 0% with reset");

        // Guard: ordinary chat lines mentioning quota keywords and percentages must NOT be misread
        var chatLines = new[]
        {
            "识别 模型额度、Gemini 模型、Claude 和 GPT 模型、剩余每周限制、剩余五小时限制",
            "从你截图看当前 Gemini 5h 窗口已剩 5%，约 2h 43m 后刷新"
        };
        PoolQuotaState g2, c2;
        bool chatOk = UiaQuotaParser.TryParseFromLines(chatLines, now, out g2, out c2);
        Assert(!chatOk && g2 == null && c2 == null, "Chat prose discussing quota labels must be rejected");
    }

    private static void TestNarrowPriorityTrim()
    {
        var pal = ThemePalette.For(false);
        var items = new List<StripItem>
        {
            new StripItem { Key = "rate", Text = "48.2 tok/s", Ink = pal.InkPrimary },
            new StripItem { Key = "today", Text = "184.5万", Ink = pal.InkMuted },
            new StripItem { Key = "cache", Text = "84.6%", Ink = pal.InkMuted },
            new StripItem { Key = "quota", Text = "G 25% · C 12%", Ink = pal.InkPrimary },
            new StripItem { Key = "context", Text = "上下文 14%", Ink = pal.InkMuted }
        };
        var priority = new List<string> { "rate", "quota", "context", "today", "cache" };
        // Fake width: each item costs 80 dip
        var trimmed = StripBuilder.FitToWidth(items, priority, 250, list => list.Count * 80.0);
        Assert(trimmed.Count == 3, "Should trim down to 3 highest-priority items");
        Assert(trimmed[0].Key == "rate" && trimmed[1].Key == "quota" && trimmed[2].Key == "context", "Should drop cache & today while keeping visual order");
    }

    private static void TestDualSlotQuotaRoundTrip()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "agy-monitor-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var now = new DateTime(2026, 11, 24, 3, 0, 0, DateTimeKind.Utc);
            var g = QuotaEvaluator.Evaluate("gemini", 25.0, now.AddHours(3), now);
            var c = QuotaEvaluator.Evaluate("claude", 12.0, now.AddHours(3), now);
            DualSlotStore.WriteQuotaFromUia(tmp, g, c, now);
            var snap = DualSlotStore.LoadSnapshot(tmp, now);
            Assert(snap.Quota != null && snap.Quota.Gemini != null && snap.Quota.Claude != null, "Should reload dual-slot quota snapshot");
            Assert(MetricFormatter.FormatCompactQuota(snap.Quota, "gemini") == "G 25% · C 12%", "Gemini active should place G first");
            Assert(MetricFormatter.FormatCompactQuota(snap.Quota, "claude") == "C 12% · G 25%", "Claude active should place C first");
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        }
    }
}
