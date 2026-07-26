using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using ProviderPriceSwitcher.Core;
using ProviderPriceSwitcher.Application;

namespace ProviderPriceSwitcher.Adapters;

public sealed class NewApiPricingAdapter : IPricingAdapter
{
    private const string TierCoefficientsPattern = @"tier\s*\([^)]*?p\s*\*\s*(?<p>[-+]?\d+(?:\.\d+)?)\s*\+\s*c\s*\*\s*(?<c>[-+]?\d+(?:\.\d+)?)\s*\+\s*cr\s*\*\s*(?<cr>[-+]?\d+(?:\.\d+)?)";
    private static readonly Regex Coefficients = new(TierCoefficientsPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex AnyTier = new(@"\btier\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ShortContextConditional = new(
        @"^\s*len\s*<=\s*\d+(?:\.\d+)?\s*\?\s*" + TierCoefficientsPattern + @"[^:]*:\s*tier\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex PriorityMultiplier = new(
        @"param\s*\(\s*[""']service_tier[""']\s*\)\s*==\s*[""']priority[""']\s*\?\s*[-+]?\d+(?:\.\d+)?\s*:\s*1",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly HttpClient _httpClient;
    public PricingAdapterDescriptor Descriptor { get; } = new("new-api", "New API", false, ["无需认证"]);

    public NewApiPricingAdapter(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<SitePricingResult> FetchAsync(SiteConfiguration site, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(site);
        var endpoint = new Uri(site.BaseUrl.ToString().TrimEnd('/') + "/api/pricing");
        JsonDocument document;
        try { document = await _httpClient.GetFromJsonAsync<JsonDocument>(endpoint, cancellationToken) ?? throw new PricingAdapterException(PricingAdapterFailure.InvalidResponse, "Empty pricing response."); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (PricingAdapterException) { throw; }
        catch (TaskCanceledException ex) { throw new PricingAdapterException(PricingAdapterFailure.Timeout, "Pricing request timed out.", ex); }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized) { throw new PricingAdapterException(PricingAdapterFailure.Authentication, "Pricing request authentication failed.", ex); }
        catch (HttpRequestException ex) { throw new PricingAdapterException(PricingAdapterFailure.Request, "Pricing request failed.", ex); }
        catch (JsonException ex) { throw new PricingAdapterException(PricingAdapterFailure.InvalidResponse, "Pricing response is not valid JSON.", ex); }

        using (document)
        {
            var root = document.RootElement;
            if (!TryBool(root, "success", out var success) || !success)
                throw new PricingAdapterException(PricingAdapterFailure.InvalidResponse, "Pricing response success=false.");
            if (!TryProperty(root, "data", out var data) || (data.ValueKind != JsonValueKind.Object && data.ValueKind != JsonValueKind.Array))
                throw new PricingAdapterException(PricingAdapterFailure.InvalidResponse, "Pricing response has no data object or array.");
            var model = FindModel(data, site.Model);
            if (model is null)
                throw new PricingAdapterException(PricingAdapterFailure.ModelNotFound, $"Model '{site.Model}' was not found.");
            var groups = ReadGroups(root, model.Value);
            if (!groups.Contains(site.CurrentGroup))
                throw new PricingAdapterException(PricingAdapterFailure.InvalidGroups, $"Current group '{site.CurrentGroup}' is not valid for model '{site.Model}'.");
            var quota = ReadDecimal(model.Value, "quota_type");
            if (quota != 0m)
                throw new PricingAdapterException(PricingAdapterFailure.InvalidQuotaType, "Only quota_type=0 is supported.");
            var modelRatio = Positive(model.Value, "model_ratio");
            var completion = Positive(model.Value, "completion_ratio");
            var cache = Positive(model.Value, "cache_ratio");
            var billingMode = TryString(model.Value, "billing_mode");
            var expression = TryString(model.Value, "billing_expr") ?? TryString(model.Value, "billing_expression");
            decimal input = 2m * modelRatio, output = input * completion, cached = input * cache;
            var warnings = Array.Empty<string>();
            if (billingMode is "tiered_expr" or "billing_expr")
            {
                if (string.IsNullOrWhiteSpace(expression))
                    throw new PricingAdapterException(PricingAdapterFailure.UnsupportedBilling, "Billing expression is empty.");

                var tierCount = AnyTier.Matches(expression).Count;
                Match match;
                if (tierCount == 1)
                {
                    match = Coefficients.Match(expression);
                    if (expression.Contains("priority", StringComparison.OrdinalIgnoreCase) && !PriorityMultiplier.IsMatch(expression))
                        throw new PricingAdapterException(PricingAdapterFailure.UnsupportedBilling, "Priority billing expression is not recognized.");
                }
                else if (tierCount == 2)
                {
                    match = ShortContextConditional.Match(expression);
                }
                else
                {
                    throw new PricingAdapterException(PricingAdapterFailure.UnsupportedBilling, "Billing expression is not a supported standard or short-context request.");
                }

                if (!match.Success || !decimal.TryParse(match.Groups["p"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out input) ||
                    !decimal.TryParse(match.Groups["c"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out output) ||
                    !decimal.TryParse(match.Groups["cr"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out cached))
                    throw new PricingAdapterException(PricingAdapterFailure.UnsupportedBilling, "Billing expression coefficients could not be uniquely parsed.");
            }
            if (input < 0 || output < 0 || cached < 0)
                throw new PricingAdapterException(PricingAdapterFailure.InvalidPrice, "Pricing coefficients cannot be negative.");
            var ratio = GroupRatio(root, site.CurrentGroup);
            var basePrices = new TokenPrices { InputPerMillion = input, OutputPerMillion = output, CachedInputPerMillion = cached };
            var prices = Scale(basePrices, ratio);
            prices.Validate();
            var min = groups.OrderBy(g => GroupRatio(root, g)).First();
            var minimumRatio = GroupRatio(root, min);
            return new SitePricingResult
            {
                Snapshot = new PricingSnapshot
                {
                    ProviderId = site.ProviderId,
                    ConfigurationKey = site.ConfigurationKey,
                    Model = site.Model,
                    CurrentGroup = site.CurrentGroup,
                    BasePrices = basePrices,
                    CurrentGroupRatio = ratio,
                    GroupRatioSource = "自动",
                    Prices = prices,
                    MinimumGroup = min,
                    MinimumGroupRatio = minimumRatio,
                    MinimumGroupPrices = Scale(basePrices, minimumRatio),
                    RefreshedAt = DateTimeOffset.UtcNow
                },
                ValidGroups = groups,
                MinimumValidGroup = min,
                MinimumGroupRatio = minimumRatio,
                BillingExpression = expression,
                Warnings = warnings
            };
        }
    }

    private static HashSet<string> ReadGroups(JsonElement root, JsonElement model)
    {
        if (!TryProperty(root, "group_ratio", out var ratios) || ratios.ValueKind != JsonValueKind.Object)
            throw new PricingAdapterException(PricingAdapterFailure.InvalidGroups, "group_ratio is required.");
        if (!TryProperty(model, "enable_groups", out var enabled) || enabled.ValueKind != JsonValueKind.Array)
            throw new PricingAdapterException(PricingAdapterFailure.InvalidGroups, "The target model has no enable_groups array.");
        var set = enabled.EnumerateArray()
            .Concat(ReadStringArray(root, "auto_groups"))
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .Where(name => ratios.TryGetProperty(name, out _))
            .ToHashSet(StringComparer.Ordinal);
        if (set.Count == 0)
            throw new PricingAdapterException(PricingAdapterFailure.InvalidGroups, "The target model has no valid pricing groups.");
        return set;
    }

    private static IEnumerable<JsonElement> ReadStringArray(JsonElement root, string name) =>
        TryProperty(root, name, out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray()
            : [];

    private static TokenPrices Scale(TokenPrices prices, decimal ratio) => new()
    {
        InputPerMillion = prices.InputPerMillion * ratio,
        CachedInputPerMillion = prices.CachedInputPerMillion * ratio,
        OutputPerMillion = prices.OutputPerMillion * ratio
    };

    private static decimal GroupRatio(JsonElement data, string group) => TryProperty(data, "group_ratio", out var ratios) && ratios.TryGetProperty(group, out var value) ? Decimal(value, "group_ratio") : throw new PricingAdapterException(PricingAdapterFailure.InvalidGroups, $"Group '{group}' has no ratio.");
    private static decimal Positive(JsonElement e, string name) { var value = ReadDecimal(e, name); if (value < 0) throw new PricingAdapterException(PricingAdapterFailure.InvalidPrice, $"{name} cannot be negative."); return value; }
    private static decimal ReadDecimal(JsonElement e, string name) => TryProperty(e, name, out var v) ? Decimal(v, name) : throw new PricingAdapterException(PricingAdapterFailure.InvalidResponse, $"Missing {name}.");
    private static decimal Decimal(JsonElement v, string name) => v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var n) ? n : throw new PricingAdapterException(PricingAdapterFailure.InvalidPrice, $"{name} must be numeric.");
    private static string? TryString(JsonElement e, string name) => TryProperty(e, name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static bool TryProperty(JsonElement e, string name, out JsonElement value) { value = default; return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out value); }
    private static bool TryBool(JsonElement e, string name, out bool value) { value = false; if (!TryProperty(e, name, out var v)) return false; if (v.ValueKind == JsonValueKind.True) { value = true; return true; } return v.ValueKind == JsonValueKind.False; }
    private static JsonElement? FindModel(JsonElement data, string name)
    {
        if (data.ValueKind == JsonValueKind.Array)
            return data.EnumerateArray().FirstOrDefault(x => x.ValueKind == JsonValueKind.Object && (TryString(x, "model_name") ?? TryString(x, "model")) == name);
        if (data.TryGetProperty(name, out var direct) && direct.ValueKind == JsonValueKind.Object) return direct;
        foreach (var prop in data.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Array)
            {
                var found = prop.Value.EnumerateArray().FirstOrDefault(x => x.ValueKind == JsonValueKind.Object && (TryString(x, "model_name") ?? TryString(x, "model")) == name);
                if (found.ValueKind != JsonValueKind.Undefined) return found;
            }
            if (prop.Value.ValueKind == JsonValueKind.Object && prop.Value.TryGetProperty(name, out var nested)) return nested;
        }
        return null;
    }
}
