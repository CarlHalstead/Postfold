using Postfold.Core.Models.Updates;
using Postfold.Desktop.Models.Updates;
using Postfold.Desktop.Services.Updates;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Postfold.Desktop.Composition;

/// <summary>
/// Installed startup admission; unpackaged builds without an embedded inventory remain inactive.
/// Inventory is embedded at publish from the freshly built broker/helper, never trusted beside them.
/// The admitted runtime owns compatibility; the installed native manifest and assembly own version/source.
/// </summary>
internal static class ProductionUpdatesPreparation
{
    internal static (ClientUpdateConfiguration Client, NativeRestartConfiguration Native)? Review(DesktopMailRuntime admitted)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }
        Assembly assembly = typeof(ProductionUpdatesPreparation).Assembly;
        using Stream? inventory = assembly.GetManifestResourceStream("Postfold.ProductionNativeInventory.json");
        if (inventory is null)
        {
            return null;
        }
        string identity = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        string installation = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
        return ReviewInventory(inventory, installation, identity, admitted.ProfileFormat, admitted.ProfileMigration);
    }

    internal static (ClientUpdateConfiguration Client, NativeRestartConfiguration Native) ReviewInventory(
        Stream inventory,
        string installation,
        string assemblyIdentity,
        int admittedFormat,
        string admittedMigration
    )
    {
        ProductionUpdateIdentity.Validate();
        using JsonDocument document = JsonDocument.Parse(inventory);
        JsonElement root = document.RootElement;
        string version = root.GetProperty("version").GetString()!;
        string source = root.GetProperty("sourceCommit").GetString()!;
        UpdateVersion.Parse(version);
        if (!Regex.IsMatch(source, @"\A[0-9a-f]{40}\z", RegexOptions.CultureInvariant) ||
            assemblyIdentity != version + "+" + source || root.GetProperty("product").GetString() != "Postfold" ||
            root.GetProperty("keyId").GetString() != ProductionUpdateIdentity.KeyId ||
            root.GetProperty("channel").GetString() != ProductionUpdateIdentity.Channel || root.GetProperty("rid").GetString() != "win-x64" ||
            admittedFormat < 2 || string.IsNullOrWhiteSpace(admittedMigration))
        {
            throw new InvalidDataException("Installed build identity mismatch.");
        }
        string broker = Path.Combine(installation, "current", "Postfold.UpdateBroker.exe");
        string helper = Path.Combine(installation, "Update.exe");
        string brokerHash = Verify(broker, root.GetProperty("brokerSha256").GetString()!, 134217728);
        string helperHash = Verify(helper, root.GetProperty("helperSha256").GetString()!, 33554432);
        string manifest = Path.Combine(installation, "current", "sq.version");
        using FileStream input = File.OpenRead(manifest);
        if (input.Length is <= 0 or > 65536)
        {
            throw new InvalidDataException("Invalid installed manifest size.");
        }
        string manifestHash = Convert.ToHexString(SHA256.HashData(input));
        input.Position = 0;
        using XmlReader reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 65536
        });
        XDocument native = XDocument.Load(reader);
        XElement[] ids = native.Descendants().Where(element => element.Name.LocalName == "id").ToArray();
        XElement[] versions = native.Descendants().Where(element => element.Name.LocalName == "version").ToArray();
        if (ids.Length != 1 || ids[0].Value != "Postfold" || versions.Length != 1 || versions[0].Value != version)
        {
            throw new InvalidDataException("Installed native identity mismatch.");
        }
        string state = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Postfold", "updates", "win-x64", ProductionUpdateIdentity.Channel);
        return (
            new ClientUpdateConfiguration(new Uri(ProductionUpdateIdentity.Discovery), ProductionUpdateIdentity.PublicKey, ProductionUpdateIdentity.Channel, version, admittedFormat, admittedMigration, state),
            new NativeRestartConfiguration(broker, brokerHash, helper, helperHash, installation, manifestHash)
        );
    }

    private static string Verify(string path, string expected, long maximum)
    {
        using FileStream stream = File.OpenRead(path);
        if (stream.Length is <= 0 || stream.Length > maximum || Convert.ToHexString(SHA256.HashData(stream)) != expected)
        {
            throw new InvalidDataException("Native build inventory mismatch.");
        }
        return expected;
    }
}
