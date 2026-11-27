using InstaSaaS.Application.Billing;
using InstaSaaS.Infrastructure;
using InstaSaaS.Infrastructure.Payment;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace InstaSaaS.Api;

public record CheckoutDto(Guid PlanId);

[ApiController, Route("api/billing")]
public class BillingController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly BillingService _billing;
    private readonly BillingOptions _opt;
    public BillingController(AppDbContext db, BillingService billing, IOptions<BillingOptions> opt)
    { _db = db; _billing = billing; _opt = opt.Value; }

    [AllowAnonymous, HttpGet("plans")]
    public async Task<IActionResult> Plans(CancellationToken ct) =>
        Ok(await _db.Plans.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.PriceRial)
            .Select(p => new { p.Id, p.Name, p.PriceRial, PriceToman = p.PriceRial / 10, p.DurationDays, p.MaxInstagramAccounts, p.MaxAutomations })
            .ToListAsync(ct));

    [Authorize(Policy = "AnyMember"), HttpGet("subscription")]
    public async Task<IActionResult> Subscription(CancellationToken ct)
    {
        var tid = Guid.Parse(User.FindFirst("tid")!.Value);
        var t = await _db.Tenants.AsNoTracking().Include(x => x.Plan).FirstAsync(x => x.Id == tid, ct);
        var daysLeft = t.ExpireDate is null ? (int?)null : Math.Max(0, (int)Math.Ceiling((t.ExpireDate.Value - DateTime.UtcNow).TotalDays));
        return Ok(new { status = t.SubscriptionStatus.ToString(), plan = t.Plan?.Name, t.ExpireDate, daysLeft });
    }

    [Authorize(Policy = "OwnerOnly"), HttpPost("checkout")]
    public async Task<IActionResult> Checkout(CheckoutDto dto, CancellationToken ct)
    {
        try
        {
            var url = await _billing.StartCheckoutAsync(Guid.Parse(User.FindFirst("tid")!.Value), dto.PlanId, ct);
            return Ok(new { paymentUrl = url });
        }
        catch (BillingException ex) { return BadRequest(new { error = ex.Message }); }
        catch (Exception ex) when (ex is PaymentGatewayException or HttpRequestException)
        { return StatusCode(502, new { error = "اتصال به درگاه پرداخت ناموفق بود" }); }
    }

    // Browser lands here after paying. We use ONLY trackId from the query and verify server-side.
    [AllowAnonymous, HttpGet("callback")]
    public async Task<IActionResult> Callback([FromQuery] string? trackId, CancellationToken ct)
    {
        var front = _opt.FrontendBaseUrl.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(trackId)) return Redirect($"{front}/dashboard/billing?status=failed");
        try
        {
            var (outcome, invId) = await _billing.FinalizeAsync(trackId, ct);
            if (outcome == PayOutcome.NotPaid && invId is not null) await _billing.MarkFailedAsync(invId.Value, ct);
            var status = outcome switch { PayOutcome.Paid => "success", PayOutcome.Mismatch => "review", _ => "failed" };
            return Redirect($"{front}/dashboard/billing?status={status}" + (invId is null ? "" : $"&invoice={invId}"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Gateway unreachable: the reconciliation worker will finish it; don't tell the user it failed.
            return Redirect($"{front}/dashboard/billing?status=pending");
        }
    }

    [Authorize(Policy = "OwnerOnly"), HttpGet("invoices")]
    public async Task<IActionResult> Invoices(CancellationToken ct) =>
        Ok(await _db.Invoices.AsNoTracking().OrderByDescending(i => i.CreatedAt)
            .Select(i => new { i.Id, i.AmountRial, Status = i.Status.ToString(), i.CreatedAt, i.PlanId }).Take(100).ToListAsync(ct));
}
