using Microsoft.EntityFrameworkCore;
using TelegramBot.Models;

namespace TelegramBot.Data;

/// <summary>
/// EF Core context for the SQLite database. Table: <c>Messages</c>.
/// </summary>
public class BotDbContext : DbContext
{
    public BotDbContext(DbContextOptions<BotDbContext> options) : base(options)
    {
    }

    public DbSet<Message> Messages => Set<Message>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Message>(entity =>
        {
            entity.ToTable("Messages");
            entity.HasKey(m => m.Id);

            // Id is generated automatically by SQLite (AUTOINCREMENT).
            entity.Property(m => m.Id).ValueGeneratedOnAdd();

            entity.Property(m => m.SenderChatId).IsRequired();
            entity.Property(m => m.ReceiverChatId).IsRequired();
            entity.Property(m => m.MessageText).IsRequired().HasMaxLength(4096);

            // Always store timestamps in UTC so the DB is timezone independent.
            entity.Property(m => m.SentAt)
                  .IsRequired()
                  .HasConversion(
                      v => v.ToUniversalTime(),
                      v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

            entity.Property(m => m.IsDelivered)
                  .IsRequired()
                  .HasDefaultValue(false);

            // Useful for "show me the history of this user" queries.
            entity.HasIndex(m => m.SentAt);
            entity.HasIndex(m => m.SenderChatId);
        });
    }
}
