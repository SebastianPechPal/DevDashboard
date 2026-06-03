namespace DevDashboard.BusinessTime;

/// <summary>
/// Computes elapsed business time between two UTC instants. Business days = Mon–Fri in
/// the configured time zone, excluding holidays. Each business day contributes up to 24h
/// of elapsed time; weekends and holidays contribute zero.
/// </summary>
public sealed class BusinessClock
{
    private readonly TimeZoneInfo _zone;
    private readonly HashSet<DateOnly> _holidays;

    public BusinessClock(TimeZoneInfo zone, IEnumerable<DateOnly> holidays)
    {
        _zone = zone;
        _holidays = holidays.ToHashSet();
    }

    public static BusinessClock Default { get; } = new(
        TimeZoneInfo.FindSystemTimeZoneById("Europe/Vienna"),
        AustriaHolidays.For2025To2026);

    public double ElapsedHours(DateTimeOffset start, DateTimeOffset end)
    {
        if (end <= start)
        {
            return 0;
        }

        var startLocal = TimeZoneInfo.ConvertTime(start, _zone).DateTime;
        var endLocal = TimeZoneInfo.ConvertTime(end, _zone).DateTime;

        var total = TimeSpan.Zero;
        var cursor = startLocal;

        while (cursor.Date < endLocal.Date)
        {
            var nextMidnight = cursor.Date.AddDays(1);
            if (IsBusinessDay(DateOnly.FromDateTime(cursor)))
            {
                total += nextMidnight - cursor;
            }
            cursor = nextMidnight;
        }

        if (IsBusinessDay(DateOnly.FromDateTime(cursor)))
        {
            total += endLocal - cursor;
        }

        return total.TotalHours;
    }

    public bool IsWithinSla(DateTimeOffset start, DateTimeOffset end, double slaHours)
    {
        return ElapsedHours(start, end) <= slaHours;
    }

    private bool IsBusinessDay(DateOnly date)
    {
        if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            return false;
        }
        return !_holidays.Contains(date);
    }

    /// <summary>
    /// Runs a small set of invariants over Default and throws if any fail. Cheap sanity
    /// check we run at startup — better than discovering off-by-one bugs in the metric.
    /// </summary>
    public static void SelfCheck()
    {
        var clock = Default;
        var vienna = clock._zone;

        Check("midweek 24h",
            Vienna(2026, 5, 4, 10, 0, vienna), Vienna(2026, 5, 5, 10, 0, vienna), expected: 24);

        Check("across weekend 24h",
            Vienna(2026, 5, 8, 14, 0, vienna), Vienna(2026, 5, 11, 14, 0, vienna), expected: 24);

        Check("across weekend 26h",
            Vienna(2026, 5, 8, 14, 0, vienna), Vienna(2026, 5, 11, 16, 0, vienna), expected: 26);

        // Wed 18:00 → Fri 18:00 spans Thu 14.05.2026 (Ascension): 6h Wed + 0h Thu + 18h Fri = 24h
        Check("holiday in middle",
            Vienna(2026, 5, 13, 18, 0, vienna), Vienna(2026, 5, 15, 18, 0, vienna), expected: 24);

        Check("same instant", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, expected: 0);

        var nowUtc = DateTimeOffset.UtcNow;
        Check("end before start", nowUtc.AddHours(1), nowUtc, expected: 0);

        static DateTimeOffset Vienna(int y, int m, int d, int hh, int mm, TimeZoneInfo zone)
        {
            var local = new DateTime(y, m, d, hh, mm, 0, DateTimeKind.Unspecified);
            return new DateTimeOffset(local, zone.GetUtcOffset(local));
        }

        void Check(string name, DateTimeOffset s, DateTimeOffset e, double expected)
        {
            var actual = clock.ElapsedHours(s, e);
            if (Math.Abs(actual - expected) > 0.01)
            {
                throw new InvalidOperationException(
                    $"BusinessClock self-check '{name}' failed: expected {expected}h, got {actual}h");
            }
        }
    }
}
