// BoxAPI webhook: verifies the HMAC signature, parses the {event, timestamp, account_id, data} envelope.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InstaSaaS.Domain;
using InstaSaaS.Engine;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace InstaSaaS.Api;

public class BoxApiWebhookOptions
{
    public string Secret { get; set; } = "";            // same value you set in BoxAPI panel > Settings > Webhook
    public bool AllowUnsigned { get; set; } = false;    // dev only: accept webhooks even if the signature is missing/invalid
    public bool LogRaw { get; set; } = false;           // dev only: log headers + body of every webhook (contains customer messages!)
}

// Pure parsing/verification, separated from the controller so it can be unit-tested.
public static class BoxApiWebhookParser
{
    public static bool VerifySignature(string raw, string? signature, string? timestamp, string secret, bool allowUnsigned, long nowUnix)
    {
        if (string.IsNullOrEmpty(secret)) return allowUnsigned;
        if (signature is null || !long.TryParse(timestamp, out var t)) return false;
        if (Math.Abs(nowUnix - t) > 300) return false;                       // replay protection (5 min)
        return MatchSignature(raw, signature, timestamp!, secret) is not null;
    }

    // The provider's exact signing scheme is not documented to us, so try the common ones and report which matched
    // (logged by the controller). Once the matching scheme is known, this can be narrowed to that single variant.
    public static string? MatchSignature(string raw, string signature, string timestamp, string secret)
    {
        var sig = signature.Trim();
        if (sig.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase)) sig = sig[7..];

        var keys = new List<(string Name, byte[] Key)> { ("secret", Encoding.UTF8.GetBytes(secret)) };
        if (secret.StartsWith("whsec_", StringComparison.Ordinal))
        {
            var rest = secret[6..];
            keys.Add(("secret-no-prefix", Encoding.UTF8.GetBytes(rest)));
            try { keys.Add(("secret-base64-decoded", Convert.FromBase64String(rest))); } catch (FormatException) { }
        }
        try { if (secret.Length % 2 == 0) keys.Add(("secret-hex-decoded", Convert.FromHexString(secret))); } catch (FormatException) { }

        var messages = new (string Name, string Value)[]
        {
            ("body", raw), ("ts.body", timestamp + "." + raw), ("tsbody", timestamp + raw), ("ts:body", timestamp + ":" + raw),
        };

        foreach (var (kn, key) in keys)
            foreach (var (mn, msg) in messages)
            {
                using var h = new HMACSHA256(key);
                var mac = h.ComputeHash(Encoding.UTF8.GetBytes(msg));
                if (string.Equals(Convert.ToHexString(mac), sig, StringComparison.OrdinalIgnoreCase)) return $"hex:{kn}:{mn}";
                if (Convert.ToBase64String(mac) == sig) return $"base64:{kn}:{mn}";
            }
        return null;
    }

    public static string Sign(string raw, string secret)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return "sha256=" + Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    // !!! data shapes are ASSUMED (Meta-style): BoxAPI's docs show "data": {} only. Adjust ONLY this method after
    // looking at a real payload in the BoxAPI panel's webhook logs. Returns null for ignored/unparsable events.
    public static object? Parse(string raw, string eventIdHeader, out bool unparsed)
    {
        unparsed = false;
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        var evName = Str(root, "event_type") ?? Str(root, "event");
        if (evName is null || !Guid.TryParse(Str(root, "account_id"), out var boxAccount)) return null;
        if (!root.TryGetProperty("data", out var data)) return null;

        var eventId = !string.IsNullOrEmpty(eventIdHeader) ? eventIdHeader
            : Str(root, "event_id") ?? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

        switch (evName)
        {
            case "messaging":      // documented shape: data.messaging[] = [{sender:{id}, recipient:{id}, message:{mid,text,is_echo?}}]
            case "message":
            case "direct_message":
            {
                var item = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("messaging", out var arr)
                           && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0 ? arr[0] : data;
                if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("message", out var m)
                    && m.ValueKind == JsonValueKind.Object && m.TryGetProperty("is_echo", out var echo) && echo.ValueKind == JsonValueKind.True)
                    return null;
                var sender = Nested(item, "sender", "id");
                var text = Nested(item, "message", "text");
                if (sender is null) { unparsed = true; return null; }
                if (text is null) return null;        // attachment / sticker / reaction without text: nothing to match
                return new IncomingEvent(eventId, boxAccount, TriggerType.Dm, sender, Nested(item, "sender", "username") ?? "", text);
            }
            case "comments":
            {
                var commentId = Str(data, "id");
                var from = Nested(data, "from", "id");
                var text = Str(data, "text");
                if (commentId is null || from is null || text is null) { unparsed = true; return null; }
                return new IncomingEvent(eventId, boxAccount, TriggerType.Comment, from,
                    Nested(data, "from", "username") ?? "", text, Nested(data, "media", "id"), commentId);
            }
            case "action.follow_status":
            case "action_follow_status":
            {
                // Documented envelope: data = {request, success, result, error}. The shape of "result" is not documented,
                // so look for the customer id in request/result and for a boolean "follow" flag in result.
                var customer = Nested(data, "request", "customer_id") ?? Nested(data, "result", "customer_id") ?? Str(data, "customer_id");
                bool? following = null;
                if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False)
                { unparsed = true; return null; }                     // failed check: surfaced in the log
                if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("result", out var res))
                {
                    if (res.ValueKind is JsonValueKind.True or JsonValueKind.False) following = res.ValueKind == JsonValueKind.True;
                    else if (res.ValueKind == JsonValueKind.Object)
                        foreach (var name in new[] { "is_user_follow_business", "is_following", "following", "is_follower", "follows", "is_follow", "follow" })
                            if (res.TryGetProperty(name, out var f) && f.ValueKind is JsonValueKind.True or JsonValueKind.False)
                            { following = f.ValueKind == JsonValueKind.True; break; }
                }
                if (following is null && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("is_following", out var f2)
                    && f2.ValueKind is JsonValueKind.True or JsonValueKind.False) following = f2.ValueKind == JsonValueKind.True;
                if (customer is null || following is null) { unparsed = true; return null; }
                return new FollowResult(boxAccount, customer, following.Value);
            }
            default: return null;   // messaging_postbacks, mentions, action.send_message ... ignored for now
        }
    }

    // For logging only: lets us see the event name of payloads that Parse ignores (e.g. the real DM event name).
    public static string? PeekEventName(string raw)
    {
        try { using var d = JsonDocument.Parse(raw); return Str(d.RootElement, "event_type") ?? Str(d.RootElement, "event"); }
        catch (JsonException) { return null; }
    }

    private static string? Str(JsonElement e, string n) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : null;
    private static string? Nested(JsonElement e, string a, string b) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(a, out var x) ? Str(x, b) : null;
}

[ApiController, Route("api/webhooks/boxapi")]
public class BoxApiWebhookController : ControllerBase
{
    private readonly EventQueue _queue;
    private readonly BoxApiWebhookOptions _opt;
    private readonly ILogger<BoxApiWebhookController> _log;
    public BoxApiWebhookController(EventQueue q, IOptions<BoxApiWebhookOptions> o, ILogger<BoxApiWebhookController> log)
    { _queue = q; _opt = o.Value; _log = log; }

    [HttpPost]
    public async Task<IActionResult> Receive(CancellationToken ct)
    {
        using var sr = new StreamReader(Request.Body, Encoding.UTF8);
        var raw = await sr.ReadToEndAsync(ct);                    // signature is computed over the raw body

        if (_opt.LogRaw)
            _log.LogInformation("Webhook received. Headers: [{Headers}] Body ({Len} chars): {Raw}",
                string.Join("; ", Request.Headers.Select(h => h.Key + "=" + (h.Key.Contains("sign", StringComparison.OrdinalIgnoreCase) ? "<hidden>" : h.Value.ToString()))),
                raw.Length, raw.Length > 3000 ? raw[..3000] : raw);

        var sigHeader = Request.Headers["X-BoxAPI-Signature"].FirstOrDefault();
        var tsHeader = Request.Headers["X-BoxAPI-Timestamp"].FirstOrDefault();
        var verified = BoxApiWebhookParser.VerifySignature(raw, sigHeader, tsHeader, _opt.Secret, false, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        if (verified && _opt.LogRaw && sigHeader is not null && tsHeader is not null)
            _log.LogInformation("Webhook signature OK, scheme = {Scheme}", BoxApiWebhookParser.MatchSignature(raw, sigHeader, tsHeader, _opt.Secret));
        if (!verified)
        {
            var clockSkew = long.TryParse(tsHeader, out var ts) ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ts : (long?)null;
            _log.LogWarning("Signature diagnostics: header length {SigLen}, starts with '{SigPrefix}', timestamp skew {Skew}s, body bytes {Bytes}",
                sigHeader?.Length ?? 0, sigHeader is { Length: >= 7 } ? sigHeader[..7] : sigHeader, clockSkew, Encoding.UTF8.GetByteCount(raw));
            // Visible on purpose: a silent 403 hides the real problem (wrong secret, different header names, clock drift...).
            _log.LogWarning("Webhook signature NOT verified (secret set: {HasSecret}, header names received: {Names}). {Action}",
                !string.IsNullOrEmpty(_opt.Secret), string.Join(", ", Request.Headers.Keys),
                _opt.AllowUnsigned ? "Accepted anyway because BoxApiWebhook:AllowUnsigned=true." : "Rejected with 403.");
            if (!_opt.AllowUnsigned) return StatusCode(403);
        }

        object? item;
        bool unparsed;
        try { item = BoxApiWebhookParser.Parse(raw, Request.Headers["X-BoxAPI-Event-ID"].FirstOrDefault() ?? "", out unparsed); }
        catch (JsonException) { return BadRequest(); }

        if (unparsed) _log.LogWarning("Unparsed webhook payload: {Raw}", raw.Length > 2000 ? raw[..2000] : raw);
        else if (item is null) _log.LogInformation("Webhook ignored (event type '{Event}' not handled, echo, or missing fields).", BoxApiWebhookParser.PeekEventName(raw));
        else _log.LogInformation("Webhook accepted: {Type}", item.GetType().Name);
        if (item is not null && !_queue.TryEnqueue(item)) _log.LogWarning("Event queue full, dropped an event");

        return Ok();   // BoxAPI timeout is 5s with 3 retries, so always answer fast
    }
}
