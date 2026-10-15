using Postfold.Core.Models.Updates;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Postfold.Core.Policies.Updates;

internal static class UpdateContract
{
    internal const int MaximumDocumentBytes = 65536;
    internal const int MaximumFeedBytes = 262144;
    internal const long MaximumPackageBytes = 2147483648;

    internal static JsonDocument Document(ReadOnlyMemory<byte> bytes, int maximum = MaximumDocumentBytes)
    {
        Require(bytes.Length is > 0 && bytes.Length <= maximum);
        string text = new UTF8Encoding(false, true).GetString(bytes.Span);
        Require(text[0] != '\uFEFF');
        JsonDocument document = JsonDocument.Parse(text, new JsonDocumentOptions
        {
            MaxDepth = 8
        });
        try
        {
            Unique(document.RootElement);
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    internal static UpdateManifest Manifest(ReadOnlyMemory<byte> bytes)
    {
        using JsonDocument document = Document(bytes);
        JsonElement root = document.RootElement;
        Fields(root, "kind,schema,keyId,product,rid,channel,toolChannel,sequence,version,sourceCommit,minimumApp,minimumFormat,maximumFormat,targetFormat,originMigrations,targetMigration,feed,package");
        Identity(root, "PostfoldUpdateManifest");
        string channel = Text(root, "channel");
        string version = Text(root, "version");
        UpdateVersion parsed = UpdateVersion.Parse(version);
        Require(channel == "beta" || parsed.Beta is null);
        Require(Text(root, "toolChannel") == "win-x64-" + channel);
        string source = Text(root, "sourceCommit");
        Require(Matches(source, "[a-f0-9]{40}"));
        string minimumApp = Text(root, "minimumApp");
        Require(UpdateVersion.Parse(minimumApp).CompareTo(parsed) <= 0);
        int minimum = Integer(root, "minimumFormat");
        int maximum = Integer(root, "maximumFormat");
        int target = Integer(root, "targetFormat");
        Require(minimum >= 2 && maximum >= minimum && target >= maximum);
        string targetMigration = Migration(Text(root, "targetMigration"));
        JsonElement origins = root.GetProperty("originMigrations");
        Require(origins.ValueKind == JsonValueKind.Array && origins.GetArrayLength() is > 0 and <= 64);
        Require(origins.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String));
        ImmutableArray<string> migrations = origins.EnumerateArray().Select(item => Migration(item.GetString()!)).ToImmutableArray();
        Require(migrations.Distinct(StringComparer.Ordinal).Count() == migrations.Length);
        Require(migrations.All(item => string.CompareOrdinal(item, targetMigration) <= 0));
        UpdateArtifact feed = Artifact(root.GetProperty("feed"), MaximumFeedBytes);
        UpdateArtifact package = Artifact(root.GetProperty("package"), MaximumPackageBytes);
        Require(feed.Name == "releases.win-x64-" + channel + ".json");
        Require(package.Name == "Postfold-" + version + "-win-x64-" + channel + "-full.nupkg");
        return new UpdateManifest(
            Hash(Text(root, "keyId")),
            channel,
            Positive(root, "sequence"),
            version,
            source,
            minimumApp,
            minimum,
            maximum,
            target,
            migrations,
            targetMigration,
            feed,
            package
        );
    }

    internal static UpdateKeyTransition Transition(ReadOnlyMemory<byte> bytes)
    {
        using JsonDocument document = Document(bytes);
        JsonElement root = document.RootElement;
        Fields(root, "kind,schema,keyId,product,rid,channel,epoch,successor,activationSequence,activationVersion,activationSha256");
        Identity(root, "PostfoldKeyTransition");
        string successor = Text(root, "successor");
        Require(successor.Length <= 1024);
        string version = Text(root, "activationVersion");
        UpdateVersion parsed = UpdateVersion.Parse(version);
        string channel = Text(root, "channel");
        Require(channel == "beta" || parsed.Beta is null);
        return new UpdateKeyTransition(
            Hash(Text(root, "keyId")),
            channel,
            Positive(root, "epoch"),
            successor,
            Positive(root, "activationSequence"),
            version,
            Hash(Text(root, "activationSha256"))
        );
    }

    internal static void Fields(JsonElement element, string fields)
    {
        Require(element.ValueKind == JsonValueKind.Object);
        Require(element.EnumerateObject().Select(item => item.Name).ToHashSet(StringComparer.Ordinal)
            .SetEquals(fields.Split(',')));
    }

    internal static string Text(JsonElement root, string name)
    {
        JsonElement value = root.GetProperty(name);
        Require(value.ValueKind == JsonValueKind.String);
        string text = value.GetString()!;
        Require(text.Length is > 0 and <= 1024);
        return text;
    }

    internal static string Hash(string value)
    {
        Require(Matches(value, "[A-F0-9]{64}"));
        return value;
    }

    internal static bool Matches(string value, string pattern)
    {
        return Regex.IsMatch(value, "\\A(?:" + pattern + ")\\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    internal static void Require(bool value)
    {
        if (!value)
        {
            throw new InvalidDataException("Update contract rejected.");
        }
    }

    private static void Unique(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                Require(names.Add(property.Name));
                Unique(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                Unique(item);
            }
        }
    }

    private static void Identity(JsonElement root, string kind)
    {
        Require(Text(root, "kind") == kind && Integer(root, "schema") == 1);
        Require(Text(root, "product") == "Postfold" && Text(root, "rid") == "win-x64");
        Require(Text(root, "channel") is "stable" or "beta");
    }

    private static int Integer(JsonElement root, string name)
    {
        JsonElement value = root.GetProperty(name);
        Require(value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _));
        return value.GetInt32();
    }

    private static long Positive(JsonElement root, string name)
    {
        JsonElement value = root.GetProperty(name);
        Require(value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) && number > 0);
        return value.GetInt64();
    }

    private static string Migration(string value)
    {
        if (value is null)
        {
            throw new InvalidDataException("Missing migration identity.");
        }
        Require(value.Length <= 128 && Matches(value, "[0-9]{14}_[A-Za-z][A-Za-z0-9]*"));
        return value;
    }

    private static UpdateArtifact Artifact(JsonElement element, long maximum)
    {
        Fields(element, "name,length,sha256");
        string name = Text(element, "name");
        Require(name.Length <= 128 && Matches(name, "[A-Za-z0-9][A-Za-z0-9._-]*"));
        Require(!name.Contains("..", StringComparison.Ordinal) && !name.EndsWith('.'));
        string stem = name.Split('.')[0].ToUpperInvariant();
        Require(stem is not ("CON" or "PRN" or "AUX" or "NUL" or "CLOCK$") &&
            !Matches(stem, "(?:COM|LPT)[0-9]"));
        long length = Positive(element, "length");
        Require(length <= maximum);
        return new UpdateArtifact(name, length, Hash(Text(element, "sha256")));
    }
}
