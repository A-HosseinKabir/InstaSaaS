// Billing: checkout, server-side verify, subscription activation.
using InstaSaaS.Domain;
using InstaSaaS.Infrastructure;
using InstaSaaS.Infrastructure.Payment;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace InstaSaaS.Application.Billing;

public class BillingOptions
{
    public string PublicApiBaseUrl { get; set; } = "";   // e.g. https://api.yourdomain.com (must be reachable by the user's browser)
    public string FrontendBaseUrl { get; set; } = "";    // e.g. https://app.yourdomain.com
    public int TrialDays { get; set; } = 7;
}

public class BillingException : Exception { public BillingException(string m) : base(m) { } }
public enum PayOutcome { Unknown, Paid, NotPaid, Mismatch }

public class BillingService
{
    private const string GatewayName = "zibal";
    private readonly AppDbContext _db;
    private readonly IPaymentGateway _gw;
    private readonly BillingOptions _opt;
    private readonly ILogger<BillingService> _log;

    public BillingService(AppDbContext db, IPaymentGateway gw, IOptions<BillingOptions> opt, ILogger<BillingService> log)
    { _db = db; _gw = gw; _opt = opt.Value; _log = log; }

    // Price ALWAYS comes from the Plan row, never from the client.
    public async Task<string> StartCheckoutAsync(Guid tenantId, Guid planId, CancellationToken ct)
    {
        var plan = await _db.Plans.FirstOrDefaultAsync(p => p.Id == planId && p.IsActive, ct)
                   ?? throw new BillingException("پلن نامعتبر است");
        if (plan.PriceRial < 1000) throw new BillingException("مبلغ پلن کمتر از حداقل درگاه است");

        var inv = new Invoice { TenantId = tenantId, PlanId = plan.Id, AmountRial = plan.PriceRial };
        _db.Invoices.Add(inv);
        await _db.SaveChangesAsync(ct);

        try
        {
            var start = await _gw.StartAsync(inv.AmountRial, inv.Id.ToString(),
                $"{_opt.PublicApiBaseUrl.TrimEnd('/')}/api/billing/callback", $"اشتراک {plan.Name}", ct);
            _db.Transactions.Add(new PaymentTransaction { InvoiceId = inv.Id, Gateway = GatewayName, Authority = start.TrackId.ToString() });
            await _db.SaveChangesAsync(ct);
            return start.PaymentUrl;
        }
        catch
        {
            inv.Status = InvoiceStatus.Failed;
            await _db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    // Idempotent: safe to call from the browser callback AND from the reconciliation worker, even concurrently.
    public async Task<(PayOutcome Outcome, Guid? InvoiceId)> FinalizeAsync(string trackId, CancellationToken ct)
    {
        if (!long.TryParse(trackId, out var track)) return (PayOutcome.Unknown, null);

        var tx = await _db.Transactions.FirstOrDefaultAsync(t => t.Authority == trackId && t.Gateway == GatewayName, ct);
        if (tx is null) return (PayOutcome.Unknown, null);
        var inv = await _db.Invoices.IgnoreQueryFilters().FirstAsync(i => i.Id == tx.InvoiceId, ct);
        if (inv.Status == InvoiceStatus.Paid) return (PayOutcome.Paid, inv.Id);

        var v = await _gw.VerifyAsync(track, ct);          // network errors propagate: caller shows "pending", worker retries later
        if (!v.Paid) return (PayOutcome.NotPaid, inv.Id);

        // Never trust the redirect: compare gateway-reported amount and orderId with OUR invoice.
        if (v.AmountRial != inv.AmountRial || v.OrderId != inv.Id.ToString())
        {
            _log.LogError("Payment mismatch! invoice {Inv} expected {Exp} got {Amt}/{Order}", inv.Id, inv.AmountRial, v.AmountRial, v.OrderId);
            return (PayOutcome.Mismatch, inv.Id);          // money may have moved: needs manual review, subscription NOT activated
        }

        await using var dbTx = await _db.Database.BeginTransactionAsync(ct);
        // Atomic claim: only one caller flips the invoice to Paid and extends the subscription.
        var claimed = await _db.Invoices.IgnoreQueryFilters()
            .Where(i => i.Id == inv.Id && i.Status != InvoiceStatus.Paid)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, InvoiceStatus.Paid), ct);
        if (claimed == 0) { await dbTx.RollbackAsync(ct); return (PayOutcome.Paid, inv.Id); }

        tx.Success = true;
        tx.RefId = v.RefNumber;

        var tenant = await _db.Tenants.FirstAsync(t => t.Id == inv.TenantId, ct);
        var plan = await _db.Plans.FirstAsync(p => p.Id == inv.PlanId, ct);
        var now = DateTime.UtcNow;
        // Renewing the same plan while active stacks time; new plan / expired starts from now (no proration).
        var renewing = tenant.PlanId == plan.Id && tenant.SubscriptionStatus == SubscriptionStatus.Active && tenant.ExpireDate > now;
        var from = renewing ? tenant.ExpireDate!.Value : now;
        tenant.PlanId = plan.Id;
        tenant.SubscriptionStatus = SubscriptionStatus.Active;
        tenant.ExpireDate = from.AddDays(plan.DurationDays);

        await _db.SaveChangesAsync(ct);
        await dbTx.CommitAsync(ct);
        return (PayOutcome.Paid, inv.Id);
    }

    public Task MarkFailedAsync(Guid invoiceId, CancellationToken ct) =>
        _db.Invoices.IgnoreQueryFilters().Where(i => i.Id == invoiceId && i.Status == InvoiceStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, InvoiceStatus.Failed), ct);
}
