using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace StarDexs.Sync.Core;

public sealed record LinkStart(string Code, string Poll, Uri LinkPage);
/// <summary>A link the player approved: this PC's token for their account, the account's public id and name.</summary>
public sealed record LinkReady(string Token, string Account, string Name);
/// <param name="Account">Which account the token belongs to (its public id).</param>
/// <param name="HelperLatest">The newest StarDexs Sync version on stardexs.com, so the app can mention an update.</param>
public sealed record SyncStatus(string? Patch, string Settings, bool HasSettings, string? Account = null, string? HelperLatest = null);
public sealed record SyncFile(byte[] Data, string Patch, string Settings);

/// <summary>The account says this PC isn't linked any more (unlinked on the website, or the token is gone).</summary>
public sealed class NotLinkedException : Exception;

/// <summary>
/// The only network traffic StarDexs Sync makes, all to stardexs.com and only when the player asks:
/// linking, asking whether the player's file is current, downloading it (or the previous one), and sending
/// blueprint names, completed contract titles and the installed file's hash. Nothing else about the PC,
/// the game or the player is sent.
/// </summary>
public sealed class SyncApi(HttpClient http)
{
    public static readonly Uri DefaultSite = new("https://stardexs.com/");

    public static HttpClient CreateClient(Uri site, string version)
    {
        var client = new HttpClient { BaseAddress = site, Timeout = TimeSpan.FromSeconds(90) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("StarDexs-Sync", version));
        return client;
    }

    public async Task<LinkStart> StartLinkAsync(string deviceName, CancellationToken cancel)
    {
        var body = await PostAsync("api/sync/link/start", new JsonObject { ["device"] = deviceName }, null, cancel);
        return new LinkStart(Text(body, "code"), Text(body, "poll"), new Uri(http.BaseAddress!, Text(body, "link")));
    }

    /// <summary>The device token once the player approves the code; null while waiting. Throws once the code has run out.</summary>
    public async Task<LinkReady?> PollLinkAsync(string poll, CancellationToken cancel)
    {
        var body = await PostAsync("api/sync/link/poll", new JsonObject { ["poll"] = poll }, null, cancel);
        return Text(body, "status") switch
        {
            "ready" => new LinkReady(Text(body, "token"), Text(body, "account"), Text(body, "name")),
            "pending" => null,
            _ => throw new TimeoutException("The link code ran out."),
        };
    }

    public async Task<SyncStatus> StatusAsync(string token, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/sync/status");
        var body = await SendJsonAsync(request, token, cancel);
        return new SyncStatus(body?["patch"]?.GetValue<string>(), Text(body, "settings"), body?["has_settings"]?.GetValue<bool>() == true,
            body?["account"]?.GetValue<string>(), body?["helper_latest"]?.GetValue<string>());
    }

    /// <summary>The file installed before the current one, kept on stardexs.com; null when there isn't one.</summary>
    public async Task<SyncFile?> PreviousFileAsync(string token, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/sync/file/previous");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, cancel);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        Check(response);
        var patch = response.Headers.TryGetValues("X-StarDexs-Patch", out var patches) ? patches.First() : "";
        return new SyncFile(await response.Content.ReadAsByteArrayAsync(cancel), patch, "");
    }

    public async Task<SyncFile> FileAsync(string token, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/sync/file");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, cancel);
        Check(response);
        var patch = response.Headers.TryGetValues("X-StarDexs-Patch", out var patches) ? patches.First() : "";
        var settings = response.Headers.TryGetValues("X-StarDexs-Settings", out var values) ? values.First() : "";
        return new SyncFile(await response.Content.ReadAsByteArrayAsync(cancel), patch, settings);
    }

    public async Task SendBlueprintsAsync(string token, IEnumerable<Blueprint> blueprints, CancellationToken cancel)
    {
        var owned = new JsonArray(blueprints.Select(b => (JsonNode)new JsonObject { ["name"] = b.Name, ["first_seen"] = b.FirstSeen.ToString("yyyy-MM-ddTHH:mm:ssZ") }).ToArray());
        await PostAsync("api/sync/blueprints", new JsonObject { ["owned"] = owned }, token, cancel);
    }

    public async Task SendCompletedAsync(string token, IReadOnlyDictionary<string, string> completed, CancellationToken cancel)
    {
        var map = new JsonObject(completed.Select(c => KeyValuePair.Create(c.Key, (JsonNode?)c.Value)));
        await PostAsync("api/sync/completed", new JsonObject { ["completed"] = map }, token, cancel);
    }

    /// <summary>Tells stardexs.com which file is now installed, so it keeps that one as a backup.</summary>
    public async Task InstalledAsync(string token, string patch, string sha256, CancellationToken cancel) =>
        await PostAsync("api/sync/installed", new JsonObject { ["patch"] = patch, ["sha256"] = sha256 }, token, cancel);

    private async Task<JsonNode?> PostAsync(string path, JsonObject body, string? token, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        return await SendJsonAsync(request, token, cancel);
    }

    private async Task<JsonNode?> SendJsonAsync(HttpRequestMessage request, string? token, CancellationToken cancel)
    {
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, cancel);
        Check(response);
        var text = await response.Content.ReadAsStringAsync(cancel);
        return text.Length == 0 ? null : JsonNode.Parse(text);
    }

    private static void Check(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new NotLinkedException();
        response.EnsureSuccessStatusCode();
    }

    private static string Text(JsonNode? body, string name) =>
        body?[name]?.GetValue<string>() ?? throw new HttpRequestException($"stardexs.com sent no {name}.");
}
