using System.Text.Json;

namespace CLIGoalHelper.Ado;

public sealed record VoteEvent(string VoterId, int VoteValue, DateTimeOffset At);

public sealed class ThreadService
{
    private readonly AdoClient _client;
    private readonly string _organizationUrl;

    public ThreadService(AdoClient client, string organizationUrl)
    {
        _client = client;
        _organizationUrl = organizationUrl.TrimEnd('/');
    }

    /// <summary>
    /// Returns all vote events on a PR in chronological order. A vote of 0 means the vote
    /// was reset and is included here — callers decide whether to count it.
    /// </summary>
    public async Task<List<VoteEvent>> GetVoteEventsAsync(
        string repoId,
        int pullRequestId,
        CancellationToken ct = default)
    {
        var url = $"{_organizationUrl}/_apis/git/repositories/{repoId}"
            + $"/pullrequests/{pullRequestId}/threads?api-version=7.1";

        using var doc = await _client.GetJsonAsync(url, ct);

        var events = new List<VoteEvent>();
        foreach (var thread in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            if (!IsVoteUpdate(thread, out var voteValue))
            {
                continue;
            }
            if (!TryReadVoter(thread, out var voterId, out var at))
            {
                continue;
            }
            events.Add(new VoteEvent(voterId, voteValue, at));
        }
        return events.OrderBy(e => e.At).ToList();
    }

    private static bool IsVoteUpdate(JsonElement thread, out int voteValue)
    {
        voteValue = 0;
        if (!TryGetObject(thread, "properties", out var props)) return false;
        if (!TryGetSystemString(props, "CodeReviewThreadType", out var threadType) || threadType != "VoteUpdate")
        {
            return false;
        }
        if (!TryGetSystemString(props, "CodeReviewVoteResult", out var voteText)) return false;
        return int.TryParse(voteText, out voteValue);
    }

    private static bool TryReadVoter(JsonElement thread, out string voterId, out DateTimeOffset at)
    {
        voterId = string.Empty;
        at = default;

        if (!thread.TryGetProperty("comments", out var comments)
            || comments.ValueKind != JsonValueKind.Array
            || comments.GetArrayLength() == 0)
        {
            return false;
        }

        var first = comments[0];
        if (!TryGetObject(first, "author", out var author)) return false;
        if (!author.TryGetProperty("id", out var idEl)) return false;

        var id = idEl.GetString();
        if (string.IsNullOrEmpty(id)) return false;

        if (!first.TryGetProperty("publishedDate", out var pub)) return false;

        voterId = id;
        at = DateTimeOffset.Parse(pub.GetString()!);
        return true;
    }

    private static bool TryGetObject(JsonElement parent, string key, out JsonElement value)
    {
        value = default;
        if (!parent.TryGetProperty(key, out var raw)) return false;
        if (raw.ValueKind != JsonValueKind.Object) return false;
        value = raw;
        return true;
    }

    private static bool TryGetSystemString(JsonElement properties, string key, out string value)
    {
        value = string.Empty;
        if (!TryGetObject(properties, key, out var wrap)) return false;
        if (!wrap.TryGetProperty("$value", out var inner)) return false;
        var s = inner.GetString();
        if (s == null) return false;
        value = s;
        return true;
    }
}
