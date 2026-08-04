using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ProviderPriceSwitcher.Core;
using ProviderPriceSwitcher.Application;

namespace ProviderPriceSwitcher.Adapters;

public sealed class SevnXPricingAdapter : IPricingAdapter
{
    private readonly HttpClient _httpClient;
    private readonly ISiteAccessCredentialStore _credentialStore;

    public PricingAdapterDescriptor Descriptor { get; } = new("sevnx", "SevnX", true, ["导入令牌"]);

    public SevnXPricingAdapter(HttpClient httpClient, ISiteAccessCredentialStore credentialStore)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
    }

    public async Task<SitePricingResult> FetchAsync(SiteConfiguration site, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(site);
        var credential = _credentialStore.LoadCredential(site.ProviderId);
        if (credential is null || string.IsNullOrWhiteSpace(credential.AccessToken))
            throw new PricingAdapterException(PricingAdapterFailure.Authentication, "未绑定 SevnX 访问令牌。请先在站点管理中绑定凭据。");

        try
        {
            var pricingTask = GetAsync(site.BaseUrl, "/api/v1/model-pricing", credential, cancellationToken);
            var groupsTask = GetAsync(site.BaseUrl, "/api/v1/groups/available", credential, cancellationToken);
            var ratesTask = GetAsync(site.BaseUrl, "/api/v1/groups/rates", credential, cancellationToken);
            await Task.WhenAll(pricingTask, groupsTask, ratesTask).ConfigureAwait(false);
            return Build(site, pricingTask.Result.RootElement, groupsTask.Result.RootElement, ratesTask.Result.RootElement);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PricingAdapterException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new PricingAdapterException(PricingAdapterFailure.Request, "SevnX 请求失败。", ex);
        }
        catch (TaskCanceledException ex)
        {
            throw new PricingAdapterException(PricingAdapterFailure.Timeout, "SevnX 请求超时。", ex);
        }
        catch (JsonException ex)
        {
            throw new PricingAdapterException(PricingAdapterFailure.InvalidResponse, "SevnX 返回了无效 JSON。", ex);
        }
    }


    private async Task<JsonDocument> GetAsync(Uri baseUrl, string path, SiteCredentialRecord credential, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint(baseUrl, path + "?timezone=Asia%2FShanghai"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        request.Headers.TryAddWithoutValidation("Accept-Language", "zh");
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/150.0.0.0 Safari/537.36 Edg/150.0.0.0");
        request.Headers.TryAddWithoutValidation("priority", "u=1, i");
        request.Headers.TryAddWithoutValidation("sec-ch-ua", "\"Not;A=Brand\";v=\"8\", \"Chromium\";v=\"150\", \"Microsoft Edge\";v=\"150\"");
        request.Headers.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
        request.Headers.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
        request.Headers.TryAddWithoutValidation("sec-fetch-dest", "empty");
        request.Headers.TryAddWithoutValidation("sec-fetch-mode", "cors");
        request.Headers.TryAddWithoutValidation("sec-fetch-site", "same-origin");
        request.Headers.Referrer = new Uri(baseUrl, "/model-pricing");
        request.Headers.TryAddWithoutValidation("x-user-ui-request", "1");
        if (!string.IsNullOrWhiteSpace(credential.CookieHeader))
            request.Headers.TryAddWithoutValidation("Cookie", credential.CookieHeader);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var detail = AuthenticationFailureDetail(body);
            throw new PricingAdapterException(PricingAdapterFailure.Authentication, $"SevnX 认证失败（{detail}）。请在浏览器确认价格接口返回 200 后，重新导入该请求使用的最新访问令牌。");
        }
        response.EnsureSuccessStatusCode();
        return await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private static string AuthenticationFailureDetail(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var code = OptionalString(document.RootElement, "code");
            var message = OptionalString(document.RootElement, "message");
            if (!string.IsNullOrWhiteSpace(code) && !string.IsNullOrWhiteSpace(message)) return $"{code}: {message}";
            if (!string.IsNullOrWhiteSpace(code)) return code;
            if (!string.IsNullOrWhiteSpace(message)) return message;
        }
        catch (JsonException)
        {
        }
        return "HTTP 401";
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static SitePricingResult Build(SiteConfiguration site, JsonElement pricingRoot, JsonElement groupsRoot, JsonElement ratesRoot)
    {
        var pricing = Unwrap(pricingRoot);
        var model = FindModel(pricing, site.Model);
        var provider = RequiredString(model, "provider");
        var availableGroups = ReadAvailableGroups(groupsRoot, provider);
        var userRates = ReadUserRates(ratesRoot);
        var rateMap = availableGroups.ToDictionary(
            group => group.Name,
            group => userRates.TryGetValue(group.Id, out var userRate) ? userRate : group.Rate,
            StringComparer.Ordinal);
        if (!rateMap.TryGetValue(site.CurrentGroup, out var currentRatio))
            throw new PricingAdapterException(PricingAdapterFailure.InvalidGroups, $"Current group '{site.CurrentGroup}' is not valid for model '{site.Model}' on platform '{provider}'.");
        const decimal perMillion = 1_000_000m;
        var input = RequiredDecimal(model, "input_cost_per_token") * perMillion;
        var cached = (OptionalDecimal(model, "cache_read_input_token_cost") ?? 0m) * perMillion;
        var output = RequiredDecimal(model, "output_cost_per_token") * perMillion;
        var basePrices = new TokenPrices { InputPerMillion = input, CachedInputPerMillion = cached, OutputPerMillion = output };
        basePrices.Validate();

        var minimum = rateMap.OrderBy(pair => pair.Value).First();
        var minimumRatio = minimum.Value;
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
                GroupRatioSource = "自动",
                Prices = Scale(basePrices, currentRatio),
                MinimumGroup = minimum.Key,
                MinimumGroupRatio = minimumRatio,
                MinimumGroupPrices = Scale(basePrices, minimumRatio),
                RefreshedAt = DateTimeOffset.UtcNow
            },
            GroupRatios = rateMap,
            MinimumValidGroup = minimum.Key,
            MinimumGroupRatio = minimumRatio,
            Warnings = []
        };
    }

    private static JsonElement FindModel(JsonElement pricing, string model)
    {
        foreach (var item in EnumerateArray(pricing, "models"))
        {
            if (string.Equals(OptionalString(item, "model"), model, StringComparison.Ordinal)) return item;
        }
        throw new PricingAdapterException(PricingAdapterFailure.ModelNotFound, $"Model '{model}' was not found.");
    }

    private static List<AvailableGroup> ReadAvailableGroups(JsonElement root, string provider)
    {
        var groups = EnumerateArray(Unwrap(root))
            .Where(item => string.Equals(OptionalString(item, "platform"), provider, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(OptionalString(item, "status"), "inactive", StringComparison.OrdinalIgnoreCase))
            .Select(item => new AvailableGroup(
                RequiredInt32(item, "id"),
                RequiredString(item, "name").Trim(),
                RequiredDecimal(item, "rate_multiplier")))
            .Where(group => group.Name.Length > 0 && group.Rate > 0)
            .ToList();
        if (groups.Count == 0)
            throw new PricingAdapterException(PricingAdapterFailure.InvalidGroups, $"No available groups were found for platform '{provider}'.");
        return groups;
    }

    private static Dictionary<int, decimal> ReadUserRates(JsonElement root)
    {
        var data = Unwrap(root);
        if (data.ValueKind != JsonValueKind.Object) return [];
        var rates = new Dictionary<int, decimal>();
        foreach (var property in data.EnumerateObject())
        {
            if (int.TryParse(property.Name, out var id) && property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDecimal(out var rate) && rate > 0)
                rates[id] = rate;
        }
        return rates;
    }

    private static decimal GroupRate(Dictionary<string, decimal> rates, string group) => rates.TryGetValue(group, out var value)
        ? value
        : throw new PricingAdapterException(PricingAdapterFailure.InvalidGroups, $"Group '{group}' has no rate multiplier.");

    private sealed record AvailableGroup(int Id, string Name, decimal Rate);

    private static JsonElement Unwrap(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return root;
        if (root.ValueKind != JsonValueKind.Object) throw new PricingAdapterException(PricingAdapterFailure.InvalidResponse, "SevnX response root must be object or array.");
        if (root.TryGetProperty("data", out var data)) return data;
        return root;
    }

    private static IEnumerable<JsonElement> EnumerateArray(JsonElement root, string? property = null)
    {
        if (property is not null)
        {
            if (!root.TryGetProperty(property, out root)) return [];
        }
        return root.ValueKind == JsonValueKind.Array ? root.EnumerateArray() : [];
    }

    private static int RequiredInt32(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
        ? number
        : throw new PricingAdapterException(PricingAdapterFailure.InvalidResponse, $"Missing integer field '{name}'.");
    private static decimal RequiredDecimal(JsonElement element, string name) => OptionalDecimal(element, name) ?? throw new PricingAdapterException(PricingAdapterFailure.InvalidResponse, $"Missing numeric field '{name}'.");
    private static decimal? OptionalDecimal(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) ? number : null;
    private static string RequiredString(JsonElement element, string name) => OptionalString(element, name) ?? throw new PricingAdapterException(PricingAdapterFailure.InvalidResponse, $"Missing string field '{name}'.");
    private static string? OptionalString(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static DateTimeOffset? OptionalExpiry(JsonElement element)
    {
        if (element.TryGetProperty("expires_at", out var expiresAt) && expiresAt.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(expiresAt.GetString(), out var parsed)) return parsed;
        if (element.TryGetProperty("expires_in", out var expiresIn) && expiresIn.ValueKind == JsonValueKind.Number && expiresIn.TryGetInt64(out var seconds)) return DateTimeOffset.UtcNow.AddSeconds(seconds);
        return null;
    }

    private static TokenPrices Scale(TokenPrices prices, decimal ratio) => new()
    {
        InputPerMillion = prices.InputPerMillion * ratio,
        CachedInputPerMillion = prices.CachedInputPerMillion * ratio,
        OutputPerMillion = prices.OutputPerMillion * ratio
    };

    private static Uri Endpoint(Uri baseUrl, string path) => new(baseUrl.ToString().TrimEnd('/') + path);
}
