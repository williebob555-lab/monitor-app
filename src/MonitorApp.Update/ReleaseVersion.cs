using System.Globalization;

namespace MonitorApp.Update;

public readonly record struct ReleaseVersion : IComparable<ReleaseVersion>
{
    public ReleaseVersion(int major, int minor, int patch)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(major);
        ArgumentOutOfRangeException.ThrowIfNegative(minor);
        ArgumentOutOfRangeException.ThrowIfNegative(patch);

        Major = major;
        Minor = minor;
        Patch = patch;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    public static ReleaseVersion Parse(string value)
    {
        if (!TryParse(value, out var version))
        {
            throw new FormatException("Version must use canonical major.minor.patch numeric form.");
        }

        return version;
    }

    public static bool TryParse(string? value, out ReleaseVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Split('.', StringSplitOptions.None);
        if (parts.Length != 3 || parts.Any(IsNonCanonicalPart))
        {
            return false;
        }

        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor) ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            return false;
        }

        version = new ReleaseVersion(major, minor, patch);
        return true;
    }

    public int CompareTo(ReleaseVersion other)
    {
        var majorComparison = Major.CompareTo(other.Major);
        if (majorComparison != 0)
        {
            return majorComparison;
        }

        var minorComparison = Minor.CompareTo(other.Minor);
        return minorComparison != 0 ? minorComparison : Patch.CompareTo(other.Patch);
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

    public static bool operator <(ReleaseVersion left, ReleaseVersion right) =>
        left.CompareTo(right) < 0;

    public static bool operator >(ReleaseVersion left, ReleaseVersion right) =>
        left.CompareTo(right) > 0;

    public static bool operator <=(ReleaseVersion left, ReleaseVersion right) =>
        left.CompareTo(right) <= 0;

    public static bool operator >=(ReleaseVersion left, ReleaseVersion right) =>
        left.CompareTo(right) >= 0;

    private static bool IsNonCanonicalPart(string part) =>
        part.Length == 0 ||
        (part.Length > 1 && part[0] == '0') ||
        part.Any(character => character is < '0' or > '9');
}
