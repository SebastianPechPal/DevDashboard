namespace CLIGoalHelper.BusinessTime;

/// <summary>
/// Hardcoded Austrian public holidays 2025–2026. Easter-relative dates are precomputed
/// rather than calculated at runtime — extend this list before Q1 2027.
/// </summary>
internal static class AustriaHolidays
{
    public static readonly IReadOnlyCollection<DateOnly> For2025To2026 = new[]
    {
        // 2025
        new DateOnly(2025, 1, 1),    // New Year
        new DateOnly(2025, 1, 6),    // Epiphany
        new DateOnly(2025, 4, 21),   // Easter Monday
        new DateOnly(2025, 5, 1),    // Labour Day
        new DateOnly(2025, 5, 29),   // Ascension
        new DateOnly(2025, 6, 9),    // Whit Monday
        new DateOnly(2025, 6, 19),   // Corpus Christi
        new DateOnly(2025, 8, 15),   // Assumption
        new DateOnly(2025, 10, 26),  // National Day (Sun in 2025)
        new DateOnly(2025, 11, 1),   // All Saints (Sat in 2025)
        new DateOnly(2025, 12, 8),   // Immaculate Conception
        new DateOnly(2025, 12, 25),  // Christmas Day
        new DateOnly(2025, 12, 26),  // St Stephen's Day
        // 2026
        new DateOnly(2026, 1, 1),
        new DateOnly(2026, 1, 6),
        new DateOnly(2026, 4, 6),    // Easter Monday
        new DateOnly(2026, 5, 1),
        new DateOnly(2026, 5, 14),   // Ascension
        new DateOnly(2026, 5, 25),   // Whit Monday
        new DateOnly(2026, 6, 4),    // Corpus Christi
        new DateOnly(2026, 8, 15),   // Sat in 2026
        new DateOnly(2026, 10, 26),
        new DateOnly(2026, 11, 1),   // Sun in 2026
        new DateOnly(2026, 12, 8),
        new DateOnly(2026, 12, 25),
        new DateOnly(2026, 12, 26),  // Sat in 2026
    };
}
