using ListingStudio.Domain.Administration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ListingStudio.Infrastructure.Persistence.Configurations;

public sealed class PlatformAdminAuditEventConfiguration : IEntityTypeConfiguration<PlatformAdminAuditEvent>
{
    public void Configure(EntityTypeBuilder<PlatformAdminAuditEvent> builder)
    {
        builder.ToTable("PlatformAdminAuditEvents");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Action).HasMaxLength(100).IsRequired();
        builder.Property(item => item.ActorType).HasMaxLength(50).IsRequired();
        builder.Property(item => item.TargetReference).HasMaxLength(64).IsRequired();
        builder.Property(item => item.Outcome).HasMaxLength(32).IsRequired();
        builder.Property(item => item.ActorReference).HasMaxLength(64);
        builder.HasIndex(item => item.OccurredAtUtc);
    }
}
