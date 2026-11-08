using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using PMS.Application.Models.Assistant;

namespace PMS.Application.Services.Assistant;

/// <summary>
/// Remembers recent answers to stand-alone questions (no earlier turns), so a question asked again,
/// such as a suggested question in the chat, is answered at once without calling the model. The help
/// files only change with a restart, which empties the cache.
/// </summary>
public sealed partial class AnswerCache(AssistantOptions options, TimeProvider clock)
{
    /// <summary>Most answers kept; the oldest go first when it is full.</summary>
    private const int Capacity = 300;

    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    public bool IsEnabled => options.AnswerCacheMinutes > 0;

    public bool TryGet(string question, out string answer, out IReadOnlyList<AssistantSource> sources)
    {
        answer = null;
        sources = [];
        if (!IsEnabled || !_entries.TryGetValue(Key(question), out var entry))
            return false;
        if (clock.GetUtcNow() - entry.SavedAt > TimeSpan.FromMinutes(options.AnswerCacheMinutes))
        {
            _entries.TryRemove(Key(question), out _);
            return false;
        }
        answer = entry.Answer;
        sources = entry.Sources;
        return true;
    }

    public void Save(string question, string answer, IReadOnlyList<AssistantSource> sources)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(answer))
            return;
        if (_entries.Count >= Capacity)
        {
            foreach (var oldest in _entries.OrderBy(e => e.Value.SavedAt).Take(Capacity / 10).ToList())
                _entries.TryRemove(oldest.Key, out _);
        }
        _entries[Key(question)] = new Entry(answer, sources, clock.GetUtcNow());
    }

    /// <summary>"How much is the deposit?" and "how much is the  deposit" are the same question.</summary>
    public static string Key(string question) =>
        Spaces().Replace(question.Trim().TrimEnd('?', '.', '!', ' ').ToLowerInvariant(), " ");

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    private sealed record Entry(string Answer, IReadOnlyList<AssistantSource> Sources, DateTimeOffset SavedAt);
}
