using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using QcTrace.Web.Data.Entities;

namespace QcTrace.Web.Data;

public class QcTraceDbContext(DbContextOptions<QcTraceDbContext> options)
    : IdentityDbContext<AppUser>(options)
{
    public DbSet<Item> Items => Set<Item>();
    public DbSet<InspectionItem> InspectionItems => Set<InspectionItem>();
    public DbSet<Lot> Lots => Set<Lot>();
    public DbSet<LotQuantity> LotQuantities => Set<LotQuantity>();
    public DbSet<Inspection> Inspections => Set<Inspection>();
    public DbSet<ShipmentApproval> ShipmentApprovals => Set<ShipmentApproval>();
    public DbSet<Shipment> Shipments => Set<Shipment>();
    public DbSet<LedgerEntry> Ledger => Set<LedgerEntry>();
    public DbSet<QcRequest> QcRequests => Set<QcRequest>();

    private const string UtcNow = "SYSUTCDATETIME()";

    /// <summary>추가만 허용하는 테이블. UPDATE·DELETE 거부 트리거는 AppendOnlyTriggers 마이그레이션에서 만든다.</summary>
    public static readonly string[] AppendOnlyTables = ["Inspections", "ShipmentApprovals", "Shipments", "Ledger"];

    public static string AppendOnlyTriggerName(string table) => $"TR_{table}_AppendOnly";

    // DB의 시각은 모두 UTC다. 읽을 때 Kind=Utc를 붙여 JSON에 "Z"가 나가도록 한다.
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<NullableUtcDateTimeConverter>();
    }

    private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
        v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private sealed class NullableUtcDateTimeConverter() : ValueConverter<DateTime?, DateTime?>(
        v => v, v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        // Identity 테이블 이름을 기획서(6장)의 Users / Roles에 맞춘다
        b.Entity<AppUser>(e =>
        {
            e.ToTable("Users");
            e.Property(x => x.DisplayName).HasMaxLength(50);
        });
        b.Entity<IdentityRole>().ToTable("Roles");
        b.Entity<IdentityUserRole<string>>().ToTable("UserRoles");
        b.Entity<IdentityUserClaim<string>>().ToTable("UserClaims");
        b.Entity<IdentityUserLogin<string>>().ToTable("UserLogins");
        b.Entity<IdentityUserToken<string>>().ToTable("UserTokens");
        b.Entity<IdentityRoleClaim<string>>().ToTable("RoleClaims");

        b.Entity<Item>(e =>
        {
            e.Property(x => x.ItemCode).HasMaxLength(20);
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Spec).HasMaxLength(200);
            e.HasIndex(x => x.ItemCode).IsUnique();
        });

        b.Entity<InspectionItem>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(50);
            e.Property(x => x.Unit).HasMaxLength(20);
            e.Property(x => x.Lower).HasPrecision(18, 4);
            e.Property(x => x.Upper).HasPrecision(18, 4);
            e.HasOne(x => x.Item).WithMany(x => x.InspectionItems)
                .HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.ItemId, x.Name }).IsUnique();
            e.ToTable(t => t.HasCheckConstraint("CK_InspectionItems_Range", "[Lower] <= [Upper]"));
        });

        b.Entity<Lot>(e =>
        {
            e.Property(x => x.LotNo).HasMaxLength(30);
            e.Property(x => x.Shift).HasMaxLength(20);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.CreatedAt).HasDefaultValueSql(UtcNow);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasIndex(x => x.LotNo).IsUnique();
            e.HasIndex(x => new { x.Status, x.ProducedOn });
            e.HasOne(x => x.Item).WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
            UserFk(e, x => x.CreatedById);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Lots_ProducedQty", "[ProducedQty] > 0");
                t.HasCheckConstraint("CK_Lots_Status",
                    $"[Status] IN ({string.Join(", ", LotStatus.All.Select(s => $"'{s}'"))})");
            });
        });

        b.Entity<LotQuantity>(e =>
        {
            e.HasKey(x => x.LotId);
            e.Property(x => x.UpdatedAt).HasDefaultValueSql(UtcNow);
            // SP·시드 스크립트가 열을 생략해도 0이 들어가게 한다
            e.Property(x => x.Scrapped).HasDefaultValue(0);
            e.HasOne(x => x.Lot).WithOne(x => x.Quantity)
                .HasForeignKey<LotQuantity>(x => x.LotId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("CK_LotQuantities_NonNegative",
                "[Uninspected] >= 0 AND [Passed] >= 0 AND [Failed] >= 0 AND [Held] >= 0 AND [Shipped] >= 0 AND [Reserved] >= 0 AND [Scrapped] >= 0"));
        });

        b.Entity<Inspection>(e =>
        {
            e.Property(x => x.Verdict).HasMaxLength(10);
            e.Property(x => x.Reason).HasMaxLength(500);
            e.Property(x => x.CreatedAt).HasDefaultValueSql(UtcNow);
            e.HasIndex(x => new { x.LotId, x.CreatedAt });
            LotFk(e, x => x.LotId);
            UserFk(e, x => x.InspectorId);
            e.HasOne<Inspection>().WithMany().HasForeignKey(x => x.CorrectsInspectionId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t =>
            {
                t.HasTrigger(AppendOnlyTriggerName("Inspections"));
                t.HasCheckConstraint("CK_Inspections_Qty", "[Qty] > 0");
                t.HasCheckConstraint("CK_Inspections_Verdict", "[Verdict] IN ('Pass', 'Fail', 'Hold')");
                // R4: 불합격은 사유 필수 (SP에서도 검사하고, DB가 마지막으로 막는다)
                t.HasCheckConstraint("CK_Inspections_FailReason",
                    "[Verdict] <> 'Fail' OR ([Reason] IS NOT NULL AND LEN(LTRIM(RTRIM([Reason]))) > 0)");
            });
        });

        b.Entity<ShipmentApproval>(e =>
        {
            e.Property(x => x.Decision).HasMaxLength(10);
            e.Property(x => x.Reason).HasMaxLength(500);
            e.Property(x => x.CreatedAt).HasDefaultValueSql(UtcNow);
            e.HasIndex(x => new { x.LotId, x.CreatedAt });
            LotFk(e, x => x.LotId);
            UserFk(e, x => x.ApproverId);
            e.ToTable(t =>
            {
                t.HasTrigger(AppendOnlyTriggerName("ShipmentApprovals"));
                t.HasCheckConstraint("CK_ShipmentApprovals_Qty", "[ApprovedQty] >= 0");
                t.HasCheckConstraint("CK_ShipmentApprovals_Decision", "[Decision] IN ('Approved', 'Rejected')");
            });
        });

        b.Entity<Shipment>(e =>
        {
            e.Property(x => x.Destination).HasMaxLength(100);
            e.Property(x => x.CreatedAt).HasDefaultValueSql(UtcNow);
            e.HasIndex(x => new { x.LotId, x.CreatedAt });
            e.HasIndex(x => x.CreatedAt);
            LotFk(e, x => x.LotId);
            UserFk(e, x => x.ShipperId);
            e.ToTable(t =>
            {
                t.HasTrigger(AppendOnlyTriggerName("Shipments"));
                t.HasCheckConstraint("CK_Shipments_Qty", "[Qty] > 0");
            });
        });

        b.Entity<LedgerEntry>(e =>
        {
            e.ToTable("Ledger", t =>
            {
                t.HasTrigger(AppendOnlyTriggerName("Ledger"));
                t.HasCheckConstraint("CK_Ledger_Kind", "[Kind] IN ('Produce', 'Request', 'Inspect', 'Correct', 'Reinspect', 'Scrap', 'Approve', 'Ship')");
                t.HasCheckConstraint("CK_Ledger_DeltaField",
                    "[DeltaField] IN ('Uninspected', 'Passed', 'Failed', 'Held', 'Shipped', 'Reserved', 'Scrapped')");
                // R10: 사유 필수
                t.HasCheckConstraint("CK_Ledger_Reason", "LEN(LTRIM(RTRIM([Reason]))) > 0");
            });
            e.Property(x => x.Kind).HasMaxLength(20);
            e.Property(x => x.DeltaField).HasMaxLength(20);
            e.Property(x => x.Reason).HasMaxLength(500);
            e.Property(x => x.SourceTable).HasMaxLength(50);
            e.Property(x => x.CreatedAt).HasDefaultValueSql(UtcNow);
            e.HasIndex(x => new { x.LotId, x.CreatedAt });
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => new { x.ActorId, x.CreatedAt });
            LotFk(e, x => x.LotId);
            UserFk(e, x => x.ActorId);
        });

        b.Entity<QcRequest>(e =>
        {
            e.Property(x => x.RequestKey).HasMaxLength(100);
            e.Property(x => x.Operation).HasMaxLength(50);
            e.Property(x => x.BodyHash).HasMaxLength(64).IsFixedLength();
            e.Property(x => x.CreatedAt).HasDefaultValueSql(UtcNow);
            e.HasIndex(x => x.RequestKey).IsUnique();
        });
    }

    private static void LotFk<T>(EntityTypeBuilder<T> e, System.Linq.Expressions.Expression<Func<T, object?>> fk)
        where T : class =>
        e.HasOne<Lot>().WithMany().HasForeignKey(fk).OnDelete(DeleteBehavior.Restrict);

    private static void UserFk<T>(EntityTypeBuilder<T> e, System.Linq.Expressions.Expression<Func<T, object?>> fk)
        where T : class =>
        e.HasOne<AppUser>().WithMany().HasForeignKey(fk).OnDelete(DeleteBehavior.Restrict);
}
