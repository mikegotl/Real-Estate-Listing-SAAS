using ListingStudio.Domain.Administration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ListingStudio.Infrastructure.Persistence.Configurations;

public sealed class OperationalEventConfiguration : IEntityTypeConfiguration<OperationalEvent>
{
    public void Configure(EntityTypeBuilder<OperationalEvent> builder)
    {
        builder.ToTable("OperationalEvents");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Kind).HasMaxLength(50).IsRequired();
        builder.Property(item => item.Category).HasMaxLength(100).IsRequired();
        builder.Property(item => item.Outcome).HasMaxLength(50).IsRequired();
        builder.HasIndex(item => new { item.OccurredAtUtc, item.Kind });
    }
}
