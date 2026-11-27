using InstaSaaS.Domain;
using Microsoft.EntityFrameworkCore;

namespace InstaSaaS.Infrastructure;

public interface ICurrentTenant { Guid? TenantId { get; } }   // filled from JWT claim "tid"

public class AppDbContext : DbContext
{
    private readonly ICurrentTenant _tenant;
    public AppDbContext(DbContextOptions<AppDbContext> options, ICurrentTenant tenant) : base(options) => _tenant = tenant;

    public DbSet<User> Users => Set<User>();
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TenantMember> TenantMembers => Set<TenantMember>();
    public DbSet<TenantInvite> TenantInvites => Set<TenantInvite>();
    public DbSet<OtpCode> OtpCodes => Set<OtpCode>();
    public DbSet<Plan> Plans => Set<Plan>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<PaymentTransaction> Transactions => Set<PaymentTransaction>();
    public DbSet<InstagramAccount> InstagramAccounts => Set<InstagramAccount>();
    public DbSet<Automation> Automations => Set<Automation>();
    public DbSet<Response> Responses => Set<Response>();
    public DbSet<PendingFollowUp> PendingFollowUps => Set<PendingFollowUp>();
    public DbSet<LogEntry> Logs => Set<LogEntry>();
    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();
    public DbSet<FollowCheck> FollowChecks => Set<FollowCheck>();
    public DbSet<ConnectSession> ConnectSessions => Set<ConnectSession>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // NOTE: keep this model provider-agnostic (no SQL Server column types) so the tests can run it on SQLite.
        b.Entity<User>(e => { e.Property(x => x.Phone).HasMaxLength(20); e.HasIndex(x => x.Phone).IsUnique(); });

        b.Entity<Tenant>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(60);
            e.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Plan).WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<TenantMember>(e =>
        {
            e.HasKey(x => new { x.TenantId, x.UserId });
            e.HasOne(x => x.Tenant).WithMany(t => t.Members).HasForeignKey(x => x.TenantId);
            e.HasOne(x => x.User).WithMany(u => u.Memberships).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<TenantInvite>(e => { e.Property(x => x.Phone).HasMaxLength(20); e.HasIndex(x => new { x.TenantId, x.Phone }); e.HasIndex(x => x.Phone); });
        b.Entity<OtpCode>(e => { e.Property(x => x.Phone).HasMaxLength(20); e.Property(x => x.CodeHash).HasMaxLength(200); e.HasIndex(x => new { x.Phone, x.CreatedAt }); });

        b.Entity<Plan>(e => e.Property(x => x.Name).HasMaxLength(100));
        b.Entity<Invoice>(e => e.HasIndex(x => x.TenantId));
        b.Entity<PaymentTransaction>(e =>
        {
            e.Property(x => x.Gateway).HasMaxLength(50);
            e.Property(x => x.Authority).HasMaxLength(100);
            e.Property(x => x.RefId).HasMaxLength(100);
            e.HasIndex(x => x.Authority);
            e.HasOne<Invoice>().WithMany(i => i.Transactions).HasForeignKey(x => x.InvoiceId);
        });

        b.Entity<InstagramAccount>(e =>
        {
            e.Property(x => x.Username).HasMaxLength(100);
            e.Property(x => x.InstagramUserId).HasMaxLength(50);
            e.HasIndex(x => new { x.TenantId, x.Username }).IsUnique();
            e.HasIndex(x => x.BoxAccountId).IsUnique();
            e.HasOne<Tenant>().WithMany(t => t.Accounts).HasForeignKey(x => x.TenantId);
        });

        b.Entity<Automation>(e =>
        {
            e.Property(x => x.Keywords).HasMaxLength(2500);      // up to 20 keywords x 100 chars + separators
            e.Property(x => x.FollowUpText).HasMaxLength(1000);
            e.HasOne(x => x.Account).WithMany(a => a.Automations).HasForeignKey(x => x.AccountId);
            e.HasIndex(x => new { x.AccountId, x.TriggerType, x.IsActive });
        });
        b.Entity<Response>(e => e.HasOne<Automation>().WithMany(a => a.Responses).HasForeignKey(x => x.AutomationId));

        b.Entity<PendingFollowUp>(e => { e.Property(x => x.IgUserId).HasMaxLength(100); e.HasIndex(x => new { x.FollowUpSent, x.Replied, x.DueAt }); });
        b.Entity<LogEntry>(e => { e.Property(x => x.EventType).HasMaxLength(50); e.Property(x => x.Status).HasMaxLength(30); e.HasIndex(x => new { x.AccountId, x.CreatedAt }); });

        b.Entity<ProcessedEvent>(e => { e.HasKey(x => x.Id); e.Property(x => x.Id).HasMaxLength(200); e.HasIndex(x => x.CreatedAt); });
        b.Entity<FollowCheck>(e =>
        {
            e.Property(x => x.SenderIgId).HasMaxLength(50);
            e.Property(x => x.SenderUsername).HasMaxLength(100);
            e.Property(x => x.CommentId).HasMaxLength(100);
            e.Property(x => x.Text).HasMaxLength(2200);
            e.HasIndex(x => new { x.AccountId, x.SenderIgId, x.Done, x.CreatedAt });
        });
        b.Entity<ConnectSession>();

        // Tenant isolation: global query filters. Webhook/background code has no tenant in context (TenantId == null,
        // so the filters let everything through) and must therefore scope by account/tenant explicitly.
        b.Entity<InstagramAccount>().HasQueryFilter(a => _tenant.TenantId == null || a.TenantId == _tenant.TenantId);
        b.Entity<Automation>().HasQueryFilter(a => _tenant.TenantId == null || a.Account.TenantId == _tenant.TenantId);
        b.Entity<Invoice>().HasQueryFilter(i => _tenant.TenantId == null || i.TenantId == _tenant.TenantId);
    }
}
