using System.Net;
using System.Net.Http;
using ProviderPriceSwitcher.Application;
using ProviderPriceSwitcher.Adapters;
using ProviderPriceSwitcher.Core;

static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

static async Task<(SitePricingResult Result, StubHandler Handler)> Fetch(string json, string group = "standard")
{
    var handler = new StubHandler(json);
    using var client = new HttpClient(handler);
    var result = await new NewApiPricingAdapter(client).FetchAsync(new SiteConfiguration
    {
        ProviderId = "p",
        ConfigurationKey = "k",
        BaseUrl = new Uri("https://example.test/root/"),
        Model = "gpt-5.6-sol",
        CurrentGroup = group
    });
    return (result, handler);
}

static async Task<(SitePricingResult Result, StubHandler Handler)> FetchPaws(string json, string group = "GPT混合池（GPT5.4卡顿）", string model = "gpt-5.6-sol")
{
    var handler = new StubHandler(json);
    using var client = new HttpClient(handler);
    var result = await new PawsAiPricingAdapter(client).FetchAsync(new SiteConfiguration
    {
        ProviderId = "pawsai",
        ConfigurationKey = "paws-key",
        BaseUrl = new Uri("https://example.test/root/"),
        SiteType = "pawsai",
        Model = model,
        CurrentGroup = group
    });
    return (result, handler);
}

static async Task ExpectPawsFailure(string json, PricingAdapterFailure expected, string group = "GPT混合池（GPT5.4卡顿）", string model = "gpt-5.6-sol")
{
    try { await FetchPaws(json, group, model); throw new InvalidOperationException($"Expected {expected}."); }
    catch (PricingAdapterException exception) when (exception.Failure == expected) { }
}


var simpleJson = """
{"success":true,"group_ratio":{"standard":1,"fast":2,"unused":0.1},"usable_group":["standard","fast"],"data":[{"model_name":"gpt-5.6-sol","quota_type":0,"model_ratio":2.5,"completion_ratio":6,"cache_ratio":0.1,"billing_mode":"","billing_expr":null,"enable_groups":["standard","fast","missing"]}]}
""";
var (simple, simpleHandler) = await Fetch(simpleJson);
Assert(simpleHandler.Requested == "https://example.test/root/api/pricing", "URL was not joined correctly");
Assert(simple.Prices.InputPerMillion == 5m && simple.Prices.OutputPerMillion == 30m, "DeepKey prices mismatch");
Assert(simple.ValidGroups.SetEquals(["standard", "fast"]), "Group intersection mismatch");
Assert(simple.MinimumValidGroup == "standard", "Minimum group mismatch");

var ektiJson = """
{"success":true,"auto_groups":["gpt-plus"],"group_ratio":{"gpt-plus":0.15,"gpt-pro":0.2},"data":[{"model_name":"gpt-5.6-sol","quota_type":0,"model_ratio":2.5,"completion_ratio":6,"cache_ratio":0.1,"billing_mode":"tiered_expr","billing_expr":"(tier(\"gpt\", p * 5 + c * 30 + cr * 0.5 + cc * 6.25)) * (param(\"service_tier\") == \"priority\" ? 2.5 : 1)","enable_groups":["gpt-pro","gpt-pro-不限速","福利分组"]}]}
""";
var (ekti, _) = await Fetch(ektiJson, "gpt-plus");
Assert(ekti.Prices.InputPerMillion == 0.75m && ekti.Prices.CachedInputPerMillion == 0.075m && ekti.Prices.OutputPerMillion == 4.5m, "Ekti standard-tier prices mismatch");
Assert(ekti.Warnings.Count == 0, "Ekti standard tier must not emit a priority warning");
Assert(ekti.ValidGroups.SetEquals(["gpt-plus", "gpt-pro"]), "Ekti auto group was not included");

var code28Json = """
{"success":true,"group_ratio":{"codex-超低价(随时拉闸)":0.045,"codex特惠分组":0.1},"data":[{"model_name":"gpt-5.6-sol","quota_type":0,"model_ratio":2.5,"completion_ratio":6,"cache_ratio":0.1,"billing_mode":"tiered_expr","billing_expr":"len <= 272000 ? tier(\"standard\", p * 5 + c * 30 + cr * 0.5 + cc * 6.25) : tier(\"long_context\", p * 10 + c * 45 + cr * 1 + cc * 12.5)","enable_groups":["codex特惠分组","codex-超低价(随时拉闸)"]}]}
""";
var (code28, _) = await Fetch(code28Json, "codex-超低价(随时拉闸)");
Assert(code28.Prices.InputPerMillion == 0.225m && code28.Prices.CachedInputPerMillion == 0.0225m && code28.Prices.OutputPerMillion == 1.35m, "Code28 short-context prices mismatch");
Assert(code28.MinimumValidGroup == "codex-超低价(随时拉闸)" && code28.Warnings.Count == 0, "Code28 short-context result mismatch");

var unsupportedJson = code28Json.Replace("len <= 272000", "len < 272000", StringComparison.Ordinal);
try
{
    await Fetch(unsupportedJson, "codex-超低价(随时拉闸)");
    throw new InvalidOperationException("Unsupported conditional expression was accepted");
}
catch (PricingAdapterException exception) when (exception.Failure == PricingAdapterFailure.UnsupportedBilling)
{
}

var pawsJson = """
{"currency":"CNY","unit":"per_1m_tokens","price_table":{"groups":[{"key":"9","multiplier":0.04,"is_exclusive":false},{"key":"11","multiplier":0.001,"is_exclusive":true},{"key":"14","multiplier":0.11,"is_exclusive":false}],"models":[{"model":"gpt-5.6-sol","groups":{"9":{"group_key":"9","group_name":"GPT混合池（GPT5.4卡顿）  ","billing_mode":"token","multiplier":0.04,"input":0.2,"cached_input":0.02,"output":1.2},"11":{"group_key":"11","group_name":"免费专用","billing_mode":"token","multiplier":0.001,"input":0.005,"cached_input":0.0005,"output":0.03},"14":{"group_key":"14","group_name":"GPT 稳定分组","billing_mode":"token","multiplier":0.11,"input":0.55,"cached_input":0.055,"output":3.3}}}]}}
""";
var (paws, pawsHandler) = await FetchPaws(pawsJson);
Assert(pawsHandler.Requested == "https://example.test/root/pawsai-pricing.json", "PawsAI URL mismatch");
Assert(paws.Prices.InputPerMillion == 0.2m && paws.Prices.CachedInputPerMillion == 0.02m && paws.Prices.OutputPerMillion == 1.2m, "PawsAI direct prices mismatch");
Assert(paws.Snapshot.BasePrices is null && paws.Snapshot.CurrentGroupRatio == 0.04m, "PawsAI prices must not be rescaled");
Assert(paws.MinimumValidGroup == "GPT混合池（GPT5.4卡顿）" && paws.MinimumGroupRatio == 0.04m, "PawsAI exclusive group became minimum");
Assert(paws.ValidGroups.SetEquals(["GPT混合池（GPT5.4卡顿）", "免费专用", "GPT 稳定分组"]), "PawsAI group names were not normalized");
var (exclusiveCurrent, _) = await FetchPaws(pawsJson, "免费专用");
Assert(exclusiveCurrent.Prices.InputPerMillion == 0.005m && exclusiveCurrent.MinimumValidGroup == "GPT混合池（GPT5.4卡顿）", "PawsAI exclusive current group handling mismatch");

await ExpectPawsFailure(pawsJson.Replace("\"CNY\"", "\"USD\"", StringComparison.Ordinal), PricingAdapterFailure.InvalidResponse);
await ExpectPawsFailure(pawsJson.Replace("\"per_1m_tokens\"", "\"per_token\"", StringComparison.Ordinal), PricingAdapterFailure.InvalidResponse);
await ExpectPawsFailure(pawsJson, PricingAdapterFailure.ModelNotFound, model: "Gpt-5.6-sol");
await ExpectPawsFailure(pawsJson, PricingAdapterFailure.InvalidGroups, group: "gpt混合池（GPT5.4卡顿）");
await ExpectPawsFailure(pawsJson.Replace("\"billing_mode\":\"token\"", "\"billing_mode\":\"request\"", StringComparison.Ordinal), PricingAdapterFailure.UnsupportedBilling);
await ExpectPawsFailure(pawsJson.Replace("\"input\":0.2", "\"input\":-0.2", StringComparison.Ordinal), PricingAdapterFailure.InvalidPrice);
await ExpectPawsFailure(pawsJson.Replace("\"group_name\":\"GPT 稳定分组\"", "\"group_name\":\"GPT混合池（GPT5.4卡顿）\"", StringComparison.Ordinal), PricingAdapterFailure.InvalidGroups);
await ExpectPawsFailure(pawsJson.Replace("\"is_exclusive\":false", "\"is_exclusive\":true", StringComparison.Ordinal), PricingAdapterFailure.InvalidGroups);
await ExpectPawsFailure("not json", PricingAdapterFailure.InvalidResponse);

var failedHandler = new StubHandler("{}", HttpStatusCode.Unauthorized);
using (var failedClient = new HttpClient(failedHandler))
{
    try { await new PawsAiPricingAdapter(failedClient).FetchAsync(new SiteConfiguration { ProviderId = "p", ConfigurationKey = "k", BaseUrl = new Uri("https://example.test"), SiteType = "pawsai", Model = "gpt-5.6-sol", CurrentGroup = "g" }); throw new InvalidOperationException("HTTP failure accepted"); }
    catch (PricingAdapterException exception) when (exception.Failure == PricingAdapterFailure.Authentication) { }
}

var subUnauthorized = new StubHandler("{\"code\":\"INVALID_TOKEN\",\"message\":\"Invalid token\"}", HttpStatusCode.Unauthorized);
using (var unauthorizedClient = new HttpClient(subUnauthorized))
{
    var credentials = new MemoryCredentialStore
    {
        Credential = new SiteCredentialRecord { ProviderId = "sevnx", SiteType = "sub2api", AuthorizationScheme = "Bearer", AccessToken = "expired", CookieHeader = "refresh_token=rt_test" }
    };
    try
    {
        await new Sub2ApiPricingAdapter(unauthorizedClient, credentials).FetchAsync(new SiteConfiguration
        {
            ProviderId = "sevnx",
            ConfigurationKey = "sub-key",
            BaseUrl = new Uri("https://example.test/"),
            SiteType = "sub2api",
            Model = "gpt-5.6-sol",
            CurrentGroup = "default"
        });
        throw new InvalidOperationException("Sub2API unauthorized response was accepted");
    }
    catch (PricingAdapterException exception)
    {
        Assert(exception.Failure == PricingAdapterFailure.Authentication, "Sub2API unauthorized failure kind mismatch");
        Assert(exception.Message.Contains("INVALID_TOKEN: Invalid token", StringComparison.Ordinal), "Sub2API unauthorized message mismatch");
    }
}

using (var missingCredentialClient = new HttpClient(new StubHandler("{}")))
{
    try
    {
        await new Sub2ApiPricingAdapter(missingCredentialClient, new MemoryCredentialStore()).FetchAsync(new SiteConfiguration
        {
            ProviderId = "missing",
            ConfigurationKey = "sub-key",
            BaseUrl = new Uri("https://example.test/"),
            SiteType = "sub2api",
            Model = "gpt-5.6-sol",
            CurrentGroup = "default"
        });
        throw new InvalidOperationException("Missing Sub2API credential was accepted");
    }
    catch (PricingAdapterException exception) when (exception.Failure == PricingAdapterFailure.Authentication) { }
}

using (var timeoutClient = new HttpClient(new TimeoutHandler()))
{
    try
    {
        await new PawsAiPricingAdapter(timeoutClient).FetchAsync(new SiteConfiguration { ProviderId = "p", ConfigurationKey = "k", BaseUrl = new Uri("https://example.test"), SiteType = "pawsai", Model = "gpt-5.6-sol", CurrentGroup = "g" });
        throw new InvalidOperationException("Timeout was accepted");
    }
    catch (PricingAdapterException exception) when (exception.Failure == PricingAdapterFailure.Timeout) { }
}

using (var requestClient = new HttpClient(new RequestFailureHandler()))
{
    try
    {
        await new NewApiPricingAdapter(requestClient).FetchAsync(new SiteConfiguration { ProviderId = "p", ConfigurationKey = "k", BaseUrl = new Uri("https://example.test"), SiteType = "new-api", Model = "gpt-5.6-sol", CurrentGroup = "g" });
        throw new InvalidOperationException("Request failure was accepted");
    }
    catch (PricingAdapterException exception) when (exception.Failure == PricingAdapterFailure.Request) { }
}

using (var canceled = new CancellationTokenSource())
using (var canceledClient = new HttpClient(new CancelHandler()))
{
    canceled.Cancel();
    try { await new PawsAiPricingAdapter(canceledClient).FetchAsync(new SiteConfiguration { ProviderId = "p", ConfigurationKey = "k", BaseUrl = new Uri("https://example.test"), SiteType = "pawsai", Model = "gpt-5.6-sol", CurrentGroup = "g" }, canceled.Token); throw new InvalidOperationException("Cancellation was swallowed"); }
    catch (OperationCanceledException) { }
}

var subHandler = new Sub2ApiHandler();
using (var subClient = new HttpClient(subHandler))
{
    var credentials = new MemoryCredentialStore
    {
        Credential = new SiteCredentialRecord
        {
            ProviderId = "sevnx",
            SiteType = "sub2api",
            AuthorizationScheme = "Bearer",
            AccessToken = "access-token",
            CookieHeader = "refresh_token=rt_test"
        }
    };
    var result = await new Sub2ApiPricingAdapter(subClient, credentials).FetchAsync(new SiteConfiguration
    {
        ProviderId = "sevnx",
        ConfigurationKey = "sub-ok",
        BaseUrl = new Uri("https://example.test/"),
        SiteType = "sub2api",
        Model = "gpt-5.6-sol",
        CurrentGroup = "gpt-plus"
    });
    Assert(result.Snapshot.BasePrices is { InputPerMillion: 5m, CachedInputPerMillion: 0.5m, OutputPerMillion: 30m }, "Sub2API base price conversion mismatch");
    Assert(result.Prices.InputPerMillion == 0.5m && result.Prices.CachedInputPerMillion == 0.05m && result.Prices.OutputPerMillion == 3m, "Sub2API group prices mismatch");
    Assert(result.MinimumValidGroup == "gpt-plus" && result.MinimumGroupRatio == 0.1m, "Sub2API minimum group mismatch");
    Assert(subHandler.Requests.Count == 3 && subHandler.Requests.Contains("https://example.test/api/v1/groups/available?timezone=Asia%2FShanghai"), "Sub2API request paths mismatch");
    Assert(subHandler.Authorization == "Bearer access-token", "Sub2API authorization header mismatch");
    Assert(subHandler.Cookie == "refresh_token=rt_test", "Sub2API cookie header mismatch");
    Assert(subHandler.UiRequestHeader == "1", "Sub2API x-user-ui-request header mismatch");
    Assert(subHandler.UserAgent?.Contains("Edg/150.0.0.0", StringComparison.Ordinal) == true, "Sub2API user agent mismatch");
    Assert(subHandler.ClientHint?.Contains("Microsoft Edge", StringComparison.Ordinal) == true, "Sub2API client hint mismatch");
    Assert(subHandler.FetchSite == "same-origin", "Sub2API fetch-site mismatch");
    Assert(subHandler.Referrer == "https://example.test/model-pricing", "Sub2API referrer mismatch");
}

var registry = new PricingAdapterRegistry([
    new NewApiPricingAdapter(new HttpClient(new StubHandler(simpleJson))),
    new PawsAiPricingAdapter(new HttpClient(new StubHandler(pawsJson))),
    new Sub2ApiPricingAdapter(new HttpClient(new StubHandler("{}")), new MemoryCredentialStore())
]);
Assert(registry.Descriptors.Select(x => x.SiteType).SequenceEqual(["new-api", "pawsai", "sub2api"]), "Registry order mismatch");
Assert(registry.Descriptors.Select(x => x.DisplayName).SequenceEqual(["New API", "PawsAI", "Sub2API"]), "Descriptor display names mismatch");
Assert(!registry.Descriptors[0].RequiresCredential && registry.Descriptors[2].RequiresCredential, "Descriptor credential capability mismatch");
Assert(registry.TryGet("new-api", out var registered) && registered.Descriptor.SiteType == "new-api", "Registry lookup failed");
Assert(!registry.TryGet("NEW-API", out _) && !registry.TryGet("missing", out _), "Registry lookup must be Ordinal");
var emptyRejected = false;
try { _ = new PricingAdapterRegistry([new DescriptorAdapter(new PricingAdapterDescriptor("", "Empty", false, []))]); } catch (ArgumentException) { emptyRejected = true; }
Assert(emptyRejected, "Empty site type accepted");
var duplicateRejected = false;
try { _ = new PricingAdapterRegistry([new DescriptorAdapter(new PricingAdapterDescriptor("same", "One", false, [])), new DescriptorAdapter(new PricingAdapterDescriptor("same", "Two", false, []))]); } catch (ArgumentException) { duplicateRejected = true; }
Assert(duplicateRejected, "Duplicate site type accepted");

if (args.Contains("--live-pawsai", StringComparer.Ordinal))
{
    using var liveClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    var live = await new PawsAiPricingAdapter(liveClient).FetchAsync(new SiteConfiguration
    {
        ProviderId = "pawsai",
        ConfigurationKey = "live",
        BaseUrl = new Uri("https://ai.furry.edu.gr"),
        SiteType = "pawsai",
        Model = "gpt-5.6-sol",
        CurrentGroup = "GPT混合池（GPT5.4卡顿）"
    });
    Assert(live.Prices.InputPerMillion == 0.2m && live.Prices.CachedInputPerMillion == 0.02m && live.Prices.OutputPerMillion == 1.2m, "Live PawsAI prices mismatch");
    Assert(live.MinimumValidGroup == "GPT混合池（GPT5.4卡顿）" && live.MinimumGroupRatio == 0.04m, "Live PawsAI minimum mismatch");
    Console.WriteLine($"Live PawsAI probe passed: {live.Snapshot.CurrentGroup}, {live.Prices.InputPerMillion}/{live.Prices.CachedInputPerMillion}/{live.Prices.OutputPerMillion}, minimum {live.MinimumValidGroup} ({live.MinimumGroupRatio}).");
}

Console.WriteLine("Adapter contract runner passed.");

sealed class DescriptorAdapter(PricingAdapterDescriptor descriptor) : IPricingAdapter
{
    public PricingAdapterDescriptor Descriptor { get; } = descriptor;
    public Task<SitePricingResult> FetchAsync(SiteConfiguration site, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

sealed class StubHandler(string body, HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
{
    public string? Requested { get; private set; }
    public string? Authorization { get; private set; }
    public string? Cookie { get; private set; }
    public string? UiRequestHeader { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requested = request.RequestUri?.ToString();
        Authorization = request.Headers.Authorization?.ToString();
        Cookie = request.Headers.TryGetValues("Cookie", out var cookies) ? string.Join("; ", cookies) : null;
        UiRequestHeader = request.Headers.TryGetValues("x-user-ui-request", out var uiValues) ? string.Join(",", uiValues) : null;
        return Task.FromResult(new HttpResponseMessage(statusCode) { Content = new StringContent(body) });
    }
}

sealed class Sub2ApiHandler : HttpMessageHandler
{
    public HashSet<string> Requests { get; } = new(StringComparer.Ordinal);
    public string? Authorization { get; private set; }
    public string? Cookie { get; private set; }
    public string? UiRequestHeader { get; private set; }
    public string? UserAgent { get; private set; }
    public string? ClientHint { get; private set; }
    public string? FetchSite { get; private set; }
    public string? Referrer { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri?.ToString() ?? string.Empty;
        Requests.Add(url);
        Authorization = request.Headers.Authorization?.ToString();
        Cookie = request.Headers.TryGetValues("Cookie", out var cookies) ? string.Join("; ", cookies) : null;
        UiRequestHeader = request.Headers.TryGetValues("x-user-ui-request", out var uiValues) ? string.Join(",", uiValues) : null;
        UserAgent = request.Headers.TryGetValues("User-Agent", out var userAgents) ? string.Join(" ", userAgents) : null;
        ClientHint = request.Headers.TryGetValues("sec-ch-ua", out var clientHints) ? string.Join(",", clientHints) : null;
        FetchSite = request.Headers.TryGetValues("sec-fetch-site", out var fetchSites) ? string.Join(",", fetchSites) : null;
        Referrer = request.Headers.Referrer?.ToString();
        var body = request.RequestUri?.AbsolutePath switch
        {
            "/api/v1/model-pricing" => "{\"code\":0,\"message\":\"success\",\"data\":{\"models\":[{\"model\":\"gpt-5.6-sol\",\"provider\":\"openai\",\"input_cost_per_token\":0.000005,\"output_cost_per_token\":0.00003,\"cache_read_input_token_cost\":0.0000005}]}}",
            "/api/v1/groups/available" => "{\"code\":0,\"message\":\"success\",\"data\":[{\"id\":19,\"name\":\"gpt-plus\",\"platform\":\"openai\",\"rate_multiplier\":0.1,\"status\":\"active\"},{\"id\":8,\"name\":\"claude-cc\",\"platform\":\"anthropic\",\"rate_multiplier\":1.2,\"status\":\"active\"}]}",
            "/api/v1/groups/rates" => "{\"code\":0,\"message\":\"success\",\"data\":{\"19\":0.1}}",
            _ => "{}"
        };
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }
}

sealed class MemoryCredentialStore : ISiteCredentialStore
{
    public SiteCredentialRecord? Credential { get; set; }
    public SiteCredentialRecord? LoadCredential(string providerId) => Credential;
    public void SaveCredential(SiteCredentialRecord credential) => Credential = credential;
    public void ClearCredential(string providerId) => Credential = null;
    public SiteCredentialSummary GetSummary(string providerId) => new() { ProviderId = providerId, Status = SiteCredentialStatus.Available };
}

sealed class TimeoutHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromException<HttpResponseMessage>(new TaskCanceledException("timeout"));
}

sealed class RequestFailureHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromException<HttpResponseMessage>(new HttpRequestException("offline"));
}

sealed class CancelHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ContinueWith<HttpResponseMessage>(_ => null!, cancellationToken);
}
