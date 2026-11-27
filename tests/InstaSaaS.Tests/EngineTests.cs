using InstaSaaS.Domain;
using InstaSaaS.Engine;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InstaSaaS.Tests;

public class EngineTests
{
    [Fact]
    public async Task Matching_dm_sends_all_responses_in_order_and_logs_ok()
    {
        using var db = new TestDb();
        var w = Seed.TenantWithPage(db);
        Seed.AddAutomation(db, w, "price", responses: new[] { ("first", false), ("second", false) });
        var ig = new FakeIg();
        var (proc, _) = Make.Engine(db, ig, out var ctx);

        await proc.ProcessAsync(Seed.Dm(w, "what is the PRICE?"), default);

        Assert.Equal(new[] { "first", "second" }, ig.Calls.Where(c => c.Kind == "send").Select(c => c.Text));
        Assert.Contains(ctx.Logs, l => l.Status == "ok");
    }

    [Fact]
    public async Task Non_matching_message_does_nothing()
    {
        using var db = new TestDb();
        var w = Seed.TenantWithPage(db);
        Seed.AddAutomation(db, w, "price");
        var ig = new FakeIg();
        var (proc, _) = Make.Engine(db, ig, out _);

        await proc.ProcessAsync(Seed.Dm(w, "hello there"), default);
        Assert.Empty(ig.Calls);
    }

    [Fact]
    public async Task Same_event_id_is_processed_only_once()
    {
        using var db = new TestDb();
        var w = Seed.TenantWithPage(db);
        Seed.AddAutomation(db, w, "price");
        var ig = new FakeIg();
        var (proc, _) = Make.Engine(db, ig, out _);
        var ev = Seed.Dm(w, "price", id: "evt-1");

        await proc.ProcessAsync(ev, default);
        await proc.ProcessAsync(ev, default);          // provider retry

        Assert.Single(ig.Calls.Where(c => c.Kind == "send"));
    }

    [Fact]
    public async Task Messages_from_the_page_itself_are_ignored()
    {
        using var db = new TestDb();
        var w = Seed.TenantWithPage(db, pageIgId: "page-1");
        Seed.AddAutomation(db, w, "price");
        var ig = new FakeIg();
        var (proc, _) = Make.Engine(db, ig, out _);

        await proc.ProcessAsync(Seed.Dm(w, "price", sender: "page-1"), default);
        Assert.Empty(ig.Calls);
    }

    [Fact]
    public async Task Expired_subscription_blocks_sending_and_logs_skipped()
    {
        using var db = new TestDb();
        var w = Seed.TenantWithPage(db, expire: DateTime.UtcNow.AddDays(-1));
        Seed.AddAutomation(db, w, "price");
        var ig = new FakeIg();
        var (proc, _) = Make.Engine(db, ig, out var ctx);

        await proc.ProcessAsync(Seed.Dm(w, "price"), default);

        Assert.Empty(ig.Calls);
        Assert.Contains(ctx.Logs, l => l.Status == "skipped");
    }

    [Fact]
    public async Task Inactive_page_or_unknown_page_is_ignored()
    {
        using var db = new TestDb();
        var w = Seed.TenantWithPage(db);
        Seed.AddAutomation(db, w, "price");
        using (var c = db.New()) { c.InstagramAccounts.IgnoreQueryFilters().First().IsActive = false; c.SaveChanges(); }
        var ig = new FakeIg();
        var (proc, _) = Make.Engine(db, ig, out _);

        await proc.ProcessAsync(Seed.Dm(w, "price"), default);
        await proc.ProcessAsync(Seed.Dm(w, "price") with { BoxAccountId = Guid.NewGuid() }, default);
        Assert.Empty(ig.Calls);
    }

    [Fact]
    public async Task Lowest_priority_number_wins_when_keywords_overlap()
    {
        using var db = new TestDb();
        var w = Seed.TenantWithPage(db);
        Seed.AddAutomation(db, w, "price", priority: 5, responses: new[] { ("low", false) });
        Seed.AddAutomation(db, w, "price", priority: 1, responses: new[] { ("high", false) });
        var ig = new FakeIg();
        var (proc, _) = Make.Engine(db, ig, out _);

        await proc.ProcessAsync(Seed.Dm(w, "price"), default);
        Assert.Equal("high", Assert.Single(ig.Calls.Where(c => c.Kind == "send")).Text);
    }

    [Fact]
    public async Task Only_automations_of_the_same_trigger_type_are_used()
    {
        using var db = new TestDb();
        var w = Seed.TenantWithPage(db);
        Seed.AddAutomation(db, w, "price", trigger: TriggerType.Comment);
        var ig = new FakeIg();
        var (proc, _) = Make.Engine(db, ig, out _);

        await proc.ProcessAsync(Seed.Dm(w, "price"), default);
        Assert.Empty(ig.Calls);
    }

    [Fact]
    public async Task Inactive_automation_is_skipped()
    {
        using var db = new TestDb();
        var w = Seed.TenantWithPage(db);
        var a = Seed.AddAutomation(db, w, "price");
        using (var c = db.New()) { c.Automations.IgnoreQueryFilters().First(x => x.Id == a.Id).IsActive = false; c.SaveChanges(); }
        var ig = new FakeIg();
        var (proc, _) = Make.Engine(db, ig, out _);

        await proc.ProcessAsync(Seed.Dm(w, "price"), default);
        Assert.Empty(ig.Calls);
    }

    [Fact]
    public async Task Comment_trigger_opens_with_private_reply_then_continues_in_dm()
    {
        using var db = new TestDb();
        var w = Seed.TenantWithPage(db);
        Seed.AddAutomation(db, w, "info", trigger: TriggerType.Comment, responses: new[] { ("one", false), ("two", false) });
        var ig = new FakeIg();
        var (proc, _) = Make.Engine(db, ig, out _);

        await proc.ProcessAsync(Seed.Comment(w, "info please"), default);

        var sends = ig.Calls.Where(c => c.Kind == "send").ToList();
        Assert.True(sends[0].IsFirst);     // gateway turns IsFirst + comment into private_reply
        Assert.False(sends[1].IsFirst);
    }

    [Fact]
    public async Task Gateway_failure_is_logged_and_does_not_throw()
    {
        using var db = new TestDb();
        var w = Seed.TenantWithPage(db);
        Seed.AddAutomation(db, w, "price");
        var ig = new FakeIg { Fail = true };
        var (proc, _) = Make.Engine(db, ig, out var ctx);

        await proc.ProcessAsync(Seed.Dm(w, "price"), default);

        var log = Assert.Single(ctx.Logs.Where(l => l.Status == "error"));
        Assert.Contains("boxapi down", log.ErrorMessage);
    }

    // ---------- follow-up scheduling ----------

    [Fact]
    public async Task Automation_with_followup_schedules_a_pending_followup()
    {
        using var db = new TestDb();
        var w = Seed.TenantWithPage(db);
        Seed.AddAutomation(db, w, "price", followUpHours: 3);
        var ig = new FakeIg();
        var (proc, _) = Make.Engine(db, ig, out var ctx);

        await proc.ProcessAsync(Seed.Dm(w, "price"), default);

        var p = Assert.Single(ctx.PendingFollowUps);
        Assert.InRange((p.DueAt - p.SentAt).TotalHours, 2.99, 3.01);
        Assert.Equal("u1", p.IgUserId);
    }

    [Fact]
    public async Task A_new_dm_from_the_user_cancels_pending_followups()
    {
        using var db = new TestDb();
        var w = Seed.TenantWithPage(db);
        var a = Seed.AddAutomation(db, w, "price", followUpHours: 3);
        using (var c = db.New())
        {
            c.PendingFollowUps.Add(new PendingFollowUp { AccountId = w.AccountId, AutomationId = a.Id, IgUserId = "u1",
                SentAt = DateTime.UtcNow, DueAt = DateTime.UtcNow.AddHours(3) });
            c.SaveChanges();
        }
        var ig = new FakeIg();
        var (proc, _) = Make.Engine(db, ig, out _);

        await proc.ProcessAsync(Seed.Dm(w, "thanks, no match here"), default);

        using var check = db.New();
        Assert.True(check.PendingFollowUps.Single().Replied);
    }

    // ---------- follow-gating ----------

    [Fact]
    public async Task Response_requiring_follow_pauses_and_asks_BoxAPI_for_follow_status()
    {
        using var db = new TestDb();
        var w = Seed.TenantWithPage(db);
        Seed.AddAutomation(db, w, "price", responses: new[] { ("public", false), ("secret", true) });
        var ig = new FakeIg();
        var (proc, _) = Make.Engine(db, ig, out var ctx);

        await proc.ProcessAsync(Seed.Dm(w, "price"), default);

        Assert.Equal(new[] { "send", "follow_status" }, ig.Calls.Select(c => c.Kind));
        Assert.DoesNotContain(ig.Calls, c => c.Text == "secret");
        var chk = Assert.Single(ctx.FollowChecks);
        Assert.Equal(1, chk.ResumeFromSort);
        Assert.True(chk.FirstSent);
    }

    [Fact]
    public async Task Follower_gets_the_gated_responses_after_webhook_answer()
    {
        using var db = new TestDb();
        var w = Seed.TenantWithPage(db);
        Seed.AddAutomation(db, w, "price", responses: new[] { ("public", false), ("secret", true), ("after", false) });
        var ig = new FakeIg();
        var (proc, gate) = Make.Engine(db, ig, out _);
        await proc.ProcessAsync(Seed.Dm(w, "price"), default);
        ig.Calls.Clear();

        await gate.ResolveAsync(new FollowResult(w.BoxAccountId, "u1", true), default);

        Assert.Equal(new[] { "secret", "after" }, ig.Calls.Select(c => c.Text));
    }

    [Fact]
    public async Task Non_follower_gets_the_notice_and_not_the_secret()
    {
        using var db = new TestDb();
        var w = Seed.TenantWithPage(db);
        Seed.AddAutomation(db, w, "price", responses: new[] { ("secret", true) });
        var ig = new FakeIg();
        var (proc, gate) = Make.Engine(db, ig, out var ctx);
        await proc.ProcessAsync(Seed.Dm(w, "price"), default);
        ig.Calls.Clear();

        await gate.ResolveAsync(new FollowResult(w.BoxAccountId, "u1", false), default);

        var call = Assert.Single(ig.Calls);
        Assert.Equal("text", call.Kind);
        Assert.DoesNotContain("secret", call.Text);
        Assert.Contains(ctx.Logs, l => l.Status == "follow_required");
    }

    [Fact]
    public async Task Follow_result_is_consumed_once_and_stale_results_are_ignored()
    {
        using var db = new TestDb();
        var w = Seed.TenantWithPage(db);
        Seed.AddAutomation(db, w, "price", responses: new[] { ("secret", true) });
        var ig = new FakeIg();
        var (proc, gate) = Make.Engine(db, ig, out _);
        await proc.ProcessAsync(Seed.Dm(w, "price"), default);
        ig.Calls.Clear();

        await gate.ResolveAsync(new FollowResult(w.BoxAccountId, "u1", true), default);
        await gate.ResolveAsync(new FollowResult(w.BoxAccountId, "u1", true), default);   // duplicate webhook
        await gate.ResolveAsync(new FollowResult(w.BoxAccountId, "nobody", true), default);

        Assert.Single(ig.Calls);
    }

    [Fact]
    public async Task Follow_check_older_than_10_minutes_is_ignored()
    {
        using var db = new TestDb();
        var w = Seed.TenantWithPage(db);
        Seed.AddAutomation(db, w, "price", responses: new[] { ("secret", true) });
        var ig = new FakeIg();
        var (proc, gate) = Make.Engine(db, ig, out _);
        await proc.ProcessAsync(Seed.Dm(w, "price"), default);
        using (var c = db.New()) { c.FollowChecks.Single().CreatedAt = DateTime.UtcNow.AddMinutes(-11); c.SaveChanges(); }
        ig.Calls.Clear();

        await gate.ResolveAsync(new FollowResult(w.BoxAccountId, "u1", true), default);
        Assert.Empty(ig.Calls);
    }
}
