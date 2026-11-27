using System.Text.Json;
using InstaSaaS.Api;
using InstaSaaS.Application.Auth;
using InstaSaaS.Domain;
using InstaSaaS.Engine;
using Xunit;

namespace InstaSaaS.Tests;

public class PhoneNormalizerTests
{
    [Theory]
    [InlineData("09123456789", "09123456789")]
    [InlineData("+989123456789", "09123456789")]
    [InlineData("989123456789", "09123456789")]
    [InlineData("9123456789", "09123456789")]
    [InlineData("۰۹۱۲۳۴۵۶۷۸۹", "09123456789")]        // Persian digits
    [InlineData("٠٩١٢٣٤٥٦٧٨٩", "09123456789")]        // Arabic-Indic digits
    [InlineData(" 0912 345 6789 ", "09123456789")]
    [InlineData("0912-345-6789", "09123456789")]
    public void Normalizes_valid_numbers(string input, string expected) =>
        Assert.Equal(expected, PhoneNormalizer.Normalize(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0912345678")]          // too short
    [InlineData("091234567890")]        // too long
    [InlineData("08123456789")]         // not a mobile prefix
    [InlineData("abc")]
    public void Rejects_invalid_numbers(string? input) => Assert.Null(PhoneNormalizer.Normalize(input));
}

public class KeywordMatcherTests
{
    [Fact] public void Contains_match_is_case_insensitive() =>
        Assert.True(KeywordMatcher.IsMatch("Send me the PRICE please", "price", MatchType.Contains));

    [Fact] public void Exact_requires_the_whole_message() =>
        Assert.False(KeywordMatcher.IsMatch("send price", "price", MatchType.Exact));

    [Fact] public void Exact_ignores_surrounding_whitespace_and_case() =>
        Assert.True(KeywordMatcher.IsMatch("  Price  ", "price", MatchType.Exact));

    [Fact] public void Arabic_yeh_and_kaf_match_persian_ones() =>
        Assert.True(KeywordMatcher.IsMatch("كتاب", "کتاب", MatchType.Exact));

    [Fact] public void Persian_and_ascii_digits_are_equivalent() =>
        Assert.True(KeywordMatcher.IsMatch("کد ۱۲۳", "کد 123", MatchType.Exact));

    [Fact] public void Zwnj_is_ignored() =>
        Assert.True(KeywordMatcher.IsMatch("می‌خوام", "میخوام", MatchType.Exact));

    [Fact] public void Any_keyword_of_the_list_matches() =>
        Assert.True(KeywordMatcher.IsMatch("قیمت؟", "سایز, قیمت ،رنگ", MatchType.Contains));

    [Fact] public void Empty_text_never_matches() =>
        Assert.False(KeywordMatcher.IsMatch("", "x", MatchType.Contains));

    [Fact] public void Empty_keyword_list_never_matches() =>
        Assert.False(KeywordMatcher.IsMatch("hello", " , ,", MatchType.Contains));

    [Fact] public void SplitKeywords_normalizes_and_drops_blanks() =>
        Assert.Equal(new[] { "a", "b", "c" }, KeywordMatcher.SplitKeywords("A, b،C,,\n").ToArray());
}

public class SubscriptionRulesTests
{
    private static Tenant T(SubscriptionStatus s, DateTime? exp) => new() { SubscriptionStatus = s, ExpireDate = exp };
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact] public void Active_in_future_is_ok() => Assert.True(SubscriptionRules.IsActive(T(SubscriptionStatus.Active, Now.AddDays(1)), Now));
    [Fact] public void Trial_in_future_is_ok() => Assert.True(SubscriptionRules.IsActive(T(SubscriptionStatus.Trial, Now.AddDays(1)), Now));
    [Fact] public void Active_but_past_date_is_not_ok() => Assert.False(SubscriptionRules.IsActive(T(SubscriptionStatus.Active, Now.AddSeconds(-1)), Now));
    [Fact] public void Expired_status_is_not_ok() => Assert.False(SubscriptionRules.IsActive(T(SubscriptionStatus.Expired, Now.AddDays(5)), Now));
    [Fact] public void Cancelled_status_is_not_ok() => Assert.False(SubscriptionRules.IsActive(T(SubscriptionStatus.Cancelled, Now.AddDays(5)), Now));
}

public class FollowUpRulesTests
{
    [Theory]
    [InlineData(null, true)] [InlineData(1, true)] [InlineData(23, true)]
    [InlineData(0, false)] [InlineData(24, false)] [InlineData(-3, false)]
    public void Validates_hours(int? hours, bool ok) => Assert.Equal(ok, FollowUpRules.IsValid(hours));
}

public class WebhookTests
{
    private const string Secret = "s3cret";
    private static readonly Guid Acc = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static string W(string json) => json.Replace("ACC", Acc.ToString());

    [Fact]
    public void Real_provider_comment_envelope_uses_event_type_and_body_event_id()
    {
        var raw = W("""{"event_id":"client_ig_1_abc_ACC","event_type":"comments","account_id":"ACC","data":{"from":{"id":"2103158233738024","username":"amir"},"media":{"id":"17958704142289594","media_product_type":"FEED"},"id":"18429022276156372","text":"قیمت"}}""");
        var ev = Assert.IsType<IncomingEvent>(BoxApiWebhookParser.Parse(raw, "", out var unparsed));
        Assert.False(unparsed);
        Assert.Equal(TriggerType.Comment, ev.Type);
        Assert.Equal("قیمت", ev.Text);
        Assert.Equal("client_ig_1_abc_" + Acc, ev.ExternalEventId);
        Assert.Equal("comments", BoxApiWebhookParser.PeekEventName(raw));
    }

    [Fact]
    public void Documented_messaging_envelope_is_parsed_as_a_dm()
    {
        var raw = W("""{"event_id":"client_ig_9","event_type":"messaging","account_id":"ACC","data":{"time":1,"id":"page","messaging":[{"sender":{"id":"42"},"recipient":{"id":"page"},"timestamp":1,"message":{"mid":"m1","text":"قیمت"}}]}}""");
        var ev = Assert.IsType<IncomingEvent>(BoxApiWebhookParser.Parse(raw, "", out _));
        Assert.Equal(TriggerType.Dm, ev.Type);
        Assert.Equal("42", ev.SenderIgId);
        Assert.Equal("قیمت", ev.Text);
    }

    [Fact]
    public void Messaging_echo_and_textless_messages_are_ignored()
    {
        var echo = W("""{"event_type":"messaging","account_id":"ACC","data":{"messaging":[{"sender":{"id":"page"},"message":{"text":"x","is_echo":true}}]}}""");
        Assert.Null(BoxApiWebhookParser.Parse(echo, "", out var u1)); Assert.False(u1);
        var sticker = W("""{"event_type":"messaging","account_id":"ACC","data":{"messaging":[{"sender":{"id":"42"},"message":{"mid":"m"}}]}}""");
        Assert.Null(BoxApiWebhookParser.Parse(sticker, "", out var u2)); Assert.False(u2);
    }

    [Fact]
    public void Action_follow_status_envelope_is_parsed()
    {
        var raw = W("""{"event_type":"action.follow_status","account_id":"ACC","data":{"request":{"customer_id":"42"},"success":true,"result":{"is_following":true},"error":null}}""");
        var r = Assert.IsType<FollowResult>(BoxApiWebhookParser.Parse(raw, "", out _));
        Assert.Equal("42", r.CustomerId); Assert.True(r.IsFollowing);
    }

    [Fact]
    public void Real_follow_status_result_field_is_is_user_follow_business()
    {
        var raw = W("""{"event_type":"action.follow_status","account_id":"ACC","data":{"request":{"account_id":"ACC","customer_id":"2103158233738024"},"success":true,"result":{"is_user_follow_business":false,"customer_id":"2103158233738024"},"error":null}}""");
        var r = Assert.IsType<FollowResult>(BoxApiWebhookParser.Parse(raw, "", out var unparsed));
        Assert.False(unparsed); Assert.False(r.IsFollowing); Assert.Equal("2103158233738024", r.CustomerId);
    }

    [Fact]
    public void Signature_scheme_matcher_accepts_timestamp_prefixed_and_base64_variants()
    {
        var raw = "{\"a\":1}"; var ts = "1700000000"; var secret = "whsec_abc123";
        using var h = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes(secret));
        var mac = h.ComputeHash(System.Text.Encoding.UTF8.GetBytes(ts + "." + raw));
        Assert.Equal("hex:secret:ts.body", BoxApiWebhookParser.MatchSignature(raw, Convert.ToHexString(mac).ToLowerInvariant(), ts, secret));
        Assert.Equal("base64:secret:ts.body", BoxApiWebhookParser.MatchSignature(raw, Convert.ToBase64String(mac), ts, secret));
        Assert.Null(BoxApiWebhookParser.MatchSignature(raw, "deadbeef", ts, secret));
    }

    [Fact]
    public void Valid_signature_passes()
    {
        var raw = "{\"event\":\"message\"}";
        var sig = BoxApiWebhookParser.Sign(raw, Secret);
        Assert.True(BoxApiWebhookParser.VerifySignature(raw, sig, "1000", Secret, false, 1000));
    }

    [Fact]
    public void Tampered_body_fails()
    {
        var sig = BoxApiWebhookParser.Sign("{\"a\":1}", Secret);
        Assert.False(BoxApiWebhookParser.VerifySignature("{\"a\":2}", sig, "1000", Secret, false, 1000));
    }

    [Fact]
    public void Wrong_secret_fails() =>
        Assert.False(BoxApiWebhookParser.VerifySignature("x", BoxApiWebhookParser.Sign("x", "other"), "1000", Secret, false, 1000));

    [Fact]
    public void Stale_timestamp_fails_replay_protection()
    {
        var sig = BoxApiWebhookParser.Sign("x", Secret);
        Assert.False(BoxApiWebhookParser.VerifySignature("x", sig, "1000", Secret, false, 1000 + 301));
        Assert.True(BoxApiWebhookParser.VerifySignature("x", sig, "1000", Secret, false, 1000 + 299));
    }

    [Fact] public void Missing_signature_or_timestamp_fails()
    {
        Assert.False(BoxApiWebhookParser.VerifySignature("x", null, "1000", Secret, false, 1000));
        Assert.False(BoxApiWebhookParser.VerifySignature("x", "sha256=00", null, Secret, false, 1000));
    }

    [Fact] public void Without_secret_only_allowed_when_AllowUnsigned()
    {
        Assert.False(BoxApiWebhookParser.VerifySignature("x", null, null, "", false, 1000));
        Assert.True(BoxApiWebhookParser.VerifySignature("x", null, null, "", true, 1000));
    }

    [Fact]
    public void Parses_direct_message()
    {
        var raw = W("""{"event":"message","timestamp":1,"account_id":"ACC","data":{"sender":{"id":"42","username":"bob"},"message":{"text":"قیمت"}}}""");
        var ev = Assert.IsType<IncomingEvent>(BoxApiWebhookParser.Parse(raw, "evt-1", out var unparsed));
        Assert.False(unparsed);
        Assert.Equal(TriggerType.Dm, ev.Type);
        Assert.Equal("42", ev.SenderIgId);
        Assert.Equal("قیمت", ev.Text);
        Assert.Equal(Acc, ev.BoxAccountId);
        Assert.Equal("evt-1", ev.ExternalEventId);
    }

    [Fact]
    public void Echo_messages_are_ignored()
    {
        var raw = W("""{"event":"message","account_id":"ACC","data":{"sender":{"id":"42"},"message":{"text":"hi","is_echo":true}}}""");
        Assert.Null(BoxApiWebhookParser.Parse(raw, "e", out var unparsed));
        Assert.False(unparsed);
    }

    [Fact]
    public void Parses_comment()
    {
        var raw = W("""{"event":"comments","account_id":"ACC","data":{"id":"c9","text":"info","from":{"id":"7","username":"amy"},"media":{"id":"m1"}}}""");
        var ev = Assert.IsType<IncomingEvent>(BoxApiWebhookParser.Parse(raw, "", out _));
        Assert.Equal(TriggerType.Comment, ev.Type);
        Assert.Equal("c9", ev.CommentId);
        Assert.Equal("m1", ev.MediaId);
        Assert.False(string.IsNullOrEmpty(ev.ExternalEventId));    // falls back to a body hash when no event-id header
    }

    [Fact]
    public void Parses_follow_status()
    {
        var raw = W("""{"event":"action_follow_status","account_id":"ACC","data":{"customer_id":"42","is_following":true}}""");
        var r = Assert.IsType<FollowResult>(BoxApiWebhookParser.Parse(raw, "", out _));
        Assert.True(r.IsFollowing);
        Assert.Equal("42", r.CustomerId);
    }

    [Fact]
    public void Unknown_shape_is_flagged_not_thrown()
    {
        var raw = W("""{"event":"message","account_id":"ACC","data":{"weird":1}}""");
        Assert.Null(BoxApiWebhookParser.Parse(raw, "", out var unparsed));
        Assert.True(unparsed);
    }

    [Fact] public void Unknown_events_and_bad_account_are_ignored()
    {
        Assert.Null(BoxApiWebhookParser.Parse(W("""{"event":"typing","account_id":"ACC","data":{}}"""), "", out var u1));
        Assert.False(u1);
        Assert.Null(BoxApiWebhookParser.Parse("""{"event":"message","account_id":"nope","data":{}}""", "", out _));
    }

    [Fact] public void Malformed_json_throws_JsonException() =>
        Assert.ThrowsAny<JsonException>(() => BoxApiWebhookParser.Parse("{not json", "", out _));
}

public class AutomationValidatorTests
{
    private static AutomationDto Ok(params ResponseDto[] r) =>
        new(Guid.NewGuid(), TriggerType.Dm, "price", MatchType.Contains, 0, null, null, true,
            r.Length > 0 ? r.ToList() : new() { new ResponseDto(MessageType.Text, "hello", false) });

    [Fact] public void Valid_automation_has_no_errors() => Assert.Empty(AutomationValidator.Validate(Ok()));
    [Fact] public void Empty_keywords_rejected() => Assert.NotEmpty(AutomationValidator.Validate(Ok() with { Keywords = " , " }));
    [Fact] public void Too_many_keywords_rejected() =>
        Assert.NotEmpty(AutomationValidator.Validate(Ok() with { Keywords = string.Join(",", Enumerable.Range(0, 21).Select(i => "k" + i)) }));
    [Fact] public void No_responses_rejected() => Assert.NotEmpty(AutomationValidator.Validate(Ok() with { Responses = new() }));
    [Fact] public void Followup_hours_out_of_range_rejected() => Assert.NotEmpty(AutomationValidator.Validate(Ok() with { FollowUpAfterHours = 30 }));
    [Fact] public void Text_too_long_rejected() =>
        Assert.NotEmpty(AutomationValidator.Validate(Ok(new ResponseDto(MessageType.Text, new string('x', 1001), false))));

    [Fact] public void Image_must_be_https() =>
        Assert.NotEmpty(AutomationValidator.Validate(Ok(new ResponseDto(MessageType.Image, "http://x.com/a.png", false))));
    [Fact] public void Https_image_ok() =>
        Assert.Empty(AutomationValidator.Validate(Ok(new ResponseDto(MessageType.Image, "https://x.com/a.png", false))));

    [Fact] public void Button_json_ok()
    {
        var json = """{"message":"pick","buttons":[{"type":"web_url","title":"Site","url":"https://x.com"},{"type":"postback","title":"More","payload":"MORE"}]}""";
        Assert.Empty(AutomationValidator.Validate(Ok(new ResponseDto(MessageType.Button, json, false))));
    }

    [Fact] public void Button_with_more_than_3_buttons_rejected()
    {
        var b = """{"type":"postback","title":"x","payload":"p"}""";
        var json = $$"""{"message":"m","buttons":[{{b}},{{b}},{{b}},{{b}}]}""";
        Assert.NotEmpty(AutomationValidator.Validate(Ok(new ResponseDto(MessageType.Button, json, false))));
    }

    [Fact] public void Button_postback_without_payload_rejected() =>
        Assert.NotEmpty(AutomationValidator.Validate(Ok(new ResponseDto(MessageType.Button, """{"message":"m","buttons":[{"type":"postback","title":"x"}]}""", false))));

    [Fact] public void Button_http_link_rejected() =>
        Assert.NotEmpty(AutomationValidator.Validate(Ok(new ResponseDto(MessageType.Button, """{"message":"m","buttons":[{"type":"web_url","title":"x","url":"http://a.com"}]}""", false))));

    [Fact] public void Broken_button_json_rejected() =>
        Assert.NotEmpty(AutomationValidator.Validate(Ok(new ResponseDto(MessageType.Button, "{oops", false))));
}
