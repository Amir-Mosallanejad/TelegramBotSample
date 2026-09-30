using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TelegramBot.Configuration;
using TelegramBot.Services;
using TelegramMessage = Telegram.Bot.Types.Message;

namespace TelegramBot.Handlers;

/// <summary>
/// Translates incoming Telegram updates into application actions:
/// <list type="bullet">
///   <item><c>/send &lt;chatId&gt; &lt;message&gt;</c> — admin-only: the bot messages that chat.</item>
///   <item>any text from a regular user — stored and forwarded to the admin.</item>
/// </list>
/// </summary>
public sealed class UpdateHandler : IUpdateHandler
{
    private const string SendCommand = "/send";
    private const string StartCommand = "/start";
    private const string HelpCommand = "/help";

    private readonly MessageService _messageService;
    private readonly BotService _botService;
    private readonly BotConfig _config;
    private readonly ILogger<UpdateHandler> _logger;

    public UpdateHandler(
        MessageService messageService,
        BotService botService,
        BotConfig config,
        ILogger<UpdateHandler> logger)
    {
        _messageService = messageService;
        _botService = botService;
        _config = config;
        _logger = logger;
    }

    /// <summary>Called by the polling loop for every update. Never throws.</summary>
    public async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        if (update.Message is not TelegramMessage incoming)
        {
            return; // Only message updates are subscribed to.
        }

        long chatId = incoming.Chat.Id;
        long? userId = incoming.From?.Id;

        _logger.LogInformation(
            "Update received: type={UpdateType}, chat={ChatId}, user={UserId}",
            update.Type, chatId, userId);

        try
        {
            // Never react to messages produced by other bots: they would create forwarding loops.
            if (incoming.From is { IsBot: true })
            {
                _logger.LogDebug("Ignoring message from bot {UserId}.", userId);
                return;
            }

            // Text for text messages, Caption for photos/documents, null for anything else.
            string? text = incoming.Text ?? incoming.Caption;

            if (string.IsNullOrWhiteSpace(text))
            {
                await _botService.SendTextAsync(
                    chatId,
                    "Only text messages are supported. Type /help to see the available commands.",
                    cancellationToken);
                return;
            }

            if (IsCommand(text, out string command))
            {
                await HandleCommandAsync(command, incoming, text, cancellationToken);
                return;
            }

            await HandleRegularTextAsync(incoming, text, chatId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Update processing cancelled during shutdown.");
            throw; // Let the host shut down cleanly.
        }
        catch (Exception ex)
        {
            // A failed update must not kill the polling loop: log it and (best effort) tell the user.
            _logger.LogError(ex, "Failed to process update {UpdateId} from chat {ChatId}.",
                update.Id, chatId);

            try
            {
                await _botService.SendTextAsync(chatId, "Something went wrong. Please try again later.", cancellationToken);
            }
            catch
            {
                // Never let the error notification mask the original failure.
            }
        }
    }

    /// <summary>Central error callback of the polling loop.</summary>
    public Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, HandleErrorSource source, CancellationToken cancellationToken)
    {
        _logger.LogError(exception, "Polling error from source {Source}.", source);
        return Task.CompletedTask;
    }
    /// <summary>Stores a user message and relays it to the admin.</summary>
    private async Task HandleRegularTextAsync(
        TelegramMessage incoming,
        string text,
        long chatId,
        CancellationToken cancellationToken)
    {
        // Do not echo the admin's own messages back to the admin. They are still persisted,
        // so the database holds the complete conversation.
        if (chatId == _config.AdminChatId)
        {
            await _messageService.StoreAsync(
                senderChatId: chatId,
                receiverChatId: chatId,
                text: text,
                isDelivered: true, // Nothing to deliver: the admin is the sender.
                cancellationToken: cancellationToken);
            return;
        }

        string userLabel = incoming.From is { } from
            ? $"{from.FirstName} (@{from.Username})".TrimEnd(' ', '(')
            : $"chat {chatId}";

        string forwarded =
            $"<b>New message</b>\n" +
            $"From: {EscapeHtml(userLabel)}\n" +
            $"Chat id: <code>{chatId}</code>\n" +
            $"At: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC\n\n" +
            $"{EscapeHtml(text)}\n\n" +
            $"Reply with: <code>/send {chatId} your message</code>";

        var stored = await _messageService.StoreAndSendAsync(
            senderChatId: chatId,
            receiverChatId: _config.AdminChatId,
            text: forwarded,
            cancellationToken: cancellationToken);

        // Let the user know their message reached the admin.
        await _botService.SendTextAsync(
            chatId,
            stored.IsDelivered
                ? "Your message has been forwarded to the admin."
                : "Your message could not be forwarded right now. Please try again later.",
            cancellationToken);
    }

    private async Task HandleCommandAsync(
        string command,
        TelegramMessage incoming,
        string fullText,
        CancellationToken cancellationToken)
    {
        long chatId = incoming.Chat.Id;

        switch (command)
        {
            case StartCommand:
                await _botService.SendTextAsync(chatId, BuildWelcomeText(), cancellationToken);
                return;

            case HelpCommand:
                await _botService.SendTextAsync(chatId, BuildHelpText(), cancellationToken);
                return;

            case SendCommand:
                await HandleSendCommandAsync(incoming, fullText, cancellationToken);
                return;

            default:
                await _botService.SendTextAsync(
                    chatId,
                    $"Unknown command '{EscapeHtml(command)}'.\n\n{BuildHelpText()}",
                    cancellationToken);
                return;
        }
    }

    /// <summary>
    /// <c>/send &lt;chatId&gt; &lt;message&gt;</c> — restricted to the admin chat defined in
    /// <c>ADMIN_CHAT_ID</c>, so regular users cannot make the bot write to arbitrary chats.
    /// </summary>
    private async Task HandleSendCommandAsync(
        TelegramMessage incoming,
        string fullText,
        CancellationToken cancellationToken)
    {
        long chatId = incoming.Chat.Id;

        if (chatId != _config.AdminChatId)
        {
            _logger.LogWarning(
                "Rejected '{SendCommand}' from non-admin chat {ChatId}.", SendCommand, chatId);
            await _botService.SendTextAsync(
                chatId, "This command is only available to the bot administrator.", cancellationToken);
            return;
        }

        // "/send@BotName 12345 hello world" -> command, chatId, message (message may contain spaces).
        string[] parts = fullText.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length < 2)
        {
            await _botService.SendTextAsync(chatId, "Usage: <code>/send &lt;chatId&gt; &lt;message&gt;</code>", cancellationToken);
            return;
        }

        string[] targetAndText = parts[1].Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (targetAndText.Length < 2 || !long.TryParse(targetAndText[0], out long targetChatId))
        {
            await _botService.SendTextAsync(
                chatId,
                "Invalid command. Usage: <code>/send &lt;chatId&gt; &lt;message&gt;</code>\n" +
                "Example: <code>/send 123456789 Hello from the bot</code>",
                cancellationToken);
            return;
        }

        string textToSend = targetAndText[1];

        // Persist the admin -> user message as well, so both directions are in the DB.
        var stored = await _messageService.StoreAndSendAsync(
            senderChatId: chatId,
            receiverChatId: targetChatId,
            text: textToSend,
            cancellationToken: cancellationToken);

        await _botService.SendTextAsync(
            chatId,
            stored.IsDelivered
                ? $"Delivered to chat {targetChatId} (message #{stored.Id})."
                : $"Failed to deliver to chat {targetChatId} (message #{stored.Id} kept in the database).",
            cancellationToken);
    }

    /// <summary>
    /// Detects a command and returns it normalised. Handles both <c>/send</c> and
    /// <c>/send@MyBot</c> (the form Telegram delivers in group chats).
    /// </summary>
    private static bool IsCommand(string text, out string command)
    {
        command = string.Empty;

        if (text.Length == 0 || text[0] != '/')
        {
            return false;
        }

        string firstToken = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0];
        int atIndex = firstToken.IndexOf('@');
        command = atIndex > 0 ? firstToken[..atIndex] : firstToken;

        return command.Length > 1;
    }

    private string BuildWelcomeText() =>
        "Hi! I forward your messages to the admin and let the admin reply to you.\n\n" +
        "Just send a text message.\nType /help for the details.";

    private string BuildHelpText() =>
        "<b>Commands</b>\n" +
        "/start - show the welcome message\n" +
        "/help - show this help\n" +
        "/send &lt;chatId&gt; &lt;message&gt; - admin only: send a message to a chat\n\n" +
        "<b>Notes</b>\n" +
        "Everything is stored in a local SQLite database (table <code>Messages</code>).";

    /// <summary>
    /// Minimal HTML escaping. Only <c>&amp;</c>, <c>&lt;</c> and <c>&gt;</c> are replaced, so
    /// emojis and other unicode characters survive unchanged.
    /// </summary>
    private static string EscapeHtml(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
             .Replace("<", "&lt;", StringComparison.Ordinal)
             .Replace(">", "&gt;", StringComparison.Ordinal);
}
