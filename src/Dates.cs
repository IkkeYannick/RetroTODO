using System.Globalization;
using System.Text.RegularExpressions;

namespace RetroTodo;

/// <summary>
/// Due dates. Stored as "yyyy-MM-dd" (whole day) or "yyyy-MM-ddTHH:mm" (with a time).
/// Typed input understands: today, tomorrow/tmr, mon..sun, 20/11, 20/11/2026, 2026-11-20,
/// 18:00, 18h30, 6pm, +2h, +30m, +3d, +1w, and combinations like "fri 18:00".
/// </summary>
static class Dates
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly string[] DayNames = { "sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday" };
    static readonly Regex DayMonth = new(@"^(\d{1,2})[/.\-](\d{1,2})(?:[/.\-](\d{4}|\d{2}))?$");
    static readonly Regex Clock = new(@"^(\d{1,2})(?:[:hu.](\d{2}))?(am|pm|h|u)?$");
    static readonly Regex Relative = new(@"^\+(\d{1,4})(min|m|h|d|w)$");

    public static string TodayKey => DateTime.Now.ToString("yyyy-MM-dd", Inv);

    public static string ToStored(DateTime d, bool hasTime) =>
        d.ToString(hasTime ? "yyyy-MM-ddTHH:mm" : "yyyy-MM-dd", Inv);

    /// <summary>Format used to prefill the DUE prompt.</summary>
    public static string Human(DateTime d, bool hasTime) =>
        d.ToString(hasTime ? "dd/MM/yyyy HH:mm" : "dd/MM/yyyy", Inv);

    public static bool TryParseStored(string? s, out DateTime d, out bool hasTime)
    {
        d = default;
        hasTime = false;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        if (DateTime.TryParseExact(s, "yyyy-MM-dd", Inv, DateTimeStyles.None, out d)) return true;
        if (DateTime.TryParseExact(s, new[] { "yyyy-MM-ddTHH:mm", "yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm:ss" },
                Inv, DateTimeStyles.None, out d))
        {
            hasTime = true;
            return true;
        }
        if (s.Length >= 10 && char.IsDigit(s[0]) && s[4] == '-' && DateTime.TryParse(s, Inv, DateTimeStyles.None, out d))
        {
            hasTime = s.Contains(':'); // full ISO from other apps, e.g. 2026-11-20T18:00:00Z
            return true;
        }
        return TryParseInput(s, DateTime.Now, out d, out hasTime); // hand-written, e.g. @due(20/11 18:00)
    }

    public static bool TryParseInput(string input, DateTime now, out DateTime d, out bool hasTime)
    {
        d = default;
        hasTime = false;
        var toks = input.Trim().ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t is not ("at" or "om" or "on" or "op"))
            .ToList();
        if (toks.Count == 0) return false;

        DateTime? date = null;
        TimeSpan? time = null;
        foreach (var t in toks)
        {
            var r = Relative.Match(t);
            if (r.Success)
            {
                int n = int.Parse(r.Groups[1].Value);
                switch (r.Groups[2].Value)
                {
                    case "m" or "min":
                        if (toks.Count != 1) return false;
                        d = Trunc(now.AddMinutes(n)); hasTime = true; return true;
                    case "h":
                        if (toks.Count != 1) return false;
                        d = Trunc(now.AddHours(n)); hasTime = true; return true;
                    case "d":
                        if (date != null) return false;
                        date = now.Date.AddDays(n); break;
                    case "w":
                        if (date != null) return false;
                        date = now.Date.AddDays(7 * n); break;
                }
                continue;
            }
            if (TryDate(t, now, out var dd))
            {
                if (date != null) return false;
                date = dd;
                continue;
            }
            if (TryParseClock(t, out var tt))
            {
                if (time != null) return false;
                time = tt;
                continue;
            }
            return false;
        }

        if (date == null)
        {
            date = now.Date;
            if (time != null && now.Date + time.Value <= now) date = date.Value.AddDays(1); // "18:00" after 18:00 = tomorrow
        }
        hasTime = time != null;
        d = date.Value + (time ?? TimeSpan.Zero);
        return true;
    }

    public static bool TryParseClock(string? s, out TimeSpan t)
    {
        t = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var m = Clock.Match(s.Trim().ToLowerInvariant());
        if (!m.Success || (m.Groups[2].Length == 0 && m.Groups[3].Length == 0)) return false; // bare "18" is not a time
        int h = int.Parse(m.Groups[1].Value);
        int min = m.Groups[2].Length > 0 ? int.Parse(m.Groups[2].Value) : 0;
        string suffix = m.Groups[3].Value;
        if (suffix == "pm" && h < 12) h += 12;
        else if (suffix == "am" && h == 12) h = 0;
        if (h > 23 || min > 59) return false;
        t = new TimeSpan(h, min, 0);
        return true;
    }

    static bool TryDate(string t, DateTime now, out DateTime d)
    {
        d = default;
        switch (t)
        {
            case "today" or "tod" or "vandaag":
                d = now.Date; return true;
            case "tomorrow" or "tmr" or "tom" or "morgen":
                d = now.Date.AddDays(1); return true;
        }
        if (t.Length >= 3 && char.IsLetter(t[0]))
        {
            for (int i = 0; i < 7; i++)
            {
                if (!DayNames[i].StartsWith(t)) continue;
                d = now.Date.AddDays((i - (int)now.DayOfWeek + 7) % 7); // "fri" on a Friday = today
                return true;
            }
        }
        if (DateTime.TryParseExact(t, "yyyy-MM-dd", Inv, DateTimeStyles.None, out d)) return true;

        var m = DayMonth.Match(t);
        if (!m.Success) return false;
        int day = int.Parse(m.Groups[1].Value), month = int.Parse(m.Groups[2].Value);
        bool hasYear = m.Groups[3].Length > 0;
        int year = hasYear ? int.Parse(m.Groups[3].Value) : now.Year;
        if (year < 100) year += 2000;
        if (month is < 1 or > 12 || year is < 2000 or > 2200 || day < 1 || day > DateTime.DaysInMonth(year, month)) return false;
        d = new DateTime(year, month, day);
        if (!hasYear && d < now.Date)
        {
            if (day > DateTime.DaysInMonth(year + 1, month)) return false;
            d = new DateTime(year + 1, month, day); // 03/01 typed in December = next January
        }
        return true;
    }

    static DateTime Trunc(DateTime t) => new(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0);

    /// <summary>Short label: OVERDUE, TODAY 18:00, TMR, FRI 09:00, 20 NOV. Compact drops "TODAY" when a time is set.</summary>
    public static string Label(DateTime d, bool hasTime, DateTime now, bool done, bool compact = false)
    {
        string time = hasTime ? d.ToString("HH:mm", Inv) : "";
        int days = (d.Date - now.Date).Days;
        if (!done && (days < 0 || (hasTime && d < now)))
            return days < -1 ? $"OVERDUE {-days}D" : "OVERDUE";
        if (compact && days == 0 && hasTime) return time;
        string day = days switch
        {
            0 => "TODAY",
            1 => "TMR",
            > 1 and < 7 => d.ToString("ddd", Inv).ToUpperInvariant(),
            _ => d.ToString(d.Year == now.Year ? "dd MMM" : "dd MMM yy", Inv).ToUpperInvariant(),
        };
        return hasTime ? day + " " + time : day;
    }

    public static bool IsOverdue(TodoItem it, DateTime now) =>
        !it.Complete() && TryParseStored(it.Due, out var d, out var ht) && (ht ? d < now : d.Date < now.Date);

    /// <summary>Not done and due today or earlier.</summary>
    public static bool DueByToday(TodoItem it, DateTime now) =>
        !it.Complete() && TryParseStored(it.Due, out var d, out _) && d.Date <= now.Date;

    /// <summary>"Call mom @fri 18:00" -> text "Call mom", due fri 18:00.</summary>
    public static void SplitDue(string input, DateTime now, out string text, out string? due, out string? error)
    {
        text = input.Trim();
        due = null;
        error = null;
        int i = text.LastIndexOf('@');
        if (i < 0 || (i > 0 && text[i - 1] != ' ')) return; // e-mail addresses etc. stay text
        string part = text[(i + 1)..].Trim();
        if (part.Length == 0) return;
        if (TryParseInput(part, now, out var d, out var ht))
        {
            due = ToStored(d, ht);
            text = text[..i].Trim();
        }
        else error = $"COULDN'T READ DATE '@{part}'.";
    }
}
