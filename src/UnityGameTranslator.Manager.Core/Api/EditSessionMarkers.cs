using System.Text.Json;
using UnityGameTranslator.Common;
using UnityGameTranslator.Manager.Core.Detection;
using UnityGameTranslator.Manager.Core.Model;

namespace UnityGameTranslator.Manager.Core.Api;

/// <summary>What the marker beside a translation says about the session open on it.</summary>
/// <param name="ModKey">
/// Null when it cannot be decrypted here — which is information, not a failure: the session belongs
/// to another account of this computer, and is neither ours to end nor ours to ignore.
/// </param>
public sealed record EditSessionMarker(string? ModKey,
                                       EditSessions.EditSessionHolder Holder,
                                       DateTimeOffset? OpenedUtc)
{
    public bool IsOurs => Holder == EditSessions.EditSessionHolder.Manager;

    /// <summary>Endable from here: we know which session it is and we can prove it to the site.</summary>
    public bool Endable => !string.IsNullOrEmpty(ModKey);
}

/// <summary>
/// The one file the mod and this tool both read, so that two browser editors are never open on one
/// translation.
///
/// 🔴 **Why it has to exist at all.** A session holds the whole translation as it stood when it
/// opened and saves it back entire. Two of them on one file means the second to save erases
/// everything the first did, with nothing said to anybody. The site cannot notice: sessions are
/// created anonymously, so it cannot tell that two of them are the same game on the same machine.
/// The only place that knows is the machine, and the only thing both programs can see is the game
/// folder.
///
/// ⚠ **This reverses "the session key never touches the game folder"**, which used to be written in
/// <see cref="EditSessionRunner"/>. That rule feared handing the next account of this computer a
/// live handle on somebody else's translation — and the encryption is NOT what answers it. Read
/// the header of <see cref="Secrets"/>: the key comes from the machine name, the user name and the
/// home path, all of them values another local account can look up, so a deliberate one rebuilds
/// it. What actually answers the fear is that the key reaches no further than the translation lying
/// beside it in plain JSON: anyone able to read the marker can already edit that file directly.
/// The rule about what may be written here is in the socle, above <c>MarkerSuffix</c>.
///
/// ⚠ **So an unreadable key is not a wall, it is a fact**: this session was opened under another
/// account, or before this game moved here. We cannot prove it is over, and it is not ours to end
/// — the same reason we refuse to write into a game somebody else set up.
///
/// ⚠ **Holder and time stay in the clear, deliberately.** They are not credentials, and they are
/// what makes the refusal answerable: "a session was opened from the manager on 14 Aug at 14:32"
/// tells somebody what to do; an opaque blob tells them they are stuck.
/// </summary>
public static class EditSessionMarkers
{
    /// <summary>
    /// Where the marker for one game's translation lives — beside the translation, named after it.
    ///
    /// ⚠ Named after the FILE, not after the game: it is what makes editing two games at once
    /// legitimate (the site allows twelve at a time, deliberately) while forbidding two editors on
    /// one file. And the uninstall sweep files anything starting with the translation's name under
    /// "Translation", so this is removed with the data it belongs to without a list to maintain.
    /// </summary>
    public static string PathFor(string gamePath, LoaderDescriptor descriptor) =>
        Path.Combine(gamePath,
                     descriptor.UserDataDir.Replace('/', Path.DirectorySeparatorChar),
                     LocalTranslationProbe.TranslationFileName + EditSessions.MarkerSuffix);

    /// <summary>The marker for this game, or null when there is none.</summary>
    public static EditSessionMarker? Read(string gamePath, LoaderDescriptor descriptor)
    {
        var path = PathFor(gamePath, descriptor);

        try
        {
            if (!File.Exists(path)) return null;

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;

            var holder =
                string.Equals(Text(root, EditSessions.MarkerHolderField),
                              EditSessions.EditSessionHolder.Manager.ToString(),
                              StringComparison.OrdinalIgnoreCase)
                    ? EditSessions.EditSessionHolder.Manager
                    // Absent on a marker the mod wrote before the field existed, and the mod was
                    // the only writer then — so that is the honest reading, not a guess.
                    : EditSessions.EditSessionHolder.Game;

            DateTimeOffset? opened = null;
            if (DateTimeOffset.TryParse(Text(root, EditSessions.MarkerOpenedField), out var parsed))
                opened = parsed;

            // ⚠ Checked before it is believed, let alone put in a URL. This file is writable by
            // anybody with an account on this computer, so what comes out of it is data.
            var key = Secrets.Unprotect(Text(root, EditSessions.MarkerKeyField));
            if (key is not null && !EditSessions.IsPlausibleKey(key)) key = null;

            return new EditSessionMarker(key, holder, opened);
        }
        catch
        {
            // A marker nobody can parse says nothing about anybody's session, and keeping it would
            // block every future one over a file that is simply damaged.
            Clear(gamePath, descriptor);
            return null;
        }
    }

    /// <summary>
    /// Record that this tool is holding a session on this game's translation.
    ///
    /// ⚠ Failure is not fatal and is not hidden either: what is lost is the ability to warn the
    /// other program, not the session. Returns the failure so the caller can say so.
    /// </summary>
    public static string? Write(string gamePath, LoaderDescriptor descriptor, string modKey)
    {
        var path = PathFor(gamePath, descriptor);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            using var stream = File.Create(path);
            using var writer = new Utf8JsonWriter(stream);

            writer.WriteStartObject();
            writer.WriteString(EditSessions.MarkerKeyField, Secrets.Protect(modKey));
            writer.WriteString(EditSessions.MarkerHolderField,
                               EditSessions.EditSessionHolder.Manager.ToString());
            writer.WriteString(EditSessions.MarkerOpenedField,
                               DateTimeOffset.UtcNow.ToString("o"));
            writer.WriteEndObject();

            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Forget the marker. Called only where the session is really over.</summary>
    public static void Clear(string gamePath, LoaderDescriptor descriptor)
    {
        try
        {
            var path = PathFor(gamePath, descriptor);
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // A marker we could not delete is stale, not dangerous: the next open asks the site
            // about it, is told the session is gone, and removes it then.
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
