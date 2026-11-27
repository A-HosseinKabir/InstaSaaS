// Automation engine: normalized event, keyword matcher, processor, follow-gate, queue + worker.
// Namespace is InstaSaaS.Engine (NOT ...Automation) so it can't clash with the Automation entity.
using System.Threading.Channels;
using InstaSaaS.Domain;
using InstaSaaS.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace InstaSaaS.Engine;

// Normalized event: the webhook parser converts raw BoxAPI JSON into this.
public record IncomingEvent(
    string ExternalEventId,      // unique id from provider, used for idempotency (webhooks are retried)
    Guid BoxAccountId,           // which of OUR connected pages received it
    TriggerType Type,            // Comment or Dm
    string SenderIgId,
    string SenderUsername,
    string Text,
    string? MediaId = null,
    string? CommentId = null);   // for comments: needed for private_reply

public record FollowResult(Guid BoxAccountId, string CustomerId, bool IsFollowing);

public interface IInstagramGateway
{
    Task RequestFollowStatusAsync(InstagramAccount a, string customerId, CancellationToken ct);
    Task SendAsync(InstagramAccount a, IncomingEvent src, Response r, bool isFirst, CancellationToken ct);
    Task SendTextAsync(InstagramAccount a, IncomingEvent src, string text, bool isFirst, CancellationToken ct);
    Task SendDirectTextAsync(InstagramAccount a, string recipientId, string text, CancellationToken ct); // used by follow-up worker
}

public static class KeywordMatcher
{
    // Normalize Persian/Arabic variants so "کتاب" typed with an Arabic kaf still matches.
    public static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s.Trim())
        {
            switch (c)
            {
                case 'ي': case 'ى': sb.Append('ی'); break;
                case 'ك': sb.Append('ک'); break;
                case '‌': case '‍': case 'ً': case 'ٌ': case 'ٍ':
                case 'َ': case 'ُ': case 'ِ': case 'ّ': case 'ْ': case 'ـ': break; // ZWNJ, harakat, tatweel
                case >= '۰' and <= '۹': sb.Append((char)('0' + (c - '۰'))); break;
                case >= '٠' and <= '٩': sb.Append((char)('0' + (c - '٠'))); break;
                default: sb.Append(char.ToLowerInvariant(c)); break;
            }
        }
        return string.Join(' ', sb.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    public static IEnumerable<string> SplitKeywords(string keywords) =>
        keywords.Split(new[] { ',', '،', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(Normalize).Where(k => k.Length > 0);

    public static bool IsMatch(string text, string keywords, MatchType type)
    {
        var t = Normalize(text);
        if (t.Length == 0) return false;
        foreach (var k in SplitKeywords(keywords))
            if (type == MatchType.Exact ? t == k : t.Contains(k)) return true;
        return false;
    }
}

public static class SubscriptionRules
{
    public static bool IsActive(Tenant t, DateTime nowUtc) =>
        t.SubscriptionStatus is SubscriptionStatus.Active or SubscriptionStatus.Trial
        && (t.ExpireDate == null || t.ExpireDate > nowUtc);
}

public class AutomationProcessor
{
    private readonly AppDbContext _db;
    private readonly IInstagramGateway _ig;
    private readonly FollowGateService _followGate;
    private readonly ILogger<AutomationProcessor> _log;

    public AutomationProcessor(AppDbContext db, IInstagramGateway ig, FollowGateService followGate, ILogger<AutomationProcessor> log)
    { _db = db; _ig = ig; _followGate = followGate; _log = log; }

    public async Task ProcessAsync(IncomingEvent ev, CancellationToken ct)
    {
        // No tenant in context here (background), so bypass the tenant filter on purpose and scope manually.
        var account = await _db.InstagramAccounts.IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.BoxAccountId == ev.BoxAccountId && a.IsActive, ct);
        if (account is null) return;

        try
        {
            // Idempotency: skip duplicates (provider retries)
            var key = $"{account.Id}:{ev.ExternalEventId}";
            if (await _db.ProcessedEvents.AnyAsync(p => p.Id == key, ct)) return;
            _db.ProcessedEvents.Add(new ProcessedEvent { Id = key });
            await _db.SaveChangesAsync(ct);

            // Never answer our own messages (loop protection)
            if (ev.SenderIgId == account.InstagramUserId) return;

            // Any DM from this user counts as a reply and cancels pending follow-ups.
            // Must run BEFORE matching, so a follow-up created by this same DM is not cancelled.
            if (ev.Type == TriggerType.Dm)
                await _db.PendingFollowUps
                    .Where(p => p.AccountId == account.Id && p.IgUserId == ev.SenderIgId && !p.Replied && !p.FollowUpSent)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.Replied, true), ct);

            // Subscription gate
            var tenant = await _db.Tenants.FirstAsync(t => t.Id == account.TenantId, ct);
            if (!SubscriptionRules.IsActive(tenant, DateTime.UtcNow))
            { await LogAsync(account.Id, ev.Type, "skipped", "subscription inactive", ct); return; }

            var automations = await _db.Automations.IgnoreQueryFilters()
                .Include(a => a.Responses)
                .Where(a => a.AccountId == account.Id && a.IsActive && a.TriggerType == ev.Type)
                .OrderBy(a => a.Priority).ThenBy(a => a.Id)
                .ToListAsync(ct);

            var hit = automations.FirstOrDefault(a => KeywordMatcher.IsMatch(ev.Text, a.Keywords, a.MatchType));
            if (hit is null) return;                              // lowest Priority number wins

            var first = true;                                     // first message of a comment trigger must be a private_reply
            foreach (var r in hit.Responses.OrderBy(r => r.SortOrder))
            {
                if (r.RequireFollow)
                {
                    // follow_status is async: persist state, resume in FollowGateService.ResolveAsync when the webhook answers
                    await _followGate.BeginAsync(account, hit, ev, r.SortOrder, firstSent: !first, ct);
                    return;
                }
                await _ig.SendAsync(account, ev, r, first, ct);
                first = false;
            }

            if (hit.FollowUpAfterHours is > 0)
                _db.PendingFollowUps.Add(new PendingFollowUp
                {
                    AccountId = account.Id, AutomationId = hit.Id, IgUserId = ev.SenderIgId,
                    SentAt = DateTime.UtcNow, DueAt = DateTime.UtcNow.AddHours(hit.FollowUpAfterHours.Value)
                });
            await _db.SaveChangesAsync(ct);
            await LogAsync(account.Id, ev.Type, "ok", null, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogError(ex, "Automation failed for account {Account}", ev.BoxAccountId);
            await LogAsync(account.Id, ev.Type, "error", ex.Message, ct);
        }
    }

    private async Task LogAsync(Guid accountId, TriggerType t, string status, string? err, CancellationToken ct)
    {
        _db.Logs.Add(new LogEntry { AccountId = accountId, EventType = t.ToString(), Status = status, ErrorMessage = err });
        await _db.SaveChangesAsync(ct);
    }
}

public class FollowGateService
{
    private const string NoticeText = "برای دریافت این پیام ابتدا پیج را دنبال کنید و دوباره پیام بدهید 🙏";
    private readonly AppDbContext _db;
    private readonly IInstagramGateway _ig;
    private readonly ILogger<FollowGateService> _log;
    public FollowGateService(AppDbContext db, IInstagramGateway ig, ILogger<FollowGateService> log) { _db = db; _ig = ig; _log = log; }

    public async Task BeginAsync(InstagramAccount acc, Automation auto, IncomingEvent ev, int resumeSort, bool firstSent, CancellationToken ct)
    {
        _db.FollowChecks.Add(new FollowCheck
        {
            AccountId = acc.Id, AutomationId = auto.Id, SenderIgId = ev.SenderIgId, SenderUsername = ev.SenderUsername,
            EventType = ev.Type, CommentId = ev.CommentId, Text = ev.Text, ResumeFromSort = resumeSort, FirstSent = firstSent
        });
        await _db.SaveChangesAsync(ct);
        await _ig.RequestFollowStatusAsync(acc, ev.SenderIgId, ct);
    }

    public async Task ResolveAsync(FollowResult r, CancellationToken ct)
    {
        var acc = await _db.InstagramAccounts.IgnoreQueryFilters().FirstOrDefaultAsync(a => a.BoxAccountId == r.BoxAccountId, ct);
        if (acc is null) return;

        var cutoff = DateTime.UtcNow.AddMinutes(-10);
        var chk = await _db.FollowChecks
            .Where(c => c.AccountId == acc.Id && c.SenderIgId == r.CustomerId && !c.Done && c.CreatedAt > cutoff)
            .OrderByDescending(c => c.CreatedAt).FirstOrDefaultAsync(ct);
        if (chk is null) return;                                   // stale or duplicate result
        chk.Done = true;
        await _db.SaveChangesAsync(ct);

        var ev = new IncomingEvent($"resume:{chk.Id}", acc.BoxAccountId, chk.EventType, chk.SenderIgId, chk.SenderUsername, chk.Text, null, chk.CommentId);
        try
        {
            if (!r.IsFollowing)
            {
                // NOTE: for comment triggers this consumes the single private reply allowed per comment.
                await _ig.SendTextAsync(acc, ev, NoticeText, !chk.FirstSent, ct);
                _db.Logs.Add(new LogEntry { AccountId = acc.Id, EventType = chk.EventType.ToString(), Status = "follow_required" });
                await _db.SaveChangesAsync(ct);
                return;
            }

            var auto = await _db.Automations.IgnoreQueryFilters().Include(a => a.Responses).FirstAsync(a => a.Id == chk.AutomationId, ct);
            var first = !chk.FirstSent;
            foreach (var resp in auto.Responses.Where(x => x.SortOrder >= chk.ResumeFromSort).OrderBy(x => x.SortOrder))
            {
                await _ig.SendAsync(acc, ev, resp, first, ct);
                first = false;
            }
            if (auto.FollowUpAfterHours is > 0)
                _db.PendingFollowUps.Add(new PendingFollowUp
                {
                    AccountId = acc.Id, AutomationId = auto.Id, IgUserId = chk.SenderIgId,
                    SentAt = DateTime.UtcNow, DueAt = DateTime.UtcNow.AddHours(auto.FollowUpAfterHours.Value)
                });
            _db.Logs.Add(new LogEntry { AccountId = acc.Id, EventType = chk.EventType.ToString(), Status = "ok" });
            await _db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogError(ex, "Resume after follow check failed");
            _db.Logs.Add(new LogEntry { AccountId = acc.Id, EventType = chk.EventType.ToString(), Status = "error", ErrorMessage = ex.Message });
            await _db.SaveChangesAsync(ct);
        }
    }
}

// Work items: IncomingEvent (message/comment) or FollowResult (async follow-status answer).
// In-memory: items still queued are lost on restart (see "known limitations").
public class EventQueue
{
    private readonly Channel<object> _ch = Channel.CreateBounded<object>(5000);
    public bool TryEnqueue(object item) => _ch.Writer.TryWrite(item);
    public IAsyncEnumerable<object> ReadAllAsync(CancellationToken ct) => _ch.Reader.ReadAllAsync(ct);
}

public class EventWorker : BackgroundService
{
    private readonly EventQueue _q;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<EventWorker> _log;
    public EventWorker(EventQueue q, IServiceScopeFactory s, ILogger<EventWorker> log) { _q = q; _scopes = s; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var item in _q.ReadAllAsync(ct))
            {
                try
                {
                    using var scope = _scopes.CreateScope();
                    var sp = scope.ServiceProvider;
                    if (item is IncomingEvent e) await sp.GetRequiredService<AutomationProcessor>().ProcessAsync(e, ct);
                    else if (item is FollowResult f) await sp.GetRequiredService<FollowGateService>().ResolveAsync(f, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _log.LogError(ex, "Worker item failed"); }   // one bad item must not kill the loop
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }
}
