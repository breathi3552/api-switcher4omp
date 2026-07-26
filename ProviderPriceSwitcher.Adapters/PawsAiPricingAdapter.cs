using System.Text.Json;
using ProviderPriceSwitcher.Core;
using ProviderPriceSwitcher.Application;

namespace ProviderPriceSwitcher.Adapters;

public sealed class PawsAiPricingAdapter : IPricingAdapter
{
    private readonly HttpClient _httpClient;
    public PricingAdapterDescriptor Descriptor { get; } = new("pawsai", "PawsAI", false, ["无需认证"]);
    public PawsAiPricingAdapter(HttpClient httpClient) => _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    public async Task<SitePricingResult> FetchAsync(SiteConfiguration site, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(site);
        var endpoint = new Uri(site.BaseUrl.ToString().TrimEnd('/') + "/pawsai-pricing.json");
        JsonDocument document;
        try
        {
            using var response = await _httpClient.GetAsync(endpoint, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                throw new PricingAdapterException(PricingAdapterFailure.Authentication, "Pricing request authentication failed.");
            if (!response.IsSuccessStatusCode)
                throw new PricingAdapterException(PricingAdapterFailure.Request, $"Pricing request failed with HTTP {(int)response.StatusCode}.");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (TaskCanceledException ex) { throw new PricingAdapterException(PricingAdapterFailure.Timeout, "Pricing request timed out.", ex); }
        catch (PricingAdapterException) { throw; }
        catch (JsonException ex) { throw new PricingAdapterException(PricingAdapterFailure.InvalidResponse, "Pricing response is not valid JSON.", ex); }
        catch (HttpRequestException ex) { throw new PricingAdapterException(PricingAdapterFailure.Request, "Pricing request failed.", ex); }
        using (document)
        {
            var root = document.RootElement;
            if (!string.Equals(TryString(root, "currency"), "CNY", StringComparison.Ordinal) || !string.Equals(TryString(root, "unit"), "per_1m_tokens", StringComparison.Ordinal))
                throw new PricingAdapterException(PricingAdapterFailure.InvalidResponse, "currency/unit are invalid.");
            var table = Property(root, "price_table", PricingAdapterFailure.InvalidResponse);
            var model = FindModel(Property(table, "models", PricingAdapterFailure.InvalidResponse), site.Model) ?? throw new PricingAdapterException(PricingAdapterFailure.ModelNotFound, $"Model '{site.Model}' was not found.");
            var metadata = ReadMetadata(Property(table, "groups", PricingAdapterFailure.InvalidResponse));
            var groups = ReadGroups(model, metadata);
            var current = groups.FirstOrDefault(g => string.Equals(g.Name, site.CurrentGroup.Trim(), StringComparison.Ordinal));
            if (current is null) throw new PricingAdapterException(PricingAdapterFailure.InvalidGroups, $"Group '{site.CurrentGroup}' was not found.");
            var candidates = groups.Where(g => !g.Exclusive && g.Multiplier > 0).ToArray();
            if (candidates.Length == 0) throw new PricingAdapterException(PricingAdapterFailure.InvalidGroups, "No valid non-exclusive minimum pricing group.");
            var minimum = candidates.OrderBy(g => g.Multiplier).First();
            return new SitePricingResult
            {
                Snapshot = new PricingSnapshot { ProviderId = site.ProviderId, ConfigurationKey = site.ConfigurationKey, Model = site.Model, CurrentGroup = site.CurrentGroup, BasePrices = null, CurrentGroupRatio = current.Multiplier, GroupRatioSource = "公开价格", Prices = ReadPrices(current.Element), MinimumGroup = minimum.Name, MinimumGroupRatio = minimum.Multiplier, MinimumGroupPrices = ReadPrices(minimum.Element), RefreshedAt = DateTimeOffset.UtcNow },
                ValidGroups = groups.Select(g => g.Name).ToHashSet(StringComparer.Ordinal),
                MinimumValidGroup = minimum.Name,
                MinimumGroupRatio = minimum.Multiplier,
                BillingExpression = TryString(current.Element, "billing_expression") ?? TryString(current.Element, "billing_expr"),
                Warnings = []
            };
        }
    }

    private static Dictionary<string, (bool Exclusive, decimal Multiplier)> ReadMetadata(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array) throw new PricingAdapterException(PricingAdapterFailure.InvalidGroups, "price_table.groups is required.");
        var result = new Dictionary<string, (bool, decimal)>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateArray())
        {
            var key = TryString(item, "key")?.Trim();
            if (string.IsNullOrEmpty(key)) throw new PricingAdapterException(PricingAdapterFailure.InvalidGroups, "Every pricing group requires a key.");
            var multiplier = Number(item, "multiplier");
            if (multiplier <= 0) throw new PricingAdapterException(PricingAdapterFailure.InvalidPrice, "Group multiplier must be positive.");
            if (!result.TryAdd(key, (TryBool(item, "is_exclusive"), multiplier)))
                throw new PricingAdapterException(PricingAdapterFailure.InvalidGroups, $"Duplicate group key '{key}'.");
        }
        return result;
    }

    private static List<Group> ReadGroups(JsonElement model, Dictionary<string, (bool Exclusive, decimal Multiplier)> metadata)
    {
        var value = Property(model, "groups", PricingAdapterFailure.InvalidGroups);
        if (value.ValueKind != JsonValueKind.Object) throw new PricingAdapterException(PricingAdapterFailure.InvalidGroups, "Model groups must be an object.");
        var result = new List<Group>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            var element = property.Value;
            var name = TryString(element, "group_name")?.Trim();
            var key = (TryString(element, "group_key") ?? property.Name).Trim();
            if (string.IsNullOrEmpty(name) || !names.Add(name))
                throw new PricingAdapterException(PricingAdapterFailure.InvalidGroups, $"Duplicate or empty group name '{name}'.");
            if (!metadata.TryGetValue(key, out var meta))
                throw new PricingAdapterException(PricingAdapterFailure.InvalidGroups, $"Group '{name}' has no metadata.");
            RequireBilling(element);
            var prices = ReadPrices(element);
            prices.Validate();
            result.Add(new Group(name, element, meta.Exclusive, meta.Multiplier));
        }
        if (result.Count == 0) throw new PricingAdapterException(PricingAdapterFailure.InvalidGroups, "The target model has no valid pricing groups.");
        return result;
    }
    private static TokenPrices ReadPrices(JsonElement e) => new() { InputPerMillion = NonNegative(e, "input"), CachedInputPerMillion = NonNegative(e, "cached_input"), OutputPerMillion = NonNegative(e, "output") };
    private static void RequireBilling(JsonElement e) { if (!string.Equals(TryString(e, "billing_mode"), "token", StringComparison.Ordinal)) throw new PricingAdapterException(PricingAdapterFailure.UnsupportedBilling, "Only token billing is supported."); }
    private static decimal NonNegative(JsonElement e, string name) { var n = Number(e, name); if (n < 0) throw new PricingAdapterException(PricingAdapterFailure.InvalidPrice, $"{name} cannot be negative."); return n; }
    private static decimal Number(JsonElement e, string name) { if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetDecimal(out var n)) throw new PricingAdapterException(PricingAdapterFailure.InvalidPrice, $"{name} must be numeric."); return n; }
    private static JsonElement? FindModel(JsonElement models, string name)
    {
        if (models.ValueKind == JsonValueKind.Array)
        {
            foreach (var model in models.EnumerateArray())
                if (model.ValueKind == JsonValueKind.Object && string.Equals(TryString(model, "model"), name, StringComparison.Ordinal)) return model;
            return null;
        }
        return models.ValueKind == JsonValueKind.Object && models.TryGetProperty(name, out var direct) && direct.ValueKind == JsonValueKind.Object ? direct : null;
    }
    private static JsonElement Property(JsonElement e, string name, PricingAdapterFailure failure) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value)
            ? value
            : throw new PricingAdapterException(failure, $"Missing or invalid {name}.");
    private static string? TryString(JsonElement e, string name) => e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool TryBool(JsonElement e, string name) => e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    private sealed record Group(string Name, JsonElement Element, bool Exclusive, decimal Multiplier);
}
