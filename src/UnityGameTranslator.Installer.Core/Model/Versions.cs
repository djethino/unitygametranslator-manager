namespace UnityGameTranslator.Installer.Core.Model;

/// <summary>
/// Comparing two version strings, by the same rules the mod uses.
///
/// ⚠ This is a MIRROR of GitHubUpdateChecker.CompareVersions in the mod, not an improvement on it.
/// The two programs read the same tags from the same publisher and must reach the same verdict:
/// one of them deciding that 0.9.66 is older than 0.9.9 while the other says the opposite would
/// show up as "the tool keeps offering an update that never applies", with nothing on screen to
/// say which of the two is wrong. If the mod's rules change, this changes with them.
///
/// The rules, spelled out because they are not the same as a plain string comparison:
/// · each dot-separated part is compared as a NUMBER, so 0.9.10 comes after 0.9.9;
/// · a missing part counts as zero, so 1.2 and 1.2.0 are the same version;
/// · anything after a dash in a part is dropped for the numeric comparison;
/// · at equal numbers, a version carrying a suffix (1.2.0-beta.1) ranks BELOW the plain one.
/// </summary>
public static class Versions
{
    /// <summary>Negative when a is older, zero when equal, positive when a is newer.</summary>
    public static int Compare(string? a, string? b)
    {
        var left = (a ?? string.Empty).Trim().TrimStart('v', 'V');
        var right = (b ?? string.Empty).Trim().TrimStart('v', 'V');

        if (left.Length == 0 && right.Length == 0) return 0;
        if (left.Length == 0) return -1;
        if (right.Length == 0) return 1;

        var leftParts = left.Split('.');
        var rightParts = right.Split('.');
        var count = Math.Max(leftParts.Length, rightParts.Length);

        for (var i = 0; i < count; i++)
        {
            var leftNumber = NumberAt(leftParts, i);
            var rightNumber = NumberAt(rightParts, i);

            if (leftNumber != rightNumber) return leftNumber.CompareTo(rightNumber);
        }

        var leftPrerelease = left.Contains('-');
        var rightPrerelease = right.Contains('-');

        if (leftPrerelease && !rightPrerelease) return -1;
        if (!leftPrerelease && rightPrerelease) return 1;

        return 0;
    }

    /// <summary>True when <paramref name="candidate"/> is strictly newer than <paramref name="current"/>.</summary>
    public static bool IsNewer(string? current, string? candidate) => Compare(current, candidate) < 0;

    private static int NumberAt(string[] parts, int index)
    {
        if (index >= parts.Length) return 0;

        // "3-beta.1" -> "3". A part that is not a number at all counts as zero rather than
        // throwing: a tag we do not recognise must never stop the tool from starting.
        var head = parts[index].Split('-')[0];
        return int.TryParse(head, out var value) ? value : 0;
    }
}
