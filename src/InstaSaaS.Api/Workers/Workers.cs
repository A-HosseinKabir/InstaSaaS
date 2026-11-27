// Background workers: automatic follow-up + billing reconciliation
using InstaSaaS.Application.Billing;
using InstaSaaS.Domain;
using InstaSaaS.Engine;
using InstaSaaS.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace InstaSaaS.Api;

public class FollowUpWorker : BackgroundService
{
    private const int MaxAttempts = 3;
    private const int BatchSize = 50;
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(10);
    // Instagram only allows messaging a user within 24h of their last message; keep a safety margin.
    private static readonly TimeSpan MessagingWindow = TimeSpan.FromHours(23.5);
    private const string DefaultText = "سلام 👋 هنوز منتظر پاسخ شما هستیم. اگر سوالی دارید بپرسید.";

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<FollowUpWorker> _log;
    private DateTime _lastCleanup = DateTime.MinValue;

    public FollowUpWorker(IServiceScopeFactory scopes, ILogger<FollowUpWorker> log) { _scopes = scopes; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Tick);
        try
        {
            do
            {
                try { await RunOnceAsync(ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _log.LogError(ex, "Follow-up cycle failed"); }   // never let the loop die
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    // Public so tests can run one cycle deterministically.
    public async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ig = scope.ServiceProvider.GetRequiredService<IInstagramGateway>();
        var now = DateTime.UtcNow;

        var ids = await db.PendingFollowUps
            .Where(p => !p.FollowUpSent && !p.Replied && p.DueAt <= now)
            .OrderBy(p => p.DueAt).Select(p => p.Id).Take(BatchSize).ToListAsync(ct);

        foreach (var id in ids)
        {
            // Atomic claim: if two app instances run, only one gets claimed == 1, so no double-send.
            var claimed = await db.PendingFollowUps
                .Where(p => p.Id == id && !p.FollowUpSent && !p.Replied)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.FollowUpSent, true), ct);
            if (claimed == 0) continue;

            var p = await db.PendingFollowUps.AsNoTracking().FirstAsync(x => x.Id == id, ct);
            try
            {
                await SendOneAsync(db, ig, p, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                var attempts = p.Attempts + 1;
                if (attempts < MaxAttempts)
                {
                    // release the claim and retry later (compute the date OUTSIDE the lambda: EF can't translate DateTime.Add)
                    var retryAt = DateTime.UtcNow.Add(RetryDelay);
                    await db.PendingFollowUps.Where(x => x.Id == id).ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.FollowUpSent, false)
                        .SetProperty(x => x.Attempts, attempts)
                        .SetProperty(x => x.DueAt, retryAt), ct);
                }
                else
                {
                    await db.PendingFollowUps.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Attempts, attempts), ct);
                    await LogAsync(db, p.AccountId, "followup_error", ex.Message, ct);   // gave up
                }
                _log.LogWarning(ex, "Follow-up {Id} failed (attempt {N})", id, attempts);
            }
        }

        if (now - _lastCleanup > TimeSpan.FromHours(1)) { await CleanupAsync(db, now, ct); _lastCleanup = now; }
    }

    private static async Task SendOneAsync(AppDbContext db, IInstagramGateway ig, PendingFollowUp p, CancellationToken ct)
    {
        if (DateTime.UtcNow - p.SentAt > MessagingWindow)
        { await LogAsync(db, p.AccountId, "followup_skipped", "outside 24h messaging window", ct); return; }

        var account = await db.InstagramAccounts.IgnoreQueryFilters().FirstOrDefaultAsync(a => a.Id == p.AccountId, ct);
        var auto = await db.Automations.IgnoreQueryFilters().FirstOrDefaultAsync(a => a.Id == p.AutomationId, ct);
        if (account is null || !account.IsActive || auto is null || !auto.IsActive)
        { await LogAsync(db, p.AccountId, "followup_skipped", "account or automation inactive", ct); return; }

        var tenant = await db.Tenants.FirstAsync(t => t.Id == account.TenantId, ct);
        if (!SubscriptionRules.IsActive(tenant, DateTime.UtcNow))
        { await LogAsync(db, p.AccountId, "followup_skipped", "subscription inactive", ct); return; }

        await ig.SendDirectTextAsync(account, p.IgUserId, string.IsNullOrWhiteSpace(auto.FollowUpText) ? DefaultText : auto.FollowUpText!, ct);
        await LogAsync(db, p.AccountId, "followup_ok", null, ct);
    }

    private static async Task LogAsync(AppDbContext db, Guid accountId, string status, string? err, CancellationToken ct)
    {
        db.Logs.Add(new LogEntry { AccountId = accountId, EventType = "FollowUp", Status = status, ErrorMessage = err });
        await db.SaveChangesAsync(ct);
    }

    private static async Task CleanupAsync(AppDbContext db, DateTime now, CancellationToken ct)
    {
        await db.ProcessedEvents.Where(x => x.CreatedAt < now.AddDays(-7)).ExecuteDeleteAsync(ct);
        await db.FollowChecks.Where(x => x.CreatedAt < now.AddDays(-1)).ExecuteDeleteAsync(ct);
        await db.ConnectSessions.Where(x => x.CreatedAt < now.AddDays(-1)).ExecuteDeleteAsync(ct);
        await db.OtpCodes.Where(x => x.ExpiresAt < now.AddDays(-1)).ExecuteDeleteAsync(ct);
        await db.PendingFollowUps.Where(x => (x.FollowUpSent || x.Replied) && x.SentAt < now.AddDays(-30)).ExecuteDeleteAsync(ct);
        await db.Logs.Where(x => x.CreatedAt < now.AddDays(-30)).ExecuteDeleteAsync(ct);
    }
}

// Catches payments whose browser callback never arrived + keeps subscription statuses honest.
public class BillingWorker : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(10);
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<BillingWorker> _log;
    public BillingWorker(IServiceScopeFactory s, ILogger<BillingWorker> log) { _scopes = s; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Tick);
        try
        {
            do
            {
                try { await RunOnceAsync(ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _log.LogError(ex, "Billing cycle failed"); }
            } while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) { }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var billing = scope.ServiceProvider.GetRequiredService<BillingService>();
        var now = DateTime.UtcNow;

        // 1) Pending invoices older than 15 min (user may have paid then closed the browser): verify them.
        var tracks = await db.Transactions
            .Where(t => !t.Success && t.Authority != null && t.CreatedAt < now.AddMinutes(-15) && t.CreatedAt > now.AddHours(-24))
            .Where(t => db.Invoices.IgnoreQueryFilters().Any(i => i.Id == t.InvoiceId && i.Status == InvoiceStatus.Pending))
            .OrderBy(t => t.CreatedAt).Select(t => t.Authority!).Take(50).ToListAsync(ct);
        foreach (var tr in tracks)
        {
            try { await billing.FinalizeAsync(tr, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "Reconcile {Track} failed", tr); }
        }

        // 2) Abandoned checkouts become Failed after 24h
        await db.Invoices.IgnoreQueryFilters().Where(i => i.Status == InvoiceStatus.Pending && i.CreatedAt < now.AddHours(-24))
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, InvoiceStatus.Failed), ct);

        // 3) Flip expired tenants to Expired (the engine also checks ExpireDate itself)
        await db.Tenants.Where(t => (t.SubscriptionStatus == SubscriptionStatus.Active || t.SubscriptionStatus == SubscriptionStatus.Trial)
                                    && t.ExpireDate != null && t.ExpireDate < now)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.SubscriptionStatus, SubscriptionStatus.Expired), ct);
    }
}
