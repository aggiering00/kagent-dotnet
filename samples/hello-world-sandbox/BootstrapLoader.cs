using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Kagent.Runtime.V1;

namespace HelloWorldSandbox;

/// <summary>
/// Loads the immutable configuration bundle kagent pins for this runtime revision.
/// Ported from kagent v1.0.0-rc3 (go/adk/pkg/bootstrap/load.go, go/api/bootstrap/bundle.go,
/// python/packages/kagent-core/src/kagent/core/_bootstrap.py): the controller sets only a
/// descriptor in KAGENT_BOOTSTRAP_JSON; the golden actor fetches the bundle over
/// kagent.runtime.v1.BootstrapService and saves it durably before readiness, so sessions,
/// forks and restores load it from disk without contacting the control plane.
/// </summary>
public static class BootstrapLoader
{
    private const string DescriptorEnvironment = "KAGENT_BOOTSTRAP_JSON";
    private const string BootstrapDirectory = "/data/kagent/bootstrap";
    private const uint FormatVersion = 1;
    private const int MaxPayloadBytes = 256 << 10;
    // Allows 1 KiB for protobuf metadata above the payload budget.
    private const int MaxBundleBytes = MaxPayloadBytes + (1 << 10);
    // Leave headroom within the 30-second wakeup probe. Exhaustion fails startup;
    // the controller's golden retry recreates the template if binding stays unavailable.
    private static readonly TimeSpan FetchBudget = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(3);

    private sealed record Descriptor(uint FormatVersion, string ControlPlaneUrl, string Revision, string Sha256);

    public static async Task<BootstrapBundle> LoadAsync(CancellationToken ct = default)
    {
        var raw = Environment.GetEnvironmentVariable(DescriptorEnvironment)
            ?? throw new InvalidOperationException($"{DescriptorEnvironment} is not set; this image must run in a kagent v1.0.0-rc3+ sandbox Harness.");
        var descriptor = ParseDescriptor(raw);

        var path = Path.Combine(BootstrapDirectory, "bundle.pb");
        if (new DirectoryInfo(BootstrapDirectory).LinkTarget != null)
        {
            throw new InvalidOperationException("invalid bootstrap directory");
        }
        var durable = ReadBundle(path);
        if (durable != null)
        {
            Verify(durable, descriptor);
            return durable;
        }

        var bundle = await FetchAsync(descriptor, ct);
        Verify(bundle, descriptor);
        Save(path, bundle);
        return bundle;
    }

    private static Descriptor ParseDescriptor(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        var descriptor = new Descriptor(
            root.TryGetProperty("format_version", out var version) && version.TryGetUInt32(out var v) ? v : 0,
            root.TryGetProperty("control_plane_url", out var url) ? url.GetString() ?? "" : "",
            root.TryGetProperty("revision", out var revision) ? revision.GetString() ?? "" : "",
            root.TryGetProperty("sha256", out var sha) ? sha.GetString() ?? "" : "");

        if (!Uri.TryCreate(descriptor.ControlPlaneUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || uri.Host.Length == 0 || uri.UserInfo.Length != 0
            || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
        {
            throw new InvalidOperationException("invalid bootstrap control-plane URL");
        }
        if (descriptor.FormatVersion != FormatVersion || !IsDigest(descriptor.Revision) || !IsDigest(descriptor.Sha256))
        {
            throw new InvalidOperationException("invalid bootstrap descriptor version or digest");
        }
        return descriptor;
    }

    /// <summary>Returns the durable bundle, or null when none has been saved yet.</summary>
    private static BootstrapBundle? ReadBundle(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return null;
        }
        if (info.LinkTarget != null || info.Length > MaxBundleBytes)
        {
            throw new InvalidOperationException("invalid durable bootstrap file");
        }
        return BootstrapBundle.Parser.ParseFrom(File.ReadAllBytes(path));
    }

    private static async Task<BootstrapBundle> FetchAsync(Descriptor descriptor, CancellationToken ct)
    {
        using var channel = GrpcChannel.ForAddress(descriptor.ControlPlaneUrl, new GrpcChannelOptions
        {
            MaxReceiveMessageSize = MaxBundleBytes,
            HttpHandler = CreateHandler(descriptor.ControlPlaneUrl),
        });
        var client = new BootstrapService.BootstrapServiceClient(channel);
        // Substrate replaces existing headers; without this placeholder the gateway
        // forwards the request without minting the actor JWT the service authorizes.
        var headers = new Metadata { { "authorization", "Bearer placeholder" } };

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(FetchBudget);
        while (true)
        {
            // Retry only while the actor's identity binding is unavailable; other errors fail startup.
            try
            {
                var response = await client.GetBootstrapConfigAsync(
                    new BootstrapServiceGetBootstrapConfigRequest(),
                    new CallOptions(headers, DateTime.UtcNow.Add(AttemptTimeout), budget.Token));
                return response.Bundle ?? throw new InvalidOperationException("bootstrap response has no bundle");
            }
            catch (RpcException ex) when (ex.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded && !budget.IsCancellationRequested)
            {
            }
            catch (Exception ex) when (budget.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new TimeoutException("bootstrap initialization deadline exceeded", ex);
            }
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(100, 300)), budget.Token);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("bootstrap initialization deadline exceeded", ex);
            }
        }
    }

    // https control planes are verified against SSL_CERT_FILE, the trust bundle the sandbox mounts.
    private static HttpMessageHandler CreateHandler(string controlPlaneUrl)
    {
        var handler = new SocketsHttpHandler();
        var caFile = Environment.GetEnvironmentVariable("SSL_CERT_FILE");
        if (controlPlaneUrl.StartsWith("https://", StringComparison.Ordinal) && !string.IsNullOrEmpty(caFile))
        {
            var roots = new X509Certificate2Collection();
            roots.ImportFromPemFile(caFile);
            var policy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust };
            policy.CustomTrustStore.AddRange(roots);
            handler.SslOptions.CertificateChainPolicy = policy;
        }
        return handler;
    }

    private static void Verify(BootstrapBundle bundle, Descriptor descriptor)
    {
        if (bundle.FormatVersion != FormatVersion || bundle.Revision != descriptor.Revision)
        {
            throw new InvalidOperationException("bootstrap bundle version or revision mismatch");
        }
        if (bundle.ConfigJson.Length + bundle.AgentCardJson.Length > MaxPayloadBytes)
        {
            throw new InvalidOperationException($"bootstrap configuration and Agent Card exceed {MaxPayloadBytes} bytes");
        }
        // Parsing throws on invalid JSON.
        using (JsonDocument.Parse(bundle.ConfigJson.Memory)) { }
        using (JsonDocument.Parse(bundle.AgentCardJson.Memory)) { }
        if (bundle.Sha256 != descriptor.Sha256 || Digest(bundle) != bundle.Sha256)
        {
            throw new InvalidOperationException("bootstrap bundle digest mismatch");
        }
    }

    // SHA-256 over the big-endian uint32 version, then each of revision, config and card
    // prefixed with its big-endian uint64 length.
    private static string Digest(BootstrapBundle bundle)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> prefix = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(prefix, bundle.FormatVersion);
        hash.AppendData(prefix[..4]);
        foreach (var field in new[] { Encoding.UTF8.GetBytes(bundle.Revision), bundle.ConfigJson.ToByteArray(), bundle.AgentCardJson.ToByteArray() })
        {
            BinaryPrimitives.WriteUInt64BigEndian(prefix, (ulong)field.Length);
            hash.AppendData(prefix);
            hash.AppendData(field);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    // Writes atomically: a crash leaves either no bundle or the complete one.
    private static void Save(string path, BootstrapBundle bundle)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("kagent sandboxes run Linux");
        }
        var directory = Directory.CreateDirectory(BootstrapDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (directory.LinkTarget != null)
        {
            throw new InvalidOperationException("invalid bootstrap directory");
        }
        var temporary = Path.Combine(BootstrapDirectory, $".bundle-{Guid.NewGuid():N}");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                bundle.WriteTo(file);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
        SyncDirectory(BootstrapDirectory);
    }

    // .NET cannot open a directory as a FileStream, so fsync the rename through libc.
    private static void SyncDirectory(string directory)
    {
        var fd = Open(directory, 0 /* O_RDONLY */);
        if (fd < 0)
        {
            throw new IOException($"open {directory} failed: errno {Marshal.GetLastPInvokeError()}");
        }
        try
        {
            if (Fsync(fd) != 0)
            {
                throw new IOException($"fsync {directory} failed: errno {Marshal.GetLastPInvokeError()}");
            }
        }
        finally
        {
            _ = Close(fd);
        }
    }

    private static bool IsDigest(string value) =>
        value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(string path, int flags);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(int fd);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int fd);
}
