using Microsoft.EntityFrameworkCore;
using VintageFades.Application.Abstractions;

namespace VintageFades.Application.Operations;

public sealed record BotStatus(bool Paused, DateTime? PausedAtUtc);

/// <summary>
/// The owner's master switch for the chatbot on WhatsApp, Instagram and Messenger. While it is off, customer messages are still
/// saved (so nothing is lost and the owner can answer them in each app), but the bot sends no replies, no "are you still there?"
/// nudges and no delayed menus. Reminders for existing bookings, owner alerts, the website and the dashboard keep working.
/// </summary>
public sealed class BotSwitch(IAppDbContext db, TimeProvider time)
{
    public async Task<bool> IsPausedAsync(CancellationToken ct) =>
        await db.ShopSettings.AsNoTracking().AnyAsync(s => s.BotPausedAtUtc != null, ct);

    public async Task<BotStatus> GetAsync(CancellationToken ct)
    {
        var pausedAt = await db.ShopSettings.AsNoTracking().OrderBy(s => s.Id).Select(s => s.BotPausedAtUtc).FirstOrDefaultAsync(ct);
        return new BotStatus(pausedAt is not null, pausedAt);
    }

    public async Task<BotStatus> SetPausedAsync(bool paused, CancellationToken ct)
    {
        var settings = await db.ShopSettings.OrderBy(s => s.Id).FirstAsync(ct);
        if (paused && settings.BotPausedAtUtc is null) settings.BotPausedAtUtc = time.GetUtcNow().UtcDateTime;
        else if (!paused) settings.BotPausedAtUtc = null;
        await db.SaveChangesAsync(ct);
        return new BotStatus(settings.BotPausedAtUtc is not null, settings.BotPausedAtUtc);
    }
}
