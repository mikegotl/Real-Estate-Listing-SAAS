using ListingStudio.Domain.Help;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ListingStudio.Infrastructure.Persistence.Configurations;

public sealed class HelpChatMessageConfiguration : IEntityTypeConfiguration<HelpChatMessage>
{
    public void Configure(EntityTypeBuilder<HelpChatMessage> builder)
    {
        builder.ToTable("HelpChatMessages");
        builder.HasKey(message => message.Id);
        builder.Property(message => message.UserId).HasMaxLength(450).IsRequired();
        builder.Property(message => message.Role).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(message => message.Text).HasMaxLength(HelpChatMessage.MaximumTextLength).IsRequired();
        builder.Property(message => message.PagePath).HasMaxLength(HelpChatMessage.MaximumPagePathLength);
        builder.HasIndex(message => new { message.OrganizationId, message.UserId, message.CreatedAtUtc });
        // Kept for the administrator review page, so there are no foreign keys that could delete it.
        builder.HasIndex(message => message.CreatedAtUtc);
        builder.HasIndex(message => message.QuestionId);
    }
}
