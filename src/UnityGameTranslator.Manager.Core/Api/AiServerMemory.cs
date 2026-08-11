namespace UnityGameTranslator.Manager.Core.Api;

/// <summary>
/// What the last search for an AI server found, kept for as long as the tool runs.
///
/// Searching means probing six ports and listing models: a couple of seconds during which the
/// settings screen sits there saying "Looking for a local AI server...". Doing that again every
/// time someone opens the window is time spent re-answering a question that has not changed —
/// servers do not appear and disappear while a settings dialog is closed.
///
/// So the answer is remembered, and refreshed on the two things that actually change it:
/// **an explicit "Search again"**, and **anything we did ourselves** — starting Ollama, installing
/// it, pulling a model. Never on a timer: a background re-probe would be the tool asking itself a
/// question nobody asked, and it would fight with whatever the user is doing on screen.
///
/// ⚠ Deliberately not persisted to disk. A server that answered yesterday says nothing about
/// today, and a remembered "no server found" would be worse still — it would hide an Ollama the
/// person installed in between, and they would have no idea why the tool cannot see it.
/// </summary>
public sealed class AiServerMemory
{
    private IReadOnlyList<AiServer>? _servers;

    /// <summary>The last result, or null when nothing has been searched yet this session.</summary>
    public IReadOnlyList<AiServer>? Remembered => _servers;

    public void Remember(IReadOnlyList<AiServer> servers) => _servers = servers;

    /// <summary>
    /// Drops what we know, so the next look starts fresh.
    ///
    /// Called after we change the situation ourselves. Keeping a stale "nothing found" right after
    /// installing Ollama would make our own work look like it failed.
    /// </summary>
    public void Forget() => _servers = null;
}
