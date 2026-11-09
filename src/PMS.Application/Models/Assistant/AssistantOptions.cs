namespace PMS.Application.Models.Assistant;

/// <summary>
/// Settings of the customer chat assistant ("Assistant" section of appsettings).
/// The assistant answers from the Markdown help files in <see cref="KnowledgePath"/> (SRS FR-SYS-10),
/// using Google Gemini for both the embeddings and the answers, through Gemini's OpenAI-compatible API.
/// </summary>
public class AssistantOptions
{
    public const string Section = "Assistant";

    /// <summary>Turns the chat endpoint and the start-up indexing on or off.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// <see cref="AssistantProviders.Gemini"/> (default) or <see cref="AssistantProviders.OpenAiCompatible"/>
    /// (another OpenAI-compatible server at <see cref="BaseUrl"/>) answer with a model;
    /// <see cref="AssistantProviders.SearchOnly"/> returns the best matching help sections without one.
    /// </summary>
    public string Provider { get; set; } = AssistantProviders.Gemini;

    /// <summary>Base URL of the OpenAI-compatible API; empty uses Gemini's.</summary>
    public string BaseUrl { get; set; }

    /// <summary>
    /// API key sent as a Bearer token (a Gemini key from Google AI Studio). Keep it in user-secrets
    /// ("Assistant:ApiKey") or the server's app settings ("Assistant__ApiKey"), never in Git.
    /// </summary>
    public string ApiKey { get; set; }

    /// <summary>Model that writes the answers.</summary>
    public string ChatModel { get; set; } = "gemini-3.5-flash-lite";

    /// <summary>
    /// Models tried in order when the main one is overloaded or out of quota, which happens on the
    /// free tier at busy times.
    /// </summary>
    public List<string> FallbackChatModels { get; set; } = ["gemini-3.6-flash", "gemini-3.8-flash"];

    /// <summary>The main chat model followed by the fallbacks, without blanks or repeats.</summary>
    public IReadOnlyList<string> ChatModels =>
        new[] { ChatModel }.Concat(FallbackChatModels ?? [])
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Embedding model; it must be multilingual because questions are Vietnamese and the help is English.</summary>
    public string EmbeddingModel { get; set; } = "gemini-embedding-001";

    /// <summary>Folder of the help files, relative to the API's content root.</summary>
    public string KnowledgePath { get; set; } = "App_Data/knowledge";

    /// <summary>Cache of the section embeddings, so a restart does not embed every section again.</summary>
    public string IndexCachePath { get; set; } = "App_Data/knowledge-index/embeddings.json";

    /// <summary>How many help sections are given to the model for one question.</summary>
    public int TopK { get; set; } = 4;

    /// <summary>Sections with a lower cosine similarity to the question are left out; tune it with the evaluation set.</summary>
    public double MinScore { get; set; } = 0.6;

    /// <summary>Longest question accepted, in characters.</summary>
    public int MaxQuestionLength { get; set; } = 500;

    /// <summary>How many earlier messages of the conversation are sent to the model.</summary>
    public int MaxHistoryMessages { get; set; } = 6;

    /// <summary>Low values keep the model close to the help text.</summary>
    public double Temperature { get; set; } = 0.2;

    /// <summary>Timeout of one call to the model API.</summary>
    public int RequestTimeoutSeconds { get; set; } = 60;

    /// <summary>A chat model that has not started its answer within this time is skipped for the next fallback.</summary>
    public int ChatStartTimeoutSeconds { get; set; } = 10;

    /// <summary>How long an answer to a stand-alone question is reused for the same question; 0 turns the cache off.</summary>
    public int AnswerCacheMinutes { get; set; } = 360;

    /// <summary>The base URL to call, with a trailing slash.</summary>
    public string ResolvedBaseUrl =>
        (string.IsNullOrWhiteSpace(BaseUrl) ? "https://generativelanguage.googleapis.com/v1beta/openai/" : BaseUrl)
        .TrimEnd('/') + "/";

    /// <summary>
    /// True when a model can be called: not SearchOnly and an API key is set. Without a key (a fresh
    /// clone, or a deploy where the key is not set yet) the assistant quietly falls back to the help search.
    /// </summary>
    public bool UsesModel =>
        !string.Equals(Provider, AssistantProviders.SearchOnly, StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(ApiKey);
}

public static class AssistantProviders
{
    public const string Gemini = "Gemini";
    public const string OpenAiCompatible = "OpenAICompatible";
    public const string SearchOnly = "SearchOnly";
}
