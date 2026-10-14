using System;
using System.IO;

namespace Postfold.Desktop.Stores;

/// <summary>Remembers an explicitly selected fresh profile without moving or deleting previous profile data.</summary>
internal sealed class ProfileSelectionStore(string directory)
{
    internal string DirectoryPath => directory;

    internal string ReadDirectory()
    {
        string selection = Path.Combine(directory, "active-profile.txt");
        if (!File.Exists(selection))
        {
            return Path.Combine(directory, "default");
        }
        string name = File.ReadAllText(selection).Trim();
        if (!name.StartsWith("fresh-", StringComparison.Ordinal) || !Guid.TryParseExact(name[6..], "N", out _))
        {
            throw new InvalidDataException("Invalid active profile selection.");
        }
        return Path.Combine(directory, name);
    }

    internal string NewDirectory()
    {
        return Path.Combine(directory, "fresh-" + Guid.NewGuid().ToString("N"));
    }

    internal void SaveDirectory(string path)
    {
        string name = Path.GetFileName(path);
        if (Path.GetDirectoryName(path) != directory || !name.StartsWith("fresh-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(name[6..], "N", out _))
        {
            throw new ArgumentException("Profile selection must be a fresh folder in the profile directory.", nameof(path));
        }
        Directory.CreateDirectory(directory);
        string selection = Path.Combine(directory, "active-profile.txt");
        string temporary = selection + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (StreamWriter writer = new StreamWriter(stream))
        {
            writer.Write(name + "\n");
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, selection, overwrite: true);
    }
}
