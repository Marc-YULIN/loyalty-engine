using Loyalty.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Loyalty.Infrastructure;

public sealed class LoyaltyDbContext(DbContextOptions<LoyaltyDbContext> options) : DbContext(options)
{
    public DbSet<Member> Members => Set<Member>();
    public DbSet<ActivityEvent> ActivityEvents => Set<ActivityEvent>();
    public DbSet<TierChange> TierChanges => Set<TierChange>();
    public DbSet<Wallet> Wallets => Set<Wallet>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();
    public DbSet<CashbackClaim> CashbackClaims => Set<CashbackClaim>();
    public DbSet<Campaign> Campaigns => Set<Campaign>();
    public DbSet<CampaignScore> CampaignScores => Set<CampaignScore>();
    public DbSet<CouponTemplate> CouponTemplates => Set<CouponTemplate>();
    public DbSet<CouponBatch> CouponBatches => Set<CouponBatch>();
    public DbSet<MemberCoupon> MemberCoupons => Set<MemberCoupon>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<decimal>().HaveConversion<MoneyConverter>();
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Member>().HasIndex(x => x.ExternalId).IsUnique();
        modelBuilder.Entity<ActivityEvent>().HasKey(x => x.EventId);
        modelBuilder.Entity<ActivityEvent>().HasIndex(x => new { x.MemberId, x.OccurredAt });
        modelBuilder.Entity<ActivityEvent>().HasIndex(x => x.OriginalEventId);
        modelBuilder.Entity<Wallet>().HasKey(x => x.MemberId);
        modelBuilder.Entity<Wallet>().Property(x => x.Version).IsConcurrencyToken();
        modelBuilder.Entity<CashbackClaim>().HasKey(x => new { x.MemberId, x.IdempotencyKey });
        modelBuilder.Entity<CashbackClaim>().HasIndex(x => new { x.MemberId, x.RequestedAt });
        modelBuilder.Entity<CampaignScore>().HasKey(x => new { x.CampaignId, x.MemberId });
        modelBuilder.Entity<LedgerEntry>().HasIndex(x => new { x.MemberId, x.CreatedAt });
        modelBuilder.Entity<LedgerEntry>().HasIndex(x => new { x.RefId, x.Type });
        modelBuilder.Entity<TierChange>().HasIndex(x => new { x.MemberId, x.At });
        modelBuilder.Entity<MemberCoupon>().HasIndex(x => new { x.BatchId, x.MemberId }).IsUnique();
        modelBuilder.Entity<MemberCoupon>().HasIndex(x => new { x.MemberId, x.Status });
        modelBuilder.Entity<MemberCoupon>().HasOne(x => x.Template).WithMany().HasForeignKey(x => x.TemplateId);
        modelBuilder.Entity<Wallet>().HasOne<Member>().WithOne().HasForeignKey<Wallet>(x => x.MemberId);
        modelBuilder.Entity<ActivityEvent>().HasOne<Member>().WithMany().HasForeignKey(x => x.MemberId);
        modelBuilder.Entity<TierChange>().HasOne<Member>().WithMany().HasForeignKey(x => x.MemberId);
        modelBuilder.Entity<LedgerEntry>().HasOne<Member>().WithMany().HasForeignKey(x => x.MemberId);
        modelBuilder.Entity<CashbackClaim>().HasOne<Member>().WithMany().HasForeignKey(x => x.MemberId);
        modelBuilder.Entity<CampaignScore>().HasOne<Campaign>().WithMany().HasForeignKey(x => x.CampaignId);
        modelBuilder.Entity<CampaignScore>().HasOne<Member>().WithMany().HasForeignKey(x => x.MemberId);
        modelBuilder.Entity<CouponBatch>().HasOne<CouponTemplate>().WithMany().HasForeignKey(x => x.TemplateId);
        modelBuilder.Entity<MemberCoupon>().HasOne<CouponBatch>().WithMany().HasForeignKey(x => x.BatchId);
        modelBuilder.Entity<MemberCoupon>().HasOne<Member>().WithMany().HasForeignKey(x => x.MemberId);
    }
}

public sealed class MoneyConverter() : ValueConverter<decimal, long>(x => MoneyStorage.Encode(x), x => x / 10_000m);
public sealed class UtcTimeConverter() : ValueConverter<DateTimeOffset, long>(x => x.UtcTicks, x => new DateTimeOffset(x, TimeSpan.Zero));

public static class MoneyStorage
{
    public const decimal Maximum = 922_337_203_685_477m;

    public static long Encode(decimal value)
    {
        Validate(value);
        return checked((long)(value * 10_000m));
    }

    public static void Validate(decimal value)
    {
        if (value > Maximum || value < -Maximum || decimal.Round(value, 4) != value)
            throw new DomainException("invalid_amount", "Amounts must fit fixed-point storage with at most four decimal places.");
    }
}
