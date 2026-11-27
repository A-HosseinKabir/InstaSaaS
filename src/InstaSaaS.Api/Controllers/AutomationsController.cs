// Automations CRUD (with responses), validation, plan limits, logs API
using System.Text.Json;
using InstaSaaS.Domain;
using InstaSaaS.Engine;
using InstaSaaS.Infrastructure;
using InstaSaaS.Infrastructure.BoxApi;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace InstaSaaS.Api;

public record ResponseDto(MessageType MessageType, string Content, bool RequireFollow);

public record AutomationDto(
    Guid AccountId, TriggerType TriggerType, string Keywords, MatchType MatchType,
    int Priority, int? FollowUpAfterHours, string? FollowUpText, bool IsActive, List<ResponseDto> Responses);

public record AutomationView(
    Guid Id, Guid AccountId, TriggerType TriggerType, string Keywords, MatchType MatchType,
    int Priority, int? FollowUpAfterHours, string? FollowUpText, bool IsActive, List<ResponseDto> Responses);

public record SetActiveDto(bool IsActive);

public static class FollowUpRules
{
    public const int MaxHours = 23;   // Instagram's 24h messaging window; valid values: 1..23 (or null = disabled)
    public static bool IsValid(int? hours) => hours is null || (hours >= 1 && hours <= MaxHours);
}

public static class AutomationValidator
{
    // Limits follow Instagram/Meta messaging limits as I know them; adjust if BoxAPI/Meta reject your content.
    public const int MaxKeywords = 20, MaxKeywordLen = 100, MaxResponses = 10;
    public const int MaxTextLen = 1000, MaxButtons = 3, MaxButtonTitle = 20;
    private static readonly JsonSerializerOptions J = new() { PropertyNameCaseInsensitive = true };

    public static List<string> Validate(AutomationDto d)
    {
        var e = new List<string>();
        var kws = KeywordMatcher.SplitKeywords(d.Keywords ?? "").Distinct().ToList();
        if (kws.Count == 0) e.Add("حداقل یک کلمه کلیدی لازم است");
        if (kws.Count > MaxKeywords) e.Add($"حداکثر {MaxKeywords} کلمه کلیدی مجاز است");
        if (kws.Any(k => k.Length > MaxKeywordLen)) e.Add($"هر کلمه کلیدی حداکثر {MaxKeywordLen} کاراکتر باشد");
        if (!Enum.IsDefined(d.TriggerType)) e.Add("نوع تریگر نامعتبر است");
        if (!Enum.IsDefined(d.MatchType)) e.Add("نوع تطبیق نامعتبر است");
        if (!FollowUpRules.IsValid(d.FollowUpAfterHours)) e.Add($"زمان پیگیری باید بین ۱ تا {FollowUpRules.MaxHours} ساعت باشد");
        if (d.FollowUpText?.Length > MaxTextLen) e.Add("متن پیگیری بیش از حد طولانی است");

        if (d.Responses is null || d.Responses.Count == 0) { e.Add("حداقل یک پاسخ لازم است"); return e; }
        if (d.Responses.Count > MaxResponses) e.Add($"حداکثر {MaxResponses} پاسخ مجاز است");

        for (var i = 0; i < d.Responses.Count; i++)
        {
            var r = d.Responses[i];
            var p = $"پاسخ {i + 1}: ";
            if (string.IsNullOrWhiteSpace(r.Content)) { e.Add(p + "محتوا خالی است"); continue; }
            switch (r.MessageType)
            {
                case MessageType.Text:
                    if (r.Content.Length > MaxTextLen) e.Add(p + $"متن حداکثر {MaxTextLen} کاراکتر");
                    break;
                case MessageType.Image:
                    if (!Uri.TryCreate(r.Content.Trim(), UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps)
                        e.Add(p + "آدرس تصویر باید https باشد");
                    break;
                case MessageType.Button:
                    ValidateButtons(r.Content, p, e);
                    break;
                default: e.Add(p + "نوع پیام نامعتبر است"); break;
            }
        }
        return e;
    }

    private static void ValidateButtons(string json, string p, List<string> e)
    {
        ButtonContent? bc;
        try { bc = JsonSerializer.Deserialize<ButtonContent>(json, J); }
        catch (JsonException) { e.Add(p + "JSON دکمه‌ها نامعتبر است"); return; }
        if (bc is null || string.IsNullOrWhiteSpace(bc.Message)) { e.Add(p + "متن پیام دکمه‌دار خالی است"); return; }
        if (bc.Message.Length > MaxTextLen) e.Add(p + "متن پیام بیش از حد طولانی است");
        if (bc.Buttons is null || bc.Buttons.Count == 0 || bc.Buttons.Count > MaxButtons)
        { e.Add(p + $"تعداد دکمه‌ها باید بین ۱ تا {MaxButtons} باشد"); return; }
        foreach (var b in bc.Buttons)
        {
            if (string.IsNullOrWhiteSpace(b.Title) || b.Title.Length > MaxButtonTitle) e.Add(p + $"عنوان دکمه حداکثر {MaxButtonTitle} کاراکتر");
            if (b.Type == "web_url")
            {
                if (!Uri.TryCreate(b.Url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) e.Add(p + "لینک دکمه باید https باشد");
            }
            else if (b.Type == "postback")
            {
                if (string.IsNullOrWhiteSpace(b.Payload)) e.Add(p + "دکمه postback به payload نیاز دارد");
            }
            else e.Add(p + "نوع دکمه فقط web_url یا postback");
        }
    }
}

[ApiController, Route("api/automations"), Authorize]
public class AutomationsController : ControllerBase
{
    private const int DefaultMaxAutomations = 3;     // used when the tenant has no plan yet (trial)

    private readonly AppDbContext _db;
    public AutomationsController(AppDbContext db) => _db = db;

    private Guid Tid => Guid.Parse(User.FindFirst("tid")!.Value);

    // Tenant isolation comes from AppDbContext global query filters (InstagramAccount / Automation).

    [Authorize(Policy = "AnyMember"), HttpGet]
    public async Task<IActionResult> List([FromQuery] Guid? accountId, CancellationToken ct)
    {
        var q = _db.Automations.AsNoTracking().Include(a => a.Responses).AsQueryable();
        if (accountId is not null) q = q.Where(a => a.AccountId == accountId);
        var list = await q.OrderBy(a => a.Priority).ThenBy(a => a.Id).ToListAsync(ct);
        return Ok(list.Select(ToView));
    }

    [Authorize(Policy = "AnyMember"), HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var a = await _db.Automations.AsNoTracking().Include(x => x.Responses).FirstOrDefaultAsync(x => x.Id == id, ct);
        return a is null ? NotFound() : Ok(ToView(a));
    }

    [Authorize(Policy = "AdminOrAbove"), HttpPost]
    public async Task<IActionResult> Create(AutomationDto dto, CancellationToken ct)
    {
        var errors = AutomationValidator.Validate(dto);
        if (errors.Count > 0) return BadRequest(new { errors });

        var account = await _db.InstagramAccounts.FirstOrDefaultAsync(a => a.Id == dto.AccountId, ct);
        if (account is null) return NotFound(new { error = "پیج پیدا نشد" });

        var tenant = await _db.Tenants.Include(t => t.Plan).FirstAsync(t => t.Id == Tid, ct);
        var max = tenant.Plan?.MaxAutomations ?? DefaultMaxAutomations;
        if (await _db.Automations.CountAsync(ct) >= max)
            return StatusCode(402, new { error = "سقف تعداد اتوماسیون‌های پلن شما پر شده است" });

        var kws = KeywordMatcher.SplitKeywords(dto.Keywords).Distinct().ToList();
        var a = new Automation { AccountId = dto.AccountId };
        Apply(a, dto, kws);
        _db.Automations.Add(a);
        await _db.SaveChangesAsync(ct);

        return Ok(new { automation = ToView(a), warnings = await WarningsAsync(dto, kws, a.Id, ct) });
    }

    // Full replace: the dashboard sends the whole automation including its responses.
    [Authorize(Policy = "AdminOrAbove"), HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, AutomationDto dto, CancellationToken ct)
    {
        var errors = AutomationValidator.Validate(dto);
        if (errors.Count > 0) return BadRequest(new { errors });

        var a = await _db.Automations.Include(x => x.Responses).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null) return NotFound();
        if (a.AccountId != dto.AccountId) return BadRequest(new { error = "پیج یک اتوماسیون قابل تغییر نیست" });

        var kws = KeywordMatcher.SplitKeywords(dto.Keywords).Distinct().ToList();
        _db.Responses.RemoveRange(a.Responses);
        a.Responses.Clear();
        Apply(a, dto, kws);
        // New Response rows already carry a generated Guid key, so EF would treat them as existing (Modified) and the
        // UPDATE would hit 0 rows (DbUpdateConcurrencyException). Mark them as Added explicitly.
        foreach (var r in a.Responses) _db.Entry(r).State = EntityState.Added;
        await _db.SaveChangesAsync(ct);

        return Ok(new { automation = ToView(a), warnings = await WarningsAsync(dto, kws, a.Id, ct) });
    }

    [Authorize(Policy = "AdminOrAbove"), HttpPatch("{id:guid}/active")]
    public async Task<IActionResult> SetActive(Guid id, SetActiveDto dto, CancellationToken ct)
    {
        var a = await _db.Automations.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null) return NotFound();
        a.IsActive = dto.IsActive;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [Authorize(Policy = "AdminOrAbove"), HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var a = await _db.Automations.FirstOrDefaultAsync(x => x.Id == id, ct);   // filter guarantees same tenant
        if (a is null) return NotFound();
        _db.Automations.Remove(a);                                                  // responses cascade
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static void Apply(Automation a, AutomationDto dto, List<string> kws)
    {
        a.TriggerType = dto.TriggerType;
        a.Keywords = string.Join(",", kws);
        a.MatchType = dto.MatchType;
        a.Priority = dto.Priority;
        a.FollowUpAfterHours = dto.FollowUpAfterHours;
        a.FollowUpText = string.IsNullOrWhiteSpace(dto.FollowUpText) ? null : dto.FollowUpText.Trim();
        a.IsActive = dto.IsActive;
        var i = 0;
        foreach (var r in dto.Responses)
            a.Responses.Add(new Response { MessageType = r.MessageType, Content = r.Content.Trim(), RequireFollow = r.RequireFollow, SortOrder = i++ });
    }

    private static AutomationView ToView(Automation a) => new(
        a.Id, a.AccountId, a.TriggerType, a.Keywords, a.MatchType, a.Priority, a.FollowUpAfterHours, a.FollowUpText, a.IsActive,
        a.Responses.OrderBy(r => r.SortOrder).Select(r => new ResponseDto(r.MessageType, r.Content, r.RequireFollow)).ToList());

    // Overlapping keywords are legal but ambiguous: lowest Priority wins. Warn instead of blocking.
    private async Task<List<string>> WarningsAsync(AutomationDto dto, List<string> kws, Guid selfId, CancellationToken ct)
    {
        var w = new List<string>();
        var others = await _db.Automations.AsNoTracking()
            .Where(a => a.AccountId == dto.AccountId && a.TriggerType == dto.TriggerType && a.IsActive && a.Id != selfId)
            .Select(a => a.Keywords).ToListAsync(ct);
        var overlap = others.SelectMany(KeywordMatcher.SplitKeywords).Intersect(kws).ToList();
        if (overlap.Count > 0) w.Add("این کلمات با اتوماسیون دیگری هم‌پوشانی دارند (اولویت کمتر برنده است): " + string.Join("، ", overlap));
        if (dto.TriggerType == TriggerType.Comment && dto.FollowUpAfterHours is > 0)
            w.Add("پیگیری خودکار برای اتوماسیون‌های کامنتی ممکن است توسط اینستاگرام رد شود.");
        return w;
    }
}

public record LogView(long Id, Guid AccountId, string EventType, string Status, string? ErrorMessage, DateTime CreatedAt);

[ApiController, Route("api/logs"), Authorize(Policy = "AnyMember")]
public class LogsController : ControllerBase
{
    private readonly AppDbContext _db;
    public LogsController(AppDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] Guid? accountId, [FromQuery] string? status,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        page = Math.Max(page, 1);

        // Logs have no TenantId column; scope through the tenant's own (filtered) accounts.
        var mine = _db.InstagramAccounts.Select(a => a.Id);
        var q = _db.Logs.AsNoTracking().Where(l => mine.Contains(l.AccountId));
        if (accountId is not null) q = q.Where(l => l.AccountId == accountId);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(l => l.Status == status);

        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(l => l.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(l => new LogView(l.Id, l.AccountId, l.EventType, l.Status, l.ErrorMessage, l.CreatedAt)).ToListAsync(ct);
        return Ok(new { total, page, pageSize, items });
    }
}
