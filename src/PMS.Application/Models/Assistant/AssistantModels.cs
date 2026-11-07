namespace PMS.Application.Models.Assistant;

/// <summary>The conversation so far; the last message is the customer's new question.</summary>
public class AssistantChatRequest
{
    public List<AssistantChatMessage> Messages { get; set; } = [];
}

public class AssistantChatMessage
{
    /// <summary>"user" or "assistant".</summary>
    public string Role { get; set; }

    public string Content { get; set; }
}

public static class AssistantRoles
{
    public const string User = "user";
    public const string Assistant = "assistant";
}

/// <summary>Whether the chat widget should be shown and how it answers right now.</summary>
public class AssistantStatusModel
{
    public bool Enabled { get; set; }

    /// <summary>"Model" when a language model writes the answers, "SearchOnly" when the help sections are returned as they are.</summary>
    public string Mode { get; set; }

    /// <summary>Number of help sections loaded.</summary>
    public int Sections { get; set; }

    /// <summary>True when sections are searched by meaning (embeddings), false when by keywords.</summary>
    public bool SemanticSearch { get; set; }
}

public static class AssistantModes
{
    public const string Model = "Model";
    public const string SearchOnly = "SearchOnly";
}

/// <summary>A help section the answer is based on.</summary>
public sealed record AssistantSource(string File, string Heading, string Title, double Score);

/// <summary>One "##" section of a help file.</summary>
public sealed class KnowledgeChunk
{
    public required string Id { get; init; }
    public required string File { get; init; }
    public required string Title { get; init; }
    public required string Heading { get; init; }
    public required string Content { get; init; }

    /// <summary>Text that is embedded: title, heading and body together.</summary>
    public string EmbeddingText => $"{Title} - {Heading}\n{Content}";

    public float[] Embedding { get; set; }
}

public sealed record ScoredChunk(KnowledgeChunk Chunk, double Score);

public sealed record RetrievalResult(IReadOnlyList<ScoredChunk> Chunks, bool Semantic);

/// <summary>Events streamed to the chat widget, in order: sources, deltas, then done (or error).</summary>
public abstract record AssistantEvent;

public sealed record AssistantSourcesEvent(IReadOnlyList<AssistantSource> Sources, string Mode) : AssistantEvent;

public sealed record AssistantDeltaEvent(string Text) : AssistantEvent;

public sealed record AssistantDoneEvent : AssistantEvent;

public sealed record AssistantErrorEvent(string Message) : AssistantEvent;
