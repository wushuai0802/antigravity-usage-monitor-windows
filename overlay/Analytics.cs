using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

internal sealed class DailyUsagePoint
{
    internal string Date;
    internal long TotalTokens;
}

internal sealed class QuotaHistorySample
{
    internal DateTime AtUtc;
    internal double RemainingPct;
    internal DateTime? ResetAtUtc;
    internal bool Boundary; // True when account switch or quota cycle reset was detected
}

internal sealed class BurnRateEstimate
{
    internal double PctPerHour;
    internal TimeSpan? EstimatedRunway;
    internal bool WillExhaustBeforeReset;
    internal TimeSpan? ShortfallBeforeReset;
    internal string SummaryText;
}

internal sealed class MilestoneAlertEvent
{
    internal string Pool;
    internal string WindowKind;
    internal int ThresholdPct;
    internal double CurrentPct;
    internal string Title;
    internal string Body;
    internal bool Critical;
}

internal static class QuotaHistoryStore
{
    private const int MaxSamplesPerSeries = 800;
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
    private static readonly object CacheLock = new object();
    private static string _cachedPath = "";
    private static DateTime _cachedWriteUtc = DateTime.MinValue;
    private static long _cachedLength = -1;
    private static Dictionary<string, List<QuotaHistorySample>> _cachedAll;

    internal static void RecordSnapshot(string stateDir, QuotaSnapshot snapshot, DateTime nowUtc)
    {
        if (string.IsNullOrEmpty(stateDir) || snapshot == null) return;
        try
        {
            var all = LoadAll(stateDir);
            bool changed = false;
            if (snapshot.Gemini != null)
            {
                if (snapshot.Gemini.FiveHour != null) changed |= AppendSample(all, "gemini:5h", snapshot.Gemini.FiveHour, nowUtc);
                else changed |= AppendSample(all, "gemini:" + NormalizeWindow(snapshot.Gemini), snapshot.Gemini, nowUtc);
                if (snapshot.Gemini.Weekly != null) changed |= AppendSample(all, "gemini:7d", snapshot.Gemini.Weekly, nowUtc);
            }
            if (snapshot.Claude != null)
            {
                if (snapshot.Claude.FiveHour != null) changed |= AppendSample(all, "claude:5h", snapshot.Claude.FiveHour, nowUtc);
                else changed |= AppendSample(all, "claude:" + NormalizeWindow(snapshot.Claude), snapshot.Claude, nowUtc);
                if (snapshot.Claude.Weekly != null) changed |= AppendSample(all, "claude:7d", snapshot.Claude.Weekly, nowUtc);
            }
            if (changed) SaveAll(stateDir, all, nowUtc);
        }
        catch { }
    }

    internal static void EnrichBurnRates(string stateDir, QuotaSnapshot snapshot, DateTime nowUtc)
    {
        if (string.IsNullOrEmpty(stateDir) || snapshot == null) return;
        try
        {
            var all = LoadAll(stateDir);
            EnrichPool(all, "gemini", snapshot.Gemini, nowUtc);
            EnrichPool(all, "claude", snapshot.Claude, nowUtc);
        }
        catch { }
    }

    private static void EnrichPool(Dictionary<string, List<QuotaHistorySample>> all, string pool, PoolQuotaState st, DateTime nowUtc)
    {
        if (st == null) return;
        if (st.FiveHour != null) st.FiveHour.BurnRate = ComputeBurnRate(GetSeries(all, pool + ":5h"), st.FiveHour, nowUtc);
        if (st.Weekly != null) st.Weekly.BurnRate = ComputeBurnRate(GetSeries(all, pool + ":7d"), st.Weekly, nowUtc);
        if (st.FiveHour != null && st.WindowKind == "5h") st.BurnRate = st.FiveHour.BurnRate;
        else if (st.Weekly != null && st.WindowKind == "7d") st.BurnRate = st.Weekly.BurnRate;
        else st.BurnRate = ComputeBurnRate(GetSeries(all, pool + ":" + NormalizeWindow(st)), st, nowUtc);
    }

    internal static bool IsBoundaryTransition(QuotaHistorySample prev, double nextPct, DateTime? nextResetUtc)
    {
        if (prev == null) return true;
        // Remaining quota jumped upward -> new cycle reset or switched to another account.
        if (nextPct > prev.RemainingPct + 1.0) return true;
        // Reset timestamp shifted significantly (> 18 min) -> switched account or new cycle.
        if (prev.ResetAtUtc.HasValue && nextResetUtc.HasValue)
        {
            if (Math.Abs((nextResetUtc.Value - prev.ResetAtUtc.Value).TotalMinutes) > 18.0) return true;
        }
        return false;
    }

    internal static BurnRateEstimate ComputeBurnRate(List<QuotaHistorySample> series, PoolQuotaState current, DateTime nowUtc)
    {
        if (series == null || series.Count < 2 || current == null || current.Alert == QuotaAlertLevel.ResetPassed) return null;
        if (current.RemainingPct <= 0.0) return null;

        // Only look at samples AFTER the most recent account-switch / cycle-reset boundary.
        int startIdx = 0;
        for (int i = series.Count - 1; i >= 0; i--)
        {
            if (series[i].Boundary) { startIdx = i; break; }
        }

        double maxLookbackHours = current.WindowKind == "7d" ? 24.0 : 4.0;
        DateTime minAt = nowUtc.AddHours(-maxLookbackHours);
        var segment = new List<QuotaHistorySample>();
        for (int i = startIdx; i < series.Count; i++)
        {
            if (series[i].AtUtc >= minAt) segment.Add(series[i]);
        }
        if (segment.Count < 2) return null;

        QuotaHistorySample oldest = segment[0];
        QuotaHistorySample newest = segment[segment.Count - 1];
        double spanHours = (newest.AtUtc - oldest.AtUtc).TotalHours;
        double dropped = oldest.RemainingPct - newest.RemainingPct;
        if (spanHours < (2.0 / 60.0) || dropped < 1.0) return null;

        double pctPerHour = dropped / spanHours;
        if (pctPerHour < 0.5) return null;

        var est = new BurnRateEstimate();
        est.PctPerHour = Math.Round(pctPerHour, 1);
        double runwayHours = current.RemainingPct / pctPerHour;
        est.EstimatedRunway = TimeSpan.FromHours(Math.Min(runwayHours, 24.0 * 30.0));

        if (current.ResetAtUtc.HasValue && current.ResetAtUtc.Value > nowUtc)
        {
            TimeSpan untilReset = current.ResetAtUtc.Value - nowUtc;
            if (est.EstimatedRunway.Value < untilReset)
            {
                est.WillExhaustBeforeReset = true;
                est.ShortfallBeforeReset = untilReset - est.EstimatedRunway.Value;
                est.SummaryText = string.Format(
                    CultureInfo.InvariantCulture,
                    "-{0:0.#}%/h · 预计 {1} 后耗尽（早于重置）",
                    est.PctPerHour,
                    MetricFormatter.FormatCountdown(est.EstimatedRunway.Value));
            }
            else
            {
                est.WillExhaustBeforeReset = false;
                est.SummaryText = string.Format(
                    CultureInfo.InvariantCulture,
                    "-{0:0.#}%/h · 可安全撑到重置",
                    est.PctPerHour);
            }
        }
        else
        {
            est.SummaryText = string.Format(
                CultureInfo.InvariantCulture,
                "-{0:0.#}%/h · 约撑 {1}",
                est.PctPerHour,
                MetricFormatter.FormatCountdown(est.EstimatedRunway.Value));
        }
        return est;
    }

    private static bool AppendSample(Dictionary<string, List<QuotaHistorySample>> all, string key, PoolQuotaState st, DateTime nowUtc)
    {
        List<QuotaHistorySample> list;
        if (!all.TryGetValue(key, out list))
        {
            list = new List<QuotaHistorySample>();
            all[key] = list;
        }
        QuotaHistorySample prev = list.Count > 0 ? list[list.Count - 1] : null;
        bool boundary = IsBoundaryTransition(prev, st.RemainingPct, st.ResetAtUtc);
        bool valueChanged = prev == null || Math.Abs(prev.RemainingPct - st.RemainingPct) >= 0.5;
        bool timeElapsed = prev == null || (nowUtc - prev.AtUtc).TotalMinutes >= 5.0;
        if (!boundary && !valueChanged && !timeElapsed) return false;

        list.Add(new QuotaHistorySample
        {
            AtUtc = nowUtc,
            RemainingPct = Math.Round(st.RemainingPct, 2),
            ResetAtUtc = st.ResetAtUtc,
            Boundary = boundary
        });

        DateTime cutoff = nowUtc.AddDays(-30);
        while (list.Count > MaxSamplesPerSeries || (list.Count > 0 && list[0].AtUtc < cutoff))
        {
            list.RemoveAt(0);
        }
        return true;
    }

    private static string NormalizeWindow(PoolQuotaState st)
    {
        return st != null && st.WindowKind == "7d" ? "7d" : "5h";
    }

    private static List<QuotaHistorySample> GetSeries(Dictionary<string, List<QuotaHistorySample>> all, string key)
    {
        List<QuotaHistorySample> list;
        return all != null && all.TryGetValue(key, out list) ? list : null;
    }

    private static Dictionary<string, List<QuotaHistorySample>> LoadAll(string stateDir)
    {
        string path = Path.Combine(stateDir, "quota-history.json");
        lock (CacheLock)
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return new Dictionary<string, List<QuotaHistorySample>>(StringComparer.OrdinalIgnoreCase);
            DateTime mtime = fi.LastWriteTimeUtc;
            long len = fi.Length;
            if (_cachedAll != null && string.Equals(_cachedPath, path, StringComparison.OrdinalIgnoreCase) && _cachedWriteUtc == mtime && _cachedLength == len)
            {
                return _cachedAll;
            }
            var map = new Dictionary<string, List<QuotaHistorySample>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var root = Json.DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
                if (root != null)
                {
                    object sObj;
                    if (root.TryGetValue("series", out sObj) && sObj is Dictionary<string, object>)
                    {
                        var sMap = (Dictionary<string, object>)sObj;
                        foreach (var kv in sMap)
                        {
                            var arr = kv.Value as object[];
                            if (arr == null) continue;
                            var list = new List<QuotaHistorySample>();
                            foreach (object item in arr)
                            {
                                var m = item as Dictionary<string, object>;
                                if (m == null) continue;
                                object atO, pctO, rstO, bndO;
                                DateTime? at = m.TryGetValue("at", out atO) ? DualSlotStore.ParseUtc(Convert.ToString(atO, CultureInfo.InvariantCulture)) : null;
                                if (!at.HasValue || !m.TryGetValue("pct", out pctO) || pctO == null) continue;
                                double pct = Convert.ToDouble(pctO, CultureInfo.InvariantCulture);
                                DateTime? rst = m.TryGetValue("resetAt", out rstO) && rstO != null ? DualSlotStore.ParseUtc(Convert.ToString(rstO, CultureInfo.InvariantCulture)) : null;
                                bool bnd = m.TryGetValue("boundary", out bndO) && bndO is bool && (bool)bndO;
                                list.Add(new QuotaHistorySample { AtUtc = at.Value, RemainingPct = pct, ResetAtUtc = rst, Boundary = bnd });
                            }
                            map[kv.Key] = list;
                        }
                    }
                }
            }
            catch { }
            _cachedPath = path;
            _cachedWriteUtc = mtime;
            _cachedLength = len;
            _cachedAll = map;
            return map;
        }
    }

    private static void SaveAll(string stateDir, Dictionary<string, List<QuotaHistorySample>> map, DateTime nowUtc)
    {
        lock (CacheLock)
        {
            Directory.CreateDirectory(stateDir);
            var ser = new Dictionary<string, object>();
            foreach (var kv in map)
            {
                var items = new List<Dictionary<string, object>>();
                foreach (var s in kv.Value)
                {
                    var d = new Dictionary<string, object>
                    {
                        { "at", s.AtUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) },
                        { "pct", s.RemainingPct }
                    };
                    if (s.ResetAtUtc.HasValue) d["resetAt"] = s.ResetAtUtc.Value.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
                    if (s.Boundary) d["boundary"] = true;
                    items.Add(d);
                }
                ser[kv.Key] = items.ToArray();
            }
            var doc = new Dictionary<string, object>
            {
                { "schemaVersion", 1 },
                { "updatedAt", nowUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) },
                { "series", ser }
            };
            string target = Path.Combine(stateDir, "quota-history.json");
            string tmp = Path.Combine(stateDir, ".quota-history." + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(tmp, Json.Serialize(doc), new UTF8Encoding(false));
            if (File.Exists(target)) File.Replace(tmp, target, null, true);
            else File.Move(tmp, target);
            var savedFi = new FileInfo(target);
            _cachedPath = target;
            _cachedWriteUtc = savedFi.Exists ? savedFi.LastWriteTimeUtc : DateTime.UtcNow;
            _cachedLength = savedFi.Exists ? savedFi.Length : -1;
            _cachedAll = map;
        }
    }
}

internal sealed class MilestoneTracker
{
    private sealed class Record
    {
        internal string Key; // pool|winKind|threshold
        internal DateTime? ResetAtUtc;
        internal DateTime FiredAtUtc;
    }

    private readonly string _path;
    private readonly List<Record> _records = new List<Record>();
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

    internal MilestoneTracker(string stateDir)
    {
        _path = Path.Combine(stateDir, "milestones.json");
        Load();
    }

    internal List<MilestoneAlertEvent> Evaluate(QuotaSnapshot quota)
    {
        var events = new List<MilestoneAlertEvent>();
        if (quota == null) return events;
        CheckPool(quota.Gemini, "Gemini 池", events);
        CheckPool(quota.Claude, "Claude / 第三方池", events);
        if (events.Count > 0) Save();
        return events;
    }

    private void CheckPool(PoolQuotaState pool, string label, List<MilestoneAlertEvent> events)
    {
        if (pool == null) return;
        if (pool.FiveHour != null) CheckWindow(pool.FiveHour, label, "5h", new[] { 10, 20 }, events);
        else if (pool.WindowKind == "5h") CheckWindow(pool, label, "5h", new[] { 10, 20 }, events);

        if (pool.Weekly != null) CheckWindow(pool.Weekly, label, "7d", new[] { 5, 10 }, events);
        else if (pool.WindowKind == "7d") CheckWindow(pool, label, "7d", new[] { 5, 10 }, events);
    }

    private bool AlreadyNotified(string key, DateTime? resetAtUtc)
    {
        foreach (var r in _records)
        {
            if (!string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase)) continue;
            if (resetAtUtc.HasValue && r.ResetAtUtc.HasValue)
            {
                if (Math.Abs((resetAtUtc.Value - r.ResetAtUtc.Value).TotalMinutes) <= 25.0) return true;
            }
            else if (!resetAtUtc.HasValue && !r.ResetAtUtc.HasValue)
            {
                if ((DateTime.UtcNow - r.FiredAtUtc).TotalHours < 4.0) return true;
            }
        }
        return false;
    }

    private void CheckWindow(PoolQuotaState st, string label, string winKind, int[] sortedAscThresholds, List<MilestoneAlertEvent> events)
    {
        if (st == null || st.Alert == QuotaAlertLevel.ResetPassed) return;
        foreach (int th in sortedAscThresholds)
        {
            if (st.RemainingPct <= th)
            {
                string key = st.Pool + "|" + winKind + "|" + th;
                if (AlreadyNotified(key, st.ResetAtUtc)) return;
                DateTime now = DateTime.UtcNow;
                foreach (int higher in sortedAscThresholds)
                {
                    if (higher >= th)
                    {
                        _records.Add(new Record { Key = st.Pool + "|" + winKind + "|" + higher, ResetAtUtc = st.ResetAtUtc, FiredAtUtc = now });
                    }
                }
                bool isCrit = (winKind == "7d" && th <= 5) || (winKind == "5h" && th <= 10);
                string advice = winKind == "7d"
                    ? (th <= 5 ? "7d 额度见底，建议准备切号交接。" : "7d 额度偏低，请注意分配。")
                    : (th <= 10 ? "5h 额度紧张，可切换另一模型家族继续。" : "5h 额度已低于 20%。");
                events.Add(new MilestoneAlertEvent
                {
                    Pool = st.Pool,
                    WindowKind = winKind,
                    ThresholdPct = th,
                    CurrentPct = st.RemainingPct,
                    Critical = isCrit,
                    Title = string.Format(CultureInfo.InvariantCulture, "Antigravity {0} ({1}) 剩 {2:0}%", label, winKind, st.RemainingPct),
                    Body = advice + (string.IsNullOrEmpty(st.CountdownText) ? "" : (" 距离重置：" + st.CountdownText))
                });
                return;
            }
        }
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var arr = Json.DeserializeObject(File.ReadAllText(_path, Encoding.UTF8)) as object[];
            if (arr == null) return;
            foreach (object o in arr)
            {
                var m = o as Dictionary<string, object>;
                if (m == null) continue;
                object kO, rO, fO;
                if (!m.TryGetValue("k", out kO) || !(kO is string)) continue;
                DateTime? rst = m.TryGetValue("r", out rO) && rO != null ? DualSlotStore.ParseUtc(Convert.ToString(rO, CultureInfo.InvariantCulture)) : null;
                DateTime? fired = m.TryGetValue("f", out fO) && fO != null ? DualSlotStore.ParseUtc(Convert.ToString(fO, CultureInfo.InvariantCulture)) : null;
                _records.Add(new Record { Key = (string)kO, ResetAtUtc = rst, FiredAtUtc = fired ?? DateTime.UtcNow });
            }
        }
        catch { }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path));
            if (_records.Count > 120) _records.RemoveRange(0, _records.Count - 120);
            var outList = new List<Dictionary<string, object>>();
            foreach (var r in _records)
            {
                var d = new Dictionary<string, object>
                {
                    { "k", r.Key },
                    { "f", r.FiredAtUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) }
                };
                if (r.ResetAtUtc.HasValue) d["r"] = r.ResetAtUtc.Value.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
                outList.Add(d);
            }
            string tmp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, Json.Serialize(outList.ToArray()), new UTF8Encoding(false));
            if (File.Exists(_path)) File.Replace(tmp, _path, null, true);
            else File.Move(tmp, _path);
        }
        catch { }
    }
}
