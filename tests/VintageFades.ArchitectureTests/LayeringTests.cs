using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using VintageFades.Application.Bookings;
using VintageFades.Domain;
using VintageFades.Infrastructure.Persistence;
using VintageFades.Integrations.Meta;
using VintageFades.Web.Controllers;

namespace VintageFades.ArchitectureTests;

public class LayeringTests
{
    private static readonly Assembly Domain = typeof(Booking).Assembly;
    private static readonly Assembly Application = typeof(BookingService).Assembly;
    private static readonly Assembly Infrastructure = typeof(AppDbContext).Assembly;
    private static readonly Assembly Meta = typeof(WhatsAppChannel).Assembly;
    private static readonly Assembly Web = typeof(WebhooksController).Assembly;

    private static IReadOnlyList<string> References(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();

    [Fact]
    public void Domain_has_no_project_or_framework_dependencies()
    {
        var refs = References(Domain);
        Assert.DoesNotContain(refs, r => r.StartsWith("VintageFades.", StringComparison.Ordinal));
        Assert.DoesNotContain(refs, r => r.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
        Assert.DoesNotContain(refs, r => r.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
    }

    [Fact]
    public void Application_does_not_depend_on_infrastructure_integrations_web_or_a_database_provider()
    {
        var refs = References(Application);
        Assert.DoesNotContain("VintageFades.Infrastructure", refs);
        Assert.DoesNotContain("VintageFades.Integrations.Meta", refs);
        Assert.DoesNotContain("VintageFades.Web", refs);
        Assert.DoesNotContain(refs, r => r.StartsWith("Npgsql", StringComparison.Ordinal));
        Assert.DoesNotContain(refs, r => r.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
    }

    [Fact]
    public void Meta_integration_does_not_touch_the_database_or_booking_engine()
    {
        var refs = References(Meta);
        Assert.DoesNotContain("VintageFades.Infrastructure", refs);
        Assert.DoesNotContain(refs, r => r.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
        Assert.DoesNotContain(refs, r => r.StartsWith("Npgsql", StringComparison.Ordinal));

        var bookingTypes = Application.GetTypes().Where(t => t.Namespace?.StartsWith("VintageFades.Application.Bookings") == true).ToHashSet();
        foreach (var type in Meta.GetTypes())
        {
            var used = type.GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType)
                .Concat(type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Select(f => f.FieldType));
            Assert.DoesNotContain(used, bookingTypes.Contains);
        }
    }

    [Fact]
    public void Infrastructure_does_not_depend_on_integrations_or_web()
    {
        var refs = References(Infrastructure);
        Assert.DoesNotContain("VintageFades.Integrations.Meta", refs);
        Assert.DoesNotContain("VintageFades.Web", refs);
    }

    [Fact]
    public void Controllers_are_thin_and_do_not_use_the_database_directly()
    {
        var controllers = Web.GetTypes().Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract).ToList();
        Assert.NotEmpty(controllers);
        foreach (var controller in controllers)
        {
            var dependencies = controller.GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType).ToList();
            Assert.DoesNotContain(typeof(AppDbContext), dependencies);
            Assert.DoesNotContain(dependencies, d => d.Name == "IAppDbContext");
        }
    }

    [Fact]
    public void All_channels_implement_the_common_adapter_interface()
    {
        var adapter = typeof(VintageFades.Application.Abstractions.IMessagingChannel);
        var channels = Meta.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false } && adapter.IsAssignableFrom(t)).ToList();
        Assert.Equal(4, channels.Count); // WhatsApp (Meta), WhatsApp (YCloud), Instagram, Messenger
        var parsers = Meta.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false } &&
                                                 typeof(VintageFades.Application.Abstractions.IWebhookPayloadParser).IsAssignableFrom(t)).ToList();
        Assert.Equal(4, parsers.Count);
    }

    [Fact]
    public void There_is_exactly_one_booking_engine()
    {
        var bookingServices = new[] { Application, Infrastructure, Meta, Web }
            .SelectMany(a => a.GetTypes())
            .Where(t => t.Name.EndsWith("BookingService", StringComparison.Ordinal) && t.IsClass)
            .ToList();
        Assert.Equal([typeof(BookingService)], bookingServices);
    }
}
