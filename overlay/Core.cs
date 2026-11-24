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

    internal static OverlaySettings CreateDefault()
    {
        var s = new OverlaySettings();
        s.ThemeMode = "auto";
        s.Visible = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (string k in AllItems) s.Visible[k] = true;
        s.Priority = new List<string>(DefaultPriority);
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
        state.CountdownText = "--";

        if (!resetAtUtc.HasValue) return state;

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
    private static readonly Regex PctRegex = new Regex(@"(?<!\d)(\d{1,3}(?:\.\d+)?)\s*%", RegexOptions.Compiled);
    private static readonly Regex RelativeResetRegex = new Regex(@"(?:(\d+)\s*(?:d|day|days|天))?\s*(?:(\d+)\s*(?:h|hr|hrs|hour|hours|小时))?\s*(?:(\d+)\s*(?:m|min|mins|minute|minutes|分钟|分))", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex QuotaHeaderRegex = new Regex(@"(models?\s*&\s*(?:quota|usage)|usage\s+limits?|model\s+quotas?|模型与配额|额度与用量|配额概览)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static bool TryParseFromLines(IEnumerable<string> lines, DateTime nowUtc, out PoolQuotaState gemini, out PoolQuotaState claude)
    {
        gemini = null;
        claude = null;
        if (lines == null) return false;

        var list = new List<string>();
        bool hasQuotaHeader = false;
        foreach (string raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string line = raw.Trim();
            list.Add(line);
            if (!hasQuotaHeader && QuotaHeaderRegex.IsMatch(line)) hasQuotaHeader = true;
        }
        // Strict gate 1: require an explicit quota/usage panel heading so chat messages are never misread.
        if (!hasQuotaHeader) return false;

        string currentSection = "";
        foreach (string line in list)
        {
            string low = line.ToLowerInvariant();

            if (low.Contains("gemini") || low.Contains("neon") || low.Contains("argon") || low.Contains("flash"))
            {
                currentSection = "gemini";
            }
            else if (low.Contains("claude") || low.Contains("opus") || low.Contains("sonnet") || low.Contains("gpt-oss") || low.Contains("third-party"))
            {
                currentSection = "claude";
            }

            if (string.IsNullOrEmpty(currentSection)) continue;
            if (!low.Contains("%") && !low.Contains("reset") && !low.Contains("重置")) continue;

            Match pm = PctRegex.Match(line);
            DateTime? resetUtc = TryParseResetTime(line, nowUtc);
            if (pm.Success)
            {
                double pct;
                if (double.TryParse(pm.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out pct) && pct >= 0 && pct <= 100)
                {
                    if (low.Contains("context") || low.Contains("上下文") || low.Contains("cache") || low.Contains("缓存")) continue;
                    if (low.Contains("used") || low.Contains("已用")) pct = Math.Max(0, 100.0 - pct);

                    PoolQuotaState candidate = QuotaEvaluator.Evaluate(currentSection, pct, resetUtc, nowUtc);
                    if (currentSection == "gemini")
                    {
                        if (gemini == null || pct < gemini.RemainingPct) gemini = candidate;
                    }
                    else if (currentSection == "claude")
                    {
                        if (claude == null || pct < claude.RemainingPct) claude = candidate;
                    }
                }
            }
            else if (resetUtc.HasValue)
            {
                if (currentSection == "gemini" && gemini != null && !gemini.ResetAtUtc.HasValue)
                {
                    gemini = QuotaEvaluator.Evaluate("gemini", gemini.RemainingPct, resetUtc, nowUtc);
                }
                else if (currentSection == "claude" && claude != null && !claude.ResetAtUtc.HasValue)
                {
                    claude = QuotaEvaluator.Evaluate("claude", claude.RemainingPct, resetUtc, nowUtc);
                }
            }
        }
        // Strict gate 2: only accept a pool when BOTH percentage and reset time were parsed.
        if (gemini != null && !gemini.ResetAtUtc.HasValue) gemini = null;
        if (claude != null && !claude.ResetAtUtc.HasValue) claude = null;
        return gemini != null || claude != null;
    }

    internal static DateTime? TryParseResetTime(string text, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string low = text.ToLowerInvariant();
        if (!low.Contains("reset") && !low.Contains("重置") && !low.Contains("恢复")) return null;

        Match m = RelativeResetRegex.Match(text);
        if (m.Success)
        {
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
            if (gemini != null)
            {
                var g = new Dictionary<string, object> { { "remainingPct", Math.Round(gemini.RemainingPct, 2) } };
                if (gemini.ResetAtUtc.HasValue) g["resetAt"] = gemini.ResetAtUtc.Value.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
                pools["gemini"] = g;
            }
            if (claude != null)
            {
                var c = new Dictionary<string, object> { { "remainingPct", Math.Round(claude.RemainingPct, 2) } };
                if (claude.ResetAtUtc.HasValue) c["resetAt"] = claude.ResetAtUtc.Value.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
                pools["claude"] = c;
            }

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
            File.WriteAllText(tmp, Json.Serialize(doc), Encoding.UTF8);
            if (File.Exists(target)) File.Replace(tmp, target, null, true);
            else File.Move(tmp, target);
        }
        catch { }
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

        return snap;
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
            var gmap = GetMap(pools, "gemini");
            if (gmap != null)
            {
                double? pct = GetNullableDouble(gmap, "remainingPct");
                if (pct.HasValue) qs.Gemini = QuotaEvaluator.Evaluate("gemini", pct.Value, ParseUtc(GetStr(gmap, "resetAt", "")), nowUtc);
            }
            var cmap = GetMap(pools, "claude");
            if (cmap != null)
            {
                double? pct = GetNullableDouble(cmap, "remainingPct");
                if (pct.HasValue) qs.Claude = QuotaEvaluator.Evaluate("claude", pct.Value, ParseUtc(GetStr(cmap, "resetAt", "")), nowUtc);
            }
        }
        return (qs.Gemini != null || qs.Claude != null) ? qs : null;
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
