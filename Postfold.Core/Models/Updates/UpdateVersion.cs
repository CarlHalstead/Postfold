using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Postfold.Core.Models.Updates;

/// <summary>Canonical v1 release versions: major.minor.patch, optionally followed by -beta.N.</summary>
public sealed record UpdateVersion(int Major, int Minor, int Patch, int? Beta) : IComparable<UpdateVersion>
{
    public static UpdateVersion Parse(string text)
    {
        if (text.Length > 64)
        {
            throw new FormatException("Unsupported update version.");
        }
        Match match = Regex.Match(
            text,
            @"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-beta\.(0|[1-9][0-9]*))?\z",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1)
        );
        if (!match.Success)
        {
            throw new FormatException("Unsupported update version.");
        }

        return new UpdateVersion(
            int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture),
            match.Groups[4].Success ? int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture) : null
        );
    }

    public int CompareTo(UpdateVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        int result = Major.CompareTo(other.Major);
        if (result == 0)
        {
            result = Minor.CompareTo(other.Minor);
        }
        if (result == 0)
        {
            result = Patch.CompareTo(other.Patch);
        }
        if (result == 0)
        {
            result = Beta is null ? (other.Beta is null ? 0 : 1) :
                other.Beta is null ? -1 : Beta.Value.CompareTo(other.Beta.Value);
        }
        return result;
    }
}
