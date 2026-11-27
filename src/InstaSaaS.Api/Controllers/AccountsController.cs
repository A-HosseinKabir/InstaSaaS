using InstaSaaS.Domain;
using InstaSaaS.Infrastructure;
using InstaSaaS.Infrastructure.BoxApi;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace InstaSaaS.Api;

[ApiController, Route("api/accounts")]
public class AccountsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly BoxApiClient _box;
    private readonly BoxApiOptions _opt;
    private readonly IConfiguration _cfg;
    private readonly ILogger<AccountsController> _log;
    public AccountsController(AppDbContext db, BoxApiClient box, IOptions<BoxApiOptions> opt, IConfiguration cfg, ILogger<AccountsController> log)
    { _db = db; _box = box; _opt = opt.Value; _cfg = cfg; _log = log; }

    private Guid Tid => Guid.Parse(User.FindFirst("tid")!.Value);
    private Guid Uid => Guid.Parse(User.FindFirst("sub")!.Value);

    // Step 1: dashboard calls this, then sends the user's browser to the returned URL (official Instagram login via BoxAPI)
    [Authorize(Policy = "AdminOrAbove"), HttpPost("connect")]
    public async Task<IActionResult> Connect(CancellationToken ct)
    {
        var tenant = await _db.Tenants.Include(t => t.Plan).FirstAsync(t => t.Id == Tid, ct);
        var count = await _db.InstagramAccounts.CountAsync(a => a.TenantId == Tid, ct);
        var max = tenant.Plan?.MaxInstagramAccounts ?? 1;
        if (count >= max) return StatusCode(402, new { error = "سقف تعداد پیج‌های پلن شما پر شده است" });
        if (string.IsNullOrWhiteSpace(_opt.InstagramOauthUrl)) return StatusCode(503, new { error = "اتصال پیج هنوز پیکربندی نشده است" });

        var s = new ConnectSession { TenantId = Tid, UserId = Uid };
        _db.ConnectSessions.Add(s);
        await _db.SaveChangesAsync(ct);

        var sep = _opt.InstagramOauthUrl.Contains('?') ? '&' : '?';
        return Ok(new { url = $"{_opt.InstagramOauthUrl}{sep}id={s.Id}" });
    }

    // Step 2: BoxAPI redirects the browser here (Redirect URL in the BoxAPI panel; must be under your registered domain).
    // Query params are user-controllable: we trust only "id" (our one-time session), then VERIFY the account against BoxAPI's own list.
    [AllowAnonymous, HttpGet("callback")]
    public async Task<IActionResult> Callback([FromQuery] Guid id, [FromQuery(Name = "account_id")] Guid accountId, CancellationToken ct)
    {
        var front = (_cfg["Frontend:BaseUrl"] ?? "").TrimEnd('/');
        var cutoff = DateTime.UtcNow.AddHours(-1);
        var s = await _db.ConnectSessions.FirstOrDefaultAsync(x => x.Id == id && !x.Completed && x.CreatedAt > cutoff, ct);
        if (s is null) return Redirect($"{front}/dashboard/accounts?status=error&reason=session");

        BoxAccountInfo? info;
        try { info = (await _box.ListAccounts(ct)).FirstOrDefault(a => a.Id == accountId); }
        catch (Exception ex) when (ex is BoxApiException or HttpRequestException)
        {
            _log.LogWarning(ex, "BoxAPI account lookup failed");
            return Redirect($"{front}/dashboard/accounts?status=error&reason=boxapi");
        }
        if (info is null) return Redirect($"{front}/dashboard/accounts?status=error&reason=not_found");

        var existing = await _db.InstagramAccounts.IgnoreQueryFilters().FirstOrDefaultAsync(a => a.BoxAccountId == info.Id, ct);
        if (existing is not null && existing.TenantId != s.TenantId)
            return Redirect($"{front}/dashboard/accounts?status=error&reason=already_linked");

        if (existing is null)
            _db.InstagramAccounts.Add(new InstagramAccount
            {
                TenantId = s.TenantId, BoxAccountId = info.Id, InstagramUserId = info.InstagramUserId,
                Username = info.Username, IsActive = info.IsActive, ExpiresAt = info.ExpiresAt
            });
        else { existing.Username = info.Username; existing.IsActive = info.IsActive; existing.ExpiresAt = info.ExpiresAt; }

        s.Completed = true;
        await _db.SaveChangesAsync(ct);
        return Redirect($"{front}/dashboard/accounts?status=success");
    }

    [Authorize(Policy = "AnyMember"), HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok(await _db.InstagramAccounts.AsNoTracking().Select(a => new { a.Id, a.Username, a.IsActive, a.ExpiresAt }).ToListAsync(ct));

    // Disconnect removes the page on BoxAPI's side too (irreversible there), so the UI must ask for confirmation.
    [Authorize(Policy = "OwnerOnly"), HttpDelete("{id:guid}")]
    public async Task<IActionResult> Disconnect(Guid id, CancellationToken ct)
    {
        var acc = await _db.InstagramAccounts.FirstOrDefaultAsync(a => a.Id == id, ct);   // filter keeps it in this tenant
        if (acc is null) return NotFound();
        try { await _box.DeleteAccount(acc.BoxAccountId, ct); }
        catch (BoxApiException ex) when (ex.Status == 404) { /* already gone on BoxAPI side */ }
        catch (Exception ex) when (ex is BoxApiException or HttpRequestException)
        { return StatusCode(502, new { error = "حذف پیج در BoxAPI ناموفق بود" }); }

        _db.InstagramAccounts.Remove(acc);   // automations + pending follow-ups cascade
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}
