using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ProviderPriceSwitcher.Application;
using ProviderPriceSwitcher.Core;

namespace ProviderPriceSwitcher.Infrastructure;

public sealed record SidecarBinaryOptions(string ExecutablePath, string Sha256, string PipeName, int Port = OmpSidecarProvider.DefaultPort);
public sealed class GatewayPortUnavailableException : IOException
{
    public GatewayPortUnavailableException(int port, SocketException innerException)
        : base($"Local gateway port {port} is unavailable.", innerException) => Port = port;

    public int Port { get; }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsSidecarSupervisor : ISidecarLifecycle, IRouteController, ISidecarStatus
{
    private readonly SidecarBinaryOptions _options;
    private readonly IInferenceApiKeyResolver _resolver;
    private readonly object _gate = new();
    private Process? _process;
    private NamedPipeClientStream? _pipe;
    private SidecarStatus _status = new(SidecarConnectionStatus.Stopped);
    private RouteSnapshot? _lastConfirmedRoute;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private Channel<WireMessage> _responses = CreateResponseChannel();
    private CancellationTokenSource? _sessionCancellation;
    private Task? _receiveTask;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(15);

    public WindowsSidecarSupervisor(SidecarBinaryOptions options, IInferenceApiKeyResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(options); ArgumentNullException.ThrowIfNull(resolver);
        _options = options; _resolver = resolver;
    }
    public SidecarStatus Status { get { lock (_gate) return _status; } }
    private RouteSnapshot? CurrentConfirmedRoute
    {
        get { lock (_gate) return _lastConfirmedRoute; }
    }
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
            if (_options.Port is < 1 or > 65535)
                throw new InvalidOperationException("gateway_port_invalid");
            EnsurePortAvailable(_options.Port);
            var psi = new ProcessStartInfo(_options.ExecutablePath) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(_options.ExecutablePath) ?? Environment.CurrentDirectory };
            psi.ArgumentList.Add("--host");
            psi.ArgumentList.Add(OmpSidecarProvider.Host);
            psi.ArgumentList.Add("--port");
            psi.ArgumentList.Add(_options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
                WindowsNamedPipeSecurity.EnsureCurrentUserOnly(_pipe.SafePipeHandle);
                var reply = await SendAsync(new("handshake", null, null, null, null), cancellationToken).ConfigureAwait(false);
                if (!string.Equals(reply.Version, SidecarProtocol.Version, StringComparison.Ordinal) || !string.Equals(reply.Type, "ready", StringComparison.Ordinal)) throw new InvalidOperationException("sidecar_protocol_mismatch");
                var route = CurrentConfirmedRoute;
                if (route is not null)
                {
                    var recoveryReply = await SendAsync(new("apply_route", route.ProviderId, route.BaseUrl, route.KeyHandle, null), cancellationToken).ConfigureAwait(false);
                    EnsureOk(recoveryReply);
                }
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
        lock (_gate) _lastConfirmedRoute = snapshot;
    }
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await EnsureReady(cancellationToken).ConfigureAwait(false);
        EnsureOk(await SendAsync(new("clear_route", null, null, null, null), cancellationToken).ConfigureAwait(false));
        lock (_gate) _lastConfirmedRoute = null;
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
    private async Task<WireMessage> SendAsync(WireMessage message, CancellationToken cancellationToken)
    {
        await _sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var abortSession = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var pipe = _pipe ?? throw new InvalidOperationException("sidecar_disconnected");
                using var timeout = new CancellationTokenSource(CommandTimeout);
                using var commandCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                try
                {
                    await WriteLockedAsync(pipe, message, commandCancellation.Token).ConfigureAwait(false);
                    return await _responses.Reader.ReadAsync(commandCancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException("sidecar_command_timeout");
                }
            }
            catch
            {
                abortSession = true;
                throw;
            }
        }
        finally
        {
            try
            {
                if (abortSession) AbortSession();
            }
            finally
            {
                _sessionGate.Release();
            }
        }
    }

    private void AbortSession()
    {
        _sessionCancellation?.Cancel();
        var pipe = _pipe;
        _pipe = null;
        pipe?.Dispose();
        var process = _process;
        _process = null;
        if (process is { HasExited: false })
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        }
        process?.Dispose();
        _responses.Writer.TryComplete(new IOException("sidecar_session_aborted"));
        SetStatus(new(SidecarConnectionStatus.Disconnected, "sidecar_session_aborted"));
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
    private static void EnsurePortAvailable(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
        }
        catch (SocketException exception)
        {
            throw new GatewayPortUnavailableException(port, exception);
        }
    }
    private void ValidateBinary() { if (!File.Exists(_options.ExecutablePath)) throw new FileNotFoundException("sidecar_binary_missing", _options.ExecutablePath); using var sha = SHA256.Create(); using var stream = File.OpenRead(_options.ExecutablePath); var actual = Convert.ToHexString(sha.ComputeHash(stream)); if (!string.Equals(actual, _options.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("sidecar_binary_hash_mismatch"); }
    private static void ValidateRoute(RouteSnapshot route) { if (string.IsNullOrWhiteSpace(route.ProviderId) || !Uri.TryCreate(route.BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(route.KeyHandle)) throw new ArgumentException("invalid_route"); }
    private static void EnsureOk(WireMessage reply) { if (!string.Equals(reply.Type, "ok", StringComparison.Ordinal)) throw new InvalidOperationException(reply.Type ?? "sidecar_request_failed"); }
    private sealed record WireMessage(string Type, string? ProviderId, string? BaseUrl, string? KeyHandle, string? ApiKey) { public string Version { get; init; } = SidecarProtocol.Version; }
}

[SupportedOSPlatform("windows")]
internal static class WindowsNamedPipeSecurity
{
    private const int SeKernelObject = 6;
    private const uint OwnerAndDacl = 0x00000001 | 0x00000004;
    private static readonly SecurityIdentifier OwnerRights = new("S-1-3-4");

    internal static void EnsureCurrentUserOnly(SafePipeHandle pipeHandle)
    {
        var result = GetSecurityInfo(pipeHandle, SeKernelObject, OwnerAndDacl, out _, out _, out _, out _, out var securityDescriptor);
        if (result != 0) throw new InvalidOperationException("sidecar_pipe_acl_unavailable");
        try
        {
            if (!ConvertSecurityDescriptorToStringSecurityDescriptor(securityDescriptor, 1, OwnerAndDacl, out var stringDescriptor, out _))
                throw new InvalidOperationException("sidecar_pipe_acl_unavailable");
            try
            {
                var descriptor = new RawSecurityDescriptor(Marshal.PtrToStringUni(stringDescriptor) ?? throw new InvalidOperationException("sidecar_pipe_acl_invalid"));
                var currentUser = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("sidecar_pipe_identity_unavailable");
                if (!IsCurrentUserOnly(descriptor, currentUser))
                    throw new UnauthorizedAccessException("sidecar_pipe_not_current_user_only");
            }
            finally
            {
                LocalFree(stringDescriptor);
            }
        }
        finally
        {
            LocalFree(securityDescriptor);
        }
    }

    internal static bool IsCurrentUserOnly(RawSecurityDescriptor descriptor, SecurityIdentifier currentUser)
    {
        var ownerMatches = descriptor.Owner?.Equals(currentUser) == true;
        var accessRules = descriptor.DiscretionaryAcl?.OfType<CommonAce>().Where(ace => ace.AceQualifier == AceQualifier.AccessAllowed).ToArray() ?? [];
        var currentUserHasAccess = accessRules.Any(ace => ace.SecurityIdentifier.Equals(currentUser) || ace.SecurityIdentifier.Equals(OwnerRights));
        var allowedSubjectsOnly = accessRules.All(ace => ace.SecurityIdentifier.Equals(currentUser) || ace.SecurityIdentifier.Equals(OwnerRights));
        return ownerMatches && currentUserHasAccess && allowedSubjectsOnly;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint GetSecurityInfo(SafePipeHandle handle, int objectType, uint securityInformation, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr securityDescriptor);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertSecurityDescriptorToStringSecurityDescriptor(IntPtr securityDescriptor, uint requestedStringSdRevision, uint securityInformation, out IntPtr stringSecurityDescriptor, out uint stringSecurityDescriptorLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
