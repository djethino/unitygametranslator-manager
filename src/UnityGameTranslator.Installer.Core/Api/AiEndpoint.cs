namespace UnityGameTranslator.Installer.Core.Api;

/// <summary>
/// Turns the address a user typed into the endpoint to call.
///
/// ⚠ MIRROR OF TranslatorCore.ResolveAIEndpoint in the mod. This is not a convenience: if the
/// two resolve differently, the installer tests one URL and the mod calls another, and a server
/// that passes here fails in the game — or the reverse, which is worse, because nothing points
/// at the cause.
///
/// A naive baseUrl + "/v1/models" was what stood here first. It works for a plain
/// "http://localhost:11434" and breaks on everything else: someone pasting the full chat URL
/// their provider documents would have been tested at ".../v1/chat/completions/v1/models".
///
/// The five rules below come from that method, in order. Providers do not agree on prefixes —
/// some have /v1, some have none, some have /v1beta/openai — so the only thing asked of the user
/// is the chat URL their own documentation shows, and everything else is derived from it.
/// </summary>
public static class AiEndpoint
{
    private const string ChatSuffix = "/chat/completions";

    /// <summary>The endpoint that answers a chat completion for this address.</summary>
    public static string Chat(string baseUrl) => Resolve(baseUrl, "chat/completions");

    /// <summary>The endpoint that lists models — what a connection test asks for.</summary>
    public static string Models(string baseUrl) => Resolve(baseUrl, "models");

    /// <summary>
    /// Examples, taken from the mod's own documentation of this method:
    ///   "http://localhost:11434"                          → .../v1/chat/completions
    ///   "https://api.openai.com/v1/chat/completions"      → unchanged, and .../v1/models to test
    ///   "https://api.deepseek.com/chat/completions"       → unchanged, and .../models (no /v1)
    ///   ".../v1beta/openai/chat/completions"              → .../v1beta/openai/models to test
    ///   "https://api.groq.com/openai/v1"                  → .../openai/v1/chat/completions
    /// </summary>
    public static string Resolve(string baseUrl, string path)
    {
        var url = baseUrl.TrimEnd('/');
        var trimmedPath = path.TrimStart('/');

        // 1. Already ends with what we want.
        if (url.EndsWith("/" + trimmedPath, StringComparison.Ordinal)
            || url.EndsWith(trimmedPath, StringComparison.Ordinal))
        {
            return url;
        }

        // 2. The chat URL was pasted but we want another endpoint — swap the tail. This is what
        //    makes providers with no /v1 and providers with unusual prefixes work without
        //    anyone having to describe their scheme to us.
        if (url.EndsWith(ChatSuffix, StringComparison.Ordinal))
            return url[..^ChatSuffix.Length] + "/" + trimmedPath;

        // 3. A /v1/ appears somewhere: cut back to it.
        var v1Index = url.LastIndexOf("/v1/", StringComparison.Ordinal);
        if (v1Index >= 0)
            return url[..(v1Index + 3)] + "/" + trimmedPath;

        // 4. Ends with /v1: append.
        if (url.EndsWith("/v1", StringComparison.Ordinal))
            return url + "/" + trimmedPath;

        // 5. Otherwise assume the usual /v1 prefix — Ollama, OpenAI, Groq. A provider without it
        //    is expected to have its full chat URL configured, which rule 2 then handles.
        return url + "/v1/" + trimmedPath;
    }
}
