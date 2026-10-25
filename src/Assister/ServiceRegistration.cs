using Assister.Diagnostics;
using Assister.Interactions;
using Assister.Persistence;
using Assister.Modules.HomeAssistant;
using Assister.Contracts;
using Assister.Intents;
using Assister.Voice;
using Assister.Llm;
using Assister.Tools;
using Assister.Conversations;
using Assister.Modules.Timers;
using Assister.Speech.Wyoming;
using Assister.Satellites;
using Microsoft.EntityFrameworkCore;
using Assister.Updates;

namespace Assister;

public static class ServiceRegistration
{
    public static IServiceCollection AddAssisterServices(this IServiceCollection Services)
    {
        Services.AddSingleton<ClientUpdateHost>();
        Services.AddGrpc(Options => Options.MaxReceiveMessageSize = 128 * 1024);
        Services.AddSingleton<VoiceAudioStore>();
        Services.AddDbContext<AssisterDbContext>((Services, Options) =>
        {
            var Configuration = Services.GetRequiredService<IConfiguration>();
            var DataPath = Path.GetFullPath(Configuration["Assister:DataPath"] ?? "data");
            Directory.CreateDirectory(DataPath);
            Options.UseSqlite($"Data Source={Path.Combine(DataPath, "assister.db")}");
        });
        Services.AddSingleton<HomeAssistantStateCache>();
        Services.AddSingleton<Func<IHomeAssistantConnection>>(_ => () => new HomeAssistantConnection());
        Services.AddSingleton<HomeAssistantClient>();
        Services.AddHostedService(Services => Services.GetRequiredService<HomeAssistantClient>());
        Services.AddHttpClient<IHomeAssistantClient, HomeAssistantActionClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        Services.AddSingleton<IntentClassifier>();
        Services.AddSingleton<IIntentActionProvider, HomeAssistantIntentActions>();
        Services.AddSingleton<IIntentActionProvider, AssisterIntentActions>();
        Services.AddSingleton<IntentActionRegistry>();
        Services.AddTransient<IIntegrationIntentExecutor, HomeAssistantIntentExecutor>();
        Services.AddTransient<IIntegrationIntentExecutor, AssisterIntentExecutor>();
        Services.AddTransient<IntegrationActionDispatcher>();
        Services.AddScoped<IntentStore>();
        Services.AddScoped<IIntentEngine, IntentEngine>();
        Services.AddScoped<IntentWorkbench>();
        Services.AddSingleton<IEntityResolver, HomeAssistantEntityResolver>();
        Services.AddTransient<DirectIntentHandler>();
        Services.AddTransient<DeviceNameRecovery>();
        Services.AddTransient<RequestCoordinator>();
        Services.AddSingleton<ConversationLocks>();
        Services.AddScoped<IRequestCoordinator, ConversationCoordinator>();
        Services.AddTransient<ToolLoop>();
        Services.AddTransient<ToolBroker>();
        Services.AddTransient<ToolRegistry>();
        Services.AddScoped<LocalStore>();
        Services.AddScoped<TimerIntentHandler>();
        Services.AddScoped<Assister.Modules.Reminders.ReminderIntentHandler>();
        Services.AddHostedService<Assister.Modules.Reminders.ReminderScheduler>();
        Services.AddSingleton<SatelliteManager>();
        Services.AddScoped<SatelliteConfiguration>();
        Services.AddSingleton<Assister.Voice.ToneCatalog>();
        Services.AddScoped<VoicePipeline>();
        Services.AddSingleton<ITurnCompletionClassifier, SmartTurnClassifier>();
        Services.AddHostedService<SmartTurnWarmup>();
        Services.AddHttpClient("echomuse").ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        Services.AddHostedService<EchoMuseProvider>();
        Services.AddTransient<ISpeechToTextProvider>(Services =>
        {
            var Config = Services.GetRequiredService<IConfiguration>();
            ISpeechToTextProvider Provider = new WyomingSpeechToTextProvider(new(Config["SpeechToText:Host"] ?? "", Config.GetValue("SpeechToText:Port", 10300)));
            return Config.GetValue("SpeechToText:OverlapEnabled", false)
                ? new OverlappingSpeechToTextProvider(Provider, Config.GetValue("SpeechToText:OverlapQuietThreshold", .003),
                    Config.GetValue("SpeechToText:OverlapQuietMilliseconds", 800)) : Provider;
        });
        Services.AddTransient<ITextToSpeechProvider>(Services =>
        {
            var Config = Services.GetRequiredService<IConfiguration>();
            return new WyomingTextToSpeechProvider(new(Config["TextToSpeech:Host"] ?? "", Config.GetValue("TextToSpeech:Port", 10200)));
        });
        Services.AddHostedService<TimerExpiryService>();
        Services.AddTransient<IAssisterTool, ChatHistoryTool>();
        Services.AddTransient<IAssisterTool, SatelliteVolumeTool>();
        Services.AddScoped<AnnouncementHandler>();
        Services.AddSingleton<AnnouncementQueue>();
        Services.AddHostedService(Services => Services.GetRequiredService<AnnouncementQueue>());
        Services.AddHostedService<AnnouncementRepeatService>();
        Services.AddTransient<IAssisterTool, SatelliteAnnouncementTool>();
        foreach (var Name in new[] { "reminder_get", "reminder_create", "reminder_edit", "reminder_cancel" })
        {
            Services.AddTransient<IAssisterTool>(Services => new ReminderTool(Name, Services.GetRequiredService<LocalStore>(), Services.GetRequiredService<IConfiguration>(), Services.GetRequiredService<RichNotificationBridge>()));
        }
        foreach (var Name in new[] { "memory_store", "memory_search", "memory_delete" })
        {
            Services.AddTransient<IAssisterTool>(Services => new MemoryTool(Name, Services.GetRequiredService<LocalStore>()));
        }
        Services.AddHttpClient("homeassistant-tools").ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        Services.AddTransient<IAssisterTool>(Services => new WeatherTool(Services.GetRequiredService<HomeAssistantStateCache>(),
            Services.GetRequiredService<IHttpClientFactory>().CreateClient("homeassistant-tools"), Services.GetRequiredService<IConfiguration>()));
        foreach (var Name in new[] { "ha_search", "ha_get_state", "ha_control", "ha_get_history" })
        {
            Services.AddTransient<IAssisterTool>(Services => new HomeAssistantTool(Name,
                Services.GetRequiredService<HomeAssistantStateCache>(), Services.GetRequiredService<IHomeAssistantClient>(),
                Services.GetRequiredService<IHttpClientFactory>().CreateClient("homeassistant-tools"), Services.GetRequiredService<IConfiguration>()));
        }
        Services.AddHttpClient<ILanguageModel, OpenAiCompatibleLanguageModel>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        Services.AddSingleton<RunStore>();
        Services.AddSingleton<RunAudioStore>();
        Services.AddHostedService<RunAudioRetentionService>();
        Services.AddDataProtection();
        Services.AddSingleton<ClientAuthentication>();
        Services.AddSingleton<ClientSignals>();
        Services.AddSingleton<InteractionStore>();
        Services.AddSingleton<NotificationStore>();
        Services.AddSingleton<RichNotificationBridge>();
        Services.AddHostedService<InteractionRetentionService>();
        Services.AddSingleton<InteractionRunner>();
        Services.AddScoped<RichSpeech>();
        Services.AddHostedService(Services => Services.GetRequiredService<InteractionRunner>());
        Services.AddHttpClient("diagnostics").ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        Services.AddSingleton<ComponentHealth>();
        Services.AddHostedService(Services => Services.GetRequiredService<ComponentHealth>());
        return Services;
    }
}
