using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using VintageFades.Application.Abstractions;
using VintageFades.Application.Messaging;
using VintageFades.Application.Operations;
using VintageFades.Domain;

namespace VintageFades.Application.Inbound;

/// <summary>
/// WhatsApp messages from the owner's configured number are commands, never customer chats:
/// RESUME &lt;id&gt; hands a conversation back to the bot; REPLY &lt;id&gt; &lt;text&gt; answers a customer through the bot.
/// </summary>
public sealed partial class OwnerCommandHandler(IAppDbContext db, NotificationService notifications, BotSwitch botSwitch, TimeProvider time)
{
    private const string Help =
        "VINTAGE FADES assistant commands:\n" +
        "RESUME <id> – hand conversation <id> back to the bot\n" +
        "REPLY <id> <message> – send a message to that customer\n" +
        "BOT OFF / BOT ON – pause or restart the bot on every platform (BOT shows the state)\n" +
        "HELP – show this list";

    [GeneratedRegex(@"^\s*resume\s+#?(\d+)\s*$", RegexOptions.IgnoreCase)] private static partial Regex Resume();
    [GeneratedRegex(@"^\s*bot(?:\s+(on|off|pause|resume|start|stop))?\s*$", RegexOptions.IgnoreCase)] private static partial Regex BotCommand();
    [GeneratedRegex(@"^\s*reply\s+#?(\d+)\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex Reply();

    public async Task HandleAsync(InboundMessage message, CancellationToken ct)
    {
        var dedupeKey = $"owner-command:{message.MessageId}";
        if (await db.OutboxMessages.AnyAsync(o => o.DeduplicationKey == dedupeKey, ct)) return;

        var text = (message.ReplyPayload ?? message.Text ?? "").Trim();
        string response;
        Match m;
        if ((m = Resume().Match(text)).Success)
            response = await ResumeAsync(long.Parse(m.Groups[1].Value), ct);
        else if ((m = BotCommand().Match(text)).Success)
            response = await BotAsync(m.Groups[1].Value.ToLowerInvariant(), ct);
        else if ((m = Reply().Match(text)).Success)
            response = await RelayAsync(long.Parse(m.Groups[1].Value), m.Groups[2].Value.Trim(), message.MessageId, ct);
        else
            response = Help;

        notifications.EnqueueOwnerCommandReply(response, dedupeKey);
        await db.SaveChangesAsync(ct);
    }

    private async Task<string> BotAsync(string word, CancellationToken ct)
    {
        if (word is "off" or "pause" or "stop")
        {
            await botSwitch.SetPausedAsync(true, ct);
            return "The bot is OFF on WhatsApp, Instagram and Messenger. Messages are saved but not answered. Send BOT ON to turn it back on.";
        }
        if (word is "on" or "resume" or "start")
        {
            await botSwitch.SetPausedAsync(false, ct);
            return "The bot is ON again on every platform.";
        }
        return await botSwitch.IsPausedAsync(ct)
            ? "The bot is OFF. Send BOT ON to turn it back on."
            : "The bot is ON. Send BOT OFF to pause it on every platform.";
    }

    private async Task<string> ResumeAsync(long conversationId, CancellationToken ct)
    {
        var conversation = await db.Conversations.Include(c => c.Customer).FirstOrDefaultAsync(c => c.Id == conversationId, ct);
        if (conversation is null) return $"Conversation #{conversationId} was not found.";
        conversation.RequiresAttention = false;
        conversation.CurrentState = ConversationState.Idle;
        conversation.CurrentBookingIntent = null;
        conversation.UpdatedAtUtc = time.GetUtcNow().UtcDateTime;
        return $"Conversation #{conversationId} ({conversation.Customer?.FullName ?? "customer"}, {conversation.Platform.DisplayName()}) is back with the bot.";
    }

    private async Task<string> RelayAsync(long conversationId, string text, string ownerMessageId, CancellationToken ct)
    {
        var conversation = await db.Conversations.Include(c => c.Customer).FirstOrDefaultAsync(c => c.Id == conversationId, ct);
        if (conversation is null) return $"Conversation #{conversationId} was not found.";

        var now = time.GetUtcNow().UtcDateTime;
        if (conversation.LastInboundAtUtc is null || now - conversation.LastInboundAtUtc > TimeSpan.FromHours(24))
            return $"Not sent: the customer last wrote more than 24 hours ago, so {conversation.Platform.DisplayName()} only allows approved templates. " +
                   "Please contact them directly.";

        var outbound = new ConversationMessage
        {
            ConversationId = conversation.Id,
            Platform = conversation.Platform,
            Direction = MessageDirection.Outbound,
            MessageType = MessageType.Text,
            MessageContent = text.Length <= 4096 ? text : text[..4096],
            ProcessingStatus = ProcessingStatus.Pending,
            ReceivedAtUtc = now
        };
        db.ConversationMessages.Add(outbound);
        await db.SaveChangesAsync(ct);
        notifications.EnqueueCustomerMessage(conversation, new OutboundMessage(text), outbound.Id, $"owner-relay:{ownerMessageId}");
        return $"Message queued for {conversation.Customer?.FullName ?? "the customer"} on {conversation.Platform.DisplayName()}. " +
               $"The bot stays paused until you send RESUME {conversation.Id}.";
    }
}
