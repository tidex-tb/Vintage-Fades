using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VintageFades.Application.Abstractions;
using VintageFades.Application.Bookings;
using VintageFades.Application.Messaging;
using VintageFades.Domain;

namespace VintageFades.Application.Chatbot;

public sealed record ConversationTurn(Conversation Conversation, Customer Customer, MessageType MessageType, string? Text);

/// <summary>
/// Platform-independent finite-state conversation engine. It reads the current state and draft from the
/// conversation, decides the reply, and delegates every booking decision to <see cref="BookingService"/>.
/// It never tells a customer a booking is confirmed unless the database says so.
/// </summary>
public sealed partial class ConversationEngine(
    BookingService bookings,
    NotificationService notifications,
    IAppDbContext db,
    TimeProvider time,
    IOptions<ChatbotOptions> chatbotOptions,
    IOptions<BookingOptions> bookingOptions)
{
    private readonly ChatbotOptions _chatbot = chatbotOptions.Value;
    private readonly BookingOptions _booking = bookingOptions.Value;

    private static readonly OutboundMessage[] NoReplies = [];
    private static readonly ReplyOption[] YesNo = [new("yes", "Yes"), new("no", "No")];

    public async Task<IReadOnlyList<OutboundMessage>> HandleAsync(ConversationTurn turn, CancellationToken ct)
    {
        var conversation = turn.Conversation;
        var now = time.GetUtcNow().UtcDateTime;
        // Human handover: stay silent until the owner hands the conversation back. A pause caused only by the shop
        // replying from the WhatsApp Business app ends by itself after ManualReplyPauseHours.
        if (conversation.CurrentState == ConversationState.HumanSupportRequested)
        {
            if (BookingDraft.Deserialize(conversation.CurrentBookingIntent).AutoResumeAtUtc is not { } resumeAt || now < resumeAt)
                return NoReplies;
            conversation.CurrentState = ConversationState.Idle;
            conversation.CurrentBookingIntent = null;
        }

        var calendar = await bookings.GetCalendarAsync(ct);
        var barbers = await db.Barbers.AsNoTracking().Where(b => b.IsActive).OrderBy(b => b.StationNumber).ToListAsync(ct);
        var ctx = new TurnContext(turn, calendar, barbers, now, BookingDraft.Deserialize(conversation.CurrentBookingIntent));

        if (conversation.CurrentState != ConversationState.Idle && conversation.LastMessageAtUtc is { } last &&
            now - last > TimeSpan.FromHours(_chatbot.ConversationResetHours))
        {
            await ResetAsync(ctx, ct);
            conversation.CurrentState = ConversationState.Idle;
        }

        if (turn.MessageType == MessageType.Unsupported)
        {
            ctx.Reply("Sorry, I can only read text messages at the moment. " + StatePrompt(ctx));
        }
        else
        {
            await RouteAsync(ctx, ct);
        }

        if (!ctx.Misunderstood) ctx.Draft.ClarificationAttempts = 0;
        conversation.CurrentBookingIntent = ctx.Draft.Serialize();
        return ctx.Flush();
    }

    private async Task RouteAsync(TurnContext ctx, CancellationToken ct)
    {
        if (ctx.Intent.HasFlag(Intent.Human))
        {
            await HandOverAsync(ctx, "Requested to speak to someone.", ct);
            return;
        }
        if (ctx.Intent.HasFlag(Intent.Menu))
        {
            await ResetAsync(ctx, ct);
            ShowMenu(ctx, welcome: false);
            return;
        }

        switch (ctx.State)
        {
            case ConversationState.CollectingBookingDate: await HandleDateAsync(ctx, ct); break;
            case ConversationState.CollectingBookingTime: await HandleTimeAsync(ctx, ct); break;
            case ConversationState.SelectingBarber: await HandleBarberSelectionAsync(ctx, ct); break;
            case ConversationState.OfferingAlternatives: await HandleAlternativesAsync(ctx, ct); break;
            case ConversationState.CollectingCustomerName: await HandleNameAsync(ctx, ct); break;
            case ConversationState.AwaitingBookingConfirmation: await HandleBookingConfirmationAsync(ctx, ct); break;
            case ConversationState.AwaitingCancellationSelection: await HandleBookingSelectionAsync(ctx, DraftIntent.Cancel, ct); break;
            case ConversationState.Rescheduling: await HandleBookingSelectionAsync(ctx, DraftIntent.Reschedule, ct); break;
            case ConversationState.AwaitingCancellationConfirmation: await HandleCancellationConfirmationAsync(ctx, ct); break;
            case ConversationState.OfferingHumanSupport: await HandleHumanOfferAsync(ctx, ct); break;
            default: await HandleEntryAsync(ctx, ct); break;
        }
    }

    // ---------------------------------------------------------------- entry / menu

    private async Task HandleEntryAsync(TurnContext ctx, CancellationToken ct)
    {
        var intent = ctx.Intent;
        if (ctx.State == ConversationState.Greeting && TryNumber(ctx.Text, out var choice))
        {
            switch (choice)
            {
                case 1: await StartBookingAsync(ctx, Details.Empty, ct); return;
                case 2: await ShowBookingsAsync(ctx, ct); return;
                case 3: await StartRescheduleAsync(ctx, Details.Empty, ct); return;
                case 4: await StartCancelAsync(ctx, ct); return;
                case 5: await HandOverAsync(ctx, "Requested to speak to someone.", ct); return;
            }
        }

        if (intent.HasFlag(Intent.Cancel)) { await StartCancelAsync(ctx, ct); return; }
        if (intent.HasFlag(Intent.Reschedule)) { await StartRescheduleAsync(ctx, Extract(ctx, ctx.Text, false), ct); return; }
        if (intent.HasFlag(Intent.ViewBookings)) { await ShowBookingsAsync(ctx, ct); return; }

        var details = Extract(ctx, ctx.Text, allowBareNumber: false);
        if (intent.HasFlag(Intent.Book) || details.Any) { await StartBookingAsync(ctx, details, ct); return; }

        if (ctx.State == ConversationState.BookingConfirmed && (intent.HasFlag(Intent.Yes) || intent.HasFlag(Intent.Thanks)))
        {
            ctx.Reply("You're all set! See you soon. 💈");
            ctx.State = ConversationState.Idle;
            return;
        }
        if (intent.HasFlag(Intent.Thanks))
        {
            ctx.Reply("You're welcome! Message me any time you'd like to book.");
            ctx.State = ConversationState.Idle;
            return;
        }
        if (intent.HasFlag(Intent.Greeting) || ctx.Conversation.LastMessageAtUtc is null)
        {
            ShowMenu(ctx, welcome: true);
            return;
        }
        Unknown(ctx);
    }

    private void ShowMenu(TurnContext ctx, bool welcome)
    {
        var text = new StringBuilder();
        if (welcome)
        {
            text.AppendLine($"Welcome to {ctx.Calendar.Settings.ShopName}!");
            text.AppendLine("Welcome to the fresh-fade experience.");
            text.AppendLine("I can help you book an appointment.");
            text.AppendLine();
        }
        text.AppendLine("What would you like to do?");
        text.AppendLine();
        text.AppendLine("1. Book an appointment");
        text.AppendLine("2. View my bookings");
        text.AppendLine("3. Reschedule an appointment");
        text.AppendLine("4. Cancel an appointment");
        text.Append("5. Speak to someone");
        ctx.Reply(text.ToString(),
        [
            new("1", "Book appointment"), new("2", "View my bookings"), new("3", "Reschedule"),
            new("4", "Cancel appointment"), new("5", "Speak to someone")
        ]);
        ctx.State = ConversationState.Greeting;
    }

    // ---------------------------------------------------------------- booking flow

    private async Task StartBookingAsync(TurnContext ctx, Details details, CancellationToken ct)
    {
        await ReleaseHoldAsync(ctx, ct);
        ctx.Draft = new BookingDraft { Intent = DraftIntent.Book, ClarificationAttempts = ctx.Draft.ClarificationAttempts };
        Apply(ctx, details);
        if (!details.Any) ctx.Note("Absolutely! Let's get you booked.");
        await ContinueAsync(ctx, ct);
    }

    /// <summary>Drives booking and rescheduling: asks only for what is still missing, then checks availability.</summary>
    private async Task ContinueAsync(TurnContext ctx, CancellationToken ct)
    {
        var d = ctx.Draft;
        var today = ctx.Calendar.Today(ctx.Now);

        if (d.Date is null)
        {
            if (d.DateCandidates.Count > 0) { AskDateClarification(ctx); return; }
            ctx.Reply(d.Intent == DraftIntent.Reschedule ? "What date would you like to move it to?" : "What date would you like to come in?");
            ctx.State = ConversationState.CollectingBookingDate;
            return;
        }

        if (d.Date < today)
        {
            d.Date = null;
            ctx.Note("That date has already passed.");
            await ContinueAsync(ctx, ct);
            return;
        }
        var lastDate = ctx.Calendar.LastBookableDate(ctx.Now);
        if (d.Date > lastDate)
        {
            d.Date = null;
            ctx.Note($"We take bookings up to {ctx.Calendar.Settings.BookingAdvanceDays} days ahead (until {Format.DateShort(lastDate)}).");
            await ContinueAsync(ctx, ct);
            return;
        }

        if (d.Time is null)
        {
            if (d.TimeCandidates.Count > 0) { AskTimeClarification(ctx); return; }
            await AskTimeAsync(ctx, ct);
            return;
        }

        var rejection = ctx.Calendar.ValidateSlot(d.Date.Value, d.Time.Value, ctx.Now);
        if (rejection != SlotRejection.None)
        {
            ctx.Note(RejectionMessage(ctx, rejection, d.Time.Value));
            d.Time = null;
            await AskTimeAsync(ctx, ct);
            return;
        }

        var date = d.Date.Value;
        var timeOfDay = d.Time.Value;
        var ignore = ctx.IgnoreBookingId;

        if (d.BarberMode == BarberPreferenceKind.None)
        {
            var free = await bookings.GetAvailableBarbersAsync(date, timeOfDay, ignore, ct);
            if (free.Count == 0)
            {
                await OfferAlternativesAsync(ctx, $"Unfortunately, all our barbers are booked for {Format.Time(timeOfDay)} on {Format.DateShort(date)}.", ct);
                return;
            }
            d.BarberChoices = free.OrderBy(b => b.StationNumber).Select(b => b.Id).ToList();
            var options = new List<ReplyOption> { new("1", "First available") };
            var lines = new StringBuilder("Would you prefer a specific barber or the first available barber?\n\n1. First available");
            for (var i = 0; i < d.BarberChoices.Count; i++)
            {
                var name = ctx.BarberName(d.BarberChoices[i]);
                lines.Append($"\n{i + 2}. {name}");
                options.Add(new((i + 2).ToString(), name));
            }
            ctx.Reply(lines.ToString(), options);
            ctx.State = ConversationState.SelectingBarber;
            return;
        }

        var requestedId = d.BarberMode == BarberPreferenceKind.Specific ? d.BarberId : null;
        var status = await bookings.GetSlotStatusAsync(date, timeOfDay, requestedId, ignore, ct);

        if (requestedId is not null)
        {
            if (status.RequestedBarber is null || !status.RequestedBarber.IsActive)
            {
                d.BarberMode = BarberPreferenceKind.None;
                d.BarberId = null;
                await ContinueAsync(ctx, ct);
                return;
            }
            if (!status.RequestedBarberAvailable)
            {
                await OfferOtherBarbersAsync(ctx, status, ct);
                return;
            }
        }
        else if (status.AvailableBarbers.Count == 0)
        {
            await OfferAlternativesAsync(ctx, $"Unfortunately, all our barbers are booked for {Format.Time(timeOfDay)} on {Format.DateShort(date)}.", ct);
            return;
        }

        if (d.Intent == DraftIntent.Reschedule)
        {
            var target = await db.Bookings.AsNoTracking().FirstAsync(b => b.Id == d.TargetBookingId, ct);
            d.ResolvedBarberId = requestedId
                ?? (status.AvailableBarbers.Any(b => b.Id == target.BarberId) ? target.BarberId : status.AvailableBarbers[0].Id);
            var oldLocal = ctx.Calendar.ToLocal(target.StartAtUtc);
            ctx.Reply($"{Format.Time(timeOfDay)} on {Format.DateShort(date)} is available!\n" +
                      $"Would you like me to move your appointment from {Format.DateShort(DateOnly.FromDateTime(oldLocal))} at {Format.Time(TimeOnly.FromDateTime(oldLocal))} " +
                      $"to {Format.DateShort(date)} at {Format.Time(timeOfDay)} with {ctx.BarberName(d.ResolvedBarberId.Value)}?", YesNo);
            ctx.State = ConversationState.AwaitingBookingConfirmation;
            return;
        }

        await HoldSlotAsync(ctx, requestedId, ct);
    }

    private async Task HoldSlotAsync(TurnContext ctx, int? barberId, CancellationToken ct)
    {
        var d = ctx.Draft;
        await ReleaseHoldAsync(ctx, ct);
        var result = await bookings.CreateBookingAsync(new CreateBookingRequest(
            ctx.Customer.Id, d.Date!.Value, d.Time!.Value, barberId, ctx.Conversation.Platform.ToBookingSource(), HoldOnly: true), ct);

        if (result.Success)
        {
            d.HeldBookingId = result.Booking!.Id;
            d.ResolvedBarberId = result.Booking.BarberId;
            if (string.IsNullOrWhiteSpace(ctx.Customer.FullName))
            {
                ctx.Reply($"Good news, {Format.Time(d.Time.Value)} on {Format.DateShort(d.Date.Value)} with {ctx.BarberName(result.Booking.BarberId)} is available! " +
                          $"I'm holding it for you for {_booking.PendingHoldMinutes} minutes.\n\nWhat name should I put the booking under?");
                ctx.State = ConversationState.CollectingCustomerName;
                return;
            }
            AskBookingConfirmation(ctx);
            return;
        }

        switch (result.Error)
        {
            case BookingError.InvalidSlot:
                ctx.Note(RejectionMessage(ctx, result.Rejection, d.Time!.Value));
                d.Time = null;
                await AskTimeAsync(ctx, ct);
                break;
            default:
                await OfferAlternativesAsync(ctx, "Sorry, that slot was just taken. Let me find another available time for you.", ct);
                break;
        }
    }

    private void AskBookingConfirmation(TurnContext ctx)
    {
        var d = ctx.Draft;
        ctx.Reply($"{Format.Time(d.Time!.Value)} is available!\n" +
                  $"Would you like me to confirm your appointment for {Format.DateShort(d.Date!.Value)} at {Format.Time(d.Time.Value)} with {ctx.BarberName(d.ResolvedBarberId!.Value)}?",
            YesNo);
        ctx.State = ConversationState.AwaitingBookingConfirmation;
    }

    private async Task AskTimeAsync(TurnContext ctx, CancellationToken ct)
    {
        var d = ctx.Draft;
        var date = d.Date!.Value;
        var barberId = d.BarberMode == BarberPreferenceKind.Specific ? d.BarberId : null;
        var times = (await bookings.GetAvailableSlotsAsync(date, barberId, ctx.IgnoreBookingId, ct))
            .Select(s => s.Time).Distinct().Order().ToList();

        if (times.Count == 0)
        {
            var who = barberId is null ? "We're" : $"{ctx.BarberName(barberId.Value)} is";
            var reason = date == ctx.Calendar.Today(ctx.Now) ? $"{who} fully booked for the rest of today." : $"{who} fully booked on {Format.DateShort(date)}.";
            await OfferAlternativesAsync(ctx, reason, ct, startTime: ctx.Calendar.Settings.OpeningTime);
            return;
        }

        var settings = ctx.Calendar.Settings;
        ctx.Reply($"{(ctx.HasNotes ? "" : "Perfect. ")}What time would you prefer on {Format.DateShort(date)}?\n" +
                  $"We operate from {Format.Time(settings.OpeningTime)} to {Format.Time(settings.ClosingTime)}, with one-hour appointments.\n\n" +
                  $"Available: {string.Join(", ", times.Select(Format.Time))}",
            times.Take(10).Select(t => new ReplyOption(Format.Time(t), Format.Time(t))).ToList());
        ctx.State = ConversationState.CollectingBookingTime;
    }

    private async Task OfferOtherBarbersAsync(TurnContext ctx, SlotStatus status, CancellationToken ct)
    {
        var d = ctx.Draft;
        var date = d.Date!.Value;
        var requested = status.RequestedBarber!;
        var others = status.AvailableBarbers.Where(b => b.Id != requested.Id).OrderBy(b => b.StationNumber).Take(3)
            .Select(b => new SlotOption(date, d.Time!.Value, b.Id, b.DisplayName)).ToList();
        var sameBarber = (await bookings.GetAvailableSlotsAsync(date, requested.Id, ctx.IgnoreBookingId, ct))
            .OrderBy(s => Math.Abs((s.Time.ToTimeSpan() - d.Time!.Value.ToTimeSpan()).TotalMinutes)).ThenBy(s => s.Time)
            .Take(_booking.MaxAlternatives - others.Count).ToList();
        var offers = others.Concat(sameBarber).ToList();

        var reason = status.RequestedBarberReason switch
        {
            BarberUnavailableReason.TimeOff or BarberUnavailableReason.NotWorking => $"{requested.DisplayName} isn't working at {Format.Time(d.Time!.Value)} on {Format.DateShort(date)}.",
            _ => $"{requested.DisplayName} is already booked at {Format.Time(d.Time!.Value)} on {Format.DateShort(date)}."
        };
        if (offers.Count == 0)
        {
            await OfferAlternativesAsync(ctx, reason, ct);
            return;
        }
        PresentOffers(ctx, reason, offers);
    }

    private async Task OfferAlternativesAsync(TurnContext ctx, string reason, CancellationToken ct, TimeOnly? startTime = null)
    {
        var d = ctx.Draft;
        var preferred = d.BarberMode == BarberPreferenceKind.Specific ? d.BarberId : null;
        var alternatives = await bookings.GetAlternativeSlotsAsync(
            d.Date!.Value, startTime ?? d.Time ?? ctx.Calendar.Settings.OpeningTime, preferred, ctx.IgnoreBookingId, _booking.MaxAlternatives, ct);

        if (alternatives.Count == 0)
        {
            d.Date = null;
            d.Time = null;
            ctx.Reply($"{reason}\nSorry, we're fully booked for the next few days. Would you like to try another date?");
            ctx.State = ConversationState.CollectingBookingDate;
            return;
        }
        PresentOffers(ctx, reason, alternatives);
    }

    private void PresentOffers(TurnContext ctx, string reason, IReadOnlyList<SlotOption> offers)
    {
        var d = ctx.Draft;
        d.Offers = offers.Select(o => new DraftOffer(o.Date, o.Time, o.BarberId)).ToList();
        var text = new StringBuilder(reason).Append("\nWe can offer you:\n");
        var options = new List<ReplyOption>();
        for (var i = 0; i < offers.Count; i++)
        {
            var o = offers[i];
            var when = o.Date == d.Date ? Format.Time(o.Time) : Format.DayAndTime(o.Date, o.Time);
            text.Append($"\n{i + 1}. {when} – {o.BarberName}");
            options.Add(new((i + 1).ToString(), $"{when} {o.BarberName}", o.Date == d.Date ? null : Format.DateShort(o.Date)));
        }
        text.Append(d.Intent == DraftIntent.Reschedule ? "\n\nWould you like to move your appointment to one of these?" : "\n\nWould you like me to book one of these?");
        ctx.Reply(text.ToString(), options);
        ctx.State = ConversationState.OfferingAlternatives;
    }

    private void AskDateClarification(TurnContext ctx)
    {
        var d = ctx.Draft;
        var options = d.DateCandidates.Select((c, i) => new ReplyOption((i + 1).ToString(), Format.DateShort(c))).ToList();
        var text = d.DateIsPeriod
            ? "Which day next week works for you?\n\n" + string.Join('\n', d.DateCandidates.Select((c, i) => $"{i + 1}. {Format.DateShort(c)}"))
            : $"Just to make sure I get it right: do you mean {string.Join(" or ", d.DateCandidates.Select(Format.DateShort))}?";
        ctx.Reply(text, options);
        ctx.State = ConversationState.CollectingBookingDate;
    }

    private void AskTimeClarification(TurnContext ctx)
    {
        var d = ctx.Draft;
        ctx.Reply($"Just to check, did you mean {string.Join(" or ", d.TimeCandidates.Select(Format.Time))}?",
            d.TimeCandidates.Select(t => new ReplyOption(Format.Time(t), Format.Time(t))).ToList());
        ctx.State = ConversationState.CollectingBookingTime;
    }

    private async Task HandleDateAsync(TurnContext ctx, CancellationToken ct)
    {
        if (await TrySwitchFlowAsync(ctx, ct)) return;
        var d = ctx.Draft;
        if (d.DateCandidates.Count > 0 && TryNumber(ctx.Text, out var n) && n >= 1 && n <= d.DateCandidates.Count)
        {
            d.Date = d.DateCandidates[n - 1];
            d.DateCandidates = [];
            d.DateIsPeriod = false;
            await ContinueAsync(ctx, ct);
            return;
        }
        var details = Extract(ctx, ctx.Text, allowBareNumber: false);
        if (!details.Any) { Unknown(ctx); return; }
        Apply(ctx, details);
        await ContinueAsync(ctx, ct);
    }

    private async Task HandleTimeAsync(TurnContext ctx, CancellationToken ct)
    {
        if (await TrySwitchFlowAsync(ctx, ct)) return;
        var d = ctx.Draft;
        if (d.TimeCandidates.Count > 0 && TryNumber(ctx.Text, out var n) && n >= 1 && n <= d.TimeCandidates.Count)
        {
            d.Time = d.TimeCandidates[n - 1];
            d.TimeCandidates = [];
            await ContinueAsync(ctx, ct);
            return;
        }
        var details = Extract(ctx, ctx.Text, allowBareNumber: true);
        if (!details.Any)
        {
            if (ctx.Intent.HasFlag(Intent.No)) { await ResetAsync(ctx, ct); ShowMenu(ctx, welcome: false); return; }
            Unknown(ctx);
            return;
        }
        Apply(ctx, details);
        await ContinueAsync(ctx, ct);
    }

    private async Task HandleBarberSelectionAsync(TurnContext ctx, CancellationToken ct)
    {
        if (await TrySwitchFlowAsync(ctx, ct)) return;
        var d = ctx.Draft;
        if (TryNumber(ctx.Text, out var n) && n >= 1 && n <= d.BarberChoices.Count + 1)
        {
            d.BarberMode = n == 1 ? BarberPreferenceKind.Any : BarberPreferenceKind.Specific;
            d.BarberId = n == 1 ? null : d.BarberChoices[n - 2];
            if (n == 1) ctx.Note("Great. Let me check availability.");
            await ContinueAsync(ctx, ct);
            return;
        }
        var details = Extract(ctx, ctx.Text, allowBareNumber: false);
        if (!details.Any) { Unknown(ctx); return; }
        if (details.Barber.Kind == BarberPreferenceKind.Any) ctx.Note("Great. Let me check availability.");
        Apply(ctx, details);
        await ContinueAsync(ctx, ct);
    }

    private async Task HandleAlternativesAsync(TurnContext ctx, CancellationToken ct)
    {
        if (await TrySwitchFlowAsync(ctx, ct)) return;
        var d = ctx.Draft;
        if (TryNumber(ctx.Text, out var n) && n >= 1 && n <= d.Offers.Count)
        {
            var offer = d.Offers[n - 1];
            d.Date = offer.Date;
            d.Time = offer.Time;
            d.BarberMode = BarberPreferenceKind.Specific;
            d.BarberId = offer.BarberId;
            d.Offers = [];
            await ContinueAsync(ctx, ct); // re-checks availability before holding
            return;
        }
        var details = Extract(ctx, ctx.Text, allowBareNumber: false);
        if (details.Any)
        {
            Apply(ctx, details);
            await ContinueAsync(ctx, ct);
            return;
        }
        if (ctx.Intent.HasFlag(Intent.No))
        {
            d.Time = null;
            d.Offers = [];
            ctx.Reply("No problem. Which other day or time would suit you? (Or type *menu* to start over.)");
            ctx.State = ConversationState.CollectingBookingTime;
            return;
        }
        Unknown(ctx);
    }

    private async Task HandleNameAsync(TurnContext ctx, CancellationToken ct)
    {
        if (await TrySwitchFlowAsync(ctx, ct)) return;
        var looksLikeCommand = (ctx.Intent & (Intent.Yes | Intent.No | Intent.Greeting | Intent.Book | Intent.Thanks)) != 0;
        var name = looksLikeCommand ? null : NameParser.Parse(ctx.RawText);
        if (name is null) { Unknown(ctx); return; }

        ctx.Customer.FullName = name;
        ctx.Customer.UpdatedAtUtc = ctx.Now;
        ctx.Note($"Thanks, {name}!");
        if (ctx.Draft.HeldBookingId is not null && ctx.Draft.ResolvedBarberId is not null)
            AskBookingConfirmation(ctx);
        else
            await ContinueAsync(ctx, ct);
    }

    private async Task HandleBookingConfirmationAsync(TurnContext ctx, CancellationToken ct)
    {
        var d = ctx.Draft;
        if (ctx.Intent.HasFlag(Intent.Yes))
        {
            if (d.Intent == DraftIntent.Reschedule) await FinalizeRescheduleAsync(ctx, ct);
            else await FinalizeBookingAsync(ctx, ct);
            return;
        }
        if (ctx.Intent.HasFlag(Intent.No) || ctx.Intent.HasFlag(Intent.Cancel))
        {
            await ReleaseHoldAsync(ctx, ct);
            d.Time = null;
            ctx.Reply(d.Intent == DraftIntent.Reschedule
                ? "No problem, your appointment stays as it is. Would you like a different time? Tell me the day or time, or type *menu* to start over."
                : "No problem, I haven't booked anything and I've released that slot. Would you like a different time? Tell me the day or time, or type *menu* to start over.");
            ctx.State = ConversationState.CollectingBookingTime;
            return;
        }
        var details = Extract(ctx, ctx.Text, allowBareNumber: false);
        if (details.Any)
        {
            await ReleaseHoldAsync(ctx, ct);
            Apply(ctx, details);
            await ContinueAsync(ctx, ct);
            return;
        }
        Unknown(ctx);
    }

    private async Task FinalizeBookingAsync(TurnContext ctx, CancellationToken ct)
    {
        var d = ctx.Draft;
        if (d.HeldBookingId is not long holdId)
        {
            await ContinueAsync(ctx, ct);
            return;
        }

        var result = await bookings.ConfirmBookingAsync(holdId, ct);
        if (result.Success)
        {
            var b = result.Booking!;
            var local = ctx.Calendar.ToLocal(b.StartAtUtc);
            ctx.Reply($"Your appointment at {ctx.Calendar.Settings.ShopName} is confirmed!\n\n" +
                      $"Booking reference: {b.BookingReference}\n" +
                      $"Date: {Format.Date(DateOnly.FromDateTime(local))}\n" +
                      $"Time: {Format.Time(TimeOnly.FromDateTime(local))}\n" +
                      $"Barber: {b.Barber?.DisplayName ?? ctx.BarberName(b.BarberId)}\n" +
                      $"Duration: {DurationText(ctx)}\n\n" +
                      "We look forward to seeing you!");
            ctx.Draft = new BookingDraft();
            ctx.State = ConversationState.BookingConfirmed;
            return;
        }

        d.HeldBookingId = null;
        switch (result.Error)
        {
            case BookingError.InvalidSlot:
                ctx.Note(RejectionMessage(ctx, result.Rejection, d.Time!.Value));
                d.Time = null;
                await AskTimeAsync(ctx, ct);
                break;
            case BookingError.NotFound or BookingError.InvalidStatus:
                await ContinueAsync(ctx, ct);
                break;
            default:
                await OfferAlternativesAsync(ctx, "Sorry, that slot was just taken. Let me find another available time for you.", ct);
                break;
        }
    }

    private async Task FinalizeRescheduleAsync(TurnContext ctx, CancellationToken ct)
    {
        var d = ctx.Draft;
        var result = await bookings.RescheduleBookingAsync(d.TargetBookingId!.Value, ctx.Customer.Id, d.Date!.Value, d.Time!.Value,
            d.ResolvedBarberId, enforceNotice: true, ct);
        if (result.Success)
        {
            var b = result.Booking!;
            var local = ctx.Calendar.ToLocal(b.StartAtUtc);
            ctx.Reply("Your appointment has been rescheduled!\n\n" +
                      $"Booking reference: {b.BookingReference}\n" +
                      $"Date: {Format.Date(DateOnly.FromDateTime(local))}\n" +
                      $"Time: {Format.Time(TimeOnly.FromDateTime(local))}\n" +
                      $"Barber: {ctx.BarberName(b.BarberId)}\n" +
                      $"Duration: {DurationText(ctx)}\n\n" +
                      "See you then!");
            ctx.Draft = new BookingDraft();
            ctx.State = ConversationState.BookingConfirmed;
            return;
        }

        switch (result.Error)
        {
            case BookingError.CancellationTooLate:
                await LateChangeAsync(ctx, result.Booking!, "reschedule", ct);
                break;
            case BookingError.InvalidSlot:
                ctx.Note(RejectionMessage(ctx, result.Rejection, d.Time!.Value));
                d.Time = null;
                await AskTimeAsync(ctx, ct);
                break;
            case BookingError.NotFound or BookingError.NotOwner or BookingError.InvalidStatus:
                ctx.Draft = new BookingDraft();
                ctx.Note("I couldn't find that appointment any more.");
                ShowMenu(ctx, welcome: false);
                break;
            default:
                await OfferAlternativesAsync(ctx, "Sorry, that slot was just taken. Let me find another available time for you.", ct);
                break;
        }
    }

    // ---------------------------------------------------------------- view / cancel / reschedule

    private async Task ShowBookingsAsync(TurnContext ctx, CancellationToken ct)
    {
        await ResetAsync(ctx, ct);
        var upcoming = await bookings.GetCustomerBookingsAsync(ctx.Customer.Id, ct);
        if (upcoming.Count == 0)
        {
            ctx.Reply("You don't have any upcoming appointments with us. Would you like to book one? Just tell me a day and time.");
            ctx.State = ConversationState.Idle;
            return;
        }
        var text = new StringBuilder(upcoming.Count == 1 ? "Here's your upcoming appointment:\n" : "Here are your upcoming appointments:\n");
        foreach (var b in upcoming) text.Append('\n').Append(Describe(ctx, b));
        text.Append("\n\nNeed to change anything? Just say *reschedule* or *cancel*.");
        ctx.Reply(text.ToString());
        ctx.State = ConversationState.ViewingBookings;
    }

    private async Task StartCancelAsync(TurnContext ctx, CancellationToken ct)
    {
        await ResetAsync(ctx, ct);
        ctx.Draft.Intent = DraftIntent.Cancel;
        var upcoming = await bookings.GetCustomerBookingsAsync(ctx.Customer.Id, ct);
        if (upcoming.Count == 0)
        {
            ctx.Draft = new BookingDraft();
            ctx.Reply("You don't have any upcoming appointments to cancel.");
            ctx.State = ConversationState.Idle;
            return;
        }
        if (upcoming.Count == 1)
        {
            ctx.Draft.TargetBookingId = upcoming[0].Id;
            AskCancellationConfirmation(ctx, upcoming[0]);
            return;
        }
        ListBookingsForSelection(ctx, upcoming, "Which appointment would you like to cancel?");
        ctx.State = ConversationState.AwaitingCancellationSelection;
    }

    private async Task StartRescheduleAsync(TurnContext ctx, Details details, CancellationToken ct)
    {
        await ResetAsync(ctx, ct);
        var upcoming = await bookings.GetCustomerBookingsAsync(ctx.Customer.Id, ct);
        if (upcoming.Count == 0)
        {
            ctx.Reply("You don't have any upcoming appointments to reschedule. Would you like to book a new one? Just tell me a day and time.");
            ctx.State = ConversationState.Idle;
            return;
        }
        ctx.Draft.Intent = DraftIntent.Reschedule;
        Apply(ctx, details);
        if (upcoming.Count == 1)
        {
            await BeginRescheduleAsync(ctx, upcoming[0], ct);
            return;
        }
        ListBookingsForSelection(ctx, upcoming, "Which appointment would you like to move?");
        ctx.State = ConversationState.Rescheduling;
    }

    private async Task BeginRescheduleAsync(TurnContext ctx, Domain.Booking booking, CancellationToken ct)
    {
        if (booking.StartAtUtc - ctx.Now < TimeSpan.FromHours(ctx.Calendar.Settings.CancellationNoticeHours))
        {
            await LateChangeAsync(ctx, booking, "reschedule", ct);
            return;
        }
        var d = ctx.Draft;
        d.TargetBookingId = booking.Id;
        if (d.BarberMode == BarberPreferenceKind.None)
        {
            d.BarberMode = BarberPreferenceKind.Specific;
            d.BarberId = booking.BarberId;
        }
        var local = ctx.Calendar.ToLocal(booking.StartAtUtc);
        ctx.Note($"Sure! Let's move your appointment on {Format.DateShort(DateOnly.FromDateTime(local))} at {Format.Time(TimeOnly.FromDateTime(local))} with {ctx.BarberName(booking.BarberId)}.");
        await ContinueAsync(ctx, ct);
    }

    private void ListBookingsForSelection(TurnContext ctx, IReadOnlyList<Domain.Booking> upcoming, string question)
    {
        ctx.Draft.BookingChoices = upcoming.Select(b => b.Id).ToList();
        var text = new StringBuilder(question).Append('\n');
        var options = new List<ReplyOption>();
        for (var i = 0; i < upcoming.Count; i++)
        {
            var local = ctx.Calendar.ToLocal(upcoming[i].StartAtUtc);
            text.Append($"\n{i + 1}. {Describe(ctx, upcoming[i])}");
            options.Add(new((i + 1).ToString(), Format.DayAndTime(DateOnly.FromDateTime(local), TimeOnly.FromDateTime(local)), upcoming[i].BookingReference));
        }
        ctx.Reply(text.ToString(), options);
    }

    private async Task HandleBookingSelectionAsync(TurnContext ctx, DraftIntent purpose, CancellationToken ct)
    {
        var d = ctx.Draft;
        long? chosen = null;
        if (TryNumber(ctx.Text, out var n) && n >= 1 && n <= d.BookingChoices.Count) chosen = d.BookingChoices[n - 1];
        else if (ReferencePattern().Match(ctx.RawText.ToUpperInvariant()) is { Success: true } m)
            chosen = await db.Bookings.Where(b => b.BookingReference == m.Value && d.BookingChoices.Contains(b.Id)).Select(b => (long?)b.Id).FirstOrDefaultAsync(ct);

        if (chosen is null)
        {
            if (ctx.Intent.HasFlag(Intent.No))
            {
                ctx.Draft = new BookingDraft();
                ctx.Reply("No problem, nothing has been changed.");
                ctx.State = ConversationState.Idle;
                return;
            }
            Unknown(ctx);
            return;
        }

        var booking = await db.Bookings.AsNoTracking().FirstAsync(b => b.Id == chosen, ct);
        d.BookingChoices = [];
        if (purpose == DraftIntent.Cancel)
        {
            d.TargetBookingId = booking.Id;
            AskCancellationConfirmation(ctx, booking);
        }
        else
        {
            await BeginRescheduleAsync(ctx, booking, ct);
        }
    }

    private void AskCancellationConfirmation(TurnContext ctx, Domain.Booking booking)
    {
        ctx.Reply($"Just to confirm, you'd like to cancel this appointment?\n\n{Describe(ctx, booking)}\n\nReply *yes* to cancel it or *no* to keep it.",
            [new("yes", "Yes, cancel it"), new("no", "No, keep it")]);
        ctx.State = ConversationState.AwaitingCancellationConfirmation;
    }

    private async Task HandleCancellationConfirmationAsync(TurnContext ctx, CancellationToken ct)
    {
        var d = ctx.Draft;
        if (ctx.Intent.HasFlag(Intent.Yes))
        {
            var result = await bookings.CancelBookingAsync(d.TargetBookingId!.Value, ctx.Customer.Id,
                $"Cancelled by customer via {ctx.Conversation.Platform.DisplayName()}", enforceNotice: true, ct);
            if (result.Success)
            {
                var local = ctx.Calendar.ToLocal(result.Booking!.StartAtUtc);
                ctx.Reply($"Your appointment on {Format.DateShort(DateOnly.FromDateTime(local))} at {Format.Time(TimeOnly.FromDateTime(local))} has been cancelled.\n" +
                          $"Reference: {result.Booking.BookingReference}\n\nWe hope to see you another time!");
                ctx.Draft = new BookingDraft();
                ctx.State = ConversationState.Idle;
                return;
            }
            if (result.Error == BookingError.CancellationTooLate)
            {
                await LateChangeAsync(ctx, result.Booking!, "cancel", ct);
                return;
            }
            ctx.Draft = new BookingDraft();
            ctx.Reply(result.Error == BookingError.InvalidStatus ? "That appointment has already been cancelled." : "I couldn't find that appointment any more.");
            ctx.State = ConversationState.Idle;
            return;
        }
        if (ctx.Intent.HasFlag(Intent.No))
        {
            ctx.Draft = new BookingDraft();
            ctx.Reply("No problem, your appointment is still booked. See you then!");
            ctx.State = ConversationState.Idle;
            return;
        }
        Unknown(ctx);
    }

    private async Task LateChangeAsync(TurnContext ctx, Domain.Booking booking, string action, CancellationToken ct)
    {
        var hours = ctx.Calendar.Settings.CancellationNoticeHours;
        ctx.Draft = new BookingDraft();
        ctx.Reply($"Your appointment is less than {hours} hour{(hours == 1 ? "" : "s")} away, so I can't {action} it automatically. " +
                  "I've let the team know and someone will get back to you shortly.");
        await RequestHumanAsync(ctx, $"Wants to {action} {booking.BookingReference} inside the {hours}-hour notice period.", ct);
    }

    // ---------------------------------------------------------------- human handover

    private async Task HandOverAsync(TurnContext ctx, string reason, CancellationToken ct)
    {
        await ResetAsync(ctx, ct);
        ctx.Reply($"No problem. I've let the team at {ctx.Calendar.Settings.ShopName} know, and someone will get back to you here as soon as possible.");
        await RequestHumanAsync(ctx, reason, ct);
    }

    private async Task RequestHumanAsync(TurnContext ctx, string reason, CancellationToken ct)
    {
        ctx.Conversation.RequiresAttention = true;
        ctx.State = ConversationState.HumanSupportRequested;
        var conversationId = ctx.Conversation.Id;
        var contact = ctx.Customer.PhoneNumber is null ? "" : $"\nPhone: {ctx.Customer.PhoneNumber}";
        await notifications.EnqueueOwnerNotificationAsync(NotificationTypes.HumanAssistance,
            "CUSTOMER NEEDS HUMAN ASSISTANCE\n" +
            $"Customer: {ctx.Customer.FullName ?? "(name not given)"}{contact}\n" +
            $"Platform: {ctx.Conversation.Platform.DisplayName()}\n" +
            $"Reason: {reason}\n" +
            $"Conversation: #{conversationId}\n" +
            $"Please respond to the customer. Reply RESUME {conversationId} to hand the chat back to the bot, or REPLY {conversationId} <message> to answer through the bot.",
            null, ct);
    }

    private async Task HandleHumanOfferAsync(TurnContext ctx, CancellationToken ct)
    {
        if (ctx.Intent.HasFlag(Intent.Yes))
        {
            await HandOverAsync(ctx, "The assistant could not understand the customer's request.", ct);
            return;
        }
        var resume = ctx.Draft.ResumeState ?? ConversationState.Greeting;
        ctx.Draft.ResumeState = null;
        ctx.State = resume;
        if (ctx.Intent.HasFlag(Intent.No))
        {
            ctx.Reply("No problem, let's keep going. " + StatePrompt(ctx));
            return;
        }
        await RouteAsync(ctx, ct);
    }

    private void Unknown(TurnContext ctx)
    {
        ctx.Misunderstood = true;
        ctx.Draft.ClarificationAttempts++;
        if (ctx.Draft.ClarificationAttempts >= _chatbot.ClarificationAttemptsBeforeHumanOffer)
        {
            ctx.Draft.ClarificationAttempts = 0;
            ctx.Draft.ResumeState = ctx.State;
            ctx.Reply("I'm having a little trouble understanding that request. Would you like me to notify someone at the shop to assist you?", YesNo);
            ctx.State = ConversationState.OfferingHumanSupport;
            return;
        }
        ctx.Reply("Sorry, I didn't quite get that. " + StatePrompt(ctx));
    }

    private static string StatePrompt(TurnContext ctx)
    {
        var s = ctx.Calendar.Settings;
        var last = ctx.Calendar.SlotStartTimes.Count > 0 ? Format.Time(ctx.Calendar.SlotStartTimes[^1]) : Format.Time(s.ClosingTime);
        return ctx.State switch
        {
            ConversationState.CollectingBookingDate => "What date would you like to come in? You can say things like *tomorrow*, *Friday* or *7 November*.",
            ConversationState.CollectingBookingTime => $"What time would you like? We take appointments on the hour from {Format.Time(s.OpeningTime)}, last one at {last}.",
            ConversationState.SelectingBarber => "Reply with the number of the barber you'd like, or *1* for the first available.",
            ConversationState.OfferingAlternatives => "Reply with the number of the option you'd like, or tell me another day or time.",
            ConversationState.CollectingCustomerName => "What name should I put the booking under?",
            ConversationState.AwaitingBookingConfirmation => "Please reply *yes* to confirm or *no* to choose another time.",
            ConversationState.AwaitingCancellationSelection or ConversationState.Rescheduling => "Reply with the number of the appointment.",
            ConversationState.AwaitingCancellationConfirmation => "Reply *yes* to cancel it or *no* to keep it.",
            _ => "I can help you *book*, *view*, *reschedule* or *cancel* an appointment, or put you through to *someone at the shop*."
        };
    }

    /// <summary>Customers may change their mind mid-flow ("actually, cancel my appointment").</summary>
    private async Task<bool> TrySwitchFlowAsync(TurnContext ctx, CancellationToken ct)
    {
        var intent = ctx.Intent;
        var mentionsBooking = BookingWord().IsMatch(ctx.Text);
        if (intent.HasFlag(Intent.Cancel) && (mentionsBooking || ctx.Draft.Intent != DraftIntent.Book))
        {
            await StartCancelAsync(ctx, ct);
            return true;
        }
        if (intent.HasFlag(Intent.ViewBookings) && mentionsBooking)
        {
            await ShowBookingsAsync(ctx, ct);
            return true;
        }
        if (intent.HasFlag(Intent.Reschedule) && mentionsBooking && ctx.Draft.Intent != DraftIntent.Reschedule)
        {
            await StartRescheduleAsync(ctx, Extract(ctx, ctx.Text, false), ct);
            return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- helpers

    private async Task ResetAsync(TurnContext ctx, CancellationToken ct)
    {
        await ReleaseHoldAsync(ctx, ct);
        ctx.Draft = new BookingDraft { ClarificationAttempts = ctx.Draft.ClarificationAttempts };
    }

    private async Task ReleaseHoldAsync(TurnContext ctx, CancellationToken ct)
    {
        if (ctx.Draft.HeldBookingId is long id)
        {
            await bookings.ReleaseHoldAsync(id, ct);
            ctx.Draft.HeldBookingId = null;
        }
    }

    private static string RejectionMessage(TurnContext ctx, SlotRejection rejection, TimeOnly requested)
    {
        var s = ctx.Calendar.Settings;
        var last = ctx.Calendar.SlotStartTimes.Count > 0 ? Format.Time(ctx.Calendar.SlotStartTimes[^1]) : Format.Time(s.ClosingTime);
        return rejection switch
        {
            SlotRejection.InPast => $"{Format.Time(requested)} has already passed.",
            SlotRejection.BeforeOpening or SlotRejection.AfterClosing or SlotRejection.EndsAfterClosing =>
                $"We're open from {Format.Time(s.OpeningTime)} to {Format.Time(s.ClosingTime)}, so the last appointment starts at {last}.",
            SlotRejection.NotOnSlotBoundary => $"Our appointments start on the hour (for example {Format.Time(s.OpeningTime)} or {Format.Time(s.OpeningTime.AddHours(1))}).",
            SlotRejection.BeyondAdvanceWindow => $"We take bookings up to {s.BookingAdvanceDays} days ahead.",
            _ => "That time isn't available."
        };
    }

    private static string Describe(TurnContext ctx, Domain.Booking b)
    {
        var local = ctx.Calendar.ToLocal(b.StartAtUtc);
        return $"{Format.DateShort(DateOnly.FromDateTime(local))} at {Format.Time(TimeOnly.FromDateTime(local))} with {b.Barber?.DisplayName ?? ctx.BarberName(b.BarberId)} ({b.BookingReference})";
    }

    private static string DurationText(TurnContext ctx)
    {
        var minutes = ctx.Calendar.Settings.AppointmentDurationMinutes;
        return minutes % 60 == 0 ? (minutes == 60 ? "1 hour" : $"{minutes / 60} hours") : $"{minutes} minutes";
    }

    private static bool TryNumber(string text, out int number)
    {
        var m = NumberChoice().Match(text);
        number = m.Success ? int.Parse(m.Groups[1].Value) : 0;
        return m.Success;
    }

    private sealed record Details(BarberPreference Barber, DateParseResult Date, TimeParseResult Time)
    {
        public static readonly Details Empty = new(new BarberPreference(BarberPreferenceKind.None, null, null), DateParseResult.None, TimeParseResult.None);
        public bool Any => Barber.Kind != BarberPreferenceKind.None || Date.Kind != ParseKind.None || Time.Kind != ParseKind.None;
    }

    /// <summary>Extracts barber, then date, then time, removing each match so "Barber 2" or "7 November" is not read as a time.</summary>
    private static Details Extract(TurnContext ctx, string text, bool allowBareNumber)
    {
        var remaining = text;
        var barber = BarberMatcher.Match(remaining, ctx.Barbers);
        if (barber.Matched is not null) remaining = Remove(remaining, barber.Matched);
        var date = DateParser.Parse(remaining, ctx.Calendar.Today(ctx.Now));
        if (date.Matched is not null) remaining = Remove(remaining, date.Matched);
        var timeResult = TimeParser.Parse(remaining, ctx.Calendar.SlotStartTimes, allowBareNumber);
        return new Details(barber, date, timeResult);

        static string Remove(string source, string matched)
        {
            var index = source.IndexOf(matched, StringComparison.OrdinalIgnoreCase);
            return index < 0 ? source : source.Remove(index, matched.Length).Insert(index, " ");
        }
    }

    private static void Apply(TurnContext ctx, Details details)
    {
        var d = ctx.Draft;
        switch (details.Barber.Kind)
        {
            case BarberPreferenceKind.Any:
                d.BarberMode = BarberPreferenceKind.Any;
                d.BarberId = null;
                break;
            case BarberPreferenceKind.Specific:
                d.BarberMode = BarberPreferenceKind.Specific;
                d.BarberId = details.Barber.BarberId;
                break;
        }
        switch (details.Date.Kind)
        {
            case ParseKind.Exact:
                d.Date = details.Date.Date;
                d.DateCandidates = [];
                d.DateIsPeriod = false;
                break;
            case ParseKind.Ambiguous or ParseKind.NeedsDay:
                d.Date = null;
                d.DateCandidates = details.Date.Candidates.ToList();
                d.DateIsPeriod = details.Date.Kind == ParseKind.NeedsDay;
                break;
        }
        switch (details.Time.Kind)
        {
            case ParseKind.Exact:
                d.Time = details.Time.Time;
                d.TimeCandidates = [];
                break;
            case ParseKind.Ambiguous:
                d.Time = null;
                var plausible = details.Time.Candidates.Where(c => ctx.Calendar.SlotStartTimes.Contains(c)).ToList();
                d.TimeCandidates = plausible.Count > 1 ? plausible : [];
                if (plausible.Count == 0)
                {
                    var s = ctx.Calendar.Settings;
                    var last = ctx.Calendar.SlotStartTimes.Count > 0 ? Format.Time(ctx.Calendar.SlotStartTimes[^1]) : Format.Time(s.ClosingTime);
                    ctx.Note($"Just to check, did you mean {string.Join(" or ", details.Time.Candidates.Select(Format.Time))}? " +
                             $"We're open from {Format.Time(s.OpeningTime)} to {Format.Time(s.ClosingTime)}, so our first appointment is at {Format.Time(s.OpeningTime)} and the last one starts at {last}.");
                }
                break;
        }
        if (details.Barber.Kind != BarberPreferenceKind.None || details.Date.Kind != ParseKind.None || details.Time.Kind != ParseKind.None)
            d.Offers = [];
    }

    [GeneratedRegex(@"^(?:option\s*|number\s*|no\.?\s*|#)?(\d{1,2})$")] private static partial Regex NumberChoice();
    [GeneratedRegex(@"VF-\d{4}-\d{6}")] private static partial Regex ReferencePattern();
    [GeneratedRegex(@"\b(booking|bookings|appointment|appointments|appt|reservation)\b")] private static partial Regex BookingWord();

    private sealed class TurnContext(ConversationTurn turn, ShopCalendar calendar, IReadOnlyList<Barber> barbers, DateTime now, BookingDraft draft)
    {
        private readonly List<string> _notes = [];
        private readonly List<OutboundMessage> _replies = [];

        public Conversation Conversation => turn.Conversation;
        public Customer Customer => turn.Customer;
        public ShopCalendar Calendar => calendar;
        public IReadOnlyList<Barber> Barbers => barbers;
        public DateTime Now => now;
        public BookingDraft Draft { get; set; } = draft;
        public string RawText { get; } = (turn.Text ?? "").Trim();
        public string Text { get; } = IntentRecognizer.Normalize(turn.Text);
        public Intent Intent => IntentRecognizer.Recognize(Text);
        public bool Misunderstood { get; set; }
        public bool HasNotes => _notes.Count > 0;

        public ConversationState State
        {
            get => Conversation.CurrentState;
            set => Conversation.CurrentState = value;
        }

        public long? IgnoreBookingId => Draft.Intent == DraftIntent.Reschedule ? Draft.TargetBookingId : null;

        public string BarberName(int id) => barbers.FirstOrDefault(b => b.Id == id)?.DisplayName ?? "your barber";

        /// <summary>Adds a sentence that is prepended to the next reply, keeping each answer to a single message.</summary>
        public void Note(string text) => _notes.Add(text);

        public void Reply(string text, IReadOnlyList<ReplyOption>? options = null)
        {
            var body = _notes.Count == 0 ? text : string.Join("\n", _notes) + "\n\n" + text;
            _notes.Clear();
            _replies.Add(new OutboundMessage(body, options is { Count: > 0 } ? options : null));
        }

        public IReadOnlyList<OutboundMessage> Flush()
        {
            if (_notes.Count > 0)
            {
                _replies.Add(new OutboundMessage(string.Join("\n", _notes)));
                _notes.Clear();
            }
            return _replies;
        }
    }
}
