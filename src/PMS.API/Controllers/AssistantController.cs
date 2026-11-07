using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PMS.Application.Models;
using PMS.Application.Models.Assistant;
using PMS.Application.Services.Assistant;

namespace PMS.API.Controllers;

/// <summary>
/// Customer chat assistant (SRS FR-SYS-10), open to Guests and Users. It answers from the help files
/// only and reads no account data, so it needs no sign-in.
/// </summary>
[AllowAnonymous]
public class AssistantController(IAssistantService assistant) : ApiController
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Whether to show the chat widget, and whether a model or the plain help search answers.</summary>
    [HttpGet("~/api/public/assistant/status")]
    public async Task<IActionResult> GetStatusAsync(CancellationToken cancellationToken) =>
        Ok(ApiResult<AssistantStatusModel>.Success(await assistant.GetStatusAsync(cancellationToken)));

    /// <summary>
    /// Answers the last message of the conversation as Server-Sent Events: <c>sources</c> (help
    /// sections used), then <c>delta</c> pieces of text, then <c>done</c> or <c>error</c>.
    /// </summary>
    [HttpPost("~/api/public/assistant/chat")]
    [EnableRateLimiting(ApiDependencyInjection.AssistantChatRateLimit)]
    [Produces("text/event-stream")]
    public async Task ChatAsync([FromBody] AssistantChatRequest request, CancellationToken cancellationToken)
    {
        // Throws before the stream starts, so a bad request still gets a normal 400 response
        var conversation = assistant.Validate(request);

        Response.ContentType = "text/event-stream; charset=utf-8";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        try
        {
            await foreach (var item in assistant.AnswerAsync(conversation, cancellationToken))
                await WriteEventAsync(item, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The customer closed the chat or pressed Stop
        }
    }

    private async Task WriteEventAsync(AssistantEvent item, CancellationToken cancellationToken)
    {
        var (name, data) = item switch
        {
            AssistantSourcesEvent s => ("sources", (object)new { s.Mode, s.Sources }),
            AssistantDeltaEvent d => ("delta", new { d.Text }),
            AssistantErrorEvent e => ("error", new { e.Message }),
            _ => ("done", new { })
        };
        await Response.WriteAsync($"event: {name}\ndata: {JsonSerializer.Serialize(data, Json)}\n\n", cancellationToken);
        await Response.Body.FlushAsync(cancellationToken);
    }
}
