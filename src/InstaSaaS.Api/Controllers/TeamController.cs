// Team management: invites by phone, roles (RBAC), tenant switching, instant revocation
using System.Security.Claims;
using InstaSaaS.Application.Auth;
using InstaSaaS.Domain;
using InstaSaaS.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace InstaSaaS.Api;

// The JWT carries tid + role and lives up to 24h. Without this check, a removed member (or a demoted Admin)
// would keep their old access until the token expires. Every request re-validates membership (cached 30s).
public static class MembershipValidator
{
    public static async Task ValidateAsync(TokenValidatedContext ctx)
    {
        var p = ctx.Principal;
        if (p is null || !Guid.TryParse(p.FindFirst("tid")?.Value, out var tid) || !Guid.TryParse(p.FindFirst("sub")?.Value, out var uid))
        { ctx.Fail("invalid token"); return; }

        var sp = ctx.HttpContext.RequestServices;
        var cache = sp.GetRequiredService<IMemoryCache>();
        var role = await cache.GetOrCreateAsync($"m:{tid}:{uid}", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
            var db = sp.GetRequiredService<AppDbContext>();
            var r = await db.TenantMembers.AsNoTracking().Where(x => x.TenantId == tid && x.UserId == uid)
                .Select(x => (int?)x.Role).FirstOrDefaultAsync();
            return r ?? -1;                                           // -1 = not a member (anymore)
        });

        if (role == -1) { ctx.Fail("membership revoked"); return; }

        // Trust the DB role, not the (possibly stale) role inside the token.
        var id = (ClaimsIdentity)p.Identity!;
        foreach (var c in id.FindAll(JwtService.RoleClaim).ToList()) id.RemoveClaim(c);
        id.AddClaim(new Claim(JwtService.RoleClaim, ((TenantRole)role).ToString()));
    }
}

public record InviteDto(string Phone, TenantRole Role);
public record RoleDto(TenantRole Role);
public record SwitchDto(Guid TenantId);
public record RenameDto(string Name);

[ApiController, Route("api/team"), Authorize]
public class TeamController : ControllerBase
{
    private const int MaxMembers = 10;          // default; move it into Plan if you want it per-plan
    private const int InviteValidDays = 7;

    private readonly AppDbContext _db;
    private readonly JwtService _jwt;
    private readonly ISmsSender _sms;
    private readonly IMemoryCache _cache;
    private readonly ILogger<TeamController> _log;

    public TeamController(AppDbContext db, JwtService jwt, ISmsSender sms, IMemoryCache cache, ILogger<TeamController> log)
    { _db = db; _jwt = jwt; _sms = sms; _cache = cache; _log = log; }

    private Guid Tid => Guid.Parse(User.FindFirst("tid")!.Value);
    private Guid Uid => Guid.Parse(User.FindFirst("sub")!.Value);
    private void Forget(Guid tid, Guid uid) => _cache.Remove($"m:{tid}:{uid}");

    public static string Mask(string phone) => phone.Length >= 11 ? $"{phone[..4]}***{phone[^3..]}" : "***";
    private static string Label(Tenant t) => string.IsNullOrWhiteSpace(t.Name) ? $"حساب {Mask(t.Owner.Phone)}" : t.Name!;

    [Authorize(Policy = "AnyMember"), HttpGet("members")]
    public async Task<IActionResult> Members(CancellationToken ct)
    {
        var viewer = User.IsInRole("Viewer");
        var ownerId = (await _db.Tenants.AsNoTracking().FirstAsync(t => t.Id == Tid, ct)).OwnerId;
        var rows = await _db.TenantMembers.AsNoTracking().Include(m => m.User)
            .Where(m => m.TenantId == Tid).OrderBy(m => m.Role).ThenBy(m => m.CreatedAt).ToListAsync(ct);
        return Ok(rows.Select(m => new
        {
            userId = m.UserId,
            phone = viewer ? Mask(m.User.Phone) : m.User.Phone,    // viewers don't get full phone numbers
            role = m.Role.ToString(),
            isOwner = m.UserId == ownerId,
            m.CreatedAt
        }));
    }

    [Authorize(Policy = "OwnerOnly"), HttpPatch("members/{userId:guid}/role")]
    public async Task<IActionResult> ChangeRole(Guid userId, RoleDto dto, CancellationToken ct)
    {
        if (dto.Role is not (TenantRole.Admin or TenantRole.Viewer)) return BadRequest(new { error = "نقش فقط Admin یا Viewer می‌تواند باشد" });
        var tenant = await _db.Tenants.AsNoTracking().FirstAsync(t => t.Id == Tid, ct);
        if (userId == tenant.OwnerId) return BadRequest(new { error = "نقش مالک قابل تغییر نیست" });

        var n = await _db.TenantMembers.Where(m => m.TenantId == Tid && m.UserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Role, dto.Role), ct);
        Forget(Tid, userId);
        return n == 0 ? NotFound() : NoContent();
    }

    [Authorize(Policy = "OwnerOnly"), HttpDelete("members/{userId:guid}")]
    public async Task<IActionResult> Remove(Guid userId, CancellationToken ct)
    {
        var tenant = await _db.Tenants.AsNoTracking().FirstAsync(t => t.Id == Tid, ct);
        if (userId == tenant.OwnerId) return BadRequest(new { error = "مالک حساب قابل حذف نیست" });

        var n = await _db.TenantMembers.Where(m => m.TenantId == Tid && m.UserId == userId).ExecuteDeleteAsync(ct);
        Forget(Tid, userId);
        return n == 0 ? NotFound() : NoContent();
    }

    // A non-owner member can leave on their own.
    [HttpPost("leave")]
    public async Task<IActionResult> Leave(CancellationToken ct)
    {
        var tenant = await _db.Tenants.AsNoTracking().FirstAsync(t => t.Id == Tid, ct);
        if (Uid == tenant.OwnerId) return BadRequest(new { error = "مالک نمی‌تواند حساب را ترک کند" });
        await _db.TenantMembers.Where(m => m.TenantId == Tid && m.UserId == Uid).ExecuteDeleteAsync(ct);
        Forget(Tid, Uid);
        return NoContent();
    }

    [Authorize(Policy = "OwnerOnly"), HttpPatch("tenant-name")]
    public async Task<IActionResult> Rename(RenameDto dto, CancellationToken ct)
    {
        var name = dto.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 60) return BadRequest(new { error = "نام باید ۱ تا ۶۰ کاراکتر باشد" });
        await _db.Tenants.Where(t => t.Id == Tid).ExecuteUpdateAsync(s => s.SetProperty(t => t.Name, name), ct);
        return NoContent();
    }

    // ---------- invites (owner side) ----------

    [Authorize(Policy = "AdminOrAbove"), HttpGet("invites")]
    public async Task<IActionResult> PendingInvites(CancellationToken ct) =>
        Ok(await _db.TenantInvites.AsNoTracking()
            .Where(i => i.TenantId == Tid && i.AcceptedAt == null && i.ExpiresAt > DateTime.UtcNow)
            .OrderByDescending(i => i.ExpiresAt)
            .Select(i => new { i.Id, i.Phone, Role = i.Role.ToString(), i.ExpiresAt }).ToListAsync(ct));

    [Authorize(Policy = "OwnerOnly"), HttpPost("invites")]
    public async Task<IActionResult> Invite(InviteDto dto, CancellationToken ct)
    {
        if (dto.Role is not (TenantRole.Admin or TenantRole.Viewer)) return BadRequest(new { error = "نقش فقط Admin یا Viewer می‌تواند باشد" });
        var phone = PhoneNormalizer.Normalize(dto.Phone);
        if (phone is null) return BadRequest(new { error = "شماره موبایل نامعتبر است" });

        var target = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Phone == phone, ct);
        if (target is not null && await _db.TenantMembers.AnyAsync(m => m.TenantId == Tid && m.UserId == target.Id, ct))
            return Conflict(new { error = "این کاربر از قبل عضو تیم است" });

        var now = DateTime.UtcNow;
        var inv = await _db.TenantInvites.FirstOrDefaultAsync(i => i.TenantId == Tid && i.Phone == phone && i.AcceptedAt == null, ct);
        if (inv is null)
        {
            var members = await _db.TenantMembers.CountAsync(m => m.TenantId == Tid, ct);
            var pending = await _db.TenantInvites.CountAsync(i => i.TenantId == Tid && i.AcceptedAt == null && i.ExpiresAt > now, ct);
            if (members + pending >= MaxMembers) return StatusCode(402, new { error = $"حداکثر {MaxMembers} عضو (به‌همراه دعوت‌های باز) مجاز است" });
            inv = new TenantInvite { TenantId = Tid, Phone = phone };
            _db.TenantInvites.Add(inv);
        }
        inv.Role = dto.Role;
        inv.ExpiresAt = now.AddDays(InviteValidDays);
        await _db.SaveChangesAsync(ct);

        // Best effort SMS; the invite stays valid even if sms.ir fails.
        try
        {
            var me = await _db.Users.AsNoTracking().FirstAsync(u => u.Id == Uid, ct);
            await _sms.SendInviteAsync(phone, Mask(me.Phone), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "Invite SMS failed for invite {Id}", inv.Id); }

        return Ok(new { inv.Id, inv.ExpiresAt });
    }

    [Authorize(Policy = "OwnerOnly"), HttpDelete("invites/{id:guid}")]
    public async Task<IActionResult> CancelInvite(Guid id, CancellationToken ct)
    {
        var n = await _db.TenantInvites.Where(i => i.Id == id && i.TenantId == Tid).ExecuteDeleteAsync(ct);
        return n == 0 ? NotFound() : NoContent();
    }

    // ---------- invites (invited user side): joining someone else's team needs the invited person's consent ----------

    [HttpGet("my-invites")]
    public async Task<IActionResult> MyInvites(CancellationToken ct)
    {
        var phone = (await _db.Users.AsNoTracking().FirstAsync(u => u.Id == Uid, ct)).Phone;
        var list = await _db.TenantInvites.AsNoTracking()
            .Where(i => i.Phone == phone && i.AcceptedAt == null && i.ExpiresAt > DateTime.UtcNow).ToListAsync(ct);
        var tenantIds = list.Select(i => i.TenantId).ToList();
        var tenants = await _db.Tenants.AsNoTracking().Include(t => t.Owner)
            .Where(t => tenantIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
        return Ok(list.Where(i => tenants.ContainsKey(i.TenantId))
            .Select(i => new { i.Id, tenant = Label(tenants[i.TenantId]), role = i.Role.ToString(), i.ExpiresAt }));
    }

    [HttpPost("my-invites/{id:guid}/accept")]
    public async Task<IActionResult> Accept(Guid id, CancellationToken ct)
    {
        var phone = (await _db.Users.AsNoTracking().FirstAsync(u => u.Id == Uid, ct)).Phone;
        var inv = await _db.TenantInvites.FirstOrDefaultAsync(i => i.Id == id && i.Phone == phone && i.AcceptedAt == null && i.ExpiresAt > DateTime.UtcNow, ct);
        if (inv is null) return NotFound(new { error = "دعوت‌نامه پیدا نشد یا منقضی شده است" });

        var role = inv.Role;
        var existing = await _db.TenantMembers.FirstOrDefaultAsync(m => m.TenantId == inv.TenantId && m.UserId == Uid, ct);
        if (existing is null)
        {
            if (await _db.TenantMembers.CountAsync(m => m.TenantId == inv.TenantId, ct) >= MaxMembers)
                return Conflict(new { error = "ظرفیت اعضای این حساب تکمیل شده است" });
            _db.TenantMembers.Add(new TenantMember { TenantId = inv.TenantId, UserId = Uid, Role = inv.Role });
        }
        else role = existing.Role;                       // already a member: never change the role through an old invite
        inv.AcceptedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        Forget(inv.TenantId, Uid);

        return Ok(new { token = _jwt.Create(Uid, inv.TenantId, role) });   // switch straight into the team
    }

    [HttpDelete("my-invites/{id:guid}")]
    public async Task<IActionResult> Decline(Guid id, CancellationToken ct)
    {
        var phone = (await _db.Users.AsNoTracking().FirstAsync(u => u.Id == Uid, ct)).Phone;
        var n = await _db.TenantInvites.Where(i => i.Id == id && i.Phone == phone && i.AcceptedAt == null).ExecuteDeleteAsync(ct);
        return n == 0 ? NotFound() : NoContent();
    }

    // ---------- tenant switching ----------

    [HttpGet("tenants")]
    public async Task<IActionResult> MyTenants(CancellationToken ct)
    {
        var rows = await _db.TenantMembers.AsNoTracking()
            .Include(m => m.Tenant).ThenInclude(t => t.Owner)
            .Where(m => m.UserId == Uid).OrderBy(m => m.Role).ToListAsync(ct);
        return Ok(rows.Select(m => new
        {
            tenantId = m.TenantId, name = Label(m.Tenant), role = m.Role.ToString(),
            isOwner = m.Tenant.OwnerId == Uid, current = m.TenantId == Tid
        }));
    }

    [HttpPost("switch")]
    public async Task<IActionResult> Switch(SwitchDto dto, CancellationToken ct)
    {
        var m = await _db.TenantMembers.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == dto.TenantId && x.UserId == Uid, ct);
        return m is null ? NotFound() : Ok(new { token = _jwt.Create(Uid, m.TenantId, m.Role) });
    }
}
