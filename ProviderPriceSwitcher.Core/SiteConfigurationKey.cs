using System.Security.Cryptography;
using System.Text;

namespace ProviderPriceSwitcher.Core;

public static class SiteConfigurationKey
{
    public static string Create(string providerId, string siteType, Uri baseUrl, string model, string group)
    {
        var normalized = string.Equals(siteType, "new-api", StringComparison.Ordinal)
            ? $"{providerId.Trim().ToLowerInvariant()}|{baseUrl.AbsoluteUri.TrimEnd('/').ToLowerInvariant()}|{group.Trim()}|{model.Trim()}"
            : $"{providerId.Trim().ToLowerInvariant()}|{siteType.Trim().ToLowerInvariant()}|{baseUrl.AbsoluteUri.TrimEnd('/').ToLowerInvariant()}|{group.Trim()}|{model.Trim()}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..24].ToLowerInvariant();
    }
}
