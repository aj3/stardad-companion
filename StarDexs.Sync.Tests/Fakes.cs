using System.Net;
using System.Text;
using StarDexs.Sync.Core;

namespace StarDexs.Sync.Tests;

/// <summary>A throwaway folder that looks like a LIVE install (it has a Data.p4k), deleted afterwards.</summary>
public sealed class FakeInstall : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("stardexs-sync-").FullName;
    public string Live => Path.Combine(Root, "LIVE");
    public string Data => Path.Combine(Root, "appdata");

    public FakeInstall()
    {
        Directory.CreateDirectory(Live);
        File.WriteAllBytes(Path.Combine(Live, "Data.p4k"), [0]);
    }

    public void Log(string name, string text)
    {
        var path = Path.Combine(Live, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}

/// <summary>Not encryption: just enough to check the token never sits in the state file as typed.</summary>
public sealed class FakeProtector : ISecretProtector
{
    public string Protect(string secret) => "p:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(secret));
    public string? Unprotect(string protectedSecret) =>
        protectedSecret.StartsWith("p:", StringComparison.Ordinal) ? Encoding.UTF8.GetString(Convert.FromBase64String(protectedSecret[2..])) : null;
}

/// <summary>A stand-in for stardexs.com that records every request.</summary>
public sealed class FakeSite : HttpMessageHandler
{
    public List<(string Path, string? Auth, string Body)> Requests { get; } = [];
    public string? Patch { get; set; } = "4.10.2-live.1";
    public string Settings { get; set; } = "s1";
    public byte[] File { get; set; } = Encoding.UTF8.GetBytes("key=Value\n");
    public bool Unlinked { get; set; }
    public byte[]? Previous { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
    {
        var path = request.RequestUri!.AbsolutePath;
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancel);
        Requests.Add((path, request.Headers.Authorization?.Parameter, body));
        if (Unlinked && path.StartsWith("/api/sync/", StringComparison.Ordinal) && !path.Contains("/link/")) return new(HttpStatusCode.Unauthorized);
        return path switch
        {
            "/api/sync/status" => Json($$"""{"patch":{{(Patch is null ? "null" : $"\"{Patch}\"")}},"settings":"{{Settings}}","has_settings":true,"account":"aaaaaaaaaaaaaaaa","helper_latest":"0.2.0"}"""),
            "/api/sync/file/previous" => Previous is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : FileResponse(Previous, "4.10.1-live.1"),
            "/api/sync/file" => FileResponse(File, Patch!),
            "/api/sync/link/start" => Json("""{"code":"ABCD-1234","poll":"p1","link":"/link?code=ABCD-1234"}"""),
            "/api/sync/link/poll" => Json("""{"status":"ready","token":"t-123","account":"aaaaaaaaaaaaaaaa","name":"Pilot One"}"""),
            _ => Json("{}"),
        };
    }

    private HttpResponseMessage FileResponse(byte[] content, string patch)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
        response.Headers.Add("X-StarDexs-Patch", patch);
        response.Headers.Add("X-StarDexs-Settings", Settings);
        return response;
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
