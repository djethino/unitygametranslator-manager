using System.Net;
using System.Text.Json;
using UnityGameTranslator.Common;
using UnityGameTranslator.Manager.Core.Api;

namespace UnityGameTranslator.Manager.Core.Checks;

/// <summary>
/// What a first publication tells the site about its game — the hit picked, and what this machine
/// read (spec/api-v1, UploadBody `game_pick` / `game_read`).
///
/// 🔴 **Checked on the request actually sent**, not on the code that builds it: a field written
/// under another name, or nested one level off, compiles and is silently ignored by the site — which
/// then searches the picked title again, the very fault this exists to end
/// (analyse/identite-des-jeux-parcours.md, T1).
/// </summary>
internal static class PublishGameChecks
{
    internal static void WhatAPublicationSaysAboutItsGame()
    {
        Program.Section("What a publication says about its game");

        var pick = GameCandidates.PickOf("igdb", 376372, "3863760");
        var read = new GameRead("Legacy of Shadows", "Studio", "3863760", "steam_appid.txt");

        var sent = Sent(publisher => publisher.PublishAsync("{}", "token", "3863760", "Legacy of Shadows",
                                                            "Simplified Chinese", "English",
                                                            pick: pick, read: read));
        using (var body = JsonDocument.Parse(sent.Body))
        {
            var root = body.RootElement;

            Program.Check(root.TryGetProperty("game_pick", out var p)
                          && p.GetProperty("source").GetString() == "igdb"
                          && p.GetProperty("id").GetString() == "376372",
                "the hit picked travels as it was given",
                "the site files the translation under THAT game, never under a new search of its title");

            Program.Check(root.TryGetProperty("game_read", out var r)
                          && r.GetProperty("product_name").GetString() == "Legacy of Shadows"
                          && r.GetProperty("steam_id").GetString() == "3863760"
                          && r.GetProperty("steam_id_from").GetString() == "steam_appid.txt"
                          && r.GetProperty("engine").GetString() == "Unity",
                "what this machine read travels beside it",
                "the key other machines resolve the game by comes from here, not from the title picked");
        }

        var bare = Sent(publisher => publisher.PublishAsync("{}", "token", "3863760", "Some Game", "English", "French"));
        using (var body = JsonDocument.Parse(bare.Body))
        {
            Program.Check(!body.RootElement.TryGetProperty("game_pick", out _)
                          && !body.RootElement.TryGetProperty("game_read", out _),
                "nothing picked and nothing read sends neither block",
                "absent means not said — never an empty pick the site would refuse");
        }

        var asked = Asked(api => api.GameAdultAsync(null, "Another Title", GameCandidates.PickOf("local", 37, null), "token"));
        Program.Check(asked.Url.Contains("game_pick%5Bsource%5D=local") && asked.Url.Contains("game_pick%5Bid%5D=37"),
            "the adult question asks about the same pick",
            "the answer and the upload resolve the same game");
    }

    private sealed record Request(string Url, string Body);

    private static Request Sent(Func<TranslationPublisher, Task> act)
    {
        var handler = new Recorder("{\"success\":true,\"translation\":{\"id\":1,\"file_hash\":\"" + new string('a', 64) + "\"}}");
        act(new TranslationPublisher(new HttpClient(handler))).GetAwaiter().GetResult();
        return handler.Last!;
    }

    private static Request Asked(Func<CatalogApiClient, Task> act)
    {
        var handler = new Recorder("{\"known\":true,\"adult\":false,\"source\":null,\"declarable\":false}");
        act(new CatalogApiClient(new HttpClient(handler))).GetAwaiter().GetResult();
        return handler.Last!;
    }

    /// <summary>Answers every request with one body and keeps the last one sent.</summary>
    private sealed class Recorder(string answer) : HttpMessageHandler
    {
        public Request? Last { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Last = new Request(request.RequestUri!.ToString(), body);

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer) };
        }
    }
}
