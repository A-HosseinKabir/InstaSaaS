using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using InstaSaaS.Api;
using InstaSaaS.Application.Auth;
using InstaSaaS.Application.Billing;
using InstaSaaS.Engine;
using InstaSaaS.Infrastructure;
using InstaSaaS.Infrastructure.BoxApi;
using InstaSaaS.Infrastructure.Payment;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

// ---------- options ----------
builder.Services.Configure<AuthOptions>(cfg.GetSection("Auth"));
builder.Services.Configure<SmsIrOptions>(cfg.GetSection("SmsIr"));
builder.Services.Configure<BoxApiOptions>(cfg.GetSection("BoxApi"));
builder.Services.Configure<BoxApiWebhookOptions>(cfg.GetSection("BoxApiWebhook"));
builder.Services.Configure<ZibalOptions>(cfg.GetSection("Zibal"));
builder.Services.Configure<BillingOptions>(cfg.GetSection("Billing"));

var auth = cfg.GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions();
// Fail fast on missing secrets instead of running with a guessable key.
if (string.IsNullOrWhiteSpace(auth.JwtKey) || auth.JwtKey.Length < 32)
    throw new InvalidOperationException("Auth:JwtKey must be at least 32 characters (set it via user-secrets or environment variables).");
if (string.IsNullOrWhiteSpace(auth.OtpSecret))
    throw new InvalidOperationException("Auth:OtpSecret is required.");

// ---------- infrastructure ----------
builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();
builder.Services.AddScoped<ICurrentTenant, HttpCurrentTenant>();
builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlServer(cfg.GetConnectionString("Default")));

if (cfg.GetValue<bool>("SmsIr:UseConsole"))
{
    if (builder.Environment.IsProduction()) throw new InvalidOperationException("SmsIr:UseConsole must never be enabled in Production.");
    builder.Services.AddSingleton<ISmsSender, ConsoleSmsSender>();
}
else builder.Services.AddHttpClient<ISmsSender, SmsIrClient>();
builder.Services.AddHttpClient<BoxApiClient>();
builder.Services.AddHttpClient<IPaymentGateway, ZibalGateway>();

builder.Services.AddScoped<OtpAuthService>();
builder.Services.AddSingleton<JwtService>();
builder.Services.AddScoped<IInstagramGateway, BoxApiGateway>();
builder.Services.AddScoped<AutomationProcessor>();
builder.Services.AddScoped<FollowGateService>();
builder.Services.AddScoped<BillingService>();

builder.Services.AddSingleton<EventQueue>();
builder.Services.AddHostedService<EventWorker>();
builder.Services.AddHostedService<FollowUpWorker>();
builder.Services.AddHostedService<BillingWorker>();

// ---------- auth ----------
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.MapInboundClaims = false;                        // keep "sub" / "role" as they are in the token
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true, ValidIssuer = auth.Issuer,
        ValidateAudience = true, ValidAudience = auth.Audience,
        ValidateLifetime = true, ClockSkew = TimeSpan.FromMinutes(1),
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(auth.JwtKey)),
        NameClaimType = "sub",
        RoleClaimType = JwtService.RoleClaim
    };
    o.Events = new JwtBearerEvents { OnTokenValidated = MembershipValidator.ValidateAsync };   // instant revocation
});

// RBAC policies: Owner > Admin > Viewer
builder.Services.AddAuthorization(o =>
{
    o.AddPolicy("OwnerOnly", p => p.RequireRole("Owner"));
    o.AddPolicy("AdminOrAbove", p => p.RequireRole("Owner", "Admin"));
    o.AddPolicy("AnyMember", p => p.RequireRole("Owner", "Admin", "Viewer"));
});

// Per-IP limiter (a plain AddFixedWindowLimiter would be ONE global bucket shared by every visitor).
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("otp", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
{
    var origins = cfg.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
    if (origins.Length > 0) p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod();
}));

// Behind a reverse proxy (nginx, IIS...) enable this so RemoteIpAddress is the real client IP.
if (cfg.GetValue<bool>("Proxy:TrustForwardedHeaders"))
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        o.KnownNetworks.Clear(); o.KnownProxies.Clear();   // only enable when the app is NOT directly reachable from the internet
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo { Title = "InstaSaaS API", Version = "v1" });
    o.CustomSchemaIds(t => t.FullName!.Replace("+", "."));
    o.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header, Description = "Paste the token returned by /api/auth/verify-otp"
    });
    o.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        [new Microsoft.OpenApi.Models.OpenApiSecurityScheme { Reference = new Microsoft.OpenApi.Models.OpenApiReference
            { Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme, Id = "Bearer" } }] = Array.Empty<string>()
    });
});
builder.Services.AddControllers().AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

if (cfg.GetValue<bool>("Database:AutoCreate"))      // dev convenience; production should use EF migrations
{
    using var scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
}

if (cfg.GetValue("Database:SeedPlans", true))       // inserts default plans only when the Plans table is empty
{
    using var scope = app.Services.CreateScope();
    await PlanSeeder.SeedAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>());
}

if (cfg.GetValue<bool>("Proxy:TrustForwardedHeaders")) app.UseForwardedHeaders();
app.UseMiddleware<ErrorMiddleware>();
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }   // /swagger (dev only)
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/health", async (AppDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "ok" }) : Results.StatusCode(503));
app.Run();

public partial class Program { }   // lets integration tests use WebApplicationFactory<Program>
