using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace BKE.Desktop.Licensing;

/// <summary>
/// PREPRODUCTION ONLY: a typed, fail-closed product startup rendezvous with
/// the installed Licensing Agent. The Agent observes this very process from
/// the Windows kernel; the product MUST NOT send any product ID, PID, path,
/// user identity, credential or activation mode as authority.
/// </summary>
public sealed class BkeStandaloneStartupGateClient
{
    public const string Protocol = "bke.standalone-startup.v1";
    internal const string PipeName = "bke-licensing-agent-standalone-startup-v1";
    private const int MaxFrame = 1024;
    private static readonly Regex Reason = new("^[a-z][a-z0-9_]{0,127}$",
        RegexOptions.CultureInvariant);

    // No environment/config override for the pipe or installed Agent path.
    public async Task<StandaloneStartupGateResult> AuthorizeConnectedProcessAsync(
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            return new(StandaloneStartupGateStatus.UnsupportedPlatform,
                "windows_required");

        var requestId = Guid.NewGuid().ToString("N");
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(8));
            using var pipe = new NamedPipeClientStream(".", PipeName,
                PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);

            // A rogue user process could otherwise create the same local
            // named pipe and answer authorized=true. Check the real server
            // PID from Windows, not a value supplied in the pipe protocol.
            var server = InstalledAgentRuntimeIdentity.TryObserve(pipe);
            if (server is null)
                return new(StandaloneStartupGateStatus.UntrustedAgent,
                    "untrusted_agent_runtime");

            var request = JsonSerializer.SerializeToUtf8Bytes(new {
                schema = Protocol, operation = "authorize", request_id = requestId
            });
            await SendAsync(pipe, request, deadline.Token).ConfigureAwait(false);
            var response = await ReceiveAsync(pipe, deadline.Token).ConfigureAwait(false);

            if (!InstalledAgentRuntimeIdentity.IsStillSame(pipe, server.Value))
                return new(StandaloneStartupGateStatus.UntrustedAgent,
                    "agent_process_changed");

            return ParseResponse(response, requestId);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(StandaloneStartupGateStatus.Timeout, "startup_gate_timeout");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException or System.Security.SecurityException
            or System.ComponentModel.Win32Exception or JsonException)
        {
            return new(StandaloneStartupGateStatus.AgentUnavailable,
                "startup_gate_unavailable");
        }
    }

    private static async Task SendAsync(Stream stream, byte[] payload, CancellationToken token)
    {
        if (payload.Length is < 2 or > MaxFrame)
            throw new InvalidDataException("Startup request exceeds Agent wire limit.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(payload, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReceiveAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is < 2 or > MaxFrame)
            throw new InvalidDataException("Startup Agent response size invalid.");
        var data = new byte[length];
        await stream.ReadExactlyAsync(data, token).ConfigureAwait(false);
        return data;
    }

    internal static StandaloneStartupGateResult ParseResponse(
        ReadOnlySpan<byte> data, string expectedRequestId)
    {
        if (data.Length is < 2 or > MaxFrame ||
            !Guid.TryParseExact(expectedRequestId, "N", out _))
            return new(StandaloneStartupGateStatus.InvalidResponse, "invalid_response");

        try
        {
            using var document = JsonDocument.Parse(data.ToArray(), new JsonDocumentOptions {
                AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4,
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new(StandaloneStartupGateStatus.InvalidResponse, "invalid_response");
            var fields = root.EnumerateObject().Select(p => p.Name).ToArray();
            if (fields.Length != 4 || fields.Distinct(StringComparer.Ordinal).Count() != 4 ||
                !fields.ToHashSet(StringComparer.Ordinal).SetEquals(
                    ["schema", "request_id", "authorized", "reason"]))
                return new(StandaloneStartupGateStatus.InvalidResponse, "invalid_response");

            if (root.GetProperty("schema").ValueKind != JsonValueKind.String ||
                root.GetProperty("schema").GetString() != Protocol ||
                root.GetProperty("request_id").ValueKind != JsonValueKind.String ||
                root.GetProperty("request_id").GetString() != expectedRequestId ||
                root.GetProperty("authorized").ValueKind is not
                    (JsonValueKind.True or JsonValueKind.False) ||
                root.GetProperty("reason").ValueKind != JsonValueKind.String)
                return new(StandaloneStartupGateStatus.InvalidResponse, "invalid_response");

            var reason = root.GetProperty("reason").GetString() ?? "";
            if (!Reason.IsMatch(reason))
                return new(StandaloneStartupGateStatus.InvalidResponse, "invalid_response");

            var authorized = root.GetProperty("authorized").GetBoolean();
            if (authorized && reason != "authorized")
                return new(StandaloneStartupGateStatus.InvalidResponse, "invalid_response");
            if (!authorized && reason == "authorized")
                return new(StandaloneStartupGateStatus.InvalidResponse, "invalid_response");

            return authorized
                ? new(StandaloneStartupGateStatus.Authorized, reason)
                : new(StandaloneStartupGateStatus.Denied, reason);
        }
        catch (JsonException)
        {
            return new(StandaloneStartupGateStatus.InvalidResponse, "invalid_response");
        }
        catch (InvalidOperationException)
        {
            return new(StandaloneStartupGateStatus.InvalidResponse, "invalid_response");
        }
    }
}

public enum StandaloneStartupGateStatus {
    Authorized, Denied, AgentUnavailable, UntrustedAgent, Timeout,
    InvalidResponse, UnsupportedPlatform,
}

public sealed record StandaloneStartupGateResult(
    StandaloneStartupGateStatus Status, string Reason)
{
    public bool Authorized => Status == StandaloneStartupGateStatus.Authorized;
}

/// <summary>
/// Kernel-observed Windows server PID plus pinned Program Files runtime path
/// and LocalSystem primary token; no user-provided path or machine identity.
/// All checks must pass again before trusting the returned decision.
/// This is local installed runtime verification, NOT publisher/TPM attestation.
/// </summary>
internal static class InstalledAgentRuntimeIdentity
{
    internal readonly record struct Evidence(uint Pid, long StartedUtcTicks, string Path);
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;

    internal static Evidence? TryObserve(NamedPipeClientStream pipe)
    {
        if (!OperatingSystem.IsWindows() ||
            !GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid) ||
            pid == 0 || !GetNamedPipeServerSessionId(pipe.SafePipeHandle, out var session) ||
            session != 0)
            return null;
        try
        {
            using var process = Process.GetProcessById(checked((int)pid));
            if (process.HasExited || process.SessionId != 0)
                return null;

            var basePath = Environment.GetFolderPath(
                Environment.SpecialFolder.ProgramFiles);
            if (string.IsNullOrWhiteSpace(basePath))
                return null;
            var root = Path.Combine(basePath, "BKE Digital Solutions", "Licensing Agent");
            var expected = Path.GetFullPath(Path.Combine(
                root, "runtime", "bke-licensing-agent-runtime.exe"));
            if (!Path.Equals(Path.GetFullPath(root), root) ||
                new[] { root, Path.Combine(root, "runtime") }
                    .Any(x => !Directory.Exists(x) ||
                        (File.GetAttributes(x) & FileAttributes.ReparsePoint) != 0))
                return null;

            using var handle = OpenProcess(ProcessQueryLimitedInformation,
                false, pid);
            if (handle.IsInvalid)
                return null;

            var buffer = new StringBuilder(32768);
            var length = buffer.Capacity;
            if (!QueryFullProcessImageName(handle, 0, buffer, ref length) ||
                !string.Equals(Path.GetFullPath(buffer.ToString()),
                    expected, StringComparison.OrdinalIgnoreCase))
                return null;
            if (!OpenProcessToken(handle, TokenQuery, out var token))
                return null;
            using (token)
            using (var user = new WindowsIdentity(token.DangerousGetHandle())) {
                if (user.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) != true)
                    return null;
            }
            return new Evidence(pid, process.StartTime.ToUniversalTime().Ticks, expected);
        }
        catch (Exception error) when (error is ArgumentException or IOException
            or UnauthorizedAccessException or InvalidOperationException
            or System.ComponentModel.Win32Exception or System.Security.SecurityException)
        {
            return null;
        }
    }

    internal static bool IsStillSame(NamedPipeClientStream pipe, Evidence original)
    {
        var candidate = TryObserve(pipe);
        return candidate is not null && candidate.Value == original;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(
        SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerSessionId(
        SafePipeHandle pipe, out uint serverSessionId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(
        SafeProcessHandle process, uint flags, StringBuilder path, ref int length);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(
        SafeProcessHandle process, uint desiredAccess, out SafeAccessTokenHandle token);
}
