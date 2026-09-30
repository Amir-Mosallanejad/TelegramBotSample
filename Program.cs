using DotNetEnv;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TelegramBot.Configuration;
using TelegramBot.Data;
using TelegramBot.Handlers;
using TelegramBot.Services;

// ---------------------------------------------------------------------------
// 1. Load the .env file into the process environment.
//    This must happen before the host builder reads configuration, because
//    AddEnvironmentVariables() below picks the values up from there.
// ---------------------------------------------------------------------------
LoadEnvironmentFile();

// ---------------------------------------------------------------------------
// 2. Build the host: configuration, logging, dependency injection.
// ---------------------------------------------------------------------------
var builder = Host.CreateApplicationBuilder(args);

// Telegram.Bot logs every request/response at Information level, which is far too noisy.
builder.Logging.AddFilter("Telegram.Bot", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
builder.Logging.SetMinimumLevel(LogLevel.Information);

// Database. AddDbContextFactory keeps BotDbContext short-lived and thread-safe for
// our singleton services; the bot.db file is created in the working directory.
const string ConnectionString = "Data Source=bot.db";

builder.Services.AddDbContextFactory<BotDbContext>(options =>
    options.UseSqlite(
        connectionString: ConnectionString,
        sqliteOptions => sqliteOptions.MigrationsAssembly(typeof(BotDbContext).Assembly.FullName)));

// Application services.
// BotConfig is validated on first resolution (right after the host is built) so that
// "dotnet ef" can still build the host without a real token in .env.
builder.Services.AddSingleton(sp => BotConfig.FromConfiguration(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<BotService>();
builder.Services.AddSingleton<MessageService>();
builder.Services.AddSingleton<UpdateHandler>();
builder.Services.AddHostedService<PollingBackgroundService>();

using var host = builder.Build();

// Fails fast with an actionable message if BOT_TOKEN / ADMIN_CHAT_ID are missing or invalid.
var botConfig = host.Services.GetRequiredService<BotConfig>();

// ---------------------------------------------------------------------------
// 3. Apply pending migrations before accepting updates.
// ---------------------------------------------------------------------------
using (var scope = host.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    await ApplyMigrationsAsync(scope.ServiceProvider, logger);
}

// ---------------------------------------------------------------------------
// 4. Validate the token once at startup and print the bot identity.
// ---------------------------------------------------------------------------
var startupLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

try
{
    var botService = host.Services.GetRequiredService<BotService>();
    string username = await botService.GetBotUsernameAsync();
    startupLogger.LogInformation("Bot is running as @{Username}.", username);
    startupLogger.LogInformation("Admin chat id: {AdminChatId}", botConfig.AdminChatId);
}
catch (Exception ex)
{
    startupLogger.LogError(
        ex, "Could not reach the Telegram API. Check BOT_TOKEN and your internet connection.");
    throw;
}

// ---------------------------------------------------------------------------
// 5. Run until Ctrl+C (SIGINT) or process termination (SIGTERM).
// ---------------------------------------------------------------------------
await host.RunAsync();
return;

// ===========================================================================
// Helper methods
// ===========================================================================

static void LoadEnvironmentFile()
{
    try
    {
        // Env.Load() parses .env and copies the entries into Environment variables.
        // Real environment variables win over .env (clobberExistingVars: false).
        Env.Load();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Warning: .env could not be loaded ({ex.Message}).");
    }
}

static async Task ApplyMigrationsAsync(IServiceProvider serviceProvider, ILogger logger)
{
    var factory = serviceProvider.GetRequiredService<IDbContextFactory<BotDbContext>>();

    try
    {
        await using var dbContext = await factory.CreateDbContextAsync();

        var pending = (await dbContext.Database.GetPendingMigrationsAsync()).ToList();
        if (pending.Count == 0)
        {
            logger.LogInformation("Database is up to date (no pending migrations).");
            return;
        }

        logger.LogInformation("Applying {Count} pending migration(s)...", pending.Count);
        await dbContext.Database.MigrateAsync();
        logger.LogInformation("Database migrated successfully.");
    }
    catch (Exception ex)
    {
        logger.LogError(
            ex,
            "Could not migrate the database. If bot.db was created by an older schema, delete it and " +
            "run: dotnet ef database update");
        throw;
    }
}

/// <summary>
/// Runs <see cref="BotService.ReceiveUpdatesAsync"/> for the whole lifetime of the host.
/// <para>
/// <see cref="BackgroundService"/> supplies a cancellation token that the host triggers during
/// shutdown and waits for <see cref="ExecuteAsync"/> to return, which gives us graceful polling
/// shutdown: the receive loop exits, no further requests are started.
/// </para>
/// </summary>
internal sealed class PollingBackgroundService : BackgroundService
{
    private readonly BotService _botService;
    private readonly UpdateHandler _updateHandler;
    private readonly ILogger<PollingBackgroundService> _logger;

    public PollingBackgroundService(
        BotService botService,
        UpdateHandler updateHandler,
        ILogger<PollingBackgroundService> logger)
    {
        _botService = botService;
        _updateHandler = updateHandler;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Polling worker started. Press Ctrl+C to stop the bot.");

        try
        {
            await _botService.ReceiveUpdatesAsync(_updateHandler, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Polling stopped: shutdown requested.");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Polling stopped because of an unrecoverable error.");
            throw;
        }
    }
}
