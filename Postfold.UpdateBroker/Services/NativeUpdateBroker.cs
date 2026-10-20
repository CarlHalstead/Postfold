using Postfold.Core.Enums;
using Postfold.Core.Models.Updates;
using Postfold.Core.Policies.Updates;
using Postfold.Desktop.Stores;
using Postfold.UpdateBroker.Models;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace Postfold.UpdateBroker.Services;

/// <summary>Independent full-only Windows apply owner, available only through explicitly trusted composition.</summary>
public static class NativeUpdateBroker
{
    /// <summary>
    /// Reads one length-prefixed bounded request from a private launcher pipe, acknowledges locked verification,
    /// observes the exact old process exit, then requires a one-byte explicit apply authorization on that pipe.
    /// EOF, cancellation or deadline before authorization installs nothing. After launch, interruption kills the job.
    /// </summary>
    public static async Task<int> RunAsync(Stream input, TextWriter events, CancellationToken cancellationToken)
    {
        using BrokerEvents output = new BrokerEvents(events);
        try
        {
            if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            {
                throw new InvalidDataException("This boundary is Windows x64 only.");
            }
            byte[] prefix = new byte[4];
            await input.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
            int length = BitConverter.ToInt32(prefix);
            if (length is <= 0 or > 524288)
            {
                throw new InvalidDataException("Invalid request size.");
            }
            byte[] bytes = new byte[length];
            await input.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            using JsonDocument envelope = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                MaxDepth = 4
            });
            JsonProperty[] properties = envelope.RootElement.EnumerateObject().ToArray();
            if (properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length)
            {
                throw new InvalidDataException("Duplicate trusted input fields.");
            }
            NativeUpdateRequest request = JsonSerializer.Deserialize<NativeUpdateRequest>(
                bytes,
                new JsonSerializerOptions
                {
                    UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
                }
            ) ??
                throw new InvalidDataException("Missing trusted request.");
            object?[] fields =
            [
                request.BrokerSha256,
                request.HelperPath,
                request.HelperSha256,
                request.InstallRoot,
                request.InstalledVersion,
                request.InstalledManifestSha256,
                request.Channel,
                request.Migration,
                request.PinnedRoot,
                request.TrustJournal,
                request.ManifestSha256,
                request.Manifest,
                request.Signature,
                request.Feed,
                request.PackagePath
            ];
            if (fields.Any(value => value is null))
            {
                throw new InvalidDataException("Incomplete trusted request.");
            }
            using LockedUpdateFiles locks = new LockedUpdateFiles();
            using LockedUpdateFiles profileLease = new LockedUpdateFiles();
            string self = Environment.ProcessPath!;
            string root = LockedUpdateFiles.CanonicalPath(request.InstallRoot);
            if (self.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase) ||
                request.HelperPath.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase) ||
                request.PackagePath.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase) ||
                request.TrustJournal.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Apply owner, helper, trust and staged bytes must survive installation replacement.");
            }
            using FileStream boundary = locks.Verify(self, request.BrokerSha256);
            using FileStream helper = locks.Verify(request.HelperPath, request.HelperSha256, 33554432);
            locks.PinDirectory(root);
            string? profile = request.RestartProfile is null ? null : LockedUpdateFiles.CanonicalPath(request.RestartProfile);
            if (profile is not null)
            {
                if (profile.Equals(root, StringComparison.OrdinalIgnoreCase) || profile.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Profile must survive installation replacement.");
                }
                locks.PinDirectory(profile);
            }
            locks.PinDirectory(Path.GetDirectoryName(request.TrustJournal)!);
            output.Attach(request.TrustJournal + ".apply-" + Guid.NewGuid().ToString("N") + ".log");
            using (Process owner = Process.GetCurrentProcess())
            {
                await ReportAsync(output, "broker-owner:" + owner.Id + ":" + owner.StartTime.ToUniversalTime().Ticks).ConfigureAwait(false);
            }
            using Process old = Process.GetProcessById(request.OldProcessId);
            // Acquire the process handle before acknowledgement: PID reuse cannot satisfy the exit barrier.
            _ = old.Handle;
            if (old.Id == Environment.ProcessId || old.StartTime.ToUniversalTime().Ticks != request.OldProcessStartUtcTicks)
            {
                throw new InvalidDataException("Old process identity mismatch.");
            }
            if (!File.Exists(request.TrustJournal) || !File.Exists(request.TrustJournal + ".head"))
            {
                throw new InvalidDataException("Native apply cannot initialize missing trust evidence.");
            }
            using UpdateTrustStore trust = UpdateTrustStore.Open(request.TrustJournal, request.PinnedRoot, request.Channel, allowCreate: false).Value ??
                throw new InvalidDataException("Trust replay failed.");
            UpdateManifest manifest = UpdateTrust.VerifyManifest(request.Manifest, request.Signature, trust.State.PublicKey).Value ??
                throw new InvalidDataException("Manifest authentication failed.");
            if (Convert.ToHexString(SHA256.HashData(request.Manifest)) != request.ManifestSha256 ||
                trust.State.ManifestSha256 != request.ManifestSha256 || trust.State.Pending is not null ||
                !UpdateTrust.Compatible(manifest, request.Channel, request.InstalledVersion, request.ProfileFormat, request.Migration) ||
                UpdateArtifacts.VerifyFeed(request.Feed, manifest) != Failure.None ||
                Path.GetFileName(request.PackagePath) != manifest.Package.Name)
            {
                throw new InvalidDataException("Selected durable release, feed or compatibility changed.");
            }
            using FileStream package = locks.OpenRead(request.PackagePath);
            if (await UpdateArtifacts.VerifyAsync(package, manifest.Package, cancellationToken).ConfigureAwait(false) != Failure.None)
            {
                throw new InvalidDataException("Package bytes changed.");
            }
            package.Position = 0;
            VerifyPackageIdentity(package, manifest.Version);
            VerifyInstalled(request);
            await ReportAsync(output, "release:" + request.ManifestSha256 + ":package:" + manifest.Package.Sha256 + ":feed:" + manifest.Feed.Sha256).ConfigureAwait(false);
            await ReportAsync(output, "verified").ConfigureAwait(false);
            await old.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await ReportAsync(output, "old-exited").ConfigureAwait(false);
            byte[] authorization = new byte[1];
            await input.ReadExactlyAsync(authorization, cancellationToken).ConfigureAwait(false);
            if (authorization[0] != 1)
            {
                throw new InvalidDataException("Apply was not authorized.");
            }
            VerifyInstalled(request);
            cancellationToken.ThrowIfCancellationRequested();
            if (profile is not null)
            {
                // This lease must already exist; never create or repair profile state at the native boundary.
                using FileStream lease = profileLease.OpenRead(Path.Combine(profile, "profile.db.lease"));
                await ReportAsync(output, "profile-released").ConfigureAwait(false);
            }
            locks.AllowChildRenames(root);
            await ReportAsync(output, "applying").ConfigureAwait(false);
            int exit = await NativeApplyProcess.RunAsync(
                request.HelperPath,
                root,
                request.PackagePath,
                request.TrustJournal + ".native.log",
                locks.Handles.Concat(profileLease.Handles).ToArray(),
                value =>
                {
                    output.WriteLine(value);
                    output.Flush();
                },
                cancellationToken
            ).ConfigureAwait(false);
            if (exit != 0)
            {
                throw new InvalidDataException("Native helper failed: " + exit);
            }
            VerifyInstalledVersion(root, manifest.Version);
            await ReportAsync(output, "installed:" + manifest.Version).ConfigureAwait(false);
            if (profile is not null)
            {
                profileLease.Dispose();
                package.Position = 0;
                using ZipArchive archive = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
                ZipArchiveEntry entry = archive.GetEntry("lib/app/Postfold.exe") ?? throw new InvalidDataException("Missing normal application.");
                using Stream payload = entry.Open();
                string digest = Convert.ToHexString(await SHA256.HashDataAsync(payload, cancellationToken).ConfigureAwait(false));
                using LockedUpdateFiles startup = new LockedUpdateFiles();
                string executable = Path.Combine(root, "current", "Postfold.exe");
                using FileStream verified = startup.Verify(executable, digest);
                // Native consumption is complete. The normal app must not inherit update/trust guards
                // and accidentally prevent its next update.
                locks.PreventInheritance();
                startup.PreventInheritance();
                PreventObserverInheritance();
                ProcessStartInfo start = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(executable)!
                };
                start.ArgumentList.Add("--profile");
                start.ArgumentList.Add(profile);
                foreach (string name in start.Environment.Keys.Where(name =>
                    name.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("CORECLR_", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("COR_", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("COMPlus_", StringComparison.OrdinalIgnoreCase)).ToArray())
                {
                    start.Environment.Remove(name);
                }
                using Process reopened = Process.Start(start) ?? throw new IOException("Normal reopening failed.");
                await ReportAsync(output, "reopened:" + reopened.Id + ":" + reopened.StartTime.ToUniversalTime().Ticks).ConfigureAwait(false);
            }
            return 0;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or Win32Exception or
            ArgumentException or InvalidOperationException or JsonException or CryptographicException or OperationCanceledException or XmlException)
        {
            await ReportAsync(output, "refused:" + exception.GetType().Name).ConfigureAwait(false);
            return 1;
        }
    }

    /// <summary>Checks the authenticated opaque package's bounded native product/version identity without extraction.</summary>
    public static void VerifyPackageIdentity(Stream package, string version)
    {
        using ZipArchive archive = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
        ZipArchiveEntry[] manifests = archive.Entries.Where(entry => entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (manifests.Length != 1 || manifests[0].FullName.Contains('/') || manifests[0].FullName.Contains('\\') || manifests[0].Length > 65536)
        {
            throw new InvalidDataException("Ambiguous native package identity.");
        }
        using Stream nuspec = manifests[0].Open();
        VerifyXmlIdentity(nuspec, version);
    }

    private static async Task ReportAsync(TextWriter events, string value)
    {
        await events.WriteLineAsync(value).ConfigureAwait(false);
        await events.FlushAsync().ConfigureAwait(false);
    }

    private static void PreventObserverInheritance()
    {
        foreach (int standard in new[] { -10, -11, -12 })
        {
            IntPtr handle = GetStdHandle(standard);
            if (handle != IntPtr.Zero && handle != new IntPtr(-1) && !SetHandleInformation(handle, 1, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int standard);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

    private static void VerifyInstalled(NativeUpdateRequest request)
    {
        using LockedUpdateFiles identity = new LockedUpdateFiles();
        using FileStream installed = identity.Verify(Path.Combine(request.InstallRoot, "current", "sq.version"), request.InstalledManifestSha256, 65536);
        VerifyXmlIdentity(installed, request.InstalledVersion);
        // Release current/manifest locks before native replacement; package/helper/path locks remain independent.
    }

    private static void VerifyInstalledVersion(string root, string version)
    {
        using LockedUpdateFiles identity = new LockedUpdateFiles();
        using FileStream installed = identity.OpenRead(Path.Combine(root, "current", "sq.version"));
        VerifyXmlIdentity(installed, version);
    }

    private static void VerifyXmlIdentity(Stream stream, string version)
    {
        using XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 65536
        });
        XDocument document = XDocument.Load(reader);
        XElement[] ids = document.Descendants().Where(element => element.Name.LocalName == "id").ToArray();
        XElement[] versions = document.Descendants().Where(element => element.Name.LocalName == "version").ToArray();
        if (ids.Length != 1 || ids[0].Value != "Postfold" || versions.Length != 1 || versions[0].Value != version)
        {
            throw new InvalidDataException("Native product/version mismatch.");
        }
    }

    private sealed class BrokerEvents(TextWriter observer) : TextWriter
    {
        private StreamWriter? _receipt;

        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            _receipt?.WriteLine(value);
            _receipt?.Flush();
            if (_receipt?.BaseStream is FileStream file)
            {
                file.Flush(flushToDisk: true);
            }
            try
            {
                observer.WriteLine(value);
                observer.Flush();
            }
            catch (IOException)
            {
                // The old application may close its observation pipe by exiting; it no longer owns apply.
            }
        }

        public override Task WriteLineAsync(string? value)
        {
            WriteLine(value);
            return Task.CompletedTask;
        }

        internal void Attach(string path)
        {
            _receipt = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _receipt?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
