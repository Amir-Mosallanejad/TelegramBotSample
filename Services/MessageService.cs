using Microsoft.EntityFrameworkCore;
using TelegramBot.Data;
using TelegramBot.Models;

namespace TelegramBot.Services;

/// <summary>
/// Business layer: persists every message in SQLite and performs the actual Telegram delivery.
/// <para>
/// A singleton, so it depends on <see cref="IDbContextFactory{TContext}"/> instead of a
/// <see cref="BotDbContext"/> directly: a DbContext is neither thread-safe nor intended to live
/// for the whole application lifetime, and the factory gives us a fresh, short-lived context per
/// operation.
/// </para>
/// </summary>
public sealed class MessageService
{
    private readonly IDbContextFactory<BotDbContext> _dbContextFactory;
    private readonly BotService _botService;
    private readonly ILogger<MessageService> _logger;

    public MessageService(
        IDbContextFactory<BotDbContext> dbContextFactory,
        BotService botService,
        ILogger<MessageService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _botService = botService;
        _logger = logger;
    }

    /// <summary>
    /// Stores a message with <c>IsDelivered = false</c>, sends it to
    /// <paramref name="receiverChatId"/> and, if the Telegram API accepted it, flips the flag to
    /// <c>true</c>. A failed delivery is kept in the database as a retryable record.
    /// </summary>
    /// <returns>The stored message, including its delivery state.</returns>
    public async Task<Message> StoreAndSendAsync(
        long senderChatId,
        long receiverChatId,
        string text,
        CancellationToken cancellationToken = default)
    {
        var entity = await StoreAsync(senderChatId, receiverChatId, text, isDelivered: false, cancellationToken);

        bool delivered = await _botService.SendTextAsync(receiverChatId, text, cancellationToken);
        entity.IsDelivered = delivered;

        if (delivered)
        {
            await MarkDeliveredAsync(entity.Id, cancellationToken);
        }
        else
        {
            _logger.LogWarning(
                "Message #{MessageId} was stored but could not be delivered to chat {ReceiverChatId}.",
                entity.Id, receiverChatId);
        }

        return entity;
    }

    /// <summary>
    /// Persists a message without sending anything. Used for messages that need no delivery
    /// (for example the admin's own chat messages, which are only kept for the audit log).
    /// </summary>
    public async Task<Message> StoreAsync(
        long senderChatId,
        long receiverChatId,
        string text,
        bool isDelivered,
        CancellationToken cancellationToken = default)
    {
        var entity = new Message
        {
            SenderChatId = senderChatId,
            ReceiverChatId = receiverChatId,
            MessageText = text,
            SentAt = DateTime.UtcNow,
            IsDelivered = isDelivered
        };

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        dbContext.Messages.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Stored message #{MessageId} from chat {SenderChatId} to chat {ReceiverChatId} (delivered: {IsDelivered}).",
            entity.Id, senderChatId, receiverChatId, isDelivered);

        return entity;
    }

    /// <summary>Updates the delivery flag of an already stored message.</summary>
    public async Task MarkDeliveredAsync(int messageId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var entity = await dbContext.Messages
            .FirstOrDefaultAsync(m => m.Id == messageId, cancellationToken);

        if (entity is null)
        {
            _logger.LogWarning("Cannot mark message #{MessageId} as delivered: not found.", messageId);
            return;
        }

        entity.IsDelivered = true;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Returns the most recent messages, newest first. Used for quick manual checks.</summary>
    public async Task<List<Message>> GetRecentAsync(int take = 20, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        return await dbContext.Messages
            .AsNoTracking()
            .OrderByDescending(m => m.Id)
            .Take(Math.Clamp(take, 1, 500))
            .ToListAsync(cancellationToken);
    }
}
