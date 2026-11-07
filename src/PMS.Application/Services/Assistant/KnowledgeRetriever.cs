using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using PMS.Application.Models.Assistant;

namespace PMS.Application.Services.Assistant;

/// <summary>Finds the help sections closest to a question.</summary>
public interface IKnowledgeRetriever
{
    Task<RetrievalResult> SearchAsync(string query, int take, CancellationToken cancellationToken);
}

/// <summary>
/// Searches by meaning (cosine similarity of embeddings) when the sections are embedded, otherwise
/// by keywords, so the assistant keeps working when the model server is down or not installed.
/// </summary>
public sealed class KnowledgeRetriever(
    KnowledgeBase knowledge,
    ModelClient model,
    TimeProvider clock,
    ILogger<KnowledgeRetriever> logger) : IKnowledgeRetriever
{
    /// <summary>After a failed embedding call, questions go straight to keyword search for this long instead of waiting on the same error.</summary>
    private static readonly TimeSpan PauseAfterFailure = TimeSpan.FromSeconds(30);

    private DateTimeOffset _semanticPausedUntil = DateTimeOffset.MinValue;

    public async Task<RetrievalResult> SearchAsync(string query, int take, CancellationToken cancellationToken)
    {
        await knowledge.EnsureReadyAsync(cancellationToken);
        var chunks = knowledge.Chunks;

        // The availability check is cached, so a server that is down costs no wait here
        if (knowledge.HasEmbeddings && clock.GetUtcNow() >= _semanticPausedUntil && await model.IsAvailableAsync(cancellationToken))
        {
            try
            {
                var vector = (await model.EmbedAsync([query], cancellationToken))[0];
                var ranked = chunks
                    .Select(c => new ScoredChunk(c, Cosine(vector, c.Embedding)))
                    .OrderByDescending(s => s.Score)
                    .Take(take)
                    .ToList();
                return new RetrievalResult(ranked, Semantic: true);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                _semanticPausedUntil = clock.GetUtcNow() + PauseAfterFailure;
                logger.LogWarning("Assistant: embedding the question failed, keyword search is used for {Seconds} s ({Error})",
                    PauseAfterFailure.TotalSeconds, e.Message);
            }
        }
        return new RetrievalResult(KeywordSearch.Rank(chunks, query, take), Semantic: false);
    }

    private static double Cosine(float[] a, float[] b)
    {
        if (a == null || b == null || a.Length != b.Length)
            return 0;
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        return na == 0 || nb == 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}

/// <summary>Keyword fallback: sums the inverse document frequency of the query words found in a section.</summary>
public static partial class KeywordSearch
{
    private static readonly HashSet<string> StopWords =
    [
        "a", "an", "and", "are", "can", "do", "does", "for", "how", "i", "if", "in", "is", "it", "my", "of", "on",
        "or", "the", "to", "what", "when", "where", "which", "why", "with", "you", "your", "me", "be", "at", "there"
    ];

    public static IReadOnlyList<ScoredChunk> Rank(IReadOnlyList<KnowledgeChunk> chunks, string query, int take)
    {
        var terms = Tokens(query).Where(t => !StopWords.Contains(t)).Distinct().ToList();
        if (terms.Count == 0 || chunks.Count == 0)
            return [];

        var documents = chunks.Select(c => Tokens(c.EmbeddingText).ToHashSet()).ToList();
        var idf = terms.ToDictionary(t => t,
            t => Math.Log(1 + chunks.Count / (1.0 + documents.Count(d => d.Contains(t)))));
        var best = terms.Sum(t => idf[t]);

        return chunks
            .Select((c, i) => new ScoredChunk(c, terms.Where(documents[i].Contains).Sum(t => idf[t]) / best))
            .Where(s => s.Score > 0)
            .OrderByDescending(s => s.Score)
            .Take(take)
            .ToList();
    }

    private static IEnumerable<string> Tokens(string text) =>
        WordPattern().Matches(text.ToLowerInvariant()).Select(m => m.Value);

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordPattern();
}
