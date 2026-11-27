// OTP auth: sms.ir client, OTP service, JWT service, current-tenant accessor (phase 2 + patches from phases 7/8)
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using InstaSaaS.Application.Billing;
using InstaSaaS.Domain;
using InstaSaaS.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace InstaSaaS.Application.Auth;

public class AuthOptions
{
    public string JwtKey { get; set; } = default!;        // >= 32 chars, from secrets/env, never commit
    public string Issuer { get; set; } = "InstaSaaS";
    public string Audience { get; set; } = "InstaSaaS.Client";
    public int JwtMinutes { get; set; } = 60 * 24;
    public string OtpSecret { get; set; } = default!;     // HMAC key for hashing OTP codes
    public int OtpTtlMinutes { get; set; } = 2;
    public int OtpMaxAttempts { get; set; } = 5;
    public int OtpMaxRequestsPer10Min { get; set; } = 3;
}

public class SmsIrOptions
{
    public string ApiKey { get; set; } = "";
    public int OtpTemplateId { get; set; }                // verify template in the sms.ir panel, parameter name "Code"
    public int InviteTemplateId { get; set; }             // optional; 0 = invite SMS disabled, parameter name "Inviter"
    public bool UseConsole { get; set; }                  // DEV ONLY: print the OTP in the server log instead of sending an SMS
}

public interface ISmsSender
{
    Task SendOtpAsync(string phone, string code, CancellationToken ct);
    Task SendInviteAsync(string phone, string inviterMasked, CancellationToken ct);
}

// DEV ONLY (SmsIr:UseConsole=true): writes codes to the log so you can log in without a real SMS.
public class ConsoleSmsSender : ISmsSender
{
    private readonly ILogger<ConsoleSmsSender> _log;
    public ConsoleSmsSender(ILogger<ConsoleSmsSender> log) => _log = log;
    public Task SendOtpAsync(string phone, string code, CancellationToken ct)
    { _log.LogWarning("[DEV SMS] OTP for {Phone}: {Code}", phone, code); return Task.CompletedTask; }
    public Task SendInviteAsync(string phone, string inviterMasked, CancellationToken ct)
    { _log.LogWarning("[DEV SMS] Team invite for {Phone} from {Inviter}", phone, inviterMasked); return Task.CompletedTask; }
}

// sms.ir "verify" (template) endpoint. Check field names against the current sms.ir docs before production.
public class SmsIrClient : ISmsSender
{
    private readonly HttpClient _http;
    private readonly SmsIrOptions _opt;
    public SmsIrClient(HttpClient http, IOptions<SmsIrOptions> opt) { _http = http; _opt = opt.Value; }

    public Task SendOtpAsync(string phone, string code, CancellationToken ct) =>
        SendTemplateAsync(phone, _opt.OtpTemplateId, "Code", code, ct);

    public Task SendInviteAsync(string phone, string inviterMasked, CancellationToken ct) =>
        _opt.InviteTemplateId <= 0 ? Task.CompletedTask : SendTemplateAsync(phone, _opt.InviteTemplateId, "Inviter", inviterMasked, ct);

    private async Task SendTemplateAsync(string phone, int templateId, string paramName, string value, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.sms.ir/v1/send/verify")
        {
            Content = JsonContent.Create(new
            {
                mobile = phone,
                templateId,
                parameters = new[] { new { name = paramName, value } }
            })
        };
        req.Headers.Add("x-api-key", _opt.ApiKey);
        using var res = await _http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"sms.ir failed: {(int)res.StatusCode} {await res.Content.ReadAsStringAsync(ct)}");
    }
}

public static class PhoneNormalizer
{
    // Accepts 09123456789 / +989123456789 / 989123456789 / 9123456789 / Persian + Arabic digits -> 09123456789
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var s = new string(input.Select(c => c >= '۰' && c <= '۹' ? (char)('0' + (c - '۰'))
                                           : c >= '٠' && c <= '٩' ? (char)('0' + (c - '٠')) : c).ToArray());
        s = new string(s.Where(char.IsDigit).ToArray());
        if (s.StartsWith("98") && s.Length == 12) s = "0" + s[2..];
        else if (s.StartsWith("9") && s.Length == 10) s = "0" + s;
        return s.Length == 11 && s.StartsWith("09") ? s : null;
    }
}

public class AuthException : Exception
{
    public int Status { get; }
    public AuthException(string m, int status = 400) : base(m) => Status = status;
}

public class OtpAuthService
{
    private readonly AppDbContext _db;
    private readonly ISmsSender _sms;
    private readonly AuthOptions _opt;
    private readonly BillingOptions _billing;
    private readonly JwtService _jwt;

    public OtpAuthService(AppDbContext db, ISmsSender sms, IOptions<AuthOptions> opt, IOptions<BillingOptions> billing, JwtService jwt)
    { _db = db; _sms = sms; _opt = opt.Value; _billing = billing.Value; _jwt = jwt; }

    public async Task RequestOtpAsync(string rawPhone, CancellationToken ct)
    {
        var phone = PhoneNormalizer.Normalize(rawPhone) ?? throw new AuthException("شماره موبایل نامعتبر است");

        // Rate limit per phone (per-IP limiting is done by the API's rate limiter)
        var since = DateTime.UtcNow.AddMinutes(-10);
        var recent = await _db.OtpCodes.CountAsync(o => o.Phone == phone && o.CreatedAt > since, ct);
        if (recent >= _opt.OtpMaxRequestsPer10Min) throw new AuthException("تعداد درخواست‌ها زیاد است، بعداً تلاش کنید", 429);

        var code = RandomNumberGenerator.GetInt32(10000, 100000).ToString();   // 5 digits, CSPRNG
        _db.OtpCodes.Add(new OtpCode
        {
            Phone = phone,
            CodeHash = Hash(phone, code),
            ExpiresAt = DateTime.UtcNow.AddMinutes(_opt.OtpTtlMinutes)
        });
        await _db.SaveChangesAsync(ct);
        await _sms.SendOtpAsync(phone, code, ct);
    }

    public async Task<string> VerifyOtpAsync(string rawPhone, string code, CancellationToken ct)
    {
        var phone = PhoneNormalizer.Normalize(rawPhone) ?? throw new AuthException("شماره موبایل نامعتبر است");

        var otp = await _db.OtpCodes
            .Where(o => o.Phone == phone && !o.Used && o.ExpiresAt > DateTime.UtcNow)
            .OrderByDescending(o => o.CreatedAt).FirstOrDefaultAsync(ct)
            ?? throw new AuthException("کد منقضی شده یا وجود ندارد");

        if (otp.Attempts >= _opt.OtpMaxAttempts) throw new AuthException("تعداد تلاش بیش از حد مجاز است", 429);
        otp.Attempts++;

        var ok = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(otp.CodeHash), Encoding.UTF8.GetBytes(Hash(phone, code ?? "")));
        if (!ok) { await _db.SaveChangesAsync(ct); throw new AuthException("کد اشتباه است"); }
        otp.Used = true;

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Phone == phone, ct);
        if (user is null)
        {
            user = new User { Phone = phone };
            _db.Users.Add(user);
            // First login = sign-up: create own tenant on a trial, user is Owner.
            // (Team invites are NOT accepted here: the invited person accepts them explicitly via /api/team/my-invites.)
            var tenant = new Tenant { Owner = user, ExpireDate = DateTime.UtcNow.AddDays(_billing.TrialDays) };
            _db.Tenants.Add(tenant);
            _db.TenantMembers.Add(new TenantMember { Tenant = tenant, User = user, Role = TenantRole.Owner });
        }
        await _db.SaveChangesAsync(ct);

        // Active membership: best role first (Owner), then earliest.
        var m = await _db.TenantMembers.Where(x => x.UserId == user.Id)
            .OrderBy(x => x.Role).ThenBy(x => x.CreatedAt).FirstAsync(ct);
        return _jwt.Create(user.Id, m.TenantId, m.Role);
    }

    private string Hash(string phone, string code)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(_opt.OtpSecret));
        return Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes($"{phone}:{code}")));
    }
}

public class JwtService
{
    public const string RoleClaim = "role";     // plain "role" (also configured as RoleClaimType in Program.cs)
    private readonly AuthOptions _opt;
    public JwtService(IOptions<AuthOptions> opt) => _opt = opt.Value;

    public string Create(Guid userId, Guid tenantId, TenantRole role)
    {
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new Claim("tid", tenantId.ToString()),
            new Claim(RoleClaim, role.ToString())
        };
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_opt.JwtKey));
        var token = new JwtSecurityToken(_opt.Issuer, _opt.Audience, claims,
            expires: DateTime.UtcNow.AddMinutes(_opt.JwtMinutes),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

// Feeds the EF global query filter from the JWT "tid" claim
public class HttpCurrentTenant : ICurrentTenant
{
    private readonly IHttpContextAccessor _ctx;
    public HttpCurrentTenant(IHttpContextAccessor ctx) => _ctx = ctx;
    public Guid? TenantId => Guid.TryParse(_ctx.HttpContext?.User.FindFirst("tid")?.Value, out var id) ? id : null;
}
