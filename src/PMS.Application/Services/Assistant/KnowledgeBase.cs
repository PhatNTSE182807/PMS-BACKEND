using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using PMS.Application.Models.Assistant;

namespace PMS.Application.Services.Assistant;

/// <summary>
/// The help sections the assistant answers from, with their embeddings. Sections are read from the
/// Markdown files once; embeddings are computed through the model server and cached on disk by content hash.
/// When the model server is down the sections still load and search falls back to keywords; embedding is
/// retried a minute later.
/// </summary>
public sealed class KnowledgeBase(
    AssistantOptions options,
    ModelClient model,
    IWebHostEnvironment environment,
    ILogger<KnowledgeBase> logger)
{
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);
    private const int EmbedBatchSize = 16;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<KnowledgeChunk> _chunks = [];
    private bool _loaded;
    private DateTime _nextEmbedAttemptUtc = DateTime.MinValue;

    public IReadOnlyList<KnowledgeChunk> Chunks => _chunks;

    /// <summary>True when every section has an embedding, so search can go by meaning.</summary>
    public bool HasEmbeddings { get; private set; }

    public async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (_loaded && (HasEmbeddings || !options.UsesModel || DateTime.UtcNow < _nextEmbedAttemptUtc))
            return;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_loaded)
            {
                _chunks = KnowledgeParser.LoadFolder(Resolve(options.KnowledgePath));
                _loaded = true;
                logger.LogInformation("Assistant knowledge base: {Count} sections loaded", _chunks.Count);
            }

            if (HasEmbeddings || !options.UsesModel || DateTime.UtcNow < _nextEmbedAttemptUtc)
                return;

            try
            {
                await EmbedAllAsync(cancellationToken);
                HasEmbeddings = true;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                _nextEmbedAttemptUtc = DateTime.UtcNow + RetryAfter;
                logger.LogWarning("Assistant: embedding the help sections failed, keyword search is used until it works ({Error})", e.Message);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EmbedAllAsync(CancellationToken cancellationToken)
    {
        var cachePath = Resolve(options.IndexCachePath);
        var cache = ReadCache(cachePath);
        var missing = new List<(KnowledgeChunk Chunk, string Hash)>();

        foreach (var chunk in _chunks)
        {
            var hash = Hash(chunk.EmbeddingText);
            if (cache.TryGetValue(hash, out var vector))
                chunk.Embedding = vector;
            else
                missing.Add((chunk, hash));
        }

        foreach (var batch in missing.Chunk(EmbedBatchSize))
        {
            var vectors = await model.EmbedAsync(batch.Select(b => b.Chunk.EmbeddingText).ToList(), cancellationToken);
            for (var i = 0; i < batch.Length; i++)
            {
                batch[i].Chunk.Embedding = vectors[i];
                cache[batch[i].Hash] = vectors[i];
            }
        }

        if (missing.Count > 0)
        {
            // Keep only the vectors of current sections so the cache does not grow forever
            var current = _chunks.Select(c => Hash(c.EmbeddingText)).ToHashSet();
            WriteCache(cachePath, cache.Where(kv => current.Contains(kv.Key)).ToDictionary());
        }
        logger.LogInformation("Assistant: {Count} sections embedded with {Model} ({New} new)",
            _chunks.Count, options.EmbeddingModel, missing.Count);
    }

    /// <summary>The cache is keyed by model and text, so changing either embeds the section again.</summary>
    private string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(options.EmbeddingModel + "\n" + text)));

    private Dictionary<string, float[]> ReadCache(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Dictionary<string, float[]>>(File.ReadAllText(path)) ?? [];
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogWarning("Assistant: the embedding cache could not be read and is rebuilt ({Error})", e.Message);
        }
        return [];
    }

    private void WriteCache(string path, Dictionary<string, float[]> cache)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(cache));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not fatal: the sections are embedded again on the next start
            logger.LogWarning("Assistant: the embedding cache could not be saved ({Error})", e.Message);
        }
    }

    private string Resolve(string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(environment.ContentRootPath, path);
}

/// <summary>Splits the help files into "##" sections. README.md is not indexed.</summary>
public static partial class KnowledgeParser
{
    public static IReadOnlyList<KnowledgeChunk> LoadFolder(string folder)
    {
        if (!Directory.Exists(folder))
            return [];
        return Directory.GetFiles(folder, "*.md")
            .Where(f => !string.Equals(Path.GetFileName(f), "README.md", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .SelectMany(f => Parse(Path.GetFileName(f), File.ReadAllText(f)))
            .ToList();
    }

    public static IEnumerable<KnowledgeChunk> Parse(string fileName, string markdown)
    {
        var title = Path.GetFileNameWithoutExtension(fileName);
        string heading = null;
        var body = new StringBuilder();

        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("# ", StringComparison.Ordinal) && heading == null)
            {
                title = line[2..].Trim();
            }
            else if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (heading != null && Make(fileName, title, heading, body) is { } chunk)
                    yield return chunk;
                heading = line[3..].Trim();
                body.Clear();
            }
            else if (heading != null)
            {
                AppendLine(body, line.Trim());
            }
        }
        if (heading != null && Make(fileName, title, heading, body) is { } last)
            yield return last;
    }

    /// <summary>
    /// The files are wrapped at about 100 characters: lines of one paragraph are joined with a space,
    /// while blank lines and list items ("- ", "1. ") start a new line.
    /// </summary>
    private static void AppendLine(StringBuilder body, string line)
    {
        if (line.Length == 0)
        {
            if (body.Length > 0 && !EndsWith(body, "\n\n"))
                body.Append(body[^1] == '\n' ? "\n" : "\n\n");
            return;
        }
        var startsItem = line.StartsWith("- ", StringComparison.Ordinal) || ListItem().IsMatch(line);
        if (body.Length > 0 && body[^1] != '\n')
            body.Append(startsItem ? '\n' : ' ');
        body.Append(line);
    }

    private static bool EndsWith(StringBuilder body, string end) =>
        body.Length >= end.Length && body.ToString(body.Length - end.Length, end.Length) == end;

    [System.Text.RegularExpressions.GeneratedRegex(@"^\d+\.\s")]
    private static partial System.Text.RegularExpressions.Regex ListItem();

    private static KnowledgeChunk Make(string fileName, string title, string heading, StringBuilder body)
    {
        var content = body.ToString().Trim();
        return content.Length == 0
            ? null
            : new KnowledgeChunk { Id = $"{fileName}#{heading}", File = fileName, Title = title, Heading = heading, Content = content };
    }
}
