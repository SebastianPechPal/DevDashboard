using System.Globalization;

namespace DevDashboard;

/// <summary>
/// Culture-invariant parsing of the ISO-8601 / round-trip ("o") timestamp strings the app
/// handles — both Azure DevOps REST responses and the values written to the SQLite cache.
/// Using <see cref="CultureInfo.InvariantCulture"/> with <see cref="DateTimeStyles.RoundtripKind"/>
/// keeps parsing identical regardless of the host machine's locale.
/// </summary>
internal static class IsoParse
{
    public static DateTimeOffset Offset(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
