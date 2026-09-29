namespace TwinkleTray.Core;

/// <summary>Strict SemVer 2.0 precedence, including prereleases and build metadata. See https://semver.org/.</summary>
public static class SemanticVersion
{
    public static int Compare(string left, string right) => TryCompare(left, right, out var result)
        ? result : throw new FormatException("Both versions must be valid major.minor.patch semantic versions.");

    public static bool TryCompare(string? left, string? right, out int result)
    {
        result = 0;
        if (!TryParts(left, out var leftCore, out var leftPre) || !TryParts(right, out var rightCore, out var rightPre)) return false;
        for (int i = 0; i < 3; i++)
        {
            result = CompareNumber(leftCore[i], rightCore[i]);
            if (result != 0) return true;
        }
        if (leftPre.Length == 0 || rightPre.Length == 0)
        {
            result = leftPre.Length == rightPre.Length ? 0 : leftPre.Length == 0 ? 1 : -1;
            return true;
        }
        for (int i = 0; i < Math.Min(leftPre.Length, rightPre.Length); i++)
        {
            bool leftNumeric = IsNumeric(leftPre[i]), rightNumeric = IsNumeric(rightPre[i]);
            result = leftNumeric && rightNumeric ? CompareNumber(leftPre[i], rightPre[i])
                : leftNumeric != rightNumeric ? leftNumeric ? -1 : 1
                : Math.Sign(string.CompareOrdinal(leftPre[i], rightPre[i]));
            if (result != 0) return true;
        }
        result = Math.Sign(leftPre.Length.CompareTo(rightPre.Length));
        return true;
    }

    private static bool TryParts(string? version, out string[] core, out string[] prerelease)
    {
        core = []; prerelease = [];
        if (string.IsNullOrEmpty(version)) return false;
        int plus = version.IndexOf('+');
        if (plus >= 0)
        {
            if (!ValidIdentifiers(version[(plus + 1)..].Split('.'), numericLeadingZeroAllowed: true)) return false;
            version = version[..plus];
        }
        int dash = version.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = version[(dash + 1)..].Split('.');
            if (!ValidIdentifiers(prerelease, numericLeadingZeroAllowed: false)) return false;
            version = version[..dash];
        }
        core = version.Split('.');
        return core.Length == 3 && core.All(item => IsNumeric(item) && (item.Length == 1 || item[0] != '0'));
    }

    private static bool ValidIdentifiers(string[] identifiers, bool numericLeadingZeroAllowed) => identifiers.All(item =>
        item.Length > 0 && item.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') &&
        (numericLeadingZeroAllowed || !IsNumeric(item) || item.Length == 1 || item[0] != '0'));
    private static bool IsNumeric(string value) => value.Length > 0 && value.All(char.IsAsciiDigit);
    private static int CompareNumber(string left, string right) => left.Length != right.Length
        ? Math.Sign(left.Length.CompareTo(right.Length)) : Math.Sign(string.CompareOrdinal(left, right));
}
