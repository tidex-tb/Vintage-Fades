using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VintageFades.Application.Abstractions;
using VintageFades.Application.Bookings;
using VintageFades.Application.Chatbot;
using VintageFades.Application.Messaging;
using VintageFades.Domain;

namespace VintageFades.Application.Inbound;

/// <summary>
/// Turns one platform message into one database transaction: message log, customer/conversation upsert,
/// chatbot decision, booking changes and outbox replies all commit together or not at all.
/// Duplicate platform message ids (Meta retries) are ignored, so a retried webhook never books twice.
/// </summary>
public sealed class InboundMessageHandler(
    IAppDbContext db,
    ConversationEngine engine,
    NotificationService notifications,
    OwnerCommandHandler ownerCommands,
    BookingService bookings,
    TimeProvider time,
    IOptions<NotificationOptions> notificationOptions,
    IOptions<ChatbotOptions> chatbotOptions,
    ILogger<InboundMessageHandler> logger)
{
    public async Task HandleAsync(InboundMessage message, CancellationToken ct)
    {
        if (message.IsEcho) return;

        // Owner messages never reach the customer chatbot (prevents notification loops).
        if (message.Platform == Platform.WhatsApp &&
            notificationOptions.Value.OwnerRecipient is { } owner && PhoneNumbers.Normalize(message.SenderId) == owner)
        {
            await ownerCommands.HandleAsync(message, ct);
            return;
        }

        await db.InTransactionAsync(async () =>
        {
            if (await db.ConversationMessages.AnyAsync(m => m.Platform == message.Platform && m.PlatformMessageId == message.MessageId, ct))
            {
                logger.LogInformation("Duplicate {Platform} message {MessageId} ignored", message.Platform, message.MessageId);
                return 0;
            }

            var now = time.GetUtcNow().UtcDateTime;
            var customer = await FindOrCreateCustomerAsync(message, now, ct);
            var conversation = await db.Conversations.FirstOrDefaultAsync(
                c => c.Platform == message.Platform && c.PlatformConversationId == message.SenderId, ct);
            if (conversation is null)
            {
                conversation = new Conversation
                {
                    Customer = customer,
                    Platform = message.Platform,
                    PlatformConversationId = message.SenderId,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                };
                db.Conversations.Add(conversation);
                await db.SaveChangesAsync(ct);
            }

            var inbound = new ConversationMessage
            {
                ConversationId = conversation.Id,
                Platform = message.Platform,
                PlatformMessageId = message.MessageId,
                Direction = MessageDirection.Inbound,
                MessageType = message.MessageType,
                MessageContent = Truncate(message.Text ?? message.ReplyPayload),
                ProcessingStatus = ProcessingStatus.Pending,
                ReceivedAtUtc = now
            };
            db.ConversationMessages.Add(inbound);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (db.IsUniqueViolation(ex))
            {
                db.Discard(inbound);
                return 0;
            }

            var replies = await engine.HandleAsync(
                new ConversationTurn(conversation, customer, message.MessageType, message.ReplyPayload ?? message.Text), ct);

            conversation.LastMessageAtUtc = now;
            conversation.LastInboundAtUtc = now;
            conversation.UpdatedAtUtc = now;
            inbound.ProcessingStatus = ProcessingStatus.Processed;
            inbound.ProcessedAtUtc = now;

            foreach (var reply in replies)
            {
                var outbound = new ConversationMessage
                {
                    ConversationId = conversation.Id,
                    Platform = message.Platform,
                    Direction = MessageDirection.Outbound,
                    MessageType = reply.Options is null ? MessageType.Text : MessageType.Interactive,
                    MessageContent = Truncate(reply.Text),
                    ProcessingStatus = ProcessingStatus.Pending,
                    ReceivedAtUtc = now
                };
                db.ConversationMessages.Add(outbound);
                await db.SaveChangesAsync(ct);
                notifications.EnqueueCustomerMessage(conversation, reply, outbound.Id);
            }
            await db.SaveChangesAsync(ct);
            return replies.Count;
        }, ct);
    }

    /// <summary>
    /// The shop replied to a customer from the WhatsApp Business app (Coexistence). The bot steps back in that chat
    /// (releasing any hold it had offered) so it never talks over a person, and resumes after
    /// <see cref="ChatbotOptions.ManualReplyPauseMinutes"/> or when the owner sends RESUME.
    /// </summary>
    public async Task HandleBusinessAppReplyAsync(BusinessAppReply reply, CancellationToken ct)
    {
        // The app's own greeting or away message is sent automatically when a customer writes: nobody has taken over.
        if (chatbotOptions.Value.AutomatedMessagePrefixes.Any(p =>
                !string.IsNullOrWhiteSpace(p) && (reply.Text ?? "").TrimStart().StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            logger.LogInformation("Ignored the Business app's automated message {MessageId}", reply.MessageId);
            return;
        }

        await db.InTransactionAsync(async () =>
        {
            if (await db.ConversationMessages.AnyAsync(m => m.Platform == reply.Platform && m.PlatformMessageId == reply.MessageId, ct))
                return 0;
            var conversation = await db.Conversations.Include(c => c.Customer)
                .FirstOrDefaultAsync(c => c.Platform == reply.Platform && c.PlatformConversationId == reply.CustomerId, ct);
            if (conversation is null) return 0; // a chat the bot has never been part of

            var now = time.GetUtcNow().UtcDateTime;
            db.ConversationMessages.Add(new ConversationMessage
            {
                ConversationId = conversation.Id,
                Platform = reply.Platform,
                PlatformMessageId = reply.MessageId,
                Direction = MessageDirection.Outbound,
                MessageType = MessageType.Text,
                MessageContent = Truncate(reply.Text),
                ProcessingStatus = ProcessingStatus.Processed,
                ReceivedAtUtc = now,
                ProcessedAtUtc = now
            });
            conversation.LastMessageAtUtc = now;
            conversation.UpdatedAtUtc = now;

            var minutes = chatbotOptions.Value.ManualReplyPauseMinutes;
            var resumeAt = now.AddMinutes(minutes);
            if (conversation.CurrentState == ConversationState.HumanSupportRequested)
            {
                // Already handed over: each reply from the shop keeps the bot quiet a little longer.
                var draft = BookingDraft.Deserialize(conversation.CurrentBookingIntent);
                if (draft.AutoResumeAtUtc is { } current && current < resumeAt)
                {
                    draft.AutoResumeAtUtc = resumeAt;
                    conversation.CurrentBookingIntent = draft.Serialize();
                }
            }
            else
            {
                if (BookingDraft.Deserialize(conversation.CurrentBookingIntent).HeldBookingId is long holdId)
                    await bookings.ReleaseHoldAsync(holdId, ct);
                conversation.CurrentState = ConversationState.HumanSupportRequested;
                conversation.CurrentBookingIntent = new BookingDraft { AutoResumeAtUtc = resumeAt }.Serialize();
                await notifications.EnqueueOwnerNotificationAsync(NotificationTypes.HumanAssistance,
                    "BOT PAUSED – VINTAGE FADES\n" +
                    $"You replied to {conversation.Customer?.FullName ?? conversation.Customer?.PhoneNumber ?? "a customer"} on {conversation.Platform.DisplayName()}, " +
                    $"so the bot will stay quiet in that chat for {minutes} minutes.\n" +
                    $"Conversation: #{conversation.Id}. Reply RESUME {conversation.Id} to hand it back sooner.",
                    $"bot-paused:{conversation.Id}:{reply.MessageId}", ct);
            }
            await db.SaveChangesAsync(ct);
            return 1;
        }, ct);
    }

    private async Task<Customer> FindOrCreateCustomerAsync(InboundMessage message, DateTime now, CancellationToken ct)
    {
        var sender = message.SenderId;
        var customer = message.Platform switch
        {
            Platform.WhatsApp => await db.Customers.FirstOrDefaultAsync(c => c.WhatsAppId == sender, ct),
            Platform.Instagram => await db.Customers.FirstOrDefaultAsync(c => c.InstagramScopedUserId == sender, ct),
            _ => await db.Customers.FirstOrDefaultAsync(c => c.FacebookScopedUserId == sender, ct)
        };
        if (customer is not null) return customer;

        // Identities are never merged across platforms automatically: there is no reliable proof
        // that an Instagram or Messenger user owns a given phone number.
        customer = new Customer { CreatedAtUtc = now, UpdatedAtUtc = now };
        switch (message.Platform)
        {
            case Platform.WhatsApp:
                customer.WhatsAppId = sender;
                customer.PhoneNumber = "+" + PhoneNumbers.Normalize(sender);
                break;
            case Platform.Instagram:
                customer.InstagramScopedUserId = sender;
                break;
            default:
                customer.FacebookScopedUserId = sender;
                break;
        }
        db.Customers.Add(customer);
        return customer;
    }

    private static string? Truncate(string? value) => value is null || value.Length <= 4096 ? value : value[..4096];
}
