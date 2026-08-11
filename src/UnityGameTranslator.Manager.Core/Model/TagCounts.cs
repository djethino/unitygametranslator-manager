using UnityGameTranslator.Common;

namespace UnityGameTranslator.Manager.Core.Model;

/// <summary>
/// What a translation file is made of: the five counts the website publishes, read here from a
/// file on disk instead.
///
/// ⚠ **The website is the reference**, exactly as it is for <see cref="Quality"/>. This is a port
/// of App\Models\Translation::extractTagCounts, not an interpretation of it — the same file has to
/// read the same way in a browser, in the game and here, or the bar moves depending on where you
/// look at it. The rules, spelled out because none of them is obvious:
///  · keys starting with "_" are metadata, not lines;
///  · an entry is {"v": "...", "t": "H"} — a bare string is the old format and counts as AI;
///  · "H" with an EMPTY value is a capture: text the mod met and nobody has dealt with. Work
///    identified, not work done, so it is neither translated nor settled;
///  · "S" — deliberately left as it is, a proper noun or a brand — counts as settled but is never
///    part of the bar;
///  · "M" is the mod's own interface. Technical noise, counted nowhere;
///  · an unknown tag counts as AI, because a file from a newer mod must still be readable.
/// </summary>
public sealed record TagCounts(int Human, int Validated, int Ai, int Captured, int Skipped)
{
    public static readonly TagCounts Empty = new(0, 0, 0, 0, 0);

    /// <summary>
    /// The same five counts as the server sends them for a published file.
    ///
    /// Exists so that one bar can draw a file on this machine and a file on the site: they are
    /// the same measurement, and giving each its own drawing code is how the two ended up able
    /// to disagree in the first place.
    /// </summary>
    public static TagCounts From(OnlineTranslation translation) => new(
        translation.HumanCount,
        translation.ValidatedCount,
        translation.AiCount,
        translation.CaptureCount,
        translation.SkippedCount);

    /// <summary>Lines whose fate is decided, translated or deliberately left alone.</summary>
    public int Settled => Human + Validated + Skipped + Ai;

    /// <summary>Lines that actually carry a translation. NOT the same as settled.</summary>
    public int Translated => Human + Validated + Ai;

    /// <summary>Nothing has been read back yet, or nothing at all is in the file.</summary>
    public bool IsEmpty => Settled + Captured == 0;

    /// <summary>How much of what the file met in game is settled.</summary>
    public double? Completeness => Quality.Completeness(Human, Validated, Skipped, Ai, Captured);

    /// <summary>How much of it a human settled.</summary>
    public double? ReviewCoverage => Quality.ReviewCoverage(Human, Validated, Skipped, Ai);

    /// <summary>Where the reading stands, or null when it is too early to say.</summary>
    public ReviewStage? Stage => Quality.Stage(Human, Validated, Skipped, Ai, Captured);

    /// <summary>Text was met and none of it was dealt with — not "a translation at zero".</summary>
    public bool IsCaptureOnly => Quality.IsCaptureOnly(Human, Validated, Skipped, Ai, Captured);
}
