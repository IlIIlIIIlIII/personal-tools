namespace PersonalTools;

// All calls are serialized by HdrController. No UI, timers or native calls in this state machine.
public sealed class HdrAutomation
{
    private readonly IHdrDisplay display;
    private readonly IHdrJournal journal;
    private readonly List<HdrSession> sessions;
    private readonly HashSet<string> seen = [];
    public string? Error { get; private set; }
    public IReadOnlyList<HdrSession> Sessions => sessions;
    public HdrAutomation(IHdrDisplay display, IHdrJournal journal)
    {
        this.display = display;
        this.journal = journal;
        sessions = journal.Load(); // A corrupt journal must never silently discard a pending restore.
    }

    public void Reconcile(IReadOnlyCollection<HdrProcess> processes, IReadOnlyCollection<HdrProgram> programs)
    {
        Error = null;
        var paths = programs.Where(p => p.Enabled).Select(p => p.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var active = processes.Where(p => paths.Contains(p.Path)).Select(p => p.Key).ToHashSet();
        var monitors = display.Read();
        foreach (var session in sessions.ToArray())
        {
            session.Owners.RemoveAll(key => !active.Contains(key));
            if (session.Owners.Count == 0)
            {
                Restore(session, monitors);
                continue;
            }
            foreach (var owner in session.Owners) seen.Add(owner);
            Error ??= session.Failure;
            // A changed state after a crash or an external manual change wins over automation.
            var monitor = monitors.FirstOrDefault(m => m.Id == session.MonitorId);
            if (!session.Manual && monitor is { Error: null, Enabled: false }) session.Manual = true;
        }
        journal.Save(sessions);
        seen.IntersectWith(active);
        // Restoration above may have changed HDR before a replacement process is admitted.
        var primary = display.Read().FirstOrDefault(m => m.Primary);
        foreach (var key in active.Where(key => !seen.Contains(key)))
        {
            if (primary is null) { Error = "주 모니터를 찾지 못했습니다."; continue; }
            if (!primary.Supported || primary.Error is not null) { Error = primary.Error ?? "주 모니터가 HDR을 지원하지 않습니다. HDR 모니터를 주 모니터로 설정하면 바로 적용합니다."; continue; }
            var session = sessions.FirstOrDefault(s => s.MonitorId == primary.Id);
            if (session is null)
            {
                session = new HdrSession { MonitorId = primary.Id, Original = primary.Enabled, StartedAt = DateTimeOffset.Now, Owners = [key] };
                sessions.Add(session);
                journal.Save(sessions); // Durable intent BEFORE changing the display.
                try { if (!primary.Enabled) display.Set(primary.Id, true); }
                catch (Exception e)
                {
                    Error = session.Failure = e.Message;
                    journal.Save(sessions);
                    seen.Add(key); continue;
                }
            }
            else { session.Owners.Add(key); journal.Save(sessions); }
            seen.Add(key);
        }
    }

    private void Restore(HdrSession session, HdrMonitor[] monitors)
    {
        if (!session.Manual)
        {
            var monitor = monitors.FirstOrDefault(m => m.Id == session.MonitorId);
            if (monitor is null) { Error = "연결 해제된 모니터의 HDR 복원을 기다립니다."; return; }
            if (monitor.Error is not null) { Error = monitor.Error; return; }
            // We only ever automatically write 'on'. A different state is an external override.
            if (monitor.Enabled && !session.Original)
            {
                try { display.Set(session.MonitorId, false); }
                catch (Exception e) { Error = "HDR 복원 실패: " + e.Message; return; }
            }
        }
        sessions.Remove(session);
        journal.Save(sessions);
    }

    public void SetManual(string id, bool enabled)
    {
        var session = sessions.FirstOrDefault(s => s.MonitorId == id);
        if (session is not null)
        {
            session.Manual = true;
            session.Failure = null;
            journal.Save(sessions); // Even a delayed/failed native operation must not be overridden.
        }
        display.Set(id, enabled);
    }

    public void Shutdown()
    {
        var monitors = display.Read();
        foreach (var session in sessions.ToArray()) Restore(session, monitors);
    }
}
