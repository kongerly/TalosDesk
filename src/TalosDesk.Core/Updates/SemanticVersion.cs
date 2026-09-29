using System.Numerics;

namespace TalosDesk.Core.Updates;

public sealed class SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
{
    private readonly Identifier[] _preRelease;

    private SemanticVersion(BigInteger major, BigInteger minor, BigInteger patch, Identifier[] preRelease, string identity)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        _preRelease = preRelease;
        Identity = identity;
    }

    public BigInteger Major { get; }
    public BigInteger Minor { get; }
    public BigInteger Patch { get; }
    public bool IsPreRelease => _preRelease.Length != 0;
    public string Identity { get; }

    public static bool TryParse(string? value, bool allowLowercaseVPrefix, out SemanticVersion? version)
    {
        version = null;
        if (string.IsNullOrEmpty(value)) return false;
        var text = value;
        if (allowLowercaseVPrefix && text[0] == 'v')
        {
            text = text[1..];
            if (text.Length == 0 || text[0] == 'v') return false;
        }

        var plus = text.IndexOf('+');
        var main = plus < 0 ? text : text[..plus];
        var build = plus < 0 ? null : text[(plus + 1)..];
        if (build is not null && !IsValidIdentifierList(build, numericLeadingZeroRule: false, out _)) return false;
        if (plus >= 0 && text.IndexOf('+', plus + 1) >= 0) return false;

        var dash = main.IndexOf('-');
        var core = dash < 0 ? main : main[..dash];
        var preReleaseText = dash < 0 ? null : main[(dash + 1)..];
        Identifier[]? identifiers = null;
        if (preReleaseText is not null && !IsValidIdentifierList(preReleaseText, numericLeadingZeroRule: true, out identifiers)) return false;

        var components = core.Split('.');
        if (components.Length != 3 || !TryParseNumber(components[0], out var major) ||
            !TryParseNumber(components[1], out var minor) || !TryParseNumber(components[2], out var patch)) return false;

        var preRelease = preReleaseText is null ? [] : identifiers!;
        var identity = $"{components[0]}.{components[1]}.{components[2]}" +
            (preReleaseText is null ? string.Empty : $"-{preReleaseText}");
        version = new SemanticVersion(major, minor, patch, preRelease, identity);
        return true;
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null) return 1;
        var comparison = Major.CompareTo(other.Major);
        if (comparison != 0) return comparison;
        comparison = Minor.CompareTo(other.Minor);
        if (comparison != 0) return comparison;
        comparison = Patch.CompareTo(other.Patch);
        if (comparison != 0) return comparison;
        if (_preRelease.Length == 0) return other._preRelease.Length == 0 ? 0 : 1;
        if (other._preRelease.Length == 0) return -1;
        for (var index = 0; index < Math.Min(_preRelease.Length, other._preRelease.Length); index++)
        {
            comparison = _preRelease[index].CompareTo(other._preRelease[index]);
            if (comparison != 0) return comparison;
        }
        return _preRelease.Length.CompareTo(other._preRelease.Length);
    }

    public bool Equals(SemanticVersion? other) => other is not null && CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is SemanticVersion other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, string.Join('.', _preRelease.Select(item => item.Value)));
    public override string ToString() => Identity;

    private static bool TryParseNumber(string value, out BigInteger number)
    {
        number = default;
        if (value.Length == 0 || value.Length > 1 && value[0] == '0' || value.Any(character => character is < '0' or > '9')) return false;
        return BigInteger.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out number);
    }

    private static bool IsValidIdentifierList(string value, bool numericLeadingZeroRule, out Identifier[]? identifiers)
    {
        identifiers = null;
        var parts = value.Split('.');
        if (parts.Any(part => part.Length == 0)) return false;
        var parsed = new Identifier[parts.Length];
        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            if (part.Any(character => !IsIdentifierCharacter(character))) return false;
            var numeric = part.All(character => character is >= '0' and <= '9');
            if (numericLeadingZeroRule && numeric && part.Length > 1 && part[0] == '0') return false;
            parsed[index] = new Identifier(part, numeric);
        }
        identifiers = parsed;
        return true;
    }

    private static bool IsIdentifierCharacter(char character) =>
        character is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '-';

    private sealed record Identifier(string Value, bool Numeric) : IComparable<Identifier>
    {
        public int CompareTo(Identifier? other)
        {
            if (other is null) return 1;
            if (Numeric != other.Numeric) return Numeric ? -1 : 1;
            if (!Numeric) return string.CompareOrdinal(Value, other.Value);
            if (Value.Length != other.Value.Length) return Value.Length.CompareTo(other.Value.Length);
            return string.CompareOrdinal(Value, other.Value);
        }
    }
}
