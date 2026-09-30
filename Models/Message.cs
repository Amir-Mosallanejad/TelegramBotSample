using System.ComponentModel.DataAnnotations;

namespace TelegramBot.Models;

/// <summary>
/// A single text message handled by the bot, persisted in the <c>Messages</c> table.
/// </summary>
public class Message
{
    [Key]
    public int Id { get; set; }

    /// <summary>Chat id the message came from.</summary>
    public long SenderChatId { get; set; }

    /// <summary>Chat id the message was (or will be) delivered to.</summary>
    public long ReceiverChatId { get; set; }

    [MaxLength(4096)]
    public string MessageText { get; set; } = string.Empty;

    /// <summary>UTC timestamp of the moment the message was received.</summary>
    public DateTime SentAt { get; set; }

    /// <summary>
    /// <c>false</c> while the message is stored but not yet accepted by the Telegram API;
    /// flipped to <c>true</c> after a successful send.
    /// </summary>
    public bool IsDelivered { get; set; }
}
