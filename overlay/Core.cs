using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

internal enum QuotaAlertLevel
{
    None,
    ResetPassed,
    Warn5h,
    Critical7d
}

internal sealed class PoolQuotaState
{
    internal string Pool;
    internal double RemainingPct;
    internal DateTime? ResetAtUtc;
    internal string WindowKind; // "5h", "7d", or ""
    internal QuotaAlertLevel Alert;
    internal string AlertHint;
    internal string CountdownText;
    // Optional secondary window when both 5h and 7d limits are available (e.g. from Antigravity "模型额度" page)
    internal PoolQuotaState FiveHour;
    internal PoolQuotaState Weekly;
    internal BurnRateEstimate BurnRate;
}

internal sealed class QuotaSnapshot
{
    internal int Sequence;
    internal DateTime UpdatedAtUtc;
    internal string Source;
    internal string PlanTier;
    internal PoolQuotaState Gemini;
    internal PoolQuotaState Claude;
}

internal sealed class TodayStats
{
    internal string Date;
    internal long TotalTokens;
    internal long InputTokens;
    internal long CacheReadTokens;
    internal long OutputTokens;
    internal double? CacheHitRate;
    internal int SessionsCount;
}

internal sealed class SessionStats
{
    internal string ConversationId;
    internal string Title;
    internal string Model;
    internal string ModelFamily;
    internal string Status;
    internal DateTime LastModifiedUtc;
    internal long SessionTokens;
    internal long InputTokens;
    internal long CacheReadTokens;
    internal long OutputTokens;
    internal double? CacheHitRate;
    internal double? LastTurnCacheHitRate;
    internal bool CacheCollapsed;
    internal long LastTurnTokens;
    internal long LastTurnOutputTokens;
    internal long? LastTurnDurationMs;
    internal double? TokPerSec;
    internal bool TokPerSecEstimated;
    internal long ContextTokens;
    internal long ContextWindowSize;
    internal double ContextPct;
    internal int SubagentCount;
    internal long SubagentTokens;
    internal long TotalWithSubagentsTokens;
    internal long ExecutionDurationMs;
}

internal sealed class MonitorSnapshot
{
    internal int Sequence;
    internal DateTime UpdatedAtUtc;
    internal string SourceEnv;
    internal string ActiveConversationId;
    internal TodayStats Today;
    internal List<DailyUsagePoint> DailyHistory;
    internal Dictionary<string, SessionStats> Sessions;
    internal QuotaSnapshot Quota;

    internal SessionStats ResolveSession(string windowTitle, string uiModelName)
    {
        if (Sessions == null || Sessions.Count == 0) return null;
        SessionStats matched = MatchSessionByWindowTitle(windowTitle, Sessions);
        if (matched != null) return matched;
        if (!string.IsNullOrEmpty(ActiveConversationId) && Sessions.ContainsKey(ActiveConversationId))
        {
            return Sessions[ActiveConversationId];
        }
        return Sessions.Values.OrderByDescending(s => s.LastModifiedUtc).FirstOrDefault();
    }

    internal static SessionStats MatchSessionByWindowTitle(string windowTitle, Dictionary<string, SessionStats> sessions)
    {
        if (string.IsNullOrWhiteSpace(windowTitle) || sessions == null || sessions.Count == 0) return null;
        // Antigravity window title pattern: "Antigravity - <project> - <title prefix> (WSL: Ubuntu)"
        string cleaned = Regex.Replace(windowTitle, @"\s*\([^)]*\)\s*$", "").Trim();
        string[] parts = cleaned.Split(new[] { " - " }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        string candidate = parts[parts.Length - 1].Trim();
        if (candidate.EndsWith("...")) candidate = candidate.Substring(0, candidate.Length - 3).Trim();
        if (candidate.Length < 3) return null;

        List<SessionStats> matches = new List<SessionStats>();
        foreach (SessionStats s in sessions.Values)
        {
            if (string.IsNullOrEmpty(s.Title)) continue;
            if (s.Title.StartsWith(candidate, StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith(s.Title, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(s);
            }
        }
        if (matches.Count == 1) return matches[0];
        if (matches.Count > 1) return matches.OrderByDescending(m => m.LastModifiedUtc).First();
        return null;
    }
}

internal sealed class OverlaySettings
{
    internal static readonly string[] AllItems = new[] { "rate", "today", "cache", "quota", "context" };
    internal static readonly string[] DefaultPriority = new[] { "rate", "quota", "context", "today", "cache" };

    internal string ThemeMode; // "auto", "dark", "light"
    internal Dictionary<string, bool> Visible;
    internal List<string> Priority;
    internal bool AutoRefreshQuota;
    internal int AutoRefreshIntervalMinutes;
    internal bool EnableMilestoneAlerts;
    internal bool ShowTrendChart;

    internal static OverlaySettings CreateDefault()
    {
        var s = new OverlaySettings();
        s.ThemeMode = "auto";
        s.Visible = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (string k in AllItems) s.Visible[k] = true;
        s.Priority = new List<string>(DefaultPriority);
        s.AutoRefreshQuota = true;
        s.AutoRefreshIntervalMinutes = 10;
        s.EnableMilestoneAlerts = true;
        s.ShowTrendChart = true;
        return s;
    }

    internal static OverlaySettings Load(string path)
    {
        OverlaySettings def = CreateDefault();
        if (!File.Exists(path)) return def;
        try
        {
            var js = new JavaScriptSerializer();
            var raw = js.DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
            if (raw == null) return def;
            object themeObj;
            if (raw.TryGetValue("themeMode", out themeObj) && themeObj is string)
            {
                string tm = ((string)themeObj).ToLowerInvariant();
                if (tm == "auto" || tm == "dark" || tm == "light") def.ThemeMode = tm;
            }
            object autoObj;
            if (raw.TryGetValue("autoRefreshQuota", out autoObj) && autoObj is bool)
            {
                def.AutoRefreshQuota = (bool)autoObj;
            }
            object intObj;
            if (raw.TryGetValue("autoRefreshIntervalMinutes", out intObj) && intObj != null)
            {
                int mins;
                if (int.TryParse(Convert.ToString(intObj, CultureInfo.InvariantCulture), out mins) && mins >= 2 && mins <= 120)
                {
                    def.AutoRefreshIntervalMinutes = mins;
                }
            }
            object alertObj;
            if (raw.TryGetValue("enableMilestoneAlerts", out alertObj) && alertObj is bool)
            {
                def.EnableMilestoneAlerts = (bool)alertObj;
            }
            object trendObj;
            if (raw.TryGetValue("showTrendChart", out trendObj) && trendObj is bool)
            {
                def.ShowTrendChart = (bool)trendObj;
            }
            object visObj;
            if (raw.TryGetValue("visible", out visObj) && visObj is Dictionary<string, object>)
            {
                var visMap = (Dictionary<string, object>)visObj;
                foreach (string k in AllItems)
                {
                    object v;
                    if (visMap.TryGetValue(k, out v) && v is bool) def.Visible[k] = (bool)v;
                }
            }
            object priObj;
            if (raw.TryGetValue("priority", out priObj) && priObj is object[])
            {
                var next = new List<string>();
                foreach (object o in (object[])priObj)
                {
                    string key = o as string;
                    if (key != null && AllItems.Contains(key) && !next.Contains(key)) next.Add(key);
                }
                foreach (string k in DefaultPriority) if (!next.Contains(k)) next.Add(k);
                def.Priority = next;
            }
        }
        catch { }
        return def;
    }

    internal void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var js = new JavaScriptSerializer();
            var payload = new Dictionary<string, object>
            {
                { "themeMode", ThemeMode },
                { "autoRefreshQuota", AutoRefreshQuota },
                { "autoRefreshIntervalMinutes", AutoRefreshIntervalMinutes },
                { "enableMilestoneAlerts", EnableMilestoneAlerts },
                { "showTrendChart", ShowTrendChart },
                { "visible", Visible },
                { "priority", Priority.ToArray() }
            };
            File.WriteAllText(path, js.Serialize(payload), Encoding.UTF8);
        }
        catch { }
    }
}

internal static class QuotaEvaluator
{
    internal static PoolQuotaState Evaluate(string pool, double remainingPct, DateTime? resetAtUtc, DateTime nowUtc)
    {
        var state = new PoolQuotaState();
        state.Pool = pool;
        state.RemainingPct = Math.Max(0.0, Math.Min(100.0, remainingPct));
        state.ResetAtUtc = resetAtUtc;
        state.WindowKind = "";
        state.Alert = QuotaAlertLevel.None;
        state.AlertHint = "";
        state.CountdownText = (state.RemainingPct >= 99.9) ? "满额" : "";

        if (!resetAtUtc.HasValue)
        {
            if (state.RemainingPct <= 5.0)
            {
                state.Alert = QuotaAlertLevel.Critical7d;
                state.AlertHint = "额度紧张";
            }
            else if (state.RemainingPct <= 10.0)
            {
                state.Alert = QuotaAlertLevel.Warn5h;
                state.AlertHint = "额度紧张";
            }
            return state;
        }

        TimeSpan delta = resetAtUtc.Value - nowUtc;
        if (delta.TotalSeconds <= 0)
        {
            state.Alert = QuotaAlertLevel.ResetPassed;
            state.CountdownText = "已重置";
            state.AlertHint = "周期已到，待新读数";
            return state;
        }

        state.CountdownText = MetricFormatter.FormatCountdown(delta);
        if (delta.TotalHours > 6.0)
        {
            state.WindowKind = "7d";
            if (state.RemainingPct <= 5.0)
            {
                state.Alert = QuotaAlertLevel.Critical7d;
                state.AlertHint = "7d 见底，建议准备切号";
            }
        }
        else
        {
            state.WindowKind = "5h";
            if (state.RemainingPct <= 10.0)
            {
                state.Alert = QuotaAlertLevel.Warn5h;
                state.AlertHint = "5h 紧张，可切另一模型家族";
            }
        }
        return state;
    }
}

internal static class UiaQuotaParser
{
    private static readonly Regex ExactPctRegex = new Regex(@"^\s*(\d{1,3}(?:\.\d+)?)\s*%(?:\s*(?:剩余|remaining|left))?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RelativeResetRegex = new Regex(
        @"(?:(\d+)\s*(?:d|day|days|天))?\s*(?:(\d+)\s*(?:h|hr|hrs|hour|hours|小时))?\s*(?:(\d+)\s*(?:m|min|mins|minute|minutes|分钟|分))?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ExactHeaderRegex = new Regex(
        @"^(模型额度|使用量|usage|model\s+quotas?|models?\s*&\s*(?:quota|usage))$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ExactGeminiPoolRegex = new Regex(
        @"^gemini\s*(模型|models?)(?:\s*[^\w\u4e00-\u9fa5]{0,3})?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ExactClaudePoolRegex = new Regex(
        @"^claude\s*(?:和|and|/|&)\s*(?:gpt|第三方)\s*(模型|models?)?(?:\s*[^\w\u4e00-\u9fa5]{0,3})?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ExactWeeklyRegex = new Regex(
        @"^(剩余每周限制|每周限制|周限额|weekly\s+limit(?:\s+remaining)?|remaining\s+weekly\s+limit)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ExactFiveHourRegex = new Regex(
        @"^(剩余五小时限制|剩余\s*5\s*小时限制|五小时限制|5\s*小时限额|五小时限额|5-?hour\s+limit(?:\s+remaining)?|five-?hour\s+limit(?:\s+remaining)?|remaining\s+5-?hour\s+limit)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static bool TryParseFromLines(IEnumerable<string> lines, DateTime nowUtc, out PoolQuotaState gemini, out PoolQuotaState claude)
    {
        gemini = null;
        claude = null;
        if (lines == null) return false;

        var rawList = new List<string>();
        foreach (string raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string line = raw.Trim();
            if (line.Length > 48) continue; // Quota modal/popover labels are always short standalone nodes; ignore chat paragraphs.
            rawList.Add(line);
        }

        // Rejoin adjacent short inline spans that Chromium UIA splits (e.g. ["6 天 16 小时", "后重置"], ["85", "%", "剩余"]).
        var list = MergeSplitLines(rawList);

        bool hasQuotaHeader = false;
        bool hasWindowLabel = false;
        bool hasGeminiPool = false;
        bool hasClaudePool = false;
        foreach (string line in list)
        {
            if (!hasQuotaHeader && ExactHeaderRegex.IsMatch(line)) hasQuotaHeader = true;
            if (!hasWindowLabel && (ExactWeeklyRegex.IsMatch(line) || ExactFiveHourRegex.IsMatch(line))) hasWindowLabel = true;
            if (!hasGeminiPool && ExactGeminiPoolRegex.IsMatch(line)) hasGeminiPool = true;
            if (!hasClaudePool && ExactClaudePoolRegex.IsMatch(line)) hasClaudePool = true;
        }
        // Strict gate 1: require standalone 5h/weekly label AND either standalone heading OR both standalone pool titles.
        if (!hasWindowLabel || (!hasQuotaHeader && !(hasGeminiPool && hasClaudePool))) return false;

        PoolQuotaState g5 = null, g7 = null, c5 = null, c7 = null;
        string currentPool = "";
        string currentWindow = "";
        DateTime? pendingReset = null;
        double? pendingPct = null;

        Action commitPending = () =>
        {
            if (string.IsNullOrEmpty(currentPool) || string.IsNullOrEmpty(currentWindow) || !pendingPct.HasValue) return;
            // Strict gate 1 already verified exact standalone heading + pool + window labels;
            // commit percentage even when the UI omits a reset-time line (e.g. 100% full, 0% exhausted, or clipped).
            PoolQuotaState st = QuotaEvaluator.Evaluate(currentPool, pendingPct.Value, pendingReset, nowUtc);
            st.WindowKind = currentWindow;
            if (currentWindow == "7d")
            {
                if (currentPool == "gemini") g7 = st;
                else if (currentPool == "claude") c7 = st;
            }
            else
            {
                if (currentPool == "gemini") g5 = st;
                else if (currentPool == "claude") c5 = st;
            }
            pendingReset = null;
            pendingPct = null;
        };

        foreach (string line in list)
        {
            if (ExactGeminiPoolRegex.IsMatch(line))
            {
                commitPending();
                currentPool = "gemini";
                currentWindow = "";
                pendingReset = null;
                pendingPct = null;
                continue;
            }
            if (ExactClaudePoolRegex.IsMatch(line))
            {
                commitPending();
                currentPool = "claude";
                currentWindow = "";
                pendingReset = null;
                pendingPct = null;
                continue;
            }

            if (string.IsNullOrEmpty(currentPool)) continue;

            if (ExactWeeklyRegex.IsMatch(line))
            {
                commitPending();
                currentWindow = "7d";
                pendingReset = null;
                pendingPct = null;
                continue;
            }
            if (ExactFiveHourRegex.IsMatch(line))
            {
                commitPending();
                currentWindow = "5h";
                pendingReset = null;
                pendingPct = null;
                continue;
            }

            if (string.IsNullOrEmpty(currentWindow)) continue;

            DateTime? resetUtc = TryParseResetTime(line, nowUtc);
            if (resetUtc.HasValue)
            {
                pendingReset = resetUtc;
            }
            else
            {
                Match pm = ExactPctRegex.Match(line);
                if (pm.Success)
                {
                    double pct;
                    if (double.TryParse(pm.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out pct) && pct >= 0 && pct <= 100)
                    {
                        pendingPct = pct;
                    }
                }
            }

            if (pendingPct.HasValue && pendingReset.HasValue)
            {
                commitPending();
            }
        }
        commitPending();

        gemini = CombinePool("gemini", g5, g7);
        claude = CombinePool("claude", c5, c7);
        return gemini != null || claude != null;
    }

    internal static List<string> MergeSplitLines(List<string> input)
    {
        var merged = new List<string>();
        for (int i = 0; i < input.Count; i++)
        {
            string cur = input[i];
            if (merged.Count > 0)
            {
                string prev = merged[merged.Count - 1];
                string lowCur = cur.ToLowerInvariant();
                // Merge ["85", "%"] or ["85%", "剩余"] or ["85", "% 剩余"]
                if ((cur == "%" || lowCur == "剩余" || lowCur == "remaining" || lowCur == "% 剩余" || lowCur == "% remaining")
                    && Regex.IsMatch(prev, @"^\d{1,3}(?:\.\d+)?%?$"))
                {
                    merged[merged.Count - 1] = cur.StartsWith("%") ? (prev + cur) : (prev + " " + cur);
                    continue;
                }
                // Merge ["6 天 16 小时", "后重置"] / ["2 小时 2 分钟", "后刷新"]
                if ((lowCur == "后重置" || lowCur == "后刷新" || lowCur == "重置" || lowCur == "刷新")
                    && Regex.IsMatch(prev, @"\d+\s*(?:天|小时|分钟|分|d|h|m)", RegexOptions.IgnoreCase))
                {
                    merged[merged.Count - 1] = prev + " " + cur;
                    continue;
                }
                // Merge ["Claude", "和 GPT 模型"] or ["Gemini", "模型"]
                if ((prev.Equals("Claude", StringComparison.OrdinalIgnoreCase) && (cur.StartsWith("和") || lowCur.StartsWith("and")))
                    || (prev.Equals("Gemini", StringComparison.OrdinalIgnoreCase) && (cur == "模型" || lowCur == "models" || lowCur == "model")))
                {
                    merged[merged.Count - 1] = prev + " " + cur;
                    continue;
                }
            }
            merged.Add(cur);
        }
        return merged;
    }

    internal static PoolQuotaState CombinePool(string pool, PoolQuotaState fiveHour, PoolQuotaState weekly)
    {
        if (fiveHour == null && weekly == null) return null;
        // Pick the tighter active window as primary (for compact strip & top alert), keep both attached for detail panel.
        PoolQuotaState primary;
        if (fiveHour != null && weekly != null)
        {
            if (weekly.Alert == QuotaAlertLevel.Critical7d) primary = weekly;
            else if (fiveHour.Alert == QuotaAlertLevel.Warn5h) primary = fiveHour;
            else primary = (fiveHour.RemainingPct <= weekly.RemainingPct) ? fiveHour : weekly;
        }
        else
        {
            primary = fiveHour ?? weekly;
        }
        return new PoolQuotaState
        {
            Pool = pool,
            RemainingPct = primary.RemainingPct,
            ResetAtUtc = primary.ResetAtUtc,
            WindowKind = primary.WindowKind,
            Alert = primary.Alert,
            AlertHint = primary.AlertHint,
            CountdownText = primary.CountdownText,
            FiveHour = fiveHour,
            Weekly = weekly
        };
    }

    internal static DateTime? TryParseResetTime(string text, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string low = text.ToLowerInvariant();
        if (!low.Contains("reset") && !low.Contains("重置") && !low.Contains("恢复") && !low.Contains("刷新") && !low.Contains("refresh")) return null;

        MatchCollection matches = RelativeResetRegex.Matches(text);
        foreach (Match m in matches)
        {
            if (!m.Success || string.IsNullOrWhiteSpace(m.Value)) continue;
            int d = ParseIntGroup(m.Groups[1]);
            int h = ParseIntGroup(m.Groups[2]);
            int min = ParseIntGroup(m.Groups[3]);
            if (d > 0 || h > 0 || min > 0)
            {
                return nowUtc.AddDays(d).AddHours(h).AddMinutes(min);
            }
        }
        return null;
    }

    private static int ParseIntGroup(Group g)
    {
        int v;
        return g.Success && int.TryParse(g.Value, out v) ? v : 0;
    }
}

internal static class DualSlotStore
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };

    internal static Dictionary<string, object> ReadLatest(string stateDir, string prefix)
    {
        int bestSeq = -1;
        Dictionary<string, object> best = null;
        for (int i = 0; i < 2; i++)
        {
            string path = Path.Combine(stateDir, prefix + ".slot" + i + ".json");
            if (!File.Exists(path)) continue;
            try
            {
                string content = File.ReadAllText(path, Encoding.UTF8);
                var doc = Json.DeserializeObject(content) as Dictionary<string, object>;
                if (doc == null) continue;
                int seq = GetInt(doc, "sequence", 0);
                if (seq >= bestSeq)
                {
                    bestSeq = seq;
                    best = doc;
                }
            }
            catch { }
        }
        return best;
    }

    internal static void WriteQuotaFromUia(string stateDir, PoolQuotaState gemini, PoolQuotaState claude, DateTime nowUtc)
    {
        if (gemini == null && claude == null) return;
        try
        {
            Directory.CreateDirectory(stateDir);
            // Isolate UIA-scraped snapshots into dedicated slots so they never race with agy-statusline writers.
            var existing = ReadLatest(stateDir, "quota.uia");
            int prevSeq = existing != null ? GetInt(existing, "sequence", 0) : 0;

            var pools = new Dictionary<string, object>();
            if (gemini != null) pools["gemini"] = SerializePool(gemini);
            if (claude != null) pools["claude"] = SerializePool(claude);

            int seq = prevSeq + 1;
            var doc = new Dictionary<string, object>
            {
                { "schemaVersion", 1 },
                { "sequence", seq },
                { "updatedAt", nowUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) },
                { "source", "uia" },
                { "pools", pools }
            };
            string target = Path.Combine(stateDir, "quota.uia.slot" + (seq % 2) + ".json");
            string tmp = Path.Combine(stateDir, ".quota.uia." + Guid.NewGuid().ToString("N") + ".tmp");
            string serialized = Json.Serialize(doc);
            File.WriteAllText(tmp, serialized, new UTF8Encoding(false));
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (File.Exists(target)) File.Replace(tmp, target, null, true);
                    else File.Move(tmp, target);
                    break;
                }
                catch (IOException)
                {
                    if (attempt == 2) throw;
                    System.Threading.Thread.Sleep(35);
                }
            }
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(Path.Combine(stateDir, "overlay.log"), DateTime.Now.ToString("s") + " quota-write-err: " + ex.GetType().Name + ": " + ex.Message + Environment.NewLine); } catch { }
        }
    }

    internal static MonitorSnapshot LoadSnapshot(string stateDir, DateTime nowUtc)
    {
        var snap = new MonitorSnapshot();
        snap.Sessions = new Dictionary<string, SessionStats>(StringComparer.OrdinalIgnoreCase);

        var mdoc = ReadLatest(stateDir, "metrics");
        if (mdoc != null)
        {
            snap.Sequence = GetInt(mdoc, "sequence", 0);
            snap.UpdatedAtUtc = ParseUtc(GetStr(mdoc, "updatedAt", "")) ?? nowUtc;
            snap.SourceEnv = GetStr(mdoc, "sourceEnv", "");
            snap.ActiveConversationId = GetStr(mdoc, "activeConversationId", "");

            var todayMap = GetMap(mdoc, "today");
            if (todayMap != null)
            {
                snap.Today = new TodayStats
                {
                    Date = GetStr(todayMap, "date", ""),
                    TotalTokens = GetLong(todayMap, "totalTokens", 0),
                    InputTokens = GetLong(todayMap, "inputTokens", 0),
                    CacheReadTokens = GetLong(todayMap, "cacheReadTokens", 0),
                    OutputTokens = GetLong(todayMap, "outputTokens", 0),
                    CacheHitRate = GetNullableDouble(todayMap, "cacheHitRate"),
                    SessionsCount = GetInt(todayMap, "sessionsCount", 0)
                };
            }

            var sessMap = GetMap(mdoc, "sessions");
            if (sessMap != null)
            {
                foreach (var kv in sessMap)
                {
                    var sm = kv.Value as Dictionary<string, object>;
                    if (sm == null) continue;
                    var s = new SessionStats
                    {
                        ConversationId = GetStr(sm, "conversationId", kv.Key),
                        Title = GetStr(sm, "title", ""),
                        Model = GetStr(sm, "model", ""),
                        ModelFamily = GetStr(sm, "modelFamily", "unknown"),
                        Status = GetStr(sm, "status", ""),
                        LastModifiedUtc = ParseUtc(GetStr(sm, "lastModifiedAt", "")) ?? DateTime.MinValue,
                        SessionTokens = GetLong(sm, "sessionTokens", 0),
                        InputTokens = GetLong(sm, "inputTokens", 0),
                        CacheReadTokens = GetLong(sm, "cacheReadTokens", 0),
                        OutputTokens = GetLong(sm, "outputTokens", 0),
                        CacheHitRate = GetNullableDouble(sm, "cacheHitRate"),
                        LastTurnCacheHitRate = GetNullableDouble(sm, "lastTurnCacheHitRate"),
                        CacheCollapsed = GetBool(sm, "cacheCollapsed", false),
                        LastTurnTokens = GetLong(sm, "lastTurnTokens", 0),
                        LastTurnOutputTokens = GetLong(sm, "lastTurnOutputTokens", 0),
                        LastTurnDurationMs = GetNullableLong(sm, "lastTurnDurationMs"),
                        TokPerSec = GetNullableDouble(sm, "tokPerSec"),
                        TokPerSecEstimated = GetBool(sm, "tokPerSecEstimated", false),
                        ContextTokens = GetLong(sm, "contextTokens", 0),
                        ContextWindowSize = GetLong(sm, "contextWindowSize", 1048576),
                        ContextPct = GetDouble(sm, "contextPct", 0.0),
                        SubagentCount = GetInt(sm, "subagentCount", 0),
                        SubagentTokens = GetLong(sm, "subagentTokens", 0),
                        TotalWithSubagentsTokens = GetLong(sm, "totalWithSubagentsTokens", 0),
                        ExecutionDurationMs = GetLong(sm, "executionDurationMs", 0)
                    };
                    snap.Sessions[s.ConversationId] = s;
                }
            }
            object histObj;
            if (mdoc.TryGetValue("dailyHistory", out histObj) && histObj is object[])
            {
                snap.DailyHistory = new List<DailyUsagePoint>();
                foreach (object o in (object[])histObj)
                {
                    var dm = o as Dictionary<string, object>;
                    if (dm == null) continue;
                    snap.DailyHistory.Add(new DailyUsagePoint
                    {
                        Date = GetStr(dm, "date", ""),
                        TotalTokens = GetLong(dm, "totalTokens", 0)
                    });
                }
            }
        }

        // Prefer high-trust agy-statusline quota; fall back to isolated UIA quota when newer or primary is absent.
        var primaryQuota = ParseQuotaDoc(ReadLatest(stateDir, "quota"), nowUtc);
        var uiaQuota = ParseQuotaDoc(ReadLatest(stateDir, "quota.uia"), nowUtc);
        if (primaryQuota != null && uiaQuota != null)
        {
            snap.Quota = (uiaQuota.UpdatedAtUtc > primaryQuota.UpdatedAtUtc.AddMinutes(10)) ? uiaQuota : primaryQuota;
        }
        else
        {
            snap.Quota = primaryQuota ?? uiaQuota;
        }

        if (snap.Quota != null)
        {
            QuotaHistoryStore.RecordSnapshot(stateDir, snap.Quota, snap.Quota.UpdatedAtUtc);
            QuotaHistoryStore.EnrichBurnRates(stateDir, snap.Quota, nowUtc);
        }

        return snap;
    }

    private static Dictionary<string, object> SerializePool(PoolQuotaState p)
    {
        var d = new Dictionary<string, object> { { "remainingPct", Math.Round(p.RemainingPct, 2) } };
        if (p.ResetAtUtc.HasValue) d["resetAt"] = p.ResetAtUtc.Value.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        if (p.FiveHour != null)
        {
            var fh = new Dictionary<string, object> { { "remainingPct", Math.Round(p.FiveHour.RemainingPct, 2) } };
            if (p.FiveHour.ResetAtUtc.HasValue) fh["resetAt"] = p.FiveHour.ResetAtUtc.Value.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            d["fiveHour"] = fh;
        }
        if (p.Weekly != null)
        {
            var wk = new Dictionary<string, object> { { "remainingPct", Math.Round(p.Weekly.RemainingPct, 2) } };
            if (p.Weekly.ResetAtUtc.HasValue) wk["resetAt"] = p.Weekly.ResetAtUtc.Value.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            d["weekly"] = wk;
        }
        return d;
    }

    private static QuotaSnapshot ParseQuotaDoc(Dictionary<string, object> qdoc, DateTime nowUtc)
    {
        if (qdoc == null) return null;
        var qs = new QuotaSnapshot
        {
            Sequence = GetInt(qdoc, "sequence", 0),
            UpdatedAtUtc = ParseUtc(GetStr(qdoc, "updatedAt", "")) ?? nowUtc,
            Source = GetStr(qdoc, "source", ""),
            PlanTier = GetStr(qdoc, "planTier", "")
        };
        var pools = GetMap(qdoc, "pools");
        if (pools != null)
        {
            qs.Gemini = ParsePoolMap("gemini", GetMap(pools, "gemini"), nowUtc);
            qs.Claude = ParsePoolMap("claude", GetMap(pools, "claude"), nowUtc);
        }
        return (qs.Gemini != null || qs.Claude != null) ? qs : null;
    }

    private static PoolQuotaState ParsePoolMap(string pool, Dictionary<string, object> pmap, DateTime nowUtc)
    {
        if (pmap == null) return null;
        PoolQuotaState fh = null, wk = null;
        var fhMap = GetMap(pmap, "fiveHour");
        if (fhMap != null)
        {
            double? pct = GetNullableDouble(fhMap, "remainingPct");
            if (pct.HasValue) fh = QuotaEvaluator.Evaluate(pool, pct.Value, ParseUtc(GetStr(fhMap, "resetAt", "")), nowUtc);
        }
        var wkMap = GetMap(pmap, "weekly");
        if (wkMap != null)
        {
            double? pct = GetNullableDouble(wkMap, "remainingPct");
            if (pct.HasValue) wk = QuotaEvaluator.Evaluate(pool, pct.Value, ParseUtc(GetStr(wkMap, "resetAt", "")), nowUtc);
        }
        if (fh != null || wk != null) return UiaQuotaParser.CombinePool(pool, fh, wk);
        double? singlePct = GetNullableDouble(pmap, "remainingPct");
        return singlePct.HasValue ? QuotaEvaluator.Evaluate(pool, singlePct.Value, ParseUtc(GetStr(pmap, "resetAt", "")), nowUtc) : null;
    }

    internal static DateTime? ParseUtc(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        DateTime dt;
        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out dt))
        {
            return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        }
        return null;
    }

    private static Dictionary<string, object> GetMap(Dictionary<string, object> d, string k)
    {
        object v;
        return d != null && d.TryGetValue(k, out v) ? v as Dictionary<string, object> : null;
    }

    private static string GetStr(Dictionary<string, object> d, string k, string fallback)
    {
        object v;
        return d != null && d.TryGetValue(k, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : fallback;
    }

    private static int GetInt(Dictionary<string, object> d, string k, int fallback)
    {
        object v;
        if (d == null || !d.TryGetValue(k, out v) || v == null) return fallback;
        try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); } catch { return fallback; }
    }

    private static long GetLong(Dictionary<string, object> d, string k, long fallback)
    {
        object v;
        if (d == null || !d.TryGetValue(k, out v) || v == null) return fallback;
        try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); } catch { return fallback; }
    }

    private static long? GetNullableLong(Dictionary<string, object> d, string k)
    {
        object v;
        if (d == null || !d.TryGetValue(k, out v) || v == null) return null;
        try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); } catch { return null; }
    }

    private static double GetDouble(Dictionary<string, object> d, string k, double fallback)
    {
        object v;
        if (d == null || !d.TryGetValue(k, out v) || v == null) return fallback;
        try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return fallback; }
    }

    private static double? GetNullableDouble(Dictionary<string, object> d, string k)
    {
        object v;
        if (d == null || !d.TryGetValue(k, out v) || v == null) return null;
        try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return null; }
    }

    private static bool GetBool(Dictionary<string, object> d, string k, bool fallback)
    {
        object v;
        if (d == null || !d.TryGetValue(k, out v) || v == null) return fallback;
        try { return Convert.ToBoolean(v, CultureInfo.InvariantCulture); } catch { return fallback; }
    }
}

internal static class MetricFormatter
{
    internal static string FormatTokens(long tokens)
    {
        if (tokens <= 0) return "0";
        if (tokens >= 100000000L) return string.Format(CultureInfo.InvariantCulture, "{0:0.00}亿", tokens / 100000000.0);
        if (tokens >= 10000L) return string.Format(CultureInfo.InvariantCulture, "{0:0.0}万", tokens / 10000.0);
        return tokens.ToString("N0", CultureInfo.InvariantCulture);
    }

    internal static string FormatRate(double? tokPerSec, bool estimated)
    {
        if (!tokPerSec.HasValue || tokPerSec.Value <= 0) return "-- tok/s";
        string prefix = estimated ? "~" : "";
        return string.Format(CultureInfo.InvariantCulture, "{0}{1:0.0} tok/s", prefix, tokPerSec.Value);
    }

    internal static string FormatHitRate(double? rate)
    {
        if (!rate.HasValue) return "--";
        return string.Format(CultureInfo.InvariantCulture, "{0:0.0}%", rate.Value * 100.0);
    }

    internal static string FormatDurationMs(long? ms)
    {
        if (!ms.HasValue || ms.Value <= 0) return "--";
        double sec = ms.Value / 1000.0;
        if (sec < 60.0) return string.Format(CultureInfo.InvariantCulture, "{0:0.1}s", sec);
        int mins = (int)(sec / 60.0);
        int remSec = (int)(sec % 60.0);
        if (mins < 60) return string.Format(CultureInfo.InvariantCulture, "{0}m {1}s", mins, remSec);
        return string.Format(CultureInfo.InvariantCulture, "{0}h {1}m", mins / 60, mins % 60);
    }

    internal static string FormatCountdown(TimeSpan delta)
    {
        if (delta.TotalSeconds <= 0) return "已重置";
        if (delta.TotalDays >= 1.0) return string.Format(CultureInfo.InvariantCulture, "{0}d {1}h", (int)delta.TotalDays, delta.Hours);
        if (delta.TotalHours >= 1.0) return string.Format(CultureInfo.InvariantCulture, "{0}h {1}m", (int)delta.TotalHours, delta.Minutes);
        return string.Format(CultureInfo.InvariantCulture, "{0}m", Math.Max(1, (int)Math.Ceiling(delta.TotalMinutes)));
    }

    internal static string ClassifyModelFamily(string modelName)
    {
        string low = (modelName ?? "").ToLowerInvariant();
        if (low.Contains("claude") || low.Contains("opus") || low.Contains("sonnet") || low.Contains("haiku") || low.Contains("gpt")) return "claude";
        if (low.Contains("gemini") || low.Contains("neon") || low.Contains("argon") || low.Contains("carbon") || low.Contains("hydrogen") || low.Contains("flash") || low.Contains("orchestrator")) return "gemini";
        return "gemini";
    }

    internal static string FormatCompactQuota(QuotaSnapshot quota, string activeFamily)
    {
        if (quota == null || (quota.Gemini == null && quota.Claude == null)) return null;
        string g = quota.Gemini != null ? FormatOnePool("G", quota.Gemini) : null;
        string c = quota.Claude != null ? FormatOnePool("C", quota.Claude) : null;
        if (activeFamily == "claude")
        {
            if (c != null && g != null) return c + " · " + g;
            return c ?? g;
        }
        if (g != null && c != null) return g + " · " + c;
        return g ?? c;
    }

    private static string FormatOnePool(string prefix, PoolQuotaState p)
    {
        if (p.Alert == QuotaAlertLevel.ResetPassed) return prefix + " 已重置";
        return string.Format(CultureInfo.InvariantCulture, "{0} {1:0}%", prefix, p.RemainingPct);
    }
}

internal static class AutoRefreshGuard
{
    internal const double MinIdleSeconds = 45.0;
    internal const double MinForegroundSeconds = 15.0;
    internal const double MinAttemptCooldownSeconds = 90.0;
    internal const long BurnThresholdTokens = 3000000L;

    internal static bool ShouldTrigger(
        bool enabled,
        bool isForeground,
        double foregroundSeconds,
        bool hasAnchor,
        bool menuOpen,
        bool isMoving,
        bool isPanelOpen,
        bool anyMouseButtonDown,
        double idleSeconds,
        double sinceLastAttemptSeconds,
        bool hasQuotaTimestamp,
        double quotaAgeMinutes,
        int intervalMinutes,
        long tokenDeltaSinceLastQuota)
    {
        if (!enabled || !isForeground || !hasAnchor || menuOpen || isMoving || isPanelOpen || anyMouseButtonDown)
            return false;
        if (foregroundSeconds < MinForegroundSeconds) return false;
        if (idleSeconds < MinIdleSeconds) return false;
        if (sinceLastAttemptSeconds < MinAttemptCooldownSeconds) return false;

        int effInterval = Math.Max(2, intervalMinutes);
        bool dueByTime = !hasQuotaTimestamp || quotaAgeMinutes >= effInterval;
        bool dueByBurn = tokenDeltaSinceLastQuota >= BurnThresholdTokens && quotaAgeMinutes >= 2.0;
        return dueByTime || dueByBurn;
    }
}
