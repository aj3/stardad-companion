using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace StarDexs.Sync.Core;

public sealed record LinkStart(string Code, string Poll, Uri LinkPage);
/// <summary>A link the player approved: this PC's token for their account, the account's public id and name.</summary>
public sealed record LinkReady(string Token, string Account, string Name);
/// <param name="Account">Which account the token belongs to (its public id).</param>
/// <param name="HelperLatest">The newest StarDad Companion version on the StarDad website, so the app can mention an update.</param>
public sealed record SyncStatus(string? Patch, string Settings, bool HasSettings, string? Account = null, string? HelperLatest = null, SyncSchedule? Schedule = null, bool KeybindBackup = false);
public sealed record KeybindCopy(string Id,string Name,string Source,string Created,string Sha256);
public sealed record KeybindDownload(string Name,byte[] Data,string Sha256);
public sealed record SyncFile(byte[] Data, string Patch, string Settings);

/// <summary>The account says this PC isn't linked any more (unlinked on the website, or the token is gone).</summary>
public sealed class NotLinkedException : Exception;

/// <summary>
/// The only network traffic StarDad Companion makes, all to stardad.space, only when the player asks or at times
/// they chose:
/// linking, asking whether the player's file is current (and which automatic syncs they chose), downloading it
/// (or the previous one), and sending blueprint names, completed contract titles and the installed file's hash.
/// Opt-in keybind actions also transfer exported XML profiles and their filename/build/hash.
/// Original game log text and unrelated PC files are never sent.
/// </summary>
public sealed class SyncApi(HttpClient http)
{
    public static readonly Uri DefaultSite = new("https://stardad.space/");

    public static HttpClient CreateClient(Uri site, string version)
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = site, Timeout = TimeSpan.FromSeconds(90) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("StarDexs-Companion", version));
        return client;
    }

    public async Task<LinkStart> StartLinkAsync(string deviceName, CancellationToken cancel)
    {
        var body = await PostAsync("api/sync/link/start", new JsonObject { ["device"] = deviceName }, null, cancel);
        var page = new Uri(http.BaseAddress!, Text(body, "link"));
        if (page.GetLeftPart(UriPartial.Authority) != http.BaseAddress!.GetLeftPart(UriPartial.Authority))
            throw new HttpRequestException("The link page was outside the StarDad website.");
        return new LinkStart(Text(body, "code"), Text(body, "poll"), page);
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
        return new SyncStatus(OptionalText(body, "patch"), Text(body, "settings"), body?["has_settings"] is JsonValue flag && flag.TryGetValue<bool>(out var has) && has,
            OptionalText(body, "account"), OptionalText(body, "helper_latest"), Schedule(body?["schedule"]), body?["keybind_backup"] is JsonValue backup && backup.TryGetValue<bool>(out var enabled) && enabled);
    }

    /// <summary>The automatic syncs the player chose for this PC; null when off or not understood.</summary>
    private static SyncSchedule? Schedule(JsonNode? node)
    {
        if (node is not JsonObject schedule) return null;
        try
        {
            var found = SyncSchedule.From(schedule["every_hours"]?.GetValue<int>() ?? 0, schedule["at_minute"]?.GetValue<int?>(),
                schedule["on_game_update"]?.GetValue<bool>() == true);
            return found is { IsOn: true } ? found : null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException) { return null; }
    }

    /// <summary>The file installed before the current one, kept on the StarDad website; null when there isn't one.</summary>
    public async Task<SyncFile?> PreviousFileAsync(string token, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/sync/file/previous");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        Check(response);
        var patch = response.Headers.TryGetValues("X-StarDexs-Patch", out var patches) ? patches.First() : "";
        return new SyncFile(await HttpBody.ReadAsync(response, 32 * 1024 * 1024, cancel), patch, "");
    }

    public async Task<SyncFile> FileAsync(string token, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/sync/file");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
        Check(response);
        var patch = response.Headers.TryGetValues("X-StarDexs-Patch", out var patches) ? patches.First() : "";
        var settings = response.Headers.TryGetValues("X-StarDexs-Settings", out var values) ? values.First() : "";
        return new SyncFile(await HttpBody.ReadAsync(response, 32 * 1024 * 1024, cancel), patch, settings);
    }

    /// <summary>Sends blueprints; returns how many the account didn't have yet.</summary>
    public async Task<int> SendBlueprintsAsync(string token, IEnumerable<Blueprint> blueprints, CancellationToken cancel)
    {
        var owned = new JsonArray(blueprints.Select(b => (JsonNode)new JsonObject { ["name"] = b.Name, ["first_seen"] = b.FirstSeen.ToString("yyyy-MM-ddTHH:mm:ssZ") }).ToArray());
        return Added(await PostAsync("api/sync/blueprints", new JsonObject { ["owned"] = owned }, token, cancel));
    }

    /// <summary>Sends finished contracts; returns how many the account didn't have yet.</summary>
    public async Task<int> SendCompletedAsync(string token, IReadOnlyDictionary<string, string> completed, CancellationToken cancel)
    {
        var map = new JsonObject(completed.Select(c => KeyValuePair.Create(c.Key, (JsonNode?)c.Value)));
        return Added(await PostAsync("api/sync/completed", new JsonObject { ["completed"] = map }, token, cancel));
    }

    private static int Added(JsonNode? body)
    {
        try { return body?["added"]?.GetValue<int>() ?? 0; }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException) { return 0; }
    }

    /// <summary>Where a sync is up to (and, at the end, how it went), for the player's account page.</summary>
    public async Task ProgressAsync(string token, string stage, string? message, bool ok, CancellationToken cancel, string? runId = null) =>
        await PostAsync("api/sync/progress", new JsonObject { ["stage"] = stage, ["message"] = message, ["ok"] = ok, ["runId"] = runId }, token, cancel);

    public async Task<string?> ClaimWorkflowAsync(string token, bool prepare, CancellationToken cancel)
    {
        var body = await PostAsync("api/sync/workflow/claim", new JsonObject { ["mode"] = prepare ? "prepare" : "apply" }, token, cancel);
        var id = OptionalText(body, "id");
        return Guid.TryParseExact(id, "N", out _) ? id : null;
    }

    public async Task WorkflowAsync(string token, string id, string? stage, CancellationToken cancel, string? message = null,
        bool? logs = null, bool? folder = null, bool? allowed = null, string? settings = null) =>
        await PostAsync("api/sync/workflow/report", new JsonObject { ["id"] = id, ["stage"] = stage, ["message"] = message,
            ["logs"] = logs, ["folder"] = folder, ["allowed"] = allowed, ["settings"] = settings }, token, cancel);

    /// <summary>Unlinks this PC on the StarDad website: its token stops working.</summary>
    public async Task UnlinkAsync(string token, CancellationToken cancel) =>
        await PostAsync("api/sync/unlink", new JsonObject(), token, cancel);

    /// <summary>Tells stardad.space which file is now installed, so it keeps that one as a backup.</summary>
    public async Task InstalledAsync(string token, string patch, string sha256, CancellationToken cancel) =>
        await PostAsync("api/sync/installed", new JsonObject { ["patch"] = patch, ["sha256"] = sha256 }, token, cancel);

    public async Task<IReadOnlyList<KeybindCopy>> KeybindsAsync(string token,CancellationToken cancel)
    {
        using var request=new HttpRequestMessage(HttpMethod.Get,"api/sync/keybinds");var body=await SendJsonAsync(request,token,cancel);
        if(body?["backups"] is not JsonArray rows)throw new HttpRequestException("StarDad sent no keybind list.");
        if(rows.Count>30)throw new HttpRequestException("The keybind list exceeded its limit.");
        return rows.Select(row=>new KeybindCopy(Text(row,"id"),Text(row,"name"),Text(row,"sourceName"),Text(row,"created"),Text(row,"sha256"))).ToArray();
    }
    public async Task<bool> UploadKeybindAsync(string token,string name,byte[] bytes,string? patch,CancellationToken cancel)
    {
        KeybindFiles.Validate(name,bytes);var result=await PostAsync("api/sync/keybinds",new JsonObject{["name"]=name,["data"]=Convert.ToBase64String(bytes),["patch"]=patch},token,cancel);
        return result?["added"] is JsonValue added&&added.TryGetValue<bool>(out var value)&&value;
    }
    public async Task SetKeybindBackupAsync(string token,bool enabled,CancellationToken cancel)=>
        await PostAsync("api/sync/keybinds/preference",new JsonObject{["enabled"]=enabled},token,cancel);
    public async Task<KeybindDownload> DownloadKeybindAsync(string token,string id,CancellationToken cancel)
    {
        if(!Guid.TryParseExact(id,"N",out _))throw new HttpRequestException("The backup id is invalid.");
        using var request=new HttpRequestMessage(HttpMethod.Get,"api/sync/keybinds/"+id);request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);
        using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,cancel);Check(response);
        var name=response.Headers.TryGetValues("X-StarDad-Keybind-Name",out var names)?names.Single():"";
        var hash=response.Headers.TryGetValues("X-StarDad-Keybind-Sha",out var hashes)?hashes.Single():"";
        var data=await HttpBody.ReadAsync(response,KeybindFiles.MaxBytes,cancel);KeybindFiles.Validate(name,data);
        if(KeybindFiles.Hash(data)!=hash)throw new HttpRequestException("The profile failed its integrity check.");return new(name,data,hash);
    }

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
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
        Check(response);
        var bytes = await HttpBody.ReadAsync(response, 64 * 1024, cancel);
        try {
            var body = bytes.Length == 0 ? null : JsonNode.Parse(bytes);
            if (body is not null and not JsonObject) throw new HttpRequestException("The StarDad response was not an object.");
            return body;
        }
        catch (System.Text.Json.JsonException exception) { throw new HttpRequestException("The StarDad response was not valid JSON.", exception); }
    }

    private static void Check(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new NotLinkedException();
        response.EnsureSuccessStatusCode();
    }

    private static string Text(JsonNode? body, string name) =>
        OptionalText(body, name) ?? throw new HttpRequestException($"The StarDad website sent no {name}.");

    private static string? OptionalText(JsonNode? body, string name) =>
        body?[name] is null ? null : body[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text
            : throw new HttpRequestException($"The StarDad website sent invalid {name}.");
}
