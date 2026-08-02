using System.Security.Cryptography;
using System.Text;
using System.Runtime.Versioning;
using System.Text.Json;
using ProviderPriceSwitcher.Core;

namespace ProviderPriceSwitcher.Infrastructure;


[SupportedOSPlatform("windows")]
public sealed class WindowsSiteCredentialStore : ISiteAccessCredentialStore
{
    private const string Purpose = "ProviderPriceSwitcher.SiteCredential";
    private readonly string? _rootDirectory;

    public WindowsSiteCredentialStore(string? rootDirectory = null) => _rootDirectory = rootDirectory;

    public SiteCredentialRecord? LoadCredential(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        var path = FilePath(providerId);
        if (!File.Exists(path)) return null;

        try
        {
            var protectedBytes = File.ReadAllBytes(path);
            var bytes = ProtectedData.Unprotect(protectedBytes, Entropy(providerId), DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<SiteCredentialRecord>(bytes, AtomicJsonFile.Options);
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or JsonException)
        {
            throw new JsonDataException(path, ex);
        }
    }

    public void SaveCredential(SiteCredentialRecord credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentException.ThrowIfNullOrWhiteSpace(credential.ProviderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(credential.SiteType);
        ArgumentException.ThrowIfNullOrWhiteSpace(credential.AuthorizationScheme);
        ArgumentException.ThrowIfNullOrWhiteSpace(credential.AccessToken);
        var path = FilePath(credential.ProviderId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var payload = JsonSerializer.SerializeToUtf8Bytes(credential with { UpdatedAt = DateTimeOffset.UtcNow }, AtomicJsonFile.Options);
        var protectedBytes = ProtectedData.Protect(payload, Entropy(credential.ProviderId), DataProtectionScope.CurrentUser);
        File.WriteAllBytes(path, protectedBytes);
    }

    public void ClearCredential(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        var path = FilePath(providerId);
        if (File.Exists(path)) File.Delete(path);
    }

    public SiteCredentialSummary GetSummary(string providerId)
    {
        var credential = LoadCredential(providerId);
        if (credential is null)
            return new SiteCredentialSummary { ProviderId = providerId, Status = SiteCredentialStatus.NotConfigured, StatusText = "未配置凭据" };

        var expired = credential.ExpiresAt is DateTimeOffset expiresAt && expiresAt <= DateTimeOffset.UtcNow;
        var cookieHint = !string.IsNullOrWhiteSpace(credential.CookieHeader) ? "；已保存浏览器会话 Cookie" : string.Empty;
        return new SiteCredentialSummary
        {
            ProviderId = providerId,
            Status = expired ? SiteCredentialStatus.Expired : SiteCredentialStatus.Available,
            StatusText = (expired ? "访问令牌已过期" : $"已绑定 {credential.AuthorizationScheme} 访问令牌") + cookieHint,
            ExpiresAt = credential.ExpiresAt,
            UpdatedAt = credential.UpdatedAt
        };
    }

    private string FilePath(string providerId)
    {
        var safe = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(providerId)));
        return Path.Combine(AppDataPaths.GetRoot(_rootDirectory), "credentials", safe + ".bin");
    }

    private static byte[] Entropy(string providerId) => Encoding.UTF8.GetBytes(Purpose + ":" + providerId);
}

[SupportedOSPlatform("windows")]
public sealed class WindowsInferenceApiKeyStore : IInferenceApiKeyStore
{
    private const string Purpose = "ProviderPriceSwitcher.InferenceApiKey";
    private readonly string? _rootDirectory;

    public WindowsInferenceApiKeyStore(string? rootDirectory = null) => _rootDirectory = rootDirectory;

    public InferenceApiKeyRecord? Load(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        var path = FilePath(providerId);
        if (!File.Exists(path)) return null;
        try
        {
            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy(providerId), DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<InferenceApiKeyRecord>(bytes, AtomicJsonFile.Options);
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or JsonException)
        {
            throw new JsonDataException(path, ex);
        }
    }

    public void Save(InferenceApiKeyRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.ProviderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.KeyHandle);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.ApiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.BoundGroup);
        var path = FilePath(record.ProviderId);
        var payload = JsonSerializer.SerializeToUtf8Bytes(record with { ApiKey = record.ApiKey.Trim(), BoundGroup = record.BoundGroup.Trim(), UpdatedAt = DateTimeOffset.UtcNow }, AtomicJsonFile.Options);
        var protectedPayload = ProtectedData.Protect(payload, Entropy(record.ProviderId), DataProtectionScope.CurrentUser);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temp, protectedPayload);
            File.Move(temp, path, true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public void Clear(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        var path = FilePath(providerId);
        if (File.Exists(path)) File.Delete(path);
    }

    public InferenceApiKeySummary? GetSummary(string providerId)
    {
        var record = Load(providerId);
        return record is null ? null : new InferenceApiKeySummary { ProviderId = record.ProviderId, KeyHandle = record.KeyHandle, BoundGroup = record.BoundGroup, MaskedKey = InferenceApiKeySummary.Mask(record.ApiKey), UpdatedAt = record.UpdatedAt };
    }

    private string FilePath(string providerId) => Path.Combine(AppDataPaths.GetRoot(_rootDirectory), "inference-keys", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(providerId))) + ".bin");
    private static byte[] Entropy(string providerId) => Encoding.UTF8.GetBytes(Purpose + ":" + providerId);
}
