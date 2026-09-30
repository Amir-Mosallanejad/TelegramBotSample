using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TelegramBot.Data;

/// <summary>
/// Design-time factory used by the <c>dotnet ef</c> CLI tools.
/// <para>
/// Without it, EF tries to build the real application host (<c>Program.cs</c>) to resolve the
/// <see cref="BotDbContext"/>, which would require a valid <c>.env</c> file and would start the
/// polling loop. This factory keeps migrations independent from the bot runtime.
/// </para>
/// </summary>
public sealed class BotDbContextFactory : IDesignTimeDbContextFactory<BotDbContext>
{
    public BotDbContext CreateDbContext(string[] args)
    {
        // Same convention as the runtime configuration in Program.cs.
        string connectionString = "Data Source=bot.db";

        string? fromEnvironment = Environment.GetEnvironmentVariable("ConnectionStrings__BotDb");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            connectionString = fromEnvironment;
        }

        var optionsBuilder = new DbContextOptionsBuilder<BotDbContext>()
            .UseSqlite(connectionString);

        return new BotDbContext(optionsBuilder.Options);
    }
}
