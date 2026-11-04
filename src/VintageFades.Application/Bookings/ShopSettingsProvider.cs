using System.Globalization;
using Microsoft.EntityFrameworkCore;
using VintageFades.Application.Abstractions;
using VintageFades.Domain;

namespace VintageFades.Application.Bookings;

/// <summary>Loads shop settings from the database once per scope.</summary>
public sealed class ShopSettingsProvider(IAppDbContext db)
{
    private ShopCalendar? _calendar;

    public async Task<ShopCalendar> GetCalendarAsync(CancellationToken ct)
    {
        if (_calendar is not null) return _calendar;
        var settings = await db.ShopSettings.AsNoTracking().OrderBy(s => s.Id).FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Shop settings are missing. Run the database initializer.");
        return _calendar = new ShopCalendar(settings);
    }
}

public static class Format
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public static string Date(DateOnly date) => date.ToString("dddd, d MMMM yyyy", Culture);
    public static string DateShort(DateOnly date) => date.ToString("dddd, d MMMM", Culture);
    public static string Time(TimeOnly time) => time.ToString("HH:mm", Culture);
    public static string DayAndTime(DateOnly date, TimeOnly time) => $"{date.ToString("ddd d MMM", Culture)} {Time(time)}";
}
