using System.Text.Json;
using UnityGameTranslator.Common;
using UnityGameTranslator.Manager.Core.Ai;

namespace UnityGameTranslator.Manager.Core.Api;

/// <summary>
/// Answers the browser editor's per-line Retranslate for a game that is closed — the part the mod
/// plays while a game runs, played here with that game's own settings.
///
/// ⚠ **Everything that decides is the socle's, and the mod goes through the same doors.** The
/// guards on a request the page sends (<see cref="PageRetranslations"/>), the rounds that ask again
/// until the answer differs (<see cref="Retranslation.Run"/>), the attempt loop and its judging
/// (<see cref="LineTranslation.AskModel"/>, <see cref="LineTranslation.CheckServiceAnswer"/>). What
/// is here is only what the Manager knows and the mod does not need to: the game's config read from
/// its folder, the file as it sits on disk, and this tool's own HTTP.
///
/// 🔴 **Never outside the file.** A request names a line; it is answered only when that line is in
/// the translation on disk — the key comes from whoever holds the page, and translating arbitrary
/// text on this machine's backend (possibly a paid key) would turn it into a free proxy and a way in
/// for prompt injection.
///
/// ⚠ **A proposal, never a write.** The answer goes back to the page, which stages it for its own
/// Save; nothing here touches the game's file.
///
/// ⚠ Answered off the polling loop: a local model can take minutes, and the session must keep being
/// followed — saves applied, presence claimed — while it thinks.
/// </summary>
public sealed class EditSessionRetranslator
{
    private readonly GameAiSettings _ai;
    private readonly string? _gameName;
    private readonly string? _sourceLanguage;
    private readonly string? _targetLanguage;

    // A client of its own: answers leave from worker tasks while the runner polls, and the client
    // keeps per-call state (LastError, SessionGone) the runner reads.
    private readonly EditSessionClient _client = new();
    private readonly AiServerProbe _models = new();
    private readonly TranslationServices _services = new(GameAiSettings.RequestCeiling);
    private readonly PageRetranslations _requests = new();
    private readonly List<Task> _answering = new();
    private readonly Random _draws = new();

    /// <param name="ai">The game's own settings (<see cref="Install.GameConfigWriter.ReadAi"/>).</param>
    /// <param name="gameName">
    /// The name Unity recorded for the game (app.info), or null — ⚠ never a folder name, which
    /// teaches a model nothing: the mod sends Application.productName and nothing else.
    /// </param>
    /// <param name="sourceLanguage">The game's source language as a name, or null when unknown.</param>
    /// <param name="targetLanguage">The language the translation is in, as a name — "auto" already resolved.</param>
    public EditSessionRetranslator(GameAiSettings ai, string? gameName, string? sourceLanguage, string? targetLanguage)
    {
        _ai = ai;
        _gameName = gameName;
        _sourceLanguage = sourceLanguage;
        _targetLanguage = targetLanguage;
    }

    /// <summary>
    /// Whether a request can be answered at all — what the page is told when the session opens, so
    /// the button is not offered for a question nobody here can answer.
    ///
    /// ⚠ Stricter than the mod on purpose: a service with no key in this game's settings, or a
    /// translation with no language to go into, would only ever answer "failed" after the person
    /// waited for it. A button that can only fail is not shown.
    /// </summary>
    public bool CanAnswer =>
        _ai.IsTranslationEnabled
        && !string.IsNullOrEmpty(_targetLanguage)
        && _ai.Backend switch
        {
            "llm" => !string.IsNullOrEmpty(_ai.AiUrl),
            "google" => !string.IsNullOrEmpty(_ai.GoogleApiKey),
            "deepl" => !string.IsNullOrEmpty(_ai.DeeplApiKey),
            _ => false,
        };

    /// <summary>The backend's name for the button's tooltip — never an address, never a key.</summary>
    public string? Label => CanAnswer ? _ai.Label : null;

    /// <summary>
    /// Take the page's waiting requests, as the latest poll carried them, against the file on disk.
    /// Each admitted one is answered in the background; the rest are dropped with a reason the
    /// page does not need — it frees a waiting row on its own after three minutes.
    /// </summary>
    /// <param name="fileJson">The translation file as it stands on disk, text.</param>
    public void Take(string modKey, IReadOnlyList<RetranslateRequest> requests, string fileJson, CancellationToken ct)
    {
        if (requests.Count == 0) return;

        Dictionary<string, string?>? lines = null;
        Dictionary<string, string?> Lines() => lines ??= ReadLines(fileJson);

        lock (_answering) _answering.RemoveAll(task => task.IsCompleted);

        foreach (var request in requests)
        {
            var verdict = _requests.Admit(request.Id, request.Key, CanAnswer, key => Lines().ContainsKey(key));
            if (verdict != PageRequestVerdict.Answer) continue;

            Lines().TryGetValue(request.Key, out var previous);
            var task = Task.Run(() => AnswerAsync(modKey, request.Key, previous, ct), ct);
            lock (_answering) _answering.Add(task);
        }
    }

    /// <summary>Every answer still on its way — waited for when the session ends, so none is cut off mid-post.</summary>
    public Task DrainAsync()
    {
        lock (_answering) return Task.WhenAll(_answering.ToArray());
    }

    private async Task AnswerAsync(string modKey, string key, string? previous, CancellationToken ct)
    {
        RetranslateResult result;
        try
        {
            result = Retranslate(key, previous, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The session is ending: nobody is left to hand an answer to.
            _requests.Forget(key);
            return;
        }
        catch (Exception)
        {
            // The boundary of a background task: whatever broke, the page is told it failed —
            // that is where the person is looking — and the line is released. Left unanswered, it
            // would stay "already pending" for the rest of the session and never be asked again.
            result = new RetranslateResult(RetranslateOutcome.Failed, previous);
        }

        var id = _requests.Settle(key);
        if (string.IsNullOrEmpty(id)) return;

        // A failed post is not retried: the page frees its row on its own, and asking again is one
        // click away. Nothing in the file depends on it.
        await _client.SendRetranslationAsync(modKey, id, key, result.Value, result.Outcome, ct)
            .ConfigureAwait(false);
    }

    private RetranslateResult Retranslate(string key, string? previous, CancellationToken ct)
    {
        bool service = LineTranslation.IsTranslationService(_ai.Backend);
        int rounds = Retranslation.Rounds(service, _ai.AttemptsAllowed);

        // hadEntry: the line is in the file — Take only lets a line of the file through.
        return Retranslation.Run(key, true, previous, rounds, round => service ? AskService(key, ct) : AskModel(key, round, ct));
    }

    private string? AskModel(string key, int round, CancellationToken ct)
    {
        // Offset by the round when the game fixed a seed, drawn fresh otherwise — Retranslation.SeedFor.
        int seed = Retranslation.SeedFor(_ai.SeedRetranslate, round, () => { lock (_draws) return _draws.Next(1, int.MaxValue); });

        var job = new ModelJob
        {
            Instructions = (markers, textType) => Prompts.ForGameText(_targetLanguage!, _sourceLanguage, _gameName,
                                                                      _ai.GameContext, _ai.StrictSourceLanguage,
                                                                      textType, markers),
            // A retranslation's warmth for every attempt: the point is to leave the basin the
            // rejected answer came from, repairs included — as the mod does.
            Temperature = _ai.TemperatureRetranslate,
            Seed = seed,
            RepairTemperature = _ai.TemperatureRepair,
            RepairSeed = seed,
            Attempts = _ai.AttemptsAllowed,
        };

        var answer = LineTranslation.AskModel(key, job, _models.Chat(_ai.AiUrl, _ai.AiModel, _ai.AiApiKey, ct, GameAiSettings.RequestCeiling));
        return answer.Outcome is LineOutcome.Translated or LineOutcome.Declined ? answer.Text : null;
    }

    private string? AskService(string key, CancellationToken ct)
    {
        if (key.Length > Limits.AiTextLength) return null;

        var prepared = Backends.Prepare(key);
        if (prepared.NothingToSend) return null;

        var sent = _services.Translate(_ai, prepared.ToSend, _sourceLanguage, _targetLanguage!, ct);
        var answer = LineTranslation.CheckServiceAnswer(prepared, sent);
        return answer.Outcome == LineOutcome.Translated ? answer.Text : null;
    }

    /// <summary>
    /// The file's lines and their current values. Metadata (keys starting with '_') is not a line.
    /// A value in either shape the file carries — <c>{"v": …}</c> or a bare string — or null.
    /// </summary>
    private static Dictionary<string, string?> ReadLines(string fileJson)
    {
        var lines = new Dictionary<string, string?>(StringComparer.Ordinal);

        try
        {
            using var document = JsonDocument.Parse(fileJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return lines;

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name.StartsWith('_')) continue;

                lines[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Object when property.Value.TryGetProperty("v", out var v)
                                              && v.ValueKind == JsonValueKind.String => v.GetString(),
                    _ => null,
                };
            }
        }
        catch (JsonException)
        {
            // A file that cannot be read has no lines: every request is refused as not in the file,
            // which is the one safe answer. The session itself says the file is broken elsewhere.
        }

        return lines;
    }
}
