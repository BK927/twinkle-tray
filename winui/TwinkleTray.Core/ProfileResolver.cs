namespace TwinkleTray.Core;

public static class ProfileResolver
{
    /// <summary>Preserves upstream comma-separated path-substring matching and last-match precedence.</summary>
    public static AppProfile? Match(string executablePath, IEnumerable<AppProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        if (string.IsNullOrWhiteSpace(executablePath)) return null;
        var path = executablePath.Replace('/', '\\');
        return profiles.LastOrDefault(profile => profile is not null && profile.Enabled && !string.IsNullOrWhiteSpace(profile.Path) &&
            profile.Path.Split(',').Select(part => part.Trim().Replace('/', '\\')).Any(part => part.Length > 0 && path.Contains(part, StringComparison.OrdinalIgnoreCase)));
    }
}
