using InstaSaaS.Application.Auth;
using InstaSaaS.Application.Billing;
using InstaSaaS.Domain;
using InstaSaaS.Engine;
using InstaSaaS.Infrastructure;
using InstaSaaS.Infrastructure.Payment;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InstaSaaS.Tests;

public class FixedTenant : ICurrentTenant { public Guid? TenantId { get; set; } }

// One shared in-memory SQLite connection: several DbContext instances see the same data (like separate requests would).
public sealed class TestDb : IDisposable
{
    public SqliteConnection Conn { get; } = new("DataSource=:memory:");
    public FixedTenant Tenant { get; } = new();

    public TestDb()
    {
        Conn.Open();
        using var c = New();
        c.Database.EnsureCreated();
    }

    public AppDbContext New() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(Conn).Options, Tenant);
    public void Dispose() => Conn.Dispose();
}

public class FakeIg : IInstagramGateway
{
    public record Call(string Kind, string Target, string Text, bool IsFirst);
    public List<Call> Calls { get; } = new();
    public bool Fail { get; set; }

    private void Rec(string kind, string target, string text, bool first)
    {
        if (Fail) throw new InvalidOperationException("boxapi down");
        Calls.Add(new Call(kind, target, text, first));
    }
    public Task RequestFollowStatusAsync(InstagramAccount a, string customerId, CancellationToken ct)
    { Rec("follow_status", customerId, "", false); return Task.CompletedTask; }
    public Task SendAsync(InstagramAccount a, IncomingEvent src, Response r, bool isFirst, CancellationToken ct)
    { Rec("send", src.SenderIgId, r.Content, isFirst); return Task.CompletedTask; }
    public Task SendTextAsync(InstagramAccount a, IncomingEvent src, string text, bool isFirst, CancellationToken ct)
    { Rec("text", src.SenderIgId, text, isFirst); return Task.CompletedTask; }
    public Task SendDirectTextAsync(InstagramAccount a, string recipientId, string text, CancellationToken ct)
    { Rec("direct", recipientId, text, false); return Task.CompletedTask; }
}

public class FakeSms : ISmsSender
{
    public List<(string Phone, string Code)> Otps { get; } = new();
    public List<(string Phone, string Inviter)> Invites { get; } = new();
    public Task SendOtpAsync(string phone, string code, CancellationToken ct) { Otps.Add((phone, code)); return Task.CompletedTask; }
    public Task SendInviteAsync(string phone, string inviterMasked, CancellationToken ct) { Invites.Add((phone, inviterMasked)); return Task.CompletedTask; }
}

public class FakePay : IPaymentGateway
{
    public string Name => "fake";
    public long NextTrack { get; set; } = 1000;
    public GatewayVerify? VerifyResult { get; set; }
    public bool VerifyThrows { get; set; }
    public int VerifyCalls { get; private set; }

    public Task<GatewayStart> StartAsync(long amountRial, string orderId, string callbackUrl, string description, CancellationToken ct)
        => Task.FromResult(new GatewayStart(NextTrack, $"https://pay.test/start/{NextTrack}"));

    public Task<GatewayVerify> VerifyAsync(long trackId, CancellationToken ct)
    {
        VerifyCalls++;
        if (VerifyThrows) throw new HttpRequestException("gateway down");
        return Task.FromResult(VerifyResult ?? new GatewayVerify(202, false, null, null, null, null, "not paid"));
    }
}

public static class Seed
{
    public record World(Guid UserId, Guid TenantId, Guid AccountId, Guid BoxAccountId, string PageIgId);

    // A tenant with an active subscription and one connected page.
    public static World TenantWithPage(TestDb db, string phone = "09120000001", string username = "shop",
        SubscriptionStatus status = SubscriptionStatus.Active, DateTime? expire = null, string pageIgId = "page-1")
    {
        using var c = db.New();
        var user = new User { Phone = phone };
        var tenant = new Tenant { Owner = user, SubscriptionStatus = status, ExpireDate = expire ?? DateTime.UtcNow.AddDays(10) };
        c.Users.Add(user);
        c.Tenants.Add(tenant);
        c.TenantMembers.Add(new TenantMember { Tenant = tenant, User = user, Role = TenantRole.Owner });
        var acc = new InstagramAccount
        {
            TenantId = tenant.Id, BoxAccountId = Guid.NewGuid(), InstagramUserId = pageIgId, Username = username
        };
        c.InstagramAccounts.Add(acc);
        c.SaveChanges();
        return new World(user.Id, tenant.Id, acc.Id, acc.BoxAccountId, pageIgId);
    }

    public static Automation AddAutomation(TestDb db, Seed.World w, string keywords, MatchType match = MatchType.Contains,
        TriggerType trigger = TriggerType.Dm, int priority = 0, int? followUpHours = null,
        params (string Content, bool RequireFollow)[] responses)
    {
        using var c = db.New();
        var a = new Automation
        {
            AccountId = w.AccountId, TriggerType = trigger, Keywords = keywords, MatchType = match,
            Priority = priority, FollowUpAfterHours = followUpHours
        };
        var i = 0;
        foreach (var (content, rf) in responses.Length > 0 ? responses : new[] { ("reply", false) })
            a.Responses.Add(new Response { MessageType = MessageType.Text, Content = content, RequireFollow = rf, SortOrder = i++ });
        c.Automations.Add(a);
        c.SaveChanges();
        return a;
    }

    public static IncomingEvent Dm(Seed.World w, string text, string sender = "u1", string? id = null) =>
        new(id ?? Guid.NewGuid().ToString(), w.BoxAccountId, TriggerType.Dm, sender, "buyer", text);

    public static IncomingEvent Comment(Seed.World w, string text, string sender = "u1", string commentId = "c1") =>
        new(Guid.NewGuid().ToString(), w.BoxAccountId, TriggerType.Comment, sender, "buyer", text, "media1", commentId);
}

public static class Make
{
    public static (AutomationProcessor Proc, FollowGateService Gate) Engine(TestDb db, FakeIg ig, out AppDbContext ctx)
    {
        ctx = db.New();
        var gate = new FollowGateService(ctx, ig, NullLogger<FollowGateService>.Instance);
        return (new AutomationProcessor(ctx, ig, gate, NullLogger<AutomationProcessor>.Instance), gate);
    }

    public static OtpAuthService Auth(AppDbContext db, FakeSms sms, AuthOptions? opt = null)
    {
        opt ??= new AuthOptions { JwtKey = new string('k', 40), OtpSecret = "secret" };
        var o = Options.Create(opt);
        return new OtpAuthService(db, sms, o, Options.Create(new BillingOptions { TrialDays = 7 }), new JwtService(o));
    }

    public static BillingService Billing(AppDbContext db, FakePay pay) =>
        new(db, pay, Options.Create(new BillingOptions { PublicApiBaseUrl = "https://api.test", FrontendBaseUrl = "https://app.test" }),
            NullLogger<BillingService>.Instance);
}
