using Microsoft.Extensions.Configuration;

namespace TelegramBot.Configuration;

/// <summary>
/// Strongly typed view over the two values the bot needs, both loaded from the <c>.env</c> file:
/// <c>BOT_TOKEN</c> and <c>ADMIN_CHAT_ID</c>.
/// </summary>
public sealed class BotConfig
{
    public const string BotTokenKey = "BOT_TOKEN";
    public const string AdminChatIdKey = "ADMIN_CHAT_ID";
    public const string BaseUrlKey = "BOT_API_BASE_URL";

    /// <summary>Telegram bot token issued by @BotFather.</summary>
    public required string Token { get; init; }

    /// <summary>Chat id of the administrator that receives forwarded user messages.</summary>
    public required long AdminChatId { get; init; }

    /// <summary>
    /// Optional API endpoint. Leave unset to talk to <c>https://api.telegram.org</c>.
    /// Set it to run against a local Bot API server (or a mock) for testing.
    /// </summary>
    public string? BaseUrl { get; init; }

    /// <summary>
    /// Builds the configuration from any <see cref="IConfiguration"/> source.
    /// Because <c>DotNetEnv</c> copies the <c>.env</c> entries into the process environment,
    /// <c>AddEnvironmentVariables()</c> exposes them as regular configuration keys.
    /// </summary>
    public static BotConfig FromConfiguration(IConfiguration configuration)
    {
        string? rawToken = configuration[BotTokenKey];
        string? rawAdminChatId = configuration[AdminChatIdKey];

        if (string.IsNullOrWhiteSpace(rawToken))
        {
            throw new InvalidOperationException(
                $"Missing '{BotTokenKey}' in the .env file. Copy .env.example to .env and fill in " +
                "the token from @BotFather (e.g. 123456789:AAExampleToken).");
        }

        if (string.IsNullOrWhiteSpace(rawAdminChatId))
        {
            throw new InvalidOperationException(
                $"Missing '{AdminChatIdKey}' in the .env file. Send /start to @userinfobot to " +
                "find your chat id (e.g. 987654321).");
        }

        if (!long.TryParse(rawAdminChatId.Trim(), out long adminChatId))
        {
            throw new InvalidOperationException(
                $"'{AdminChatIdKey}' must be a 64-bit integer chat id, but was '{rawAdminChatId}'.");
        }

        return new BotConfig
        {
            Token = rawToken.Trim(),
            AdminChatId = adminChatId,

            // Optional: only set when using a local Bot API server or a test double.
            BaseUrl = string.IsNullOrWhiteSpace(configuration[BaseUrlKey])
                ? null
                : configuration[BaseUrlKey]!.Trim()
        };
    }
}
