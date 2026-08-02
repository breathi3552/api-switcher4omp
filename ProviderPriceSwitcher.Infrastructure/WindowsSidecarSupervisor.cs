using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ProviderPriceSwitcher.Application;
using ProviderPriceSwitcher.Core;

namespace ProviderPriceSwitcher.Infrastructure;

public sealed record SidecarBinaryOptions(string ExecutablePath, string Sha256, string PipeName);

public sealed class WindowsSidecarSupervisor : ISidecarLifecycle, IRouteController, ISidecarStatus
{
    private readonly SidecarBinaryOptions _options;
    private readonly IInferenceApiKeyResolver _resolver;
    private readonly object _gate = new();
    private Process? _process;
    private NamedPipeClientStream? _pipe;
    private SidecarStatus _status = new(SidecarConnectionStatus.Stopped);
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private Channel<WireMessage> _responses = CreateResponseChannel();
    private CancellationTokenSource? _sessionCancellation;
    private Task? _receiveTask;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public WindowsSidecarSupervisor(SidecarBinaryOptions options, IInferenceApiKeyResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(options); ArgumentNullException.ThrowIfNull(resolver);
        _options = options; _resolver = resolver;
    }
    public SidecarStatus Status { get { lock (_gate) return _status; } }
    public SidecarStatus Current => Status;
    public event Action<SidecarStatus>? Changed;
    private void SetStatus(SidecarStatus value) { lock (_gate) _status = value; Changed?.Invoke(value); }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_status.IsReady) return;
            await CleanupSessionAsync(CancellationToken.None).ConfigureAwait(false);
            SetStatus(new(SidecarConnectionStatus.Starting));
            ValidateBinary();
            var psi = new ProcessStartInfo(_options.ExecutablePath) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(_options.ExecutablePath) ?? Environment.CurrentDirectory };
            psi.ArgumentList.Add("--control-pipe");
            psi.ArgumentList.Add($@"\\.\pipe\{_options.PipeName}");
            _process = Process.Start(psi) ?? throw new InvalidOperationException("sidecar_start_failed");
            _pipe = new NamedPipeClientStream(".", _options.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            _sessionCancellation = new CancellationTokenSource();
            _responses = CreateResponseChannel();
            try
            {
                await _pipe.ConnectAsync(10000, cancellationToken).ConfigureAwait(false);
                _receiveTask = ReceiveLoopAsync(_pipe, _sessionCancellation.Token);
                var reply = await SendAsync(new("handshake", null, null, null, null), cancellationToken).ConfigureAwait(false);
                if (!string.Equals(reply.Version, SidecarProtocol.Version, StringComparison.Ordinal) || !string.Equals(reply.Type, "ready", StringComparison.Ordinal)) throw new InvalidOperationException("sidecar_protocol_mismatch");
                SetStatus(new(SidecarConnectionStatus.Ready));
            }
            catch
            {
                SetStatus(new(SidecarConnectionStatus.Faulted, "sidecar_handshake_failed"));
                await CleanupSessionAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task ApplyAsync(RouteSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot); await EnsureReady(cancellationToken).ConfigureAwait(false);
        ValidateRoute(snapshot);
        var reply = await SendAsync(new("apply_route", snapshot.ProviderId, snapshot.BaseUrl, snapshot.KeyHandle, null), cancellationToken).ConfigureAwait(false);
        EnsureOk(reply);
    }
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await EnsureReady(cancellationToken).ConfigureAwait(false);
        EnsureOk(await SendAsync(new("clear_route", null, null, null, null), cancellationToken).ConfigureAwait(false));
    }
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CleanupSessionAsync(cancellationToken).ConfigureAwait(false);
            SetStatus(new(SidecarConnectionStatus.Stopped));
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }
    public async ValueTask DisposeAsync() { await StopAsync().ConfigureAwait(false); _lifecycleGate.Dispose(); _sessionGate.Dispose(); _writeGate.Dispose(); }
    private async Task EnsureReady(CancellationToken ct) { if (!Status.IsReady) await StartAsync(ct).ConfigureAwait(false); }
    private async Task<WireMessage> SendAsync(WireMessage message, CancellationToken ct)
    {
        await _sessionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var pipe = _pipe ?? throw new InvalidOperationException("sidecar_disconnected");
            await WriteLockedAsync(pipe, message, ct).ConfigureAwait(false);
            return await _responses.Reader.ReadAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _sessionGate.Release();
        }
    }
    private async Task ReceiveLoopAsync(Stream stream, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                var response = await ReadAsync(stream, ct).ConfigureAwait(false);
                if (string.Equals(response.Type, "resolve_key", StringComparison.Ordinal))
                {
                    var key = await _resolver.ResolveAsync(response.KeyHandle ?? "", ct).ConfigureAwait(false);
                    await WriteLockedAsync(stream, new("resolve_key_response", null, null, response.KeyHandle, key), ct).ConfigureAwait(false);
                }
                else
                {
                    await _responses.Writer.WriteAsync(response, ct).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            _responses.Writer.TryComplete(ex);
            var status = ex is EndOfStreamException or IOException or OperationCanceledException
                ? new SidecarStatus(SidecarConnectionStatus.Disconnected, "sidecar_disconnected")
                : new SidecarStatus(SidecarConnectionStatus.Faulted, "sidecar_protocol_invalid");
            if (Status.Status is not SidecarConnectionStatus.Stopped) SetStatus(status);
        }
    }
    private async Task CleanupSessionAsync(CancellationToken cancellationToken)
    {
        var pipe = _pipe;
        if (pipe is { IsConnected: true }) { try { await SendAsync(new("stop", null, null, null, null), cancellationToken).ConfigureAwait(false); } catch { } }
        _sessionCancellation?.Cancel();
        pipe?.Dispose();
        _pipe = null;
        if (_receiveTask is not null) { try { await _receiveTask.ConfigureAwait(false); } catch { } _receiveTask = null; }
        _sessionCancellation?.Dispose();
        _sessionCancellation = null;
        var process = _process;
        _process = null;
        if (process is { HasExited: false })
        {
            using var timeout = new CancellationTokenSource(2000);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try { await process.WaitForExitAsync(linked.Token).ConfigureAwait(false); }
            catch { try { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { } }
        }
        process?.Dispose();
    }
    private static Channel<WireMessage> CreateResponseChannel() => Channel.CreateUnbounded<WireMessage>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private async Task WriteLockedAsync(Stream stream, WireMessage message, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try { await WriteAsync(stream, message, ct).ConfigureAwait(false); }
        finally { _writeGate.Release(); }
    }
    private static async Task WriteAsync(Stream stream, WireMessage message, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message); var len = BitConverter.GetBytes(bytes.Length);
        await stream.WriteAsync(len, ct).ConfigureAwait(false); await stream.WriteAsync(bytes, ct).ConfigureAwait(false); await stream.FlushAsync(ct).ConfigureAwait(false);
    }
    private async Task<WireMessage> ReadAsync(Stream stream, CancellationToken ct)
    {
        var lenBytes = new byte[4]; await ReadExact(stream, lenBytes, ct).ConfigureAwait(false); var length = BitConverter.ToInt32(lenBytes);
        if (length is <= 0 or > 1024 * 1024) throw new InvalidDataException("sidecar_frame_invalid");
        var bytes = new byte[length]; await ReadExact(stream, bytes, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<WireMessage>(bytes, _json) ?? throw new InvalidDataException("sidecar_message_invalid");
    }
    private static async Task ReadExact(Stream stream, byte[] buffer, CancellationToken ct) { var offset = 0; while (offset < buffer.Length) { var n = await stream.ReadAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false); if (n == 0) throw new EndOfStreamException(); offset += n; } }
    private void ValidateBinary() { if (!File.Exists(_options.ExecutablePath)) throw new FileNotFoundException("sidecar_binary_missing", _options.ExecutablePath); using var sha = SHA256.Create(); using var stream = File.OpenRead(_options.ExecutablePath); var actual = Convert.ToHexString(sha.ComputeHash(stream)); if (!string.Equals(actual, _options.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("sidecar_binary_hash_mismatch"); }
    private static void ValidateRoute(RouteSnapshot route) { if (string.IsNullOrWhiteSpace(route.ProviderId) || !Uri.TryCreate(route.BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(route.KeyHandle)) throw new ArgumentException("invalid_route"); }
    private static void EnsureOk(WireMessage reply) { if (!string.Equals(reply.Type, "ok", StringComparison.Ordinal)) throw new InvalidOperationException(reply.Type ?? "sidecar_request_failed"); }
    private sealed record WireMessage(string Type, string? ProviderId, string? BaseUrl, string? KeyHandle, string? ApiKey) { public string Version { get; init; } = SidecarProtocol.Version; }
}
