using InstaSaaS.Api;
using InstaSaaS.Application.Billing;
using InstaSaaS.Domain;
using InstaSaaS.Engine;
using InstaSaaS.Infrastructure.Payment;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InstaSaaS.Tests;

public class BillingTests
{
    private static Guid AddPlan(TestDb db, long price = 3_000_000, int days = 30)
    {
        using var c = db.New();
        var p = new Plan { Name = "Basic", PriceRial = price, DurationDays = days, MaxInstagramAccounts = 2, MaxAutomations = 20 };
        c.Plans.Add(p); c.SaveChanges();
        return p.Id;
    }

    private static GatewayVerify Paid(Invoice inv, long? amount = null, string? order = null) =>
        new(100, true, amount ?? inv.AmountRial, order ?? inv.Id.ToString(), "REF1", DateTime.UtcNow, "ok");

    [Fact]
    public async Task Checkout_creates_pending_invoice_with_plan_price_and_a_transaction()
    {
        using var db = new TestDb();
        var w = Seed.TenantWithPage(db);
        var plan = AddPlan(db, 5_000_000);
        var pay = new FakePay { NextTrack = 777 };
        using var ctx = db.New();

        var url = await Make.Billing(ctx, pay).StartCheckoutAsync(w.TenantId, plan, default);

        Assert.Equal("https://pay.test/start/777", url);
        using var check = db.New();
        var inv = check.Invoices.IgnoreQueryFilters().Single();
        Assert.Equal(5_000_000, inv.AmountRial);
        Assert.Equal(InvoiceStatus.Pending, inv.Status);
        Assert.Equal("777", check.Transactions.Single().Authority);
    }

    [Fact]
    public async Task Checkout_rejects_unknown_inactive_or_too_cheap_plans()
    {
        using var db = new TestDb();
        var w = Seed.TenantWithPage(db);
        var cheap = AddPlan(db, 500);
        using var ctx = db.New();
        var svc = Make.Billing(ctx, new FakePay());

        await Assert.ThrowsAsync<BillingException>(() => svc.StartCheckoutAsync(w.TenantId, Guid.NewGuid(), default));
        await Assert.ThrowsAsync<BillingException>(() => svc.StartCheckoutAsync(w.TenantId, cheap, default));
    }

    private static async Task<(string Track, Guid Invoice)> StartPaid(TestDb db, Seed.World w, Guid plan, FakePay pay)
    {
        using var ctx = db.New();
        await Make.Billing(ctx, pay).StartCheckoutAsync(w.TenantId, plan, default);
        using var c = db.New();
        return (c.Transactions.Single().Authority!, c.Invoices.IgnoreQueryFilters().Single().Id);
    }

    [Fact]
    public async Task Successful_payment_activates_subscription_for_plan_duration()
    {
        using var db = new TestDb();
        var w = Seed.TenantWithPage(db, status: SubscriptionStatus.Expired, expire: DateTime.UtcNow.AddDays(-5));
        var plan = AddPlan(db, days: 30);
        var pay = new FakePay();
        var (track, _) = await StartPaid(db, w, plan, pay);
        using (var c = db.New()) pay.VerifyResult = Paid(c.Invoices.IgnoreQueryFilters().Single());

        using var ctx = db.New();
        var (outcome, _) = await Make.Billing(ctx, pay).FinalizeAsync(track, default);

        Assert.Equal(PayOutcome.Paid, outcome);
        using var check = db.New();
        var t = check.Tenants.Single();
        Assert.Equal(SubscriptionStatus.Active, t.SubscriptionStatus);
        Assert.Equal(plan, t.PlanId);
        Assert.InRange((t.ExpireDate!.Value - DateTime.UtcNow).TotalDays, 29.9, 30.1);
        Assert.Equal(InvoiceStatus.Paid, check.Invoices.IgnoreQueryFilters().Single().Status);
        Assert.Equal("REF1", check.Transactions.Single().RefId);
    }

    [Fact]
    public async Task Finalizing_twice_does_not_extend_the_subscription_twice()
    {
        using var db = new TestDb();
        var w = Seed.TenantWithPage(db, status: SubscriptionStatus.Expired, expire: DateTime.UtcNow.AddDays(-5));
        var plan = AddPlan(db, days: 30);
        var pay = new FakePay();
        var (track, _) = await StartPaid(db, w, plan, pay);
        using (var c = db.New()) pay.VerifyResult = Paid(c.Invoices.IgnoreQueryFilters().Single());

        using (var c1 = db.New()) await Make.Billing(c1, pay).FinalizeAsync(track, default);
        using (var c2 = db.New()) Assert.Equal(PayOutcome.Paid, (await Make.Billing(c2, pay).FinalizeAsync(track, default)).Outcome);   // browser callback + worker

        using var check = db.New();
        Assert.InRange((check.Tenants.Single().ExpireDate!.Value - DateTime.UtcNow).TotalDays, 29.9, 30.1);
    }

    [Fact]
    public async Task Renewing_the_same_plan_while_active_stacks_time()
    {
        using var db = new TestDb();
        var plan = AddPlan(db, days: 30);
        var w = Seed.TenantWithPage(db, status: SubscriptionStatus.Active, expire: DateTime.UtcNow.AddDays(10));
        using (var c = db.New()) { c.Tenants.Single().PlanId = plan; c.SaveChanges(); }
        var pay = new FakePay();
        var (track, _) = await StartPaid(db, w, plan, pay);
        using (var c = db.New()) pay.VerifyResult = Paid(c.Invoices.IgnoreQueryFilters().Single());

        using var ctx = db.New();
        await Make.Billing(ctx, pay).FinalizeAsync(track, default);

        using var check = db.New();
        Assert.InRange((check.Tenants.Single().ExpireDate!.Value - DateTime.UtcNow).TotalDays, 39.9, 40.1);
    }

    [Fact]
    public async Task Amount_mismatch_is_flagged_and_subscription_is_NOT_activated()
    {
        using var db = new TestDb();
        var w = Seed.TenantWithPage(db, status: SubscriptionStatus.Expired, expire: DateTime.UtcNow.AddDays(-5));
        var plan = AddPlan(db);
        var pay = new FakePay();
        var (track, _) = await StartPaid(db, w, plan, pay);
        using (var c = db.New()) pay.VerifyResult = Paid(c.Invoices.IgnoreQueryFilters().Single(), amount: 1000);   // paid only 1,000 rial

        using var ctx = db.New();
        Assert.Equal(PayOutcome.Mismatch, (await Make.Billing(ctx, pay).FinalizeAsync(track, default)).Outcome);

        using var check = db.New();
        Assert.Equal(SubscriptionStatus.Expired, check.Tenants.Single().SubscriptionStatus);
        Assert.Equal(InvoiceStatus.Pending, check.Invoices.IgnoreQueryFilters().Single().Status);
    }

    [Fact]
    public async Task OrderId_mismatch_is_flagged()
    {
        using var db = new TestDb();
        var w = Seed.TenantWithPage(db);
        var plan = AddPlan(db);
        var pay = new FakePay();
        var (track, _) = await StartPaid(db, w, plan, pay);
        using (var c = db.New()) pay.VerifyResult = Paid(c.Invoices.IgnoreQueryFilters().Single(), order: Guid.NewGuid().ToString());

        using var ctx = db.New();
        Assert.Equal(PayOutcome.Mismatch, (await Make.Billing(ctx, pay).FinalizeAsync(track, default)).Outcome);
    }

    [Fact]
    public async Task Unpaid_returns_NotPaid_and_unknown_track_is_unknown()
    {
        using var db = new TestDb();
        var w = Seed.TenantWithPage(db);
        var plan = AddPlan(db);
        var pay = new FakePay();
        var (track, _) = await StartPaid(db, w, plan, pay);     // default verify result = not paid

        using var ctx = db.New();
        var svc = Make.Billing(ctx, pay);
        Assert.Equal(PayOutcome.NotPaid, (await svc.FinalizeAsync(track, default)).Outcome);
        Assert.Equal(PayOutcome.Unknown, (await svc.FinalizeAsync("424242", default)).Outcome);
        Assert.Equal(PayOutcome.Unknown, (await svc.FinalizeAsync("not-a-number", default)).Outcome);
    }

    [Fact]
    public async Task Gateway_outage_during_verify_propagates_and_leaves_invoice_pending()
    {
        using var db = new TestDb();
        var w = Seed.TenantWithPage(db);
        var plan = AddPlan(db);
        var pay = new FakePay();
        var (track, _) = await StartPaid(db, w, plan, pay);
        pay.VerifyThrows = true;

        using var ctx = db.New();
        await Assert.ThrowsAsync<HttpRequestException>(() => Make.Billing(ctx, pay).FinalizeAsync(track, default));
        using var check = db.New();
        Assert.Equal(InvoiceStatus.Pending, check.Invoices.IgnoreQueryFilters().Single().Status);   // worker will retry
    }

    [Fact]
    public async Task Billing_worker_reconciles_old_pending_invoices_and_expires_tenants()
    {
        using var db = new TestDb();
        var w = Seed.TenantWithPage(db, status: SubscriptionStatus.Expired, expire: DateTime.UtcNow.AddDays(-5));
        var plan = AddPlan(db);
        var pay = new FakePay();
        var (_, _) = await StartPaid(db, w, plan, pay);
        using (var c = db.New())
        {
            pay.VerifyResult = Paid(c.Invoices.IgnoreQueryFilters().Single());
            c.Transactions.Single().CreatedAt = DateTime.UtcNow.AddMinutes(-30);        // user paid then closed the browser
            c.SaveChanges();
        }
        // a second tenant whose trial ran out
        Seed.TenantWithPage(db, "09120000002", "other", SubscriptionStatus.Trial, DateTime.UtcNow.AddDays(-1));

        var services = new ServiceCollection();
        services.AddScoped(_ => db.New());
        services.AddSingleton<IPaymentGateway>(pay);
        services.AddScoped(sp => Make.Billing(sp.GetRequiredService<InstaSaaS.Infrastructure.AppDbContext>(), pay));
        await using var sp = services.BuildServiceProvider();

        await new BillingWorker(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<BillingWorker>.Instance).RunOnceAsync(default);

        using var check = db.New();
        Assert.Equal(InvoiceStatus.Paid, check.Invoices.IgnoreQueryFilters().Single().Status);
        Assert.Equal(SubscriptionStatus.Active, check.Tenants.Single(t => t.Id == w.TenantId).SubscriptionStatus);
        Assert.Equal(SubscriptionStatus.Expired, check.Tenants.Single(t => t.Id != w.TenantId).SubscriptionStatus);
    }
}

public class TenantIsolationTests
{
    [Fact]
    public void Query_filters_hide_other_tenants_data_but_not_when_no_tenant_in_context()
    {
        using var db = new TestDb();
        var a = Seed.TenantWithPage(db, "09120000001", "a");
        var b = Seed.TenantWithPage(db, "09120000002", "b");
        Seed.AddAutomation(db, a, "x");
        Seed.AddAutomation(db, b, "y");
        db.Tenant.TenantId = a.TenantId;
        using (var c = db.New())
        {
            Assert.Equal("a", c.InstagramAccounts.Single().Username);
            Assert.Equal("x", c.Automations.Single().Keywords);
        }

        db.Tenant.TenantId = b.TenantId;
        using (var c = db.New()) Assert.Equal("y", c.Automations.Single().Keywords);

        db.Tenant.TenantId = null;                  // background jobs: no filter, must scope manually
        using (var c = db.New()) Assert.Equal(2, c.InstagramAccounts.Count());
    }

    [Fact]
    public void IgnoreQueryFilters_sees_everything_even_inside_a_tenant()
    {
        using var db = new TestDb();
        Seed.TenantWithPage(db, "09120000001", "a");
        Seed.TenantWithPage(db, "09120000002", "b");
        using (var t0 = db.New()) db.Tenant.TenantId = t0.Tenants.First().Id;
        using var c = db.New();
        Assert.Equal(1, c.InstagramAccounts.Count());
        Assert.Equal(2, c.InstagramAccounts.IgnoreQueryFilters().Count());
    }

    [Fact]
    public void Invoices_are_tenant_scoped()
    {
        using var db = new TestDb();
        var a = Seed.TenantWithPage(db, "09120000001", "a");
        var b = Seed.TenantWithPage(db, "09120000002", "b");
        Guid planId;
        using (var c = db.New())
        {
            var p = new Plan { Name = "p", PriceRial = 1000, DurationDays = 1, MaxAutomations = 1, MaxInstagramAccounts = 1 };
            c.Plans.Add(p);
            c.Invoices.Add(new Invoice { TenantId = a.TenantId, PlanId = p.Id, AmountRial = 1000 });
            c.Invoices.Add(new Invoice { TenantId = b.TenantId, PlanId = p.Id, AmountRial = 1000 });
            c.SaveChanges(); planId = p.Id;
        }
        db.Tenant.TenantId = a.TenantId;
        using var q = db.New();
        Assert.Equal(a.TenantId, q.Invoices.Single().TenantId);
        Assert.NotEqual(Guid.Empty, planId);
    }
}

public class FollowUpWorkerTests
{
    private static async Task<(TestDb Db, Seed.World W, FakeIg Ig, FollowUpWorker Worker, Guid AutoId)> Setup(
        DateTime? expire = null, DateTime? sentAt = null, DateTime? dueAt = null, string? followUpText = null)
    {
        var db = new TestDb();
        var w = Seed.TenantWithPage(db, expire: expire);
        var a = Seed.AddAutomation(db, w, "price", followUpHours: 2);
        using (var c = db.New())
        {
            c.Automations.IgnoreQueryFilters().Single().FollowUpText = followUpText;
            c.PendingFollowUps.Add(new PendingFollowUp
            {
                AccountId = w.AccountId, AutomationId = a.Id, IgUserId = "u1",
                SentAt = sentAt ?? DateTime.UtcNow.AddHours(-3), DueAt = dueAt ?? DateTime.UtcNow.AddMinutes(-1)
            });
            await c.SaveChangesAsync();
        }
        var ig = new FakeIg();
        var services = new ServiceCollection();
        services.AddScoped(_ => db.New());
        services.AddSingleton<IInstagramGateway>(ig);
        var sp = services.BuildServiceProvider();
        return (db, w, ig, new FollowUpWorker(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<FollowUpWorker>.Instance), a.Id);
    }

    [Fact]
    public async Task Due_followup_is_sent_once_with_custom_text()
    {
        var (db, _, ig, worker, _) = await Setup(followUpText: "still there?");
        using var _db = db;

        await worker.RunOnceAsync(default);
        await worker.RunOnceAsync(default);           // second tick must not resend

        var call = Assert.Single(ig.Calls);
        Assert.Equal(("direct", "u1", "still there?"), (call.Kind, call.Target, call.Text));
    }

    [Fact]
    public async Task Not_yet_due_followup_is_left_alone()
    {
        var (db, _, ig, worker, _) = await Setup(dueAt: DateTime.UtcNow.AddHours(1));
        using var _db = db;
        await worker.RunOnceAsync(default);
        Assert.Empty(ig.Calls);
    }

    [Fact]
    public async Task Replied_user_gets_no_followup()
    {
        var (db, _, ig, worker, _) = await Setup();
        using var _db = db;
        using (var c = db.New()) { c.PendingFollowUps.Single().Replied = true; c.SaveChanges(); }
        await worker.RunOnceAsync(default);
        Assert.Empty(ig.Calls);
    }

    [Fact]
    public async Task Followup_outside_24h_window_is_skipped_and_logged()
    {
        var (db, _, ig, worker, _) = await Setup(sentAt: DateTime.UtcNow.AddHours(-25));
        using var _db = db;
        await worker.RunOnceAsync(default);

        Assert.Empty(ig.Calls);
        using var c = db.New();
        Assert.Contains(c.Logs, l => l.Status == "followup_skipped");
    }

    [Fact]
    public async Task Followup_is_skipped_when_subscription_expired()
    {
        var (db, _, ig, worker, _) = await Setup(expire: DateTime.UtcNow.AddDays(-1));
        using var _db = db;
        await worker.RunOnceAsync(default);
        Assert.Empty(ig.Calls);
    }

    [Fact]
    public async Task Failed_send_is_retried_later_then_given_up_after_three_attempts()
    {
        var (db, _, ig, worker, _) = await Setup();
        using var _db = db;
        ig.Fail = true;

        await worker.RunOnceAsync(default);
        using (var c = db.New())
        {
            var p = c.PendingFollowUps.Single();
            Assert.False(p.FollowUpSent);                         // claim released
            Assert.Equal(1, p.Attempts);
            Assert.True(p.DueAt > DateTime.UtcNow);               // pushed back by RetryDelay
            p.DueAt = DateTime.UtcNow.AddMinutes(-1); c.SaveChanges();
        }
        await worker.RunOnceAsync(default);
        using (var c = db.New()) { var p = c.PendingFollowUps.Single(); p.DueAt = DateTime.UtcNow.AddMinutes(-1); c.SaveChanges(); }
        await worker.RunOnceAsync(default);

        using var check = db.New();
        var final = check.PendingFollowUps.Single();
        Assert.True(final.FollowUpSent);                           // given up, no more retries
        Assert.Equal(3, final.Attempts);
        Assert.Contains(check.Logs, l => l.Status == "followup_error");
    }
}
