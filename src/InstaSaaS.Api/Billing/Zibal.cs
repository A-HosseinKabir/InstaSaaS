// Payment gateway abstraction + Zibal implementation.
// Zibal: POST {base}/v1/request, POST {base}/v1/verify, redirect to {base}/start/{trackId}. Amounts are in RIALS. Merchant "zibal" = sandbox.
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace InstaSaaS.Infrastructure.Payment;

public class ZibalOptions
{
    public string Merchant { get; set; } = "zibal";                 // "zibal" = sandbox; real merchant code goes in secrets
    public string BaseUrl { get; set; } = "https://gateway.zibal.ir";
}

public record GatewayStart(long TrackId, string PaymentUrl);

public record GatewayVerify(
    int Result,                 // 100 = ok, 201 = already verified, 202 = not paid / failed, other = error
    bool Paid,
    long? AmountRial,
    string? OrderId,
    string? RefNumber,
    DateTime? PaidAt,
    string? Message);

public class PaymentGatewayException : Exception
{
    public int Code { get; }
    public PaymentGatewayException(int code, string? message) : base($"Gateway error {code}: {message}") => Code = code;
}

public interface IPaymentGateway
{
    string Name { get; }
    Task<GatewayStart> StartAsync(long amountRial, string orderId, string callbackUrl, string description, CancellationToken ct);
    Task<GatewayVerify> VerifyAsync(long trackId, CancellationToken ct);
}

public class ZibalGateway : IPaymentGateway
{
    private readonly HttpClient _http;
    private readonly ZibalOptions _opt;
    public string Name => "zibal";

    public ZibalGateway(HttpClient http, IOptions<ZibalOptions> opt)
    {
        _http = http; _opt = opt.Value;
        _http.BaseAddress = new Uri(_opt.BaseUrl);
        _http.Timeout = TimeSpan.FromSeconds(20);
    }

    public async Task<GatewayStart> StartAsync(long amountRial, string orderId, string callbackUrl, string description, CancellationToken ct)
    {
        // Zibal minimum is 1,000 Rials. "mobile" is deliberately NOT sent (it can restrict which cards may be used).
        using var res = await _http.PostAsJsonAsync("/v1/request",
            new { merchant = _opt.Merchant, amount = amountRial, callbackUrl, description, orderId }, ct);
        var root = await ReadAsync(res, ct);

        var result = Int(root, "result") ?? -1;
        if (result != 100) throw new PaymentGatewayException(result, Str(root, "message"));
        var track = root.GetProperty("trackId").GetInt64();
        return new GatewayStart(track, $"{_opt.BaseUrl.TrimEnd('/')}/start/{track}");
    }

    public async Task<GatewayVerify> VerifyAsync(long trackId, CancellationToken ct)
    {
        using var res = await _http.PostAsJsonAsync("/v1/verify", new { merchant = _opt.Merchant, trackId }, ct);
        var root = await ReadAsync(res, ct);

        var result = Int(root, "result") ?? -1;
        DateTime? paidAt = DateTime.TryParse(Str(root, "paidAt"), out var d) ? d.ToUniversalTime() : null;
        return new GatewayVerify(
            result,
            Paid: result is 100 or 201,
            AmountRial: root.TryGetProperty("amount", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetInt64() : null,
            OrderId: Str(root, "orderId"),
            RefNumber: Str(root, "refNumber"),
            PaidAt: paidAt,
            Message: Str(root, "message"));
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage res, CancellationToken ct)
    {
        var text = await res.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(text)) throw new HttpRequestException($"Zibal HTTP {(int)res.StatusCode}: empty body");
        try { return JsonDocument.Parse(text).RootElement.Clone(); }
        catch (JsonException) { throw new HttpRequestException($"Zibal HTTP {(int)res.StatusCode}: invalid JSON"); }
    }
    private static string? Str(JsonElement e, string n) =>
        e.TryGetProperty(n, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : null;
    private static int? Int(JsonElement e, string n) =>
        e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
}
