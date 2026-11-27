using System.Security.Claims;
using InstaSaaS.Api;
using InstaSaaS.Application.Auth;
using InstaSaaS.Domain;
using InstaSaaS.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace InstaSaaS.Tests;

public class AuthTests
{
    [Fact]
    public async Task First_login_creates_user_owner_tenant_with_trial()
    {
        using var db = new TestDb();
        var sms = new FakeSms();
        using var ctx = db.New();
        var auth = Make.Auth(ctx, sms);

        await auth.RequestOtpAsync("0912 000 0001", default);
        var token = await auth.VerifyOtpAsync("09120000001", sms.Otps.Single().Code, default);

        Assert.False(string.IsNullOrEmpty(token));
        using var check = db.New();
        var m = check.TenantMembers.Include(x => x.Tenant).Single();
        Assert.Equal(TenantRole.Owner, m.Role);
        Assert.Equal(SubscriptionStatus.Trial, m.Tenant.SubscriptionStatus);
        Assert.InRange((m.Tenant.ExpireDate!.Value - DateTime.UtcNow).TotalDays, 6.9, 7.1);
    }

    [Fact]
    public async Task Second_login_reuses_the_same_user_and_tenant()
    {
        using var db = new TestDb();
        var sms = new FakeSms();
        using var ctx = db.New();
        var auth = Make.Auth(ctx, sms);

        await auth.RequestOtpAsync("09120000001", default);
        await auth.VerifyOtpAsync("09120000001", sms.Otps[0].Code, default);
        await auth.RequestOtpAsync("09120000001", default);
        await auth.VerifyOtpAsync("09120000001", sms.Otps[1].Code, default);

        using var check = db.New();
        Assert.Equal(1, check.Users.Count());
        Assert.Equal(1, check.Tenants.Count());
    }

    [Fact]
    public async Task Otp_is_stored_hashed_never_raw()
    {
        using var db = new TestDb();
        var sms = new FakeSms();
        using var ctx = db.New();
        await Make.Auth(ctx, sms).RequestOtpAsync("09120000001", default);

        using var check = db.New();
        Assert.DoesNotContain(sms.Otps.Single().Code, check.OtpCodes.Single().CodeHash);
    }

    [Fact]
    public async Task Wrong_code_is_rejected_and_locks_after_max_attempts()
    {
        using var db = new TestDb();
        var sms = new FakeSms();
        using var ctx = db.New();
        var auth = Make.Auth(ctx, sms);
        await auth.RequestOtpAsync("09120000001", default);
        var real = sms.Otps.Single().Code;
        var wrong = real == "11111" ? "22222" : "11111";

        for (var i = 0; i < 5; i++)
            await Assert.ThrowsAsync<AuthException>(() => auth.VerifyOtpAsync("09120000001", wrong, default));

        var locked = await Assert.ThrowsAsync<AuthException>(() => auth.VerifyOtpAsync("09120000001", real, default));
        Assert.Equal(429, locked.Status);       // even the correct code is refused after 5 failures
    }

    [Fact]
    public async Task Code_cannot_be_used_twice()
    {
        using var db = new TestDb();
        var sms = new FakeSms();
        using var ctx = db.New();
        var auth = Make.Auth(ctx, sms);
        await auth.RequestOtpAsync("09120000001", default);
        var code = sms.Otps.Single().Code;

        await auth.VerifyOtpAsync("09120000001", code, default);
        await Assert.ThrowsAsync<AuthException>(() => auth.VerifyOtpAsync("09120000001", code, default));
    }

    [Fact]
    public async Task Expired_code_is_rejected()
    {
        using var db = new TestDb();
        var sms = new FakeSms();
        using var ctx = db.New();
        var auth = Make.Auth(ctx, sms);
        await auth.RequestOtpAsync("09120000001", default);
        using (var c = db.New()) { c.OtpCodes.Single().ExpiresAt = DateTime.UtcNow.AddSeconds(-1); c.SaveChanges(); }

        await Assert.ThrowsAsync<AuthException>(() => auth.VerifyOtpAsync("09120000001", sms.Otps.Single().Code, default));
    }

    [Fact]
    public async Task Fourth_request_within_10_minutes_is_rate_limited()
    {
        using var db = new TestDb();
        var sms = new FakeSms();
        using var ctx = db.New();
        var auth = Make.Auth(ctx, sms);

        for (var i = 0; i < 3; i++) await auth.RequestOtpAsync("09120000001", default);
        var ex = await Assert.ThrowsAsync<AuthException>(() => auth.RequestOtpAsync("09120000001", default));
        Assert.Equal(429, ex.Status);
        await auth.RequestOtpAsync("09120000002", default);     // other numbers are unaffected
    }

    [Fact]
    public async Task Invalid_phone_is_rejected()
    {
        using var db = new TestDb();
        using var ctx = db.New();
        await Assert.ThrowsAsync<AuthException>(() => Make.Auth(ctx, new FakeSms()).RequestOtpAsync("123", default));
    }

    [Fact]
    public async Task Login_does_not_auto_accept_team_invites()
    {
        using var db = new TestDb();
        var owner = Seed.TenantWithPage(db);
        using (var c = db.New())
        {
            c.TenantInvites.Add(new TenantInvite { TenantId = owner.TenantId, Phone = "09120000009", Role = TenantRole.Admin, ExpiresAt = DateTime.UtcNow.AddDays(1) });
            c.SaveChanges();
        }
        var sms = new FakeSms();
        using var ctx = db.New();
        var auth = Make.Auth(ctx, sms);
        await auth.RequestOtpAsync("09120000009", default);
        await auth.VerifyOtpAsync("09120000009", sms.Otps.Single().Code, default);

        using var check = db.New();
        var invited = check.Users.Single(u => u.Phone == "09120000009");
        Assert.Equal(1, check.TenantMembers.Count(m => m.UserId == invited.Id));      // only its OWN tenant
        Assert.Null(check.TenantInvites.Single().AcceptedAt);
    }

    [Fact]
    public void Jwt_carries_sub_tid_and_role()
    {
        var jwt = new JwtService(Options.Create(new AuthOptions { JwtKey = new string('k', 40), OtpSecret = "s" }));
        var uid = Guid.NewGuid(); var tid = Guid.NewGuid();
        var parsed = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(jwt.Create(uid, tid, TenantRole.Admin));

        Assert.Equal(uid.ToString(), parsed.Claims.Single(c => c.Type == "sub").Value);
        Assert.Equal(tid.ToString(), parsed.Claims.Single(c => c.Type == "tid").Value);
        Assert.Equal("Admin", parsed.Claims.Single(c => c.Type == JwtService.RoleClaim).Value);
    }
}

public class TeamTests
{
    private static TeamController Controller(AppDbContext db, Guid userId, Guid tenantId, TenantRole role, FakeSms? sms = null)
    {
        var opts = Options.Create(new AuthOptions { JwtKey = new string('k', 40), OtpSecret = "s" });
        var c = new TeamController(db, new JwtService(opts), sms ?? new FakeSms(), new MemoryCache(new MemoryCacheOptions()), NullLogger<TeamController>.Instance);
        var identity = new ClaimsIdentity(new[]
        {
            new Claim("sub", userId.ToString()), new Claim("tid", tenantId.ToString()), new Claim(JwtService.RoleClaim, role.ToString())
        }, "test", "sub", JwtService.RoleClaim);
        c.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) } };
        return c;
    }

    private static Guid AddUser(TestDb db, string phone)
    {
        using var c = db.New();
        var u = new User { Phone = phone };
        c.Users.Add(u); c.SaveChanges();
        return u.Id;
    }

    [Fact]
    public async Task Invite_then_accept_adds_member_with_the_invited_role()
    {
        using var db = new TestDb();
        var w = Seed.TenantWithPage(db);
        var guestId = AddUser(db, "09120000009");
        var sms = new FakeSms();

        using (var ctx = db.New())
            Assert.IsType<OkObjectResult>(await Controller(ctx, w.UserId, w.TenantId, TenantRole.Owner, sms).Invite(new InviteDto("0912 000 0009", TenantRole.Viewer), default));
        Assert.Single(sms.Invites);

        Guid inviteId;
        using (var ctx = db.New()) inviteId = ctx.TenantInvites.Single().Id;
        using (var ctx = db.New())
            Assert.IsType<OkObjectResult>(await Controller(ctx, guestId, Guid.NewGuid(), TenantRole.Owner).Accept(inviteId, default));

        using var check = db.New();
        Assert.Equal(TenantRole.Viewer, check.TenantMembers.Single(m => m.UserId == guestId && m.TenantId == w.TenantId).Role);
        Assert.NotNull(check.TenantInvites.Single().AcceptedAt);
    }

    [Fact]
    public async Task Invite_cannot_be_accepted_by_someone_else()
    {
        using var db = new TestDb();
        var w = Seed.TenantWithPage(db);
        var stranger = AddUser(db, "09120000077");
        using (var ctx = db.New())
            await Controller(ctx, w.UserId, w.TenantId, TenantRole.Owner).Invite(new InviteDto("09120000009", TenantRole.Admin), default);
        Guid inviteId;
        using (var ctx = db.New()) inviteId = ctx.TenantInvites.Single().Id;

        using var c2 = db.New();
        Assert.IsType<NotFoundObjectResult>(await Controller(c2, stranger, Guid.NewGuid(), TenantRole.Owner).Accept(inviteId, default));
    }

    [Fact]
    public async Task Expired_invite_cannot_be_accepted()
    {
        using var db = new TestDb();
        var w = Seed.TenantWithPage(db);
        var guest = AddUser(db, "09120000009");
        Guid id;
        using (var c = db.New())
        {
            var inv = new TenantInvite { TenantId = w.TenantId, Phone = "09120000009", Role = TenantRole.Admin, ExpiresAt = DateTime.UtcNow.AddMinutes(-1) };
            c.TenantInvites.Add(inv); c.SaveChanges(); id = inv.Id;
        }
        using var ctx = db.New();
        Assert.IsType<NotFoundObjectResult>(await Controller(ctx, guest, Guid.NewGuid(), TenantRole.Owner).Accept(id, default));
    }

    [Fact]
    public async Task Cannot_invite_as_owner_or_with_bad_phone_or_existing_member()
    {
        using var db = new TestDb();
        var w = Seed.TenantWithPage(db);
        using var ctx = db.New();
        var c = Controller(ctx, w.UserId, w.TenantId, TenantRole.Owner);

        Assert.IsType<BadRequestObjectResult>(await c.Invite(new InviteDto("09120000009", TenantRole.Owner), default));
        Assert.IsType<BadRequestObjectResult>(await c.Invite(new InviteDto("12", TenantRole.Admin), default));
        Assert.IsType<ConflictObjectResult>(await c.Invite(new InviteDto("09120000001", TenantRole.Admin), default));   // the owner's own number
    }

    [Fact]
    public async Task Owner_cannot_be_removed_or_demoted()
    {
        using var db = new TestDb();
        var w = Seed.TenantWithPage(db);
        using var ctx = db.New();
        var c = Controller(ctx, w.UserId, w.TenantId, TenantRole.Owner);

        Assert.IsType<BadRequestObjectResult>(await c.Remove(w.UserId, default));
        Assert.IsType<BadRequestObjectResult>(await c.ChangeRole(w.UserId, new RoleDto(TenantRole.Viewer), default));
    }

    [Fact]
    public async Task Owner_can_change_role_and_remove_member()
    {
        using var db = new TestDb();
        var w = Seed.TenantWithPage(db);
        var guest = AddUser(db, "09120000009");
        using (var c = db.New()) { c.TenantMembers.Add(new TenantMember { TenantId = w.TenantId, UserId = guest, Role = TenantRole.Viewer }); c.SaveChanges(); }

        using (var ctx = db.New())
        {
            var c = Controller(ctx, w.UserId, w.TenantId, TenantRole.Owner);
            Assert.IsType<NoContentResult>(await c.ChangeRole(guest, new RoleDto(TenantRole.Admin), default));
        }
        using (var check = db.New()) Assert.Equal(TenantRole.Admin, check.TenantMembers.Single(m => m.UserId == guest).Role);

        using (var ctx = db.New())
            Assert.IsType<NoContentResult>(await Controller(ctx, w.UserId, w.TenantId, TenantRole.Owner).Remove(guest, default));
        using (var check = db.New()) Assert.DoesNotContain(check.TenantMembers, m => m.UserId == guest);
    }

    [Fact]
    public async Task Member_limit_counts_pending_invites()
    {
        using var db = new TestDb();
        var w = Seed.TenantWithPage(db);
        using (var c = db.New())
        {
            for (var i = 0; i < 9; i++)
                c.TenantInvites.Add(new TenantInvite { TenantId = w.TenantId, Phone = $"0912111000{i}", Role = TenantRole.Viewer, ExpiresAt = DateTime.UtcNow.AddDays(1) });
            c.SaveChanges();
        }
        using var ctx = db.New();
        var r = await Controller(ctx, w.UserId, w.TenantId, TenantRole.Owner).Invite(new InviteDto("09125550000", TenantRole.Viewer), default);
        Assert.Equal(402, Assert.IsType<ObjectResult>(r).StatusCode);   // 1 member + 9 pending = 10 = limit
    }

    [Fact]
    public async Task Owner_cannot_leave_but_member_can()
    {
        using var db = new TestDb();
        var w = Seed.TenantWithPage(db);
        var guest = AddUser(db, "09120000009");
        using (var c = db.New()) { c.TenantMembers.Add(new TenantMember { TenantId = w.TenantId, UserId = guest, Role = TenantRole.Admin }); c.SaveChanges(); }

        using (var ctx = db.New())
            Assert.IsType<BadRequestObjectResult>(await Controller(ctx, w.UserId, w.TenantId, TenantRole.Owner).Leave(default));
        using (var ctx = db.New())
            Assert.IsType<NoContentResult>(await Controller(ctx, guest, w.TenantId, TenantRole.Admin).Leave(default));
        using var check = db.New();
        Assert.DoesNotContain(check.TenantMembers, m => m.UserId == guest);
    }

    [Fact]
    public async Task Switch_only_works_for_tenants_the_user_belongs_to()
    {
        using var db = new TestDb();
        var a = Seed.TenantWithPage(db, "09120000001", "a");
        var b = Seed.TenantWithPage(db, "09120000002", "b");
        using var ctx = db.New();
        var c = Controller(ctx, a.UserId, a.TenantId, TenantRole.Owner);

        Assert.IsType<NotFoundResult>(await c.Switch(new SwitchDto(b.TenantId), default));
        Assert.IsType<OkObjectResult>(await c.Switch(new SwitchDto(a.TenantId), default));
    }

    [Fact]
    public void Phones_are_masked_for_display() => Assert.Equal("0912***789", TeamController.Mask("09123456789"));
}
