using InstaSaaS.Application.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace InstaSaaS.Api;

public record RequestOtpDto(string Phone);
public record VerifyOtpDto(string Phone, string Code);

[ApiController, Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly OtpAuthService _auth;
    public AuthController(OtpAuthService auth) => _auth = auth;

    [HttpPost("request-otp"), EnableRateLimiting("otp")]
    public async Task<IActionResult> RequestOtp(RequestOtpDto dto, CancellationToken ct)
    {
        await _auth.RequestOtpAsync(dto.Phone, ct);
        return Ok(new { message = "کد ارسال شد" });
    }

    [HttpPost("verify-otp"), EnableRateLimiting("otp")]
    public async Task<IActionResult> Verify(VerifyOtpDto dto, CancellationToken ct)
        => Ok(new { token = await _auth.VerifyOtpAsync(dto.Phone, dto.Code, ct) });

    [Authorize, HttpGet("me")]
    public IActionResult Me() => Ok(User.Claims.Select(c => new { c.Type, c.Value }));
}

public class ErrorMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ErrorMiddleware> _log;
    public ErrorMiddleware(RequestDelegate next, ILogger<ErrorMiddleware> log) { _next = next; _log = log; }

    public async Task Invoke(HttpContext ctx)
    {
        try { await _next(ctx); }
        catch (AuthException ex)
        {
            ctx.Response.StatusCode = ex.Status;
            await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { /* client went away */ }
        catch (Exception ex)
        {
            _log.LogError(ex, "Unhandled error");
            if (ctx.Response.HasStarted) throw;
            ctx.Response.StatusCode = 500;
            await ctx.Response.WriteAsJsonAsync(new { error = "خطای داخلی سرور" });
        }
    }
}
