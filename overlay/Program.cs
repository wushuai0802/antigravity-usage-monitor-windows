using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;

[assembly: AssemblyVersion("0.2.1.0")]
[assembly: AssemblyFileVersion("0.2.1.0")]
[assembly: AssemblyTitle("Antigravity Usage Monitor (unofficial)")]

internal static class Program
{
    internal const string Version = "0.2.1";
    private static string _stateDir;
    private static readonly ManualResetEvent StopEvent = new ManualResetEvent(false);
    private static volatile bool _desktopAlive;
    private static IntPtr _window = IntPtr.Zero;
    private static readonly object LogLock = new object();

    internal static void Note(string text)
    {
        lock (LogLock)
        {
            try
            {
                string path = Path.Combine(_stateDir, "overlay.log");
                if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024)
                {
                    string prev = path + ".1";
                    if (File.Exists(prev)) File.Delete(prev);
                    File.Move(path, prev);
                }
                File.AppendAllText(path, DateTime.Now.ToString("s") + " " + text + Environment.NewLine);
            }
            catch { }
        }
    }

    private static void Worker(Action run)
    {
        var t = new Thread(() =>
        {
            try { run(); }
            catch (Exception ex) { Note("worker stopped: " + ex.GetType().Name); }
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.MTA);
        t.Start();
    }

    private static bool TriggerFallbackScan(string stateDir, bool preferWsl)
    {
        try
        {
            string script = Path.Combine(stateDir, "scripts", "collect.py");
            if (!File.Exists(script)) return false;

            ProcessStartInfo psi;
            if (preferWsl)
            {
                string wslScript = ToWslPath(script);
                string wslState = ToWslPath(stateDir);
                if (string.IsNullOrEmpty(wslScript) || string.IsNullOrEmpty(wslState)) return false;
                psi = new ProcessStartInfo
                {
                    FileName = "wsl.exe",
                    Arguments = string.Format("-e python3 \"{0}\" --scan-only --state-dir \"{1}\"", wslScript, wslState),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
            }
            else
            {
                psi = new ProcessStartInfo
                {
                    FileName = "python",
                    Arguments = string.Format("\"{0}\" --scan-only --state-dir \"{1}\"", script, stateDir),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
            }
            using (var p = Process.Start(psi))
            {
                if (p == null) return false;
                return p.WaitForExit(5000) && p.ExitCode == 0;
            }
        }
        catch { return false; }
    }

    private static string ToWslPath(string winPath)
    {
        if (string.IsNullOrEmpty(winPath) || winPath.Length < 3 || winPath[1] != ':') return null;
        char drive = char.ToLowerInvariant(winPath[0]);
        return "/mnt/" + drive + winPath.Substring(2).Replace('\\', '/');
    }

    [STAThread]
    private static void Main(string[] args)
    {
        _stateDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AntigravityUsageMonitor");
        int idx = Array.IndexOf(args, "--state-dir");
        if (idx >= 0 && idx + 1 < args.Length) _stateDir = Path.GetFullPath(args[idx + 1]);
        Directory.CreateDirectory(_stateDir);

        int previewIdx = Array.IndexOf(args, "--render-preview");
        if (previewIdx >= 0 && previewIdx + 1 < args.Length)
        {
            OverlayController.RenderPreview(Path.GetFullPath(args[previewIdx + 1]));
            return;
        }

        bool created;
        using (var mutex = new Mutex(true, "Local\\AntigravityUsageOverlay", out created))
        {
            if (!created) return;
            try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); }
            catch (EntryPointNotFoundException) { Native.SetProcessDPIAware(); }
            AppContext.SetSwitch("Switch.System.Windows.DoNotScaleForDpiChanges", false);

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.DispatcherUnhandledException += (s, e) => { Note("UI error: " + e.Exception.GetType().Name); e.Handled = true; };
            app.Exit += (s, e) => StopEvent.Set();

            var refreshFlag = new ManualResetEvent(false);
            var quotaRefreshFlag = new ManualResetEvent(false);
            long latestTodayTokens = 0;
            long lastQuotaTokens = 0;
            long lastQuotaUpdatedTicks = 0;
            var controller = new OverlayController(_stateDir, () => refreshFlag.Set(), () => quotaRefreshFlag.Set());
            app.Exit += (s, e) => controller.DisposeResources();

            // 1. Track Antigravity host process & active main window
            Worker(() =>
            {
                string signature = "";
                while (!StopEvent.WaitOne(0))
                {
                    try
                    {
                        var pids = Native.DesktopPids();
                        _desktopAlive = pids.Count > 0;
                        string next = Native.DesktopIdentity(pids);
                        if (next != signature) { signature = next; controller.HostState(signature); }
                        IntPtr chosen = Native.ChooseWindow(pids, Interlocked.CompareExchange(ref _window, IntPtr.Zero, IntPtr.Zero));
                        Interlocked.Exchange(ref _window, chosen);
                    }
                    catch (Exception ex) { Note("host check: " + ex.GetType().Name); }
                    StopEvent.WaitOne(1500);
                }
            });

            // 2. Track UIA composer toolbar anchor, passive quota capture, and safe auto/manual quota refresh
            Worker(() =>
            {
                var finder = new AnchorFinder();
                DateTime lastAutoAttempt = DateTime.MinValue;
                DateTime foregroundSinceUtc = DateTime.MinValue;
                IntPtr lastForegroundWindow = IntPtr.Zero;
                while (!StopEvent.WaitOne(0))
                {
                    try
                    {
                        DateTime nowUtc = DateTime.UtcNow;
                        IntPtr w = Interlocked.CompareExchange(ref _window, IntPtr.Zero, IntPtr.Zero);
                        bool isFg = w != IntPtr.Zero && Native.IsForeground(w);
                        if (isFg)
                        {
                            if (w != lastForegroundWindow || foregroundSinceUtc == DateTime.MinValue)
                            {
                                lastForegroundWindow = w;
                                foregroundSinceUtc = nowUtc;
                            }
                        }
                        else
                        {
                            lastForegroundWindow = IntPtr.Zero;
                            foregroundSinceUtc = DateTime.MinValue;
                        }

                        bool manualQuota = quotaRefreshFlag.WaitOne(0);
                        if (manualQuota)
                        {
                            quotaRefreshFlag.Reset();
                            if (w != IntPtr.Zero && !controller.IsMoving)
                            {
                                lastAutoAttempt = nowUtc;
                                if (finder.TriggerQuotaRefresh(w, _stateDir, false))
                                {
                                    Interlocked.Exchange(ref lastQuotaTokens, Interlocked.Read(ref latestTodayTokens));
                                    Interlocked.Exchange(ref lastQuotaUpdatedTicks, DateTime.UtcNow.Ticks);
                                }
                            }
                        }

                        Anchor anchor = null;
                        if (!controller.IsMoving)
                        {
                            anchor = w == IntPtr.Zero ? null : finder.Find(w, _stateDir);
                            controller.PostAnchor(anchor);
                        }

                        // Guarded foreground-only idle auto-refresh:
                        // never run while Antigravity is in the background or freshly focused (< 15s),
                        // require >= 45s keyboard/mouse idle, no menu/panel open, no mouse buttons held,
                        // and either interval elapsed or >= 3M new tokens consumed since last quota sync.
                        long qTicks = Interlocked.Read(ref lastQuotaUpdatedTicks);
                        DateTime lastQuotaUtc = qTicks > 0 ? new DateTime(qTicks, DateTimeKind.Utc) : DateTime.MinValue;
                        double ageMin = qTicks > 0 ? (nowUtc - lastQuotaUtc).TotalMinutes : 0.0;
                        long curTok = Interlocked.Read(ref latestTodayTokens);
                        long prevTok = Interlocked.Read(ref lastQuotaTokens);
                        double fgSec = (isFg && foregroundSinceUtc != DateTime.MinValue) ? (nowUtc - foregroundSinceUtc).TotalSeconds : 0.0;
                        double sinceAttemptSec = lastAutoAttempt == DateTime.MinValue ? 9999.0 : (nowUtc - lastAutoAttempt).TotalSeconds;

                        if (AutoRefreshGuard.ShouldTrigger(
                            controller.AutoRefreshQuotaEnabled,
                            isFg,
                            fgSec,
                            anchor != null,
                            anchor != null && anchor.MenuOpen,
                            controller.IsMoving,
                            controller.IsPanelOpen,
                            Native.AnyMouseButtonDown(),
                            Native.IdleSeconds(),
                            sinceAttemptSec,
                            qTicks > 0,
                            ageMin,
                            controller.AutoRefreshIntervalMinutes,
                            curTok - prevTok))
                        {
                            lastAutoAttempt = nowUtc;
                            if (finder.TriggerQuotaRefresh(w, _stateDir, true))
                            {
                                Interlocked.Exchange(ref lastQuotaTokens, curTok);
                                Interlocked.Exchange(ref lastQuotaUpdatedTicks, DateTime.UtcNow.Ticks);
                            }
                        }
                    }
                    catch (Exception ex) { Note("locating: " + ex.GetType().Name); }
                    StopEvent.WaitOne(_desktopAlive ? 380 : 2000);
                }
            });

            // 3. Poll dual-slot local NTFS snapshots (and WSL/Windows-aware fallback scan with backoff)
            Worker(() =>
            {
                DateTime lastFallback = DateTime.MinValue;
                int failStreak = 0;
                var milestones = new MilestoneTracker(_stateDir);
                while (!StopEvent.WaitOne(0))
                {
                    if (!_desktopAlive)
                    {
                        StopEvent.WaitOne(2000);
                        continue;
                    }
                    try
                    {
                        DateTime now = DateTime.UtcNow;
                        bool manual = refreshFlag.WaitOne(0);
                        if (manual) refreshFlag.Reset();
                        MonitorSnapshot snap = DualSlotStore.LoadSnapshot(_stateDir, now);
                        bool stale = snap.Sequence == 0 || (now - snap.UpdatedAtUtc).TotalSeconds > 45;
                        int cooldownSec = Math.Min(300, 20 * (1 << Math.Min(failStreak, 4)));
                        if ((manual || stale) && (now - lastFallback).TotalSeconds >= cooldownSec)
                        {
                            lastFallback = now;
                            IntPtr activeW = Interlocked.CompareExchange(ref _window, IntPtr.Zero, IntPtr.Zero);
                            string title = Native.GetTitle(activeW);
                            bool preferWsl = snap.SourceEnv == "wsl" || title.IndexOf("(WSL:", StringComparison.OrdinalIgnoreCase) >= 0;
                            bool ok = TriggerFallbackScan(_stateDir, preferWsl);
                            failStreak = ok ? 0 : (failStreak + 1);
                            snap = DualSlotStore.LoadSnapshot(_stateDir, DateTime.UtcNow);
                        }
                        if (snap.Today != null) Interlocked.Exchange(ref latestTodayTokens, snap.Today.TotalTokens);
                        if (snap.Quota != null)
                        {
                            Interlocked.Exchange(ref lastQuotaUpdatedTicks, snap.Quota.UpdatedAtUtc.Ticks);
                            if (controller.MilestoneAlertsEnabled)
                            {
                                var alerts = milestones.Evaluate(snap.Quota);
                                foreach (var ev in alerts) controller.ShowMilestoneToast(ev);
                            }
                        }
                        controller.PostSnapshot(snap);
                    }
                    catch (Exception ex) { Note("polling: " + ex.GetType().Name); }
                    StopEvent.WaitOne(1000);
                }
            });

            Note("started v" + Version);
            app.Run();
            Note("stopped");
        }
    }
}
