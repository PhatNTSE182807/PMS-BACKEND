using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PMS.Application.Models.Assistant;
using PMS.Application.Services;
using PMS.Application.Services.Assistant;

namespace PMS.Application;

/// <summary>
/// Customer chat assistant answering from the help files (SRS FR-SYS-10), on any OpenAI-compatible
/// API: Google Gemini by default, for both the embeddings and the answers.
/// </summary>
public static class AssistantDependencyInjection
{
    public static IServiceCollection AddAssistant(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(AssistantOptions.Section).Get<AssistantOptions>() ?? new AssistantOptions();
        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);

        services.AddHttpClient(ModelClient.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
        services.AddSingleton<ModelClient>();
        services.AddSingleton<KnowledgeBase>();
        services.AddSingleton<AnswerCache>();
        services.AddSingleton<LiveLotData>();
        // Lot and price changes reach the assistant's lot list at once, not after its one-minute cache
        services.AddSingleton<ILotDataChangeListener>(sp => sp.GetRequiredService<LiveLotData>());
        services.AddSingleton<IKnowledgeRetriever, KnowledgeRetriever>();
        services.AddSingleton<IAssistantService, AssistantService>();

        if (options.Enabled)
            services.AddHostedService<AssistantWarmUp>();
        return services;
    }
}

/// <summary>Loads and embeds the help sections after start-up, so the first question does not wait for it.</summary>
internal sealed class AssistantWarmUp(KnowledgeBase knowledge, ILogger<AssistantWarmUp> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await knowledge.EnsureReadyAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down
        }
        catch (Exception e)
        {
            // The first question tries again; the API must start even when the help cannot be loaded
            logger.LogWarning(e, "Assistant: warm-up failed");
        }
    }
}
