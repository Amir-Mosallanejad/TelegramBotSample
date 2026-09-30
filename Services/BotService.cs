using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TelegramBot.Configuration;

namespace TelegramBot.Services;

/// <summary>
/// Owns the <see cref="TelegramBotClient"/> instance and wraps the long-polling loop.
/// <para>
/// Telegram.Bot 22.10 renamed the client (<c>TelegramClient</c> -> <c>TelegramBotClient</c>),
/// dropped the <c>Async</c> suffix from the API surface (<c>SendMessage</c>, <c>GetMe</c>) and
/// exposed the awaitable polling loop as <c>ReceiveAsync</c>. Shutdown is driven purely by the
/// <see cref="CancellationToken"/> - there is no <c>StopPolling</c> to call.
/// </para>
/// <para>
/// Long polling (not webhooks) is used, so the bot needs no public HTTPS endpoint.
/// </para>
/// </summary>
public sealed class BotService
{
    // Telegram allows 1 message per second per chat; 1.5s keeps us safely below the limit.
    private static readonly TimeSpan SendDelay = TimeSpan.FromSeconds(1.5);

    private readonly ITelegramBotClient _client;
    private readonly BotConfig _config;
    private readonly ILogger<BotService> _logger;

    public BotService(BotConfig config, ILogger<BotService> logger)
    {
        _config = config;
        _logger = logger;

        if (string.IsNullOrWhiteSpace(config.BaseUrl))
        {
            _client = new TelegramBotClient(config.Token);
        }
        else
        {
            // Custom endpoint: a local Bot API server, or a test double.
            _logger.LogInformation("Using custom Telegram API endpoint {BaseUrl}.", config.BaseUrl);
            _client = new TelegramBotClient(new TelegramBotClientOptions(config.Token, config.BaseUrl));
        }
    }

    /// <summary>Chat id of the admin, used to decide who may run privileged commands.</summary>
    public long AdminChatId => _config.AdminChatId;

    /// <summary>Validates the token and returns the bot username (proves the token works).</summary>
    public async Task<string> GetBotUsernameAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        User me = await _client.GetMe(timeout.Token);
        return me.Username ?? me.Id.ToString();
    }

    /// <summary>
    /// Sends a text message. Returns <c>true</c> when the Telegram API accepted it, so the caller
    /// can record the delivery state in the database.
    /// </summary>
    public async Task<bool> SendTextAsync(long chatId, string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        // Telegram rejects messages longer than 4096 characters.
        if (text.Length > 4096)
        {
            text = string.Concat(text.AsSpan(0, 4093), "...");
        }

        try
        {
            // Rate limiting: 1 message per second per chat.
            await Task.Delay(SendDelay, cancellationToken);

            // In 22.10 the chat id is wrapped in a ChatId instance (identifier or @username).
            await _client.SendMessage(
                chatId: new ChatId(chatId),
                text: text,
                parseMode: ParseMode.Html,
                linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
                cancellationToken: cancellationToken);

            _logger.LogInformation("Message sent to chat {ChatId}.", chatId);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // Normal shutdown path, not a failure.
        }
        catch (Exception ex)
        {
            LogSendError(ex, chatId);
            return false;
        }
    }

    /// <summary>
    /// Runs the long-polling loop until <paramref name="cancellationToken"/> is cancelled.
    /// The token is owned by the host, which cancels it on Ctrl+C (SIGINT) or process exit (SIGTERM).
    /// </summary>
    public async Task ReceiveUpdatesAsync(IUpdateHandler updateHandler, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting long polling...");

        var receiverOptions = new ReceiverOptions
        {
            // Only message updates matter for this bot (plain text, commands and captions).
            AllowedUpdates = [UpdateType.Message],
            DropPendingUpdates = false
        };

        await _client.ReceiveAsync(updateHandler, receiverOptions, cancellationToken);
    }

    /// <summary>Central error sink so one bad update never kills the polling loop.</summary>
    private void LogSendError(Exception exception, long chatId)
    {
        if (exception is ApiRequestException apiException)
        {
            _logger.LogError(
                apiException,
                "Telegram API rejected the message for chat {ChatId} (error {ErrorCode}, HTTP {StatusCode}): {Reason}",
                chatId, apiException.ErrorCode, apiException.HttpStatusCode, apiException.Message);
        }
        else
        {
            _logger.LogError(exception, "Failed to send a message to chat {ChatId}.", chatId);
        }
    }
}
