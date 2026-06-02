using System.Text.Json;

namespace CLIGoalHelper.Ado;

public sealed record VoteEvent(string VoterId, int VoteValue, DateTimeOffset At);

public sealed record CommentAuthor(string Id, string? DisplayName, string? Email, DateTimeOffset FirstCommentAt);

public sealed record ThreadActivity(IReadOnlyList<VoteEvent> Votes, IReadOnlyList<CommentAuthor> Commenters, DateTimeOffset? LatestCommentUtc);

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
    /// Fetches all threads on a PR and returns vote events plus the set of human commenters
    /// (one entry per author, timestamp = their earliest text comment). Vote events with a
    /// value of 0 (resets) are included — callers decide whether to count them.
    /// </summary>
    public async Task<ThreadActivity> GetThreadActivityAsync(
        string repoId,
        int pullRequestId,
        CancellationToken ct = default)
    {
        var url = $"{_organizationUrl}/_apis/git/repositories/{repoId}"
            + $"/pullrequests/{pullRequestId}/threads?api-version=7.1";

        using var doc = await _client.GetJsonAsync(url, ct);

        var votes = new List<VoteEvent>();
        var earliestCommentByAuthor = new Dictionary<string, CommentAuthor>();
        DateTimeOffset? latestCommentUtc = null;

        foreach (var thread in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            if (IsVoteUpdate(thread, out var voteValue) && TryReadVoter(thread, out var voterId, out var voteAt))
            {
                votes.Add(new VoteEvent(voterId, voteValue, voteAt));
                continue;
            }

            foreach (var comment in EnumerateTextComments(thread))
            {
                if (!earliestCommentByAuthor.TryGetValue(comment.Id, out var existing)
                    || comment.FirstCommentAt < existing.FirstCommentAt)
                {
                    earliestCommentByAuthor[comment.Id] = comment;
                }
                if (latestCommentUtc is null || comment.FirstCommentAt > latestCommentUtc)
                {
                    latestCommentUtc = comment.FirstCommentAt;
                }
            }
        }

        votes.Sort((a, b) => a.At.CompareTo(b.At));
        return new ThreadActivity(votes, earliestCommentByAuthor.Values.ToList(), latestCommentUtc);
    }

    private static IEnumerable<CommentAuthor> EnumerateTextComments(JsonElement thread)
    {
        if (!thread.TryGetProperty("comments", out var comments) || comments.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var comment in comments.EnumerateArray())
        {
            // Skip system / codeChange / deleted comments — only count real human text.
            if (comment.TryGetProperty("commentType", out var ct) && ct.GetString() is { } kind && kind != "text")
            {
                continue;
            }
            if (comment.TryGetProperty("isDeleted", out var del) && del.ValueKind == JsonValueKind.True)
            {
                continue;
            }
            if (!TryGetObject(comment, "author", out var author)) continue;
            if (!author.TryGetProperty("id", out var idEl)) continue;
            var id = idEl.GetString();
            if (string.IsNullOrEmpty(id)) continue;
            if (!comment.TryGetProperty("publishedDate", out var pub)) continue;

            var displayName = author.TryGetProperty("displayName", out var n) ? n.GetString() : null;
            var email = author.TryGetProperty("uniqueName", out var u) ? u.GetString() : null;
            var at = DateTimeOffset.Parse(pub.GetString()!);

            yield return new CommentAuthor(id, displayName, email, at);
        }
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
