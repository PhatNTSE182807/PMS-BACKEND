using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PMS.Application.Models.Assistant;

namespace PMS.Application.Services.Assistant;

/// <summary>
/// Client for an OpenAI-compatible API (<c>/models</c>, <c>/embeddings</c>, streamed
/// <c>/chat/completions</c>). Gemini serves it under
/// <c>/v1beta/openai/</c>; any other OpenAI-compatible server works through the same client.
/// </summary>
public sealed class ModelClient(
    IHttpClientFactory httpClientFactory,
    AssistantOptions options,
    TimeProvider clock,
    ILogger<ModelClient> logger)
{
    public const string HttpClientName = "assistant-model";

    /// <summary>The availability check is cached so a busy chat does not call /models every time.</summary>
    private static readonly TimeSpan AvailabilityCache = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private bool _available;
    private DateTimeOffset _checkedAt = DateTimeOffset.MinValue;

    /// <summary>Longest wait for the availability check; an unreachable server is then treated as down.</summary>
    private static readonly TimeSpan AvailabilityTimeout = TimeSpan.FromSeconds(3);

    /// <summary>True when the model server answers; never throws.</summary>
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        if (!options.UsesModel)
            return false;
        if (clock.GetUtcNow() - _checkedAt < AvailabilityCache)
            return _available;

        // Not tied to the caller's request: if the visitor leaves, the result is still cached for the next one
        var check = CheckAvailabilityAsync();
        return await check.WaitAsync(cancellationToken);
    }

    private async Task<bool> CheckAvailabilityAsync()
    {
        bool available;
        try
        {
            using var timeout = new CancellationTokenSource(AvailabilityTimeout);
            using var response = await CreateClient().GetAsync("models", timeout.Token);
            available = response.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            available = false;
        }
        _available = available;
        _checkedAt = clock.GetUtcNow();
        return available;
    }

    /// <summary>One vector per input, in the same order.</summary>
    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken)
    {
        using var response = await CreateClient().PostAsJsonAsync("embeddings",
            new { model = options.EmbeddingModel, input = inputs }, Json, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(Json, cancellationToken);
        if (body?.Data == null || body.Data.Count != inputs.Count)
            throw new HttpRequestException("The embedding model returned the wrong number of vectors.");
        return body.Data.OrderBy(d => d.Index).Select(d => d.Embedding).ToArray();
    }

    /// <summary>Streams the answer of the chat model piece by piece.</summary>
    public async IAsyncEnumerable<string> StreamChatAsync(IReadOnlyList<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var response = await StartChatAsync(messages, cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        // Server-Sent Events: "data: {json}" lines, ended by "data: [DONE]"
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;
            var data = line[5..].Trim();
            if (data == "[DONE]")
                yield break;
            var chunk = JsonSerializer.Deserialize<ChatChunk>(data, Json);
            if (chunk?.Error?.Message is { Length: > 0 } error)
                throw new HttpRequestException(error);
            var text = chunk?.Choices?.FirstOrDefault()?.Delta?.Content;
            if (!string.IsNullOrEmpty(text))
                yield return text;
        }
    }

    /// <summary>
    /// Opens the answer stream with the main chat model, or with the next fallback model when one
    /// fails (overloaded 503, out of quota 429, a parameter it refuses 400, too slow). Only the opening is retried: once text
    /// is flowing, an error ends the answer.
    /// </summary>
    private async Task<HttpResponseMessage> StartChatAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken)
    {
        var models = options.ChatModels;
        for (var i = 0; ; i++)
        {
            var isLast = i == models.Count - 1;
            using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
            {
                Content = JsonContent.Create(ChatBody(models[i], messages), options: Json)
            };

            HttpResponseMessage response;
            using (var startTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                // A model that has not started answering in time is treated as overloaded
                startTimeout.CancelAfter(TimeSpan.FromSeconds(options.ChatStartTimeoutSeconds));
                try
                {
                    response = await CreateClient().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, startTimeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !isLast)
                {
                    logger.LogWarning("Assistant: chat model {Model} did not answer within {Seconds} s, trying {Next}",
                        models[i], options.ChatStartTimeoutSeconds, models[i + 1]);
                    continue;
                }
            }

            // A key problem (401, 403) fails every model the same way; anything else may work on the next one
            var retryable = response.StatusCode is not (System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden);
            if (response.IsSuccessStatusCode || !retryable || isLast)
            {
                try
                {
                    await EnsureSuccessAsync(response, cancellationToken);
                    return response;
                }
                catch
                {
                    response.Dispose();
                    throw;
                }
            }
            logger.LogWarning("Assistant: chat model {Model} returned {Status}, trying {Next}",
                models[i], (int)response.StatusCode, models[i + 1]);
            response.Dispose();
        }
    }

    /// <summary>
    /// Request body of a streamed chat. On Gemini, thinking is kept to the minimum: these are short
    /// help-desk answers, and long thinking delays the first word by many seconds. Gemini 2.5 models can
    /// turn it off ("none"); later models go down to "low" ("minimal" is refused by some).
    /// </summary>
    private Dictionary<string, object> ChatBody(string model, IReadOnlyList<ChatMessage> messages)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = messages,
            ["stream"] = true,
            ["temperature"] = options.Temperature
        };
        if (string.Equals(options.Provider, AssistantProviders.Gemini, StringComparison.OrdinalIgnoreCase))
            body["reasoning_effort"] = model.StartsWith("gemini-2.5", StringComparison.OrdinalIgnoreCase) ? "none" : "low";
        return body;
    }

    private HttpClient CreateClient()
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        client.BaseAddress = new Uri(options.ResolvedBaseUrl);
        if (!string.IsNullOrWhiteSpace(options.ApiKey))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        return client;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException($"The model server returned {(int)response.StatusCode}: {body}");
    }

    private sealed class EmbeddingResponse
    {
        public List<EmbeddingItem> Data { get; set; }
    }

    private sealed class EmbeddingItem
    {
        public int Index { get; set; }
        public float[] Embedding { get; set; }
    }

    private sealed class ChatChunk
    {
        public List<ChatChoice> Choices { get; set; }
        public ChatError Error { get; set; }
    }

    private sealed class ChatChoice
    {
        public ChatMessage Delta { get; set; }
    }

    private sealed class ChatError
    {
        public string Message { get; set; }
    }
}

/// <summary>A chat message in the OpenAI format; role is "system", "user" or "assistant".</summary>
public sealed record ChatMessage(string Role, string Content);
