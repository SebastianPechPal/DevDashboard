using System.Text.RegularExpressions;

namespace DevDashboard.Config;

/// <summary>
/// Reads a git mailmap (~/.mailmap) and exposes a two-letter short-code lookup
/// keyed by email, used to compact author/reviewer names in the dashboard.
/// </summary>
public sealed class MailmapResolver
{
    // Matches one "<Name> <email>" or "<email>" segment. Mailmap lines have 1, 2, or 4 such segments.
    private static readonly Regex Segment = new(@"([^<>]*?)\s*<([^>]+)>", RegexOptions.Compiled);

    private readonly Dictionary<string, string> _emailToName;

    private MailmapResolver(Dictionary<string, string> emailToName)
    {
        _emailToName = emailToName;
    }

    public static MailmapResolver LoadFromHome()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".mailmap");
        return File.Exists(path)
            ? Load(File.ReadAllLines(path))
            : Empty();
    }

    public static MailmapResolver Empty() => new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    public static MailmapResolver Load(IEnumerable<string> lines)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            var matches = Segment.Matches(line);
            if (matches.Count == 0) continue;

            // The canonical name is always the FIRST name token on the line; remaining
            // emails on that line are aliases that should map to the same canonical name.
            var canonicalName = matches[0].Groups[1].Value.Trim();
            if (canonicalName.Length == 0) continue;

            foreach (Match m in matches)
            {
                var email = m.Groups[2].Value.Trim();
                if (email.Length > 0)
                {
                    map[email] = canonicalName;
                }
            }
        }
        return new MailmapResolver(map);
    }

    /// <summary>
    /// Returns a two-letter short code such as "SP" derived from the canonical name
    /// (looked up by email) or, failing that, from <paramref name="displayName"/>.
    /// First letter of the first word + first letter of the second word, uppercased.
    /// </summary>
    public string Shorten(string? displayName, string? email)
    {
        var name = email != null && _emailToName.TryGetValue(email, out var canonical)
            ? canonical
            : displayName ?? string.Empty;

        var parts = name.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "??",
            1 => InitialOf(parts[0]) + "?",
            _ => InitialOf(parts[0]) + InitialOf(parts[1]),
        };
    }

    private static string InitialOf(string word)
        => word.Length == 0 ? "?" : word[..1].ToUpperInvariant();
}
