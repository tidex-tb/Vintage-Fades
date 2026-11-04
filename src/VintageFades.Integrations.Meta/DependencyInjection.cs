using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VintageFades.Application.Abstractions;

namespace VintageFades.Integrations.Meta;

public static class DependencyInjection
{
    public static IServiceCollection AddMetaIntegrations(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<MetaOptions>(configuration.GetSection(MetaOptions.SectionName));
        services.Configure<WhatsAppOptions>(configuration.GetSection(WhatsAppOptions.SectionName));
        services.Configure<YCloudOptions>(configuration.GetSection(YCloudOptions.SectionName));
        services.Configure<InstagramOptions>(configuration.GetSection(InstagramOptions.SectionName));
        services.Configure<FacebookOptions>(configuration.GetSection(FacebookOptions.SectionName));

        var timeout = TimeSpan.FromSeconds(15);
        // Exactly one WhatsApp adapter is active: direct Meta Cloud API, or YCloud for Coexistence.
        if (configuration.GetValue(WhatsAppOptions.SectionName + ":Provider", WhatsAppProvider.Meta) == WhatsAppProvider.YCloud)
        {
            services.AddHttpClient<YCloudWhatsAppChannel>(c => c.Timeout = timeout);
            services.AddTransient<IMessagingChannel>(sp => sp.GetRequiredService<YCloudWhatsAppChannel>());
            services.AddSingleton<IWebhookPayloadParser, YCloudWebhookParser>();
        }
        else
        {
            services.AddHttpClient<WhatsAppChannel>(c => c.Timeout = timeout);
            services.AddTransient<IMessagingChannel>(sp => sp.GetRequiredService<WhatsAppChannel>());
            services.AddSingleton<IWebhookPayloadParser, WhatsAppWebhookParser>();
        }

        services.AddHttpClient<MessengerChannel>(c => c.Timeout = timeout);
        services.AddHttpClient<InstagramChannel>(c => c.Timeout = timeout);
        services.AddTransient<IMessagingChannel>(sp => sp.GetRequiredService<MessengerChannel>());
        services.AddTransient<IMessagingChannel>(sp => sp.GetRequiredService<InstagramChannel>());
        services.AddSingleton<IWebhookPayloadParser, MessengerWebhookParser>();
        services.AddSingleton<IWebhookPayloadParser, InstagramWebhookParser>();
        return services;
    }
}
