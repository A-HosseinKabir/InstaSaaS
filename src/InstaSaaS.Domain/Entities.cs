// Domain entities (.NET 8, EF Core, SQL Server) - consolidated from phases 1-8 with all patches applied.
// Multi-tenant: every tenant-owned row carries TenantId (or reaches it via a parent).
namespace InstaSaaS.Domain;

public enum TenantRole { Owner = 1, Admin = 2, Viewer = 3 }
public enum SubscriptionStatus { Trial = 0, Active = 1, Expired = 2, Cancelled = 3 }
public enum TriggerType { Comment = 1, Dm = 2 }
public enum MatchType { Exact = 1, Contains = 2 }
public enum MessageType { Text = 1, Image = 2, Button = 3 }
public enum InvoiceStatus { Pending = 0, Paid = 1, Failed = 2 }

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Phone { get; set; } = default!;          // normalized 09xxxxxxxxx
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<TenantMember> Memberships { get; set; } = new List<TenantMember>();
}

public class Tenant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? Name { get; set; }                       // display label for the tenant switcher
    public Guid OwnerId { get; set; }
    public User Owner { get; set; } = default!;
    public Guid? PlanId { get; set; }
    public Plan? Plan { get; set; }
    public SubscriptionStatus SubscriptionStatus { get; set; } = SubscriptionStatus.Trial;
    public DateTime? ExpireDate { get; set; }
    public ICollection<TenantMember> Members { get; set; } = new List<TenantMember>();
    public ICollection<InstagramAccount> Accounts { get; set; } = new List<InstagramAccount>();
}

// Role lives HERE (per tenant), not on users: one phone can be Owner of one tenant and Viewer of another.
public class TenantMember
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = default!;
    public Guid UserId { get; set; }
    public User User { get; set; } = default!;
    public TenantRole Role { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// Invite by phone. Accepted explicitly by the invited user (never automatically).
public class TenantInvite
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Phone { get; set; } = default!;
    public TenantRole Role { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? AcceptedAt { get; set; }
}

public class OtpCode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Phone { get; set; } = default!;
    public string CodeHash { get; set; } = default!;       // never store the raw code
    public DateTime ExpiresAt { get; set; }
    public int Attempts { get; set; }
    public bool Used { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Plan
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = default!;
    public long PriceRial { get; set; }
    public int DurationDays { get; set; }
    public int MaxInstagramAccounts { get; set; }
    public int MaxAutomations { get; set; }
    public bool IsActive { get; set; } = true;
}

public class Invoice
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid PlanId { get; set; }
    public long AmountRial { get; set; }
    public InvoiceStatus Status { get; set; } = InvoiceStatus.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<PaymentTransaction> Transactions { get; set; } = new List<PaymentTransaction>();
}

public class PaymentTransaction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InvoiceId { get; set; }
    public string Gateway { get; set; } = default!;
    public string? Authority { get; set; }                 // gateway trackId
    public string? RefId { get; set; }                     // gateway reference after verify
    public bool Success { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// A customer's Instagram page connected through BoxAPI's official login link.
// We store NO Instagram credentials/tokens: BoxAPI holds the access, we only keep its account_id.
public class InstagramAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid BoxAccountId { get; set; }                 // BoxAPI account_id (unique)
    public string InstagramUserId { get; set; } = default!;
    public string Username { get; set; } = default!;
    public bool IsActive { get; set; } = true;
    public DateTime? ExpiresAt { get; set; }               // after expiry the user must log in again
    public ICollection<Automation> Automations { get; set; } = new List<Automation>();
}

public class Automation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public InstagramAccount Account { get; set; } = default!;
    public TriggerType TriggerType { get; set; }
    public string Keywords { get; set; } = default!;       // comma-separated, normalized
    public MatchType MatchType { get; set; }
    public int Priority { get; set; }                      // lower = checked first
    public int? FollowUpAfterHours { get; set; }           // null = no follow-up
    public string? FollowUpText { get; set; }
    public bool IsActive { get; set; } = true;
    public ICollection<Response> Responses { get; set; } = new List<Response>();
}

public class Response
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AutomationId { get; set; }
    public MessageType MessageType { get; set; }
    public string Content { get; set; } = default!;        // text, image URL, or button JSON
    public bool RequireFollow { get; set; }                // follow-gating
    public int SortOrder { get; set; }
}

// Conversations awaiting a user reply, for the follow-up worker.
public class PendingFollowUp
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public Guid AutomationId { get; set; }
    public string IgUserId { get; set; } = default!;
    public DateTime SentAt { get; set; }
    public DateTime DueAt { get; set; }
    public bool Replied { get; set; }
    public bool FollowUpSent { get; set; }
    public int Attempts { get; set; }
}

public class LogEntry
{
    public long Id { get; set; }
    public Guid AccountId { get; set; }
    public string EventType { get; set; } = default!;
    public string Status { get; set; } = default!;
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// Webhook idempotency (providers retry): key = "{accountId}:{externalEventId}"
public class ProcessedEvent
{
    public string Id { get; set; } = default!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// BoxAPI's follow_status is ASYNC (result comes by webhook), so a half-finished conversation is persisted and resumed.
public class FollowCheck
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public Guid AutomationId { get; set; }
    public string SenderIgId { get; set; } = default!;
    public string SenderUsername { get; set; } = "";
    public TriggerType EventType { get; set; }
    public string? CommentId { get; set; }
    public string Text { get; set; } = "";
    public int ResumeFromSort { get; set; }                // SortOrder of the first response that required follow
    public bool FirstSent { get; set; }                    // was the conversation already opened (private reply used)?
    public bool Done { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// Links a dashboard user to the Instagram login flow (id is sent to BoxAPI and returned unchanged on redirect).
public class ConnectSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public bool Completed { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
