namespace RetroTodo;

/// <summary>
/// Windows notifications: "due in N min" and "due now" for todos with a time, plus a morning
/// summary of everything due today/overdue. Wakes only when the next event is due
/// (at least every 10 minutes, to notice the day changing), so it costs nothing in between.
/// </summary>
sealed class Reminders : IDisposable
{
    readonly Store store;
    readonly Action<string, string> notify;
    readonly Action refresh;
    readonly System.Windows.Forms.Timer timer = new();
    DateTime lastScan = DateTime.Now;

    public Reminders(Store store, Action<string, string> notify, Action refresh)
    {
        this.store = store;
        this.notify = notify;
        this.refresh = refresh;
        timer.Tick += (_, _) => Tick();
        timer.Interval = 5000; // first check shortly after startup (catches a missed morning summary)
        timer.Start();
    }

    public void Reschedule()
    {
        var now = DateTime.Now;
        var next = NextEvent(now);
        double ms = next == null ? 600_000 : Math.Clamp((next.Value - now).TotalMilliseconds + 300, 1000, 600_000);
        timer.Stop();
        timer.Interval = (int)ms;
        timer.Start();
    }

    IEnumerable<(DateTime at, string title, TodoItem it)> Events()
    {
        int before = store.Settings.RemindMinutes;
        foreach (var s in store.Sections)
            foreach (var it in s.All())
            {
                if (it.Complete() || !Dates.TryParseStored(it.Due, out var d, out bool hasTime) || !hasTime) continue;
                if (before > 0) yield return (d.AddMinutes(-before), $"DUE IN {before} MIN", it);
                yield return (d, "DUE NOW", it);
            }
    }

    DateTime? MorningAt(DateTime day) =>
        Dates.TryParseClock(store.Settings.MorningTime, out var t) ? day.Date + t : null;

    DateTime? NextEvent(DateTime now)
    {
        DateTime? best = null;
        foreach (var e in Events())
            if (e.at > now && (best == null || e.at < best)) best = e.at;
        if (MorningAt(now) is { } m)
        {
            var next = store.Settings.LastMorning == Dates.TodayKey || m <= now ? MorningAt(now.AddDays(1))!.Value : m;
            if (best == null || next < best) best = next;
        }
        return best;
    }

    void Tick()
    {
        var now = DateTime.Now;
        foreach (var e in Events())
            if (e.at > lastScan && e.at <= now && now - e.at < TimeSpan.FromHours(2)) // skip stale ones after sleep
                notify(e.title, Line(e.it, now));

        if (MorningAt(now) is { } m && now >= m && store.Settings.LastMorning != Dates.TodayKey)
        {
            store.Settings.LastMorning = Dates.TodayKey;
            store.Save();
            Digest(now);
        }
        lastScan = now;
        refresh(); // overdue colours and "today" change with the clock
        Reschedule();
    }

    public void Digest(DateTime now)
    {
        var items = store.TodayItems(now);
        if (items.Count == 0)
        {
            notify("GOOD MORNING", "Nothing due today. Mark something for today with T.");
            return;
        }
        int overdue = items.Count(i => Dates.IsOverdue(i, now));
        string title = $"TODAY: {items.Count} TODO{(items.Count == 1 ? "" : "S")}" + (overdue > 0 ? $"  ({overdue} OVERDUE)" : "");
        var lines = items.Take(5).Select(i => "• " + Line(i, now)).ToList();
        if (items.Count > 5) lines.Add($"…and {items.Count - 5} more");
        notify(title, string.Join("\n", lines));
    }

    static string Line(TodoItem it, DateTime now)
    {
        string when = Dates.TryParseStored(it.Due, out var d, out var ht) ? Dates.Label(d, ht, now, false, compact: true) + "  " : "";
        string where = it.Section.IsLinked ? $"  [{it.Section.Title}]" : "";
        return when + it.Text + where;
    }

    public void Dispose() => timer.Dispose();
}
