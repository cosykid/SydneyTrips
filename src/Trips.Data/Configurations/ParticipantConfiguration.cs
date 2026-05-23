using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Trips.Core.Domain;

namespace Trips.Data.Configurations;

internal sealed class ParticipantConfiguration : IEntityTypeConfiguration<Participant>
{
    public void Configure(EntityTypeBuilder<Participant> b)
    {
        b.ToTable("participants");
        b.HasKey(x => x.Id);

        b.Property(x => x.UserId).IsRequired();
        b.Property(x => x.TripId).IsRequired();
        b.Property(x => x.DisplayName).IsRequired().HasMaxLength(120);

        b.Property(x => x.Home)
            .HasColumnType("geometry(Point, 4326)")
            .IsRequired();

        b.Property(x => x.HasCar).IsRequired();
        b.Property(x => x.Seats).IsRequired();

        b.Property(x => x.WalkBudgetMins).IsRequired();
        b.Property(x => x.DetourToleranceMins).IsRequired();
        b.Property(x => x.FairnessWeight).IsRequired();

        b.HasMany(x => x.CandidateNodes)
            .WithOne()
            .HasForeignKey(c => c.ParticipantId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => x.TripId);
        b.HasIndex(x => x.UserId);
        b.HasIndex(x => x.Home).HasMethod("gist");
    }
}
