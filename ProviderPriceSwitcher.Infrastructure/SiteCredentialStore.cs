using System.Security.Cryptography;
using System.Text;
using System.Runtime.Versioning;
using System.Text.Json;
using ProviderPriceSwitcher.Core;
using ProviderPriceSwitcher.Application;

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
        try
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
                AccessTokenSummary = Mask(credential.AccessToken),
                CookieSummary = MaskNullable(credential.CookieHeader),
                ExpiresAt = credential.ExpiresAt,
                UpdatedAt = credential.UpdatedAt
            };
        }
        catch (JsonDataException)
        {
            return new SiteCredentialSummary { ProviderId = providerId, Status = SiteCredentialStatus.Invalid, StatusText = "本地凭据无法读取，请重新保存或清除凭据" };
        }
    }

    private string FilePath(string providerId)
    {
        var safe = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(providerId)));
        return Path.Combine(AppDataPaths.GetRoot(_rootDirectory), "credentials", safe + ".bin");
    }

    private static byte[] Entropy(string providerId) => Encoding.UTF8.GetBytes(Purpose + ":" + providerId);
    private static string Mask(string secret) =>
        secret.Length >= 12 ? $"{secret[..4]}********{secret[^4..]}" : "********";

    private static string? MaskNullable(string? secret) =>
        string.IsNullOrEmpty(secret) ? null : Mask(secret);
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
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            throw new JsonDataException(path, ex);
        }
    }

    internal static byte[] Protect(InferenceApiKeyRecord record)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(record with { ApiKey = record.ApiKey.Trim(), BoundGroup = record.BoundGroup.Trim(), UpdatedAt = DateTimeOffset.UtcNow }, AtomicJsonFile.Options);
        return ProtectedData.Protect(payload, Entropy(record.ProviderId), DataProtectionScope.CurrentUser);
    }

    internal void WriteProtected(string providerId, ReadOnlySpan<byte> protectedPayload)
    {
        var path = FilePath(providerId);
        AtomicJsonFile.WriteBytes(path, protectedPayload);
    }

    public void Save(InferenceApiKeyRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.ProviderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.KeyHandle);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.ApiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.BoundGroup);
        WriteProtected(record.ProviderId, Protect(record));
    }

    public void Clear(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        var path = FilePath(providerId);
        if (File.Exists(path)) File.Delete(path);
    }

    public InferenceApiKeySummary? GetSummary(string providerId)
    {
        try
        {
            var record = Load(providerId);
            return record is null ? null : new InferenceApiKeySummary { ProviderId = record.ProviderId, KeyHandle = record.KeyHandle, BoundGroup = record.BoundGroup, MaskedKey = InferenceApiKeySummary.Mask(record.ApiKey), UpdatedAt = record.UpdatedAt };
        }
        catch (JsonDataException)
        {
            return null;
        }
    }

    private string FilePath(string providerId) => Path.Combine(AppDataPaths.GetRoot(_rootDirectory), "inference-keys", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(providerId))) + ".bin");
    private static byte[] Entropy(string providerId) => Encoding.UTF8.GetBytes(Purpose + ":" + providerId);
}

[SupportedOSPlatform("windows")]
public sealed class WindowsInferenceBindingStore : IInferenceBindingStore
{
    private const string TransactionDirectoryName = "inference-binding-transaction";
    private readonly JsonSettingsRepository _settingsRepository;
    private readonly WindowsInferenceApiKeyStore _keyStore;
    private readonly string _rootDirectory;

    public WindowsInferenceBindingStore(JsonSettingsRepository settingsRepository, WindowsInferenceApiKeyStore keyStore, string? rootDirectory = null)
    {
        _settingsRepository = settingsRepository;
        _keyStore = keyStore;
        _rootDirectory = AppDataPaths.GetRoot(rootDirectory);
    }

    public void Recover() => _settingsRepository.ExecuteLocked(RecoverLocked);

    private void RecoverLocked()
    {
        var transactionDirectory = TransactionDirectory;
        if (!Directory.Exists(transactionDirectory)) return;
        var settingsPath = Path.Combine(transactionDirectory, "settings.json");
        var keyPath = Path.Combine(transactionDirectory, "key.bin");
        var providerPath = Path.Combine(transactionDirectory, "provider-id.txt");
        if (!File.Exists(settingsPath) || !File.Exists(keyPath) || !File.Exists(providerPath))
        {
            Directory.Delete(transactionDirectory, true);
            return;
        }
        CommitPrepared(transactionDirectory, File.ReadAllText(providerPath));
    }

    public InferenceApiKeySummary Save(string providerId, string apiKey, string boundGroup)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(boundGroup);
        return _settingsRepository.ExecuteLocked(() =>
        {
            RecoverLocked();
            var settings = _settingsRepository.Load();
            var siteIndex = settings.Sites.ToList().FindIndex(site => string.Equals(site.ProviderId, providerId, StringComparison.Ordinal));
            if (siteIndex < 0) throw new InvalidOperationException($"供应商 '{providerId}' 不存在。");
            var record = new InferenceApiKeyRecord
            {
                ProviderId = providerId,
                KeyHandle = Guid.NewGuid().ToString("N"),
                ApiKey = apiKey,
                BoundGroup = boundGroup
            };
            var sites = settings.Sites.ToArray();
            sites[siteIndex] = sites[siteIndex] with { CurrentGroup = boundGroup };
            var updatedSettings = settings with { Sites = sites };
            var transactionDirectory = TransactionDirectory;
            Directory.CreateDirectory(transactionDirectory);
            var protectedKey = WindowsInferenceApiKeyStore.Protect(record);
            WriteDurable(Path.Combine(transactionDirectory, "settings.json"), JsonSerializer.SerializeToUtf8Bytes(updatedSettings, AtomicJsonFile.Options));
            WriteDurable(Path.Combine(transactionDirectory, "key.bin"), protectedKey);
            WriteDurable(Path.Combine(transactionDirectory, "provider-id.txt"), Encoding.UTF8.GetBytes(providerId));
            CommitPrepared(transactionDirectory, providerId);
            return _keyStore.GetSummary(providerId) ?? throw new InvalidOperationException("inference_binding_commit_failed");
        });
    }

    private string TransactionDirectory => Path.Combine(_rootDirectory, TransactionDirectoryName);

    private void CommitPrepared(string transactionDirectory, string providerId)
    {
        var settingsBytes = File.ReadAllBytes(Path.Combine(transactionDirectory, "settings.json"));
        var keyBytes = File.ReadAllBytes(Path.Combine(transactionDirectory, "key.bin"));
        AtomicJsonFile.WriteBytes(_settingsRepository.FilePath, settingsBytes);
        _keyStore.WriteProtected(providerId, keyBytes);
        Directory.Delete(transactionDirectory, true);
    }

    private static void WriteDurable(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(true);
    }
}
