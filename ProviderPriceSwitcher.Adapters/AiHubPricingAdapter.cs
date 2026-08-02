using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ProviderPriceSwitcher.Application;
using ProviderPriceSwitcher.Core;

namespace ProviderPriceSwitcher.Adapters;

public sealed class AiHubPricingAdapter : IPricingAdapter
{
    private const string Timezone = "?timezone=Asia%2FShanghai";
    private readonly HttpClient _httpClient;
    private readonly ISiteAccessCredentialStore _credentialStore;

    public PricingAdapterDescriptor Descriptor { get; } = new("aihub", "AIHub", true, ["导入令牌"]);

    public AiHubPricingAdapter(HttpClient httpClient, ISiteAccessCredentialStore credentialStore)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
    }

    public async Task<SitePricingResult> FetchAsync(SiteConfiguration site, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(site);
        var credential = _credentialStore.LoadCredential(site.ProviderId);
        if (credential is null || string.IsNullOrWhiteSpace(credential.AccessToken))
            throw new PricingAdapterException(PricingAdapterFailure.Authentication, "未绑定 AIHub 访问令牌。请先在站点管理中绑定凭据。");

        try
        {
            var availableTask = GetAsync(site.BaseUrl, "/api/v1/groups/available" + Timezone, credential, cancellationToken);
            var ratesTask = GetAsync(site.BaseUrl, "/api/v1/groups/rates" + Timezone, credential, cancellationToken);
            await Task.WhenAll(availableTask, ratesTask).ConfigureAwait(false);
            return Build(site, availableTask.Result.RootElement, ratesTask.Result.RootElement);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PricingAdapterException)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            throw new PricingAdapterException(PricingAdapterFailure.Timeout, "AIHub 请求超时。", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new PricingAdapterException(PricingAdapterFailure.Request, "AIHub 请求失败。", ex);
        }
        catch (JsonException ex)
        {
            throw new PricingAdapterException(PricingAdapterFailure.InvalidResponse, "AIHub 返回了无效 JSON。", ex);
        }
    }

    private async Task<JsonDocument> GetAsync(Uri baseUrl, string path, SiteCredentialRecord credential, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUrl.ToString().TrimEnd('/') + path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
        request.Headers.Referrer = new Uri(baseUrl, "/keys");
        request.Headers.TryAddWithoutValidation("X-User-UI-Request", "1");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (!string.IsNullOrWhiteSpace(credential.CookieHeader))
            request.Headers.TryAddWithoutValidation("Cookie", credential.CookieHeader);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new PricingAdapterException(PricingAdapterFailure.Authentication, "AIHub 认证失败。请重新导入访问令牌。");
        if (!response.IsSuccessStatusCode)
            throw new PricingAdapterException(PricingAdapterFailure.Request, "AIHub 请求失败。");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static SitePricingResult Build(SiteConfiguration site, JsonElement availableRoot, JsonElement ratesRoot)
    {
        var available = ReadAvailable(availableRoot);
        var accountRates = ReadRates(ratesRoot);
        var rateMap = available.ToDictionary(
            x => x.Name,
            x => accountRates.TryGetValue(x.Id, out var accountRate) ? accountRate : x.Rate,
            StringComparer.Ordinal);
        if (!rateMap.TryGetValue(site.CurrentGroup, out var automaticCurrentRatio))
            throw new PricingAdapterException(PricingAdapterFailure.InvalidGroups, $"Current group '{site.CurrentGroup}' is not valid for platform 'openai'.");

        var currentRatio = site.CurrentGroupRatio is > 0 ? site.CurrentGroupRatio.Value : automaticCurrentRatio;
        var minimum = rateMap.OrderBy(x => x.Value).First();
        const decimal input = 5m;
        const decimal cached = 0.5m;
        const decimal output = 30m;
        var basePrices = new TokenPrices { InputPerMillion = input, CachedInputPerMillion = cached, OutputPerMillion = output };
        basePrices.Validate();
        return new SitePricingResult
        {
            Snapshot = new PricingSnapshot
            {
                ProviderId = site.ProviderId,
                ConfigurationKey = site.ConfigurationKey,
                Model = site.Model,
                CurrentGroup = site.CurrentGroup,
                BasePrices = basePrices,
                CurrentGroupRatio = currentRatio,
                GroupRatioSource = site.CurrentGroupRatio is > 0 ? site.GroupRatioSource : "自动",
                Prices = Scale(basePrices, currentRatio),
                MinimumGroup = minimum.Key,
                MinimumGroupRatio = minimum.Value,
                MinimumGroupPrices = Scale(basePrices, minimum.Value),
                RefreshedAt = DateTimeOffset.UtcNow
            },
            ValidGroups = rateMap.Keys.ToHashSet(StringComparer.Ordinal),
            MinimumValidGroup = minimum.Key,
            MinimumGroupRatio = minimum.Value,
            Warnings = []
        };
    }

    private sealed record AvailableGroup(int Id, string Name, decimal Rate);

    private static List<AvailableGroup> ReadAvailable(JsonElement root)
    {
        var result = new List<AvailableGroup>();
        foreach (var item in Elements(Unwrap(root)))
        {
            var status = String(item, "status");
            if (!string.Equals(String(item, "platform"), "openai", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(status, "active", StringComparison.OrdinalIgnoreCase)) continue;
            var id = Int(item, "id");
            var name = String(item, "name")?.Trim();
            var rate = Decimal(item, "rate_multiplier");
            if (id is null || string.IsNullOrEmpty(name) || rate is not > 0)
                throw new PricingAdapterException(PricingAdapterFailure.InvalidGroups, "AIHub 分组字段无效。");
            result.Add(new AvailableGroup(id.Value, name, rate.Value));
        }
        if (result.Count == 0) throw new PricingAdapterException(PricingAdapterFailure.InvalidGroups, "AIHub 没有可用的 openai 分组。");
        return result;
    }

    private static Dictionary<int, decimal> ReadRates(JsonElement root)
    {
        var result = new Dictionary<int, decimal>();
        foreach (var item in Elements(Unwrap(root)))
        {
            if (item.ValueKind == JsonValueKind.Object)
            {
                var id = Int(item, "group_id") ?? Int(item, "id");
                var rate = Decimal(item, "rate_multiplier") ?? Decimal(item, "rate");
                if (id is not null && rate is > 0) result[id.Value] = rate.Value;
            }
        }
        var value = Unwrap(root);
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
                if (int.TryParse(property.Name, out var id) && property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDecimal(out var rate) && rate > 0)
                    result[id] = rate;
        return result;
    }

    private static JsonElement Unwrap(JsonElement root) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) ? data : root;
    private static IEnumerable<JsonElement> Elements(JsonElement root) => root.ValueKind == JsonValueKind.Array ? root.EnumerateArray() : root.ValueKind == JsonValueKind.Object ? root.EnumerateObject().Select(x => x.Value) : throw new PricingAdapterException(PricingAdapterFailure.InvalidResponse, "AIHub 响应结构无效。");
    private static string? String(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int? Int(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : null;
    private static decimal? Decimal(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var n) ? n : null;
    private static TokenPrices Scale(TokenPrices p, decimal ratio) => new() { InputPerMillion = p.InputPerMillion * ratio, CachedInputPerMillion = p.CachedInputPerMillion * ratio, OutputPerMillion = p.OutputPerMillion * ratio };
}
