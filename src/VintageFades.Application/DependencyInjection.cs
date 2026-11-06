using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VintageFades.Application.Bookings;
using VintageFades.Application.Chatbot;
using VintageFades.Application.Inbound;
using VintageFades.Application.Messaging;
using VintageFades.Application.Operations;

namespace VintageFades.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<BookingOptions>(configuration.GetSection(BookingOptions.SectionName));
        services.Configure<ChatbotOptions>(configuration.GetSection(ChatbotOptions.SectionName));
        services.Configure<ShopInfoOptions>(configuration.GetSection(ShopInfoOptions.SectionName));
        services.Configure<NotificationOptions>(configuration.GetSection(NotificationOptions.SectionName));
        services.Configure<OutboxOptions>(configuration.GetSection(OutboxOptions.SectionName));
        services.Configure<RetentionOptions>(configuration.GetSection(RetentionOptions.SectionName));

        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ShopSettingsProvider>();
        services.AddScoped<NotificationService>();
        services.AddScoped<BookingService>();
        services.AddSingleton<ShopInfoResponder>();
        services.AddScoped<ConversationEngine>();
        services.AddScoped<OwnerCommandHandler>();
        services.AddScoped<InboundMessageHandler>();
        services.AddScoped<WebhookIngestionService>();
        services.AddScoped<WebhookProcessor>();
        services.AddScoped<OutboxDispatcher>();
        services.AddScoped<ReminderScheduler>();
        services.AddScoped<IdleConversationService>();
        services.AddScoped<OperationalMonitor>();
        services.AddScoped<DataRetentionService>();
        services.AddScoped<OperationalStatusService>();
        services.AddScoped<AdminService>();
        return services;
    }
}
