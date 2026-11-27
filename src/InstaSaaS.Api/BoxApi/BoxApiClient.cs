// BoxAPI HTTP client + IInstagramGateway implementation (endpoints per BoxAPI docs)
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using InstaSaaS.Domain;
using InstaSaaS.Engine;
using Microsoft.Extensions.Options;

namespace InstaSaaS.Infrastructure.BoxApi;

public class BoxApiOptions
{
    public string BaseUrl { get; set; } = "https://boxapi.ir";
    public string ApiKey { get; set; } = "";                  // ONE platform-level key (X-Api-Key), not per customer
    public string InstagramOauthUrl { get; set; } = "";       // "instagram_oauth_url" from your BoxAPI panel
}

public class BoxApiException : Exception
{
    public int Status { get; }
    public string Body { get; }
    public BoxApiException(int status, string body) : base($"BoxAPI {status}: {body}") { Status = status; Body = body; }
}

public record BoxButton(string Type, string Title, string? Payload = null, string? Url = null); // type: postback | web_url

public record BoxAccountInfo(Guid Id, string Username, string InstagramUserId, bool IsActive, DateTime? ExpiresAt);

// Response.Content for MessageType.Button is JSON: {"message":"...","buttons":[{"type":"web_url","title":"..","url":".."}]}
public record ButtonContent(string Message, List<BoxButton> Buttons);

public class BoxApiClient
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions J = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public BoxApiClient(HttpClient http, IOptions<BoxApiOptions> opt)
    {
        _http = http;
        // BaseUrl may contain a path (e.g. https://api.sendbox.chat/api/v1). For HttpClient to KEEP that path,
        // the base must end with "/" and request paths must be relative (no leading "/").
        _http.BaseAddress = new Uri(opt.Value.BaseUrl.TrimEnd('/') + "/");
        _http.DefaultRequestHeaders.Add("X-Api-Key", opt.Value.ApiKey);
        _http.Timeout = TimeSpan.FromSeconds(20);
    }

    // Retries 429/5xx/network errors up to 3 times. Retrying a send after a 5xx could rarely duplicate a message.
    private async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(method, path.TrimStart('/'));
                if (body is not null) req.Content = JsonContent.Create(body, options: J);
                using var res = await _http.SendAsync(req, ct);
                var text = await res.Content.ReadAsStringAsync(ct);
                if (res.IsSuccessStatusCode)
                    return string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone();

                var code = (int)res.StatusCode;
                if ((code != 429 && code < 500) || attempt >= 3) throw new BoxApiException(code, text);
                await Task.Delay(res.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
            }
            catch (HttpRequestException) when (attempt < 3) { await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct); }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < 3) { /* timeout -> retry */ }
        }
    }

    public Task SendMessage(Guid account, string recipientId, string message, IEnumerable<BoxButton>? buttons, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "/service/actions/send_message",
            new { account_id = account, recipient_id = recipientId, message, buttons }, ct);

    public Task SendImage(Guid account, string recipientId, string url, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "/service/actions/send_image",
            new { account_id = account, recipient_id = recipientId, images = new[] { new { url } } }, ct);

    public Task SendAttachment(Guid account, string recipientId, string type /*audio|video|file*/, string url, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "/service/actions/send_attachment",
            new { account_id = account, recipient_id = recipientId, attachment_type = type, url }, ct);

    public Task ReplyComment(Guid account, string commentId, string message, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "/service/actions/reply_comment", new { account_id = account, comment_id = commentId, message }, ct);

    // Opens the FIRST DM to someone who commented (valid up to 7 days; one private reply per comment)
    public Task PrivateReply(Guid account, string commentId, string message, IEnumerable<BoxButton>? buttons, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "/service/actions/private_reply",
            new { account_id = account, comment_id = commentId, message, buttons }, ct);

    // Async: the result arrives later on our webhook as event "action_follow_status"
    public Task RequestFollowStatus(Guid account, string customerId, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "/service/actions/follow_status", new { account_id = account, customer_id = customerId }, ct);

    // DELETE /service/accounts/{id}: irreversible on BoxAPI's side
    public Task DeleteAccount(Guid boxAccountId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"/service/accounts/{boxAccountId}", null, ct);

    // GET /service/accounts (paginated). The "page" query parameter name is assumed; verify against the panel docs.
    public async Task<List<BoxAccountInfo>> ListAccounts(CancellationToken ct)
    {
        var all = new List<BoxAccountInfo>();
        for (var page = 1; page <= 50; page++)
        {
            var root = await SendAsync(HttpMethod.Get, $"/service/accounts?page={page}", null, ct);
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) break;
            foreach (var a in data.EnumerateArray())
                all.Add(new BoxAccountInfo(
                    Guid.Parse(a.GetProperty("id").GetString()!),
                    a.GetProperty("username").GetString() ?? "",
                    a.GetProperty("instagram_user_id").GetString() ?? "",
                    a.TryGetProperty("is_active", out var ia) && ia.ValueKind == JsonValueKind.True,
                    a.TryGetProperty("expires_at", out var ex) && ex.ValueKind == JsonValueKind.String ? ex.GetDateTime() : null));
            if (!root.TryGetProperty("pagination", out var p) || p.GetProperty("current_page").GetInt32() >= p.GetProperty("last_page").GetInt32()) break;
        }
        return all;
    }
}

public class BoxApiGateway : IInstagramGateway
{
    private readonly BoxApiClient _box;
    private readonly ILogger<BoxApiGateway> _log;
    private static readonly JsonSerializerOptions J = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    public BoxApiGateway(BoxApiClient box, ILogger<BoxApiGateway> log) { _box = box; _log = log; }

    public Task RequestFollowStatusAsync(InstagramAccount a, string customerId, CancellationToken ct) =>
        _box.RequestFollowStatus(a.BoxAccountId, customerId, ct);

    public Task SendDirectTextAsync(InstagramAccount a, string recipientId, string text, CancellationToken ct) =>
        _box.SendMessage(a.BoxAccountId, recipientId, text, null, ct);

    public Task SendTextAsync(InstagramAccount a, IncomingEvent src, string text, bool isFirst, CancellationToken ct) =>
        Core(a, src, text, null, isFirst, ct);

    public async Task SendAsync(InstagramAccount a, IncomingEvent src, Response r, bool isFirst, CancellationToken ct)
    {
        switch (r.MessageType)
        {
            case MessageType.Text:
                await Core(a, src, r.Content, null, isFirst, ct); break;

            case MessageType.Button:
                var bc = JsonSerializer.Deserialize<ButtonContent>(r.Content, J)
                         ?? throw new InvalidOperationException("Invalid button content JSON");
                await Core(a, src, bc.Message, bc.Buttons, isFirst, ct); break;

            case MessageType.Image:
                // A comment-triggered conversation must START with private_reply, which accepts text/buttons only.
                if (src.Type == TriggerType.Comment && isFirst)
                {
                    _log.LogWarning("Image as first message of a comment trigger; sending URL as text instead");
                    await Core(a, src, r.Content, null, true, ct);
                }
                else await _box.SendImage(a.BoxAccountId, src.SenderIgId, r.Content, ct);
                break;
        }
    }

    private Task Core(InstagramAccount a, IncomingEvent src, string text, List<BoxButton>? buttons, bool isFirst, CancellationToken ct) =>
        src.Type == TriggerType.Comment && isFirst && src.CommentId is not null
            ? _box.PrivateReply(a.BoxAccountId, src.CommentId, text, buttons, ct)
            : _box.SendMessage(a.BoxAccountId, src.SenderIgId, text, buttons, ct);
}