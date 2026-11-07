using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using PMS.Application.Exceptions;
using PMS.Application.Models.Assistant;

namespace PMS.Application.Services.Assistant;

public interface IAssistantService
{
    Task<AssistantStatusModel> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>Checks the request and returns the conversation to answer; throws <see cref="BadRequestException"/>.</summary>
    IReadOnlyList<AssistantChatMessage> Validate(AssistantChatRequest request);

    /// <summary>Streams the answer to the last message of a validated conversation.</summary>
    IAsyncEnumerable<AssistantEvent> AnswerAsync(IReadOnlyList<AssistantChatMessage> conversation, CancellationToken cancellationToken);
}

/// <summary>
/// Customer chat assistant (SRS FR-SYS-10): retrieves the closest help sections and lets the model
/// answer from them only. Without a model (SearchOnly, no API key, or server unreachable) it returns the best
/// sections as they are. It never reads account data.
/// </summary>
public sealed class AssistantService(
    AssistantOptions options,
    IKnowledgeRetriever retriever,
    KnowledgeBase knowledge,
    ModelClient model,
    ILogger<AssistantService> logger) : IAssistantService
{
    /// <summary>A follow-up shorter than this is searched together with the previous question.</summary>
    private const int ShortFollowUpLength = 60;

    /// <summary>Sections shown when there is no model to write an answer.</summary>
    private const int SearchOnlySections = 2;

    public async Task<AssistantStatusModel> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (!options.Enabled)
            return new AssistantStatusModel { Enabled = false, Mode = AssistantModes.SearchOnly };

        await knowledge.EnsureReadyAsync(cancellationToken);
        var modelUp = options.UsesModel && await model.IsAvailableAsync(cancellationToken);
        return new AssistantStatusModel
        {
            Enabled = true,
            Mode = modelUp ? AssistantModes.Model : AssistantModes.SearchOnly,
            Sections = knowledge.Chunks.Count,
            SemanticSearch = knowledge.HasEmbeddings
        };
    }

    public IReadOnlyList<AssistantChatMessage> Validate(AssistantChatRequest request)
    {
        if (!options.Enabled)
            throw new BadRequestException("The assistant is turned off.");

        var messages = (request?.Messages ?? [])
            .Where(m => m != null && !string.IsNullOrWhiteSpace(m.Content))
            .Select(m => new AssistantChatMessage { Role = m.Role?.Trim().ToLowerInvariant(), Content = m.Content.Trim() })
            .ToList();

        if (messages.Count == 0 || messages[^1].Role != AssistantRoles.User)
            throw new BadRequestException("Type a question.");
        if (messages.Any(m => m.Role is not (AssistantRoles.User or AssistantRoles.Assistant)))
            throw new BadRequestException("A message role must be user or assistant.");
        if (messages[^1].Content.Length > options.MaxQuestionLength)
            throw new BadRequestException($"Keep the question under {options.MaxQuestionLength} characters.");

        // Older turns and very long earlier messages are dropped; they only cost time
        return messages
            .TakeLast(Math.Max(1, options.MaxHistoryMessages))
            .Select(m => m.Content.Length <= options.MaxQuestionLength * 4 ? m
                : new AssistantChatMessage { Role = m.Role, Content = m.Content[..(options.MaxQuestionLength * 4)] })
            .ToList();
    }

    public async IAsyncEnumerable<AssistantEvent> AnswerAsync(IReadOnlyList<AssistantChatMessage> conversation,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var retrieval = await retriever.SearchAsync(SearchQuery(conversation), options.TopK, cancellationToken);
        var context = retrieval.Semantic
            ? retrieval.Chunks.Where(c => c.Score >= options.MinScore).ToList()
            : retrieval.Chunks.ToList();

        var useModel = options.UsesModel && await model.IsAvailableAsync(cancellationToken);
        yield return new AssistantSourcesEvent(
            context.Select(c => new AssistantSource(c.Chunk.File, c.Chunk.Heading, c.Chunk.Title, Math.Round(c.Score, 3))).ToList(),
            useModel ? AssistantModes.Model : AssistantModes.SearchOnly);

        if (useModel)
        {
            var sentAnything = false;
            Exception failure = null;
            var stream = model.StreamChatAsync(AssistantPrompt.Build(conversation, context), cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
            try
            {
                while (true)
                {
                    bool hasNext;
                    try
                    {
                        hasNext = await stream.MoveNextAsync();
                    }
                    catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
                    {
                        failure = e;
                        break;
                    }
                    if (!hasNext)
                        break;
                    sentAnything = true;
                    yield return new AssistantDeltaEvent(stream.Current);
                }
            }
            finally
            {
                await stream.DisposeAsync();
            }

            if (failure == null)
            {
                yield return new AssistantDoneEvent();
                yield break;
            }

            logger.LogWarning("Assistant: the chat model failed ({Error})", failure.Message);
            if (sentAnything)
            {
                yield return new AssistantErrorEvent("The answer was cut off. Please ask again.");
                yield break;
            }
        }

        // No model: hand back the closest help sections as they are
        yield return new AssistantDeltaEvent(SearchOnlyAnswer(context));
        yield return new AssistantDoneEvent();
    }

    /// <summary>"And with a deposit?" means nothing alone, so a short follow-up is searched with the question before it.</summary>
    private static string SearchQuery(IReadOnlyList<AssistantChatMessage> conversation)
    {
        var questions = conversation.Where(m => m.Role == AssistantRoles.User).Select(m => m.Content).ToList();
        var last = questions[^1];
        return questions.Count > 1 && last.Length < ShortFollowUpLength ? questions[^2] + "\n" + last : last;
    }

    private static string SearchOnlyAnswer(IReadOnlyList<ScoredChunk> context)
    {
        if (context.Count == 0)
            return "I could not find this in the XXIV Parking help. Try other words, or ask the staff at the lot.";

        var answer = new StringBuilder("Here is what the XXIV Parking help says:");
        foreach (var section in context.Take(SearchOnlySections))
            answer.Append("\n\n**").Append(section.Chunk.Heading).Append("**\n").Append(section.Chunk.Content);
        return answer.ToString();
    }
}
