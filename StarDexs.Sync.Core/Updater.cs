using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace StarDexs.Sync.Core;

/// <summary>A release stardad.space offers, after its signature checked out.</summary>
public sealed record Release(Version Version, string File, string Sha256);
public sealed record UpdateResult(Release? Release,bool Installed,string? Problem);

/// <summary>
/// Updating StarDad Companion, only when the player presses Update. stardad.space/downloads/companion.json
/// names the newest release; it's accepted only if it's signed with the maintainer's release key (whose
/// public half is built in below; the private half never leaves the maintainer's PC) and is newer than
/// this copy. The download must then match the signed SHA-256 exactly. So whoever controls the website
/// still can't hand players a program the maintainer didn't sign, or an older one.
/// </summary>
public sealed class Updater(HttpClient http, string publicKey = Updater.ReleaseKey)
{
    /// <summary>The release key's public half (P-256, SubjectPublicKeyInfo, base64).</summary>
    public const string ReleaseKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEXBFazEisW/LQvPtUqhTjkeDxaSs04SWzT1VHURNF/9LlbAJwjKcv//Y1rOwia+84yXwdyuvW94o/JwqvX8RWqA==";
    public const string ManifestPath = "downloads/companion.json";
    public const long MaxDownloadBytes = 100L * 1024 * 1024;

    /// <summary>What a release's signature covers: the version and the file's SHA-256.</summary>
    public static byte[] SignedText(string version, string sha256) => Encoding.UTF8.GetBytes($"StarDexs Companion {version}\n{sha256.ToLowerInvariant()}\n");

    /// <summary>The release named in a manifest, if it's validly signed and newer than <paramref name="current"/>.</summary>
    public Release? Check(string manifest, Version current)
    {
        if (manifest.Length > 64 * 1024) return null;
        try { return CheckBody(manifest, current); }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidOperationException or FormatException or CryptographicException) { return null; }
    }

    private Release? CheckBody(string manifest, Version current)
    {
        var body = JsonNode.Parse(manifest);
        if (body?["version"]?.GetValue<string>() is not { } text || !Version.TryParse(text, out var version)
            || body["file"]?.GetValue<string>() is not { } file || !IsPlainName(file)
            || body["sha256"]?.GetValue<string>() is not { Length: 64 } sha || !sha.All(Uri.IsHexDigit)
            || body["signature"]?.GetValue<string>() is not { } signature)
            return null;
        byte[] signed;
        try { signed = Convert.FromBase64String(signature); }
        catch (FormatException) { return null; }
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
        if (!key.VerifyData(SignedText(text, sha), signed, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)) return null;
        return version > current ? new Release(version, file, sha.ToLowerInvariant()) : null;
    }

    /// <summary>The newest verified release, or null only when a valid listing has no newer release.</summary>
    public async Task<Release?> NewestAsync(Version current, CancellationToken cancel)
    {
        using var request=new HttpRequestMessage(HttpMethod.Get,ManifestPath);
        request.Headers.CacheControl=new(){NoCache=true,NoStore=true};
        using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,cancel);
        response.EnsureSuccessStatusCode();
        var manifest=Encoding.UTF8.GetString(await HttpBody.ReadAsync(response,64*1024,cancel));
        var verified=Check(manifest,new Version(0,0));
        if(verified is null)throw new InvalidDataException("The update listing could not be verified. The installed app was kept.");
        return verified.Version>current ? verified : null;
    }

    /// <summary>Recheck at click time; retry once if a newer signed release supersedes a removed download.</summary>
    public async Task<UpdateResult> InstallNewestAsync(Version current,string exe,Action<Release>? selected,CancellationToken cancel)
    {
        Release? release=null;
        try
        {
            release=await NewestAsync(current,cancel);
            if(release is null)return new(null,false,null);
            selected?.Invoke(release);
            var attempt=await TryInstallAsync(release,exe,cancel);
            if(attempt.Missing)
            {
                var replacement=await NewestAsync(current,cancel);
                if(replacement is not null&&replacement.Version>release.Version)
                {
                    release=replacement;selected?.Invoke(release);
                    attempt=await TryInstallAsync(release,exe,cancel);
                }
            }
            return new(release,attempt.Problem is null,attempt.Problem);
        }
        catch(HttpRequestException){return new(release,false,"Couldn't check the latest update. The installed app was kept. Try again shortly.");}
        catch(InvalidDataException e){return new(release,false,e.Message);}
    }

    private sealed record InstallAttempt(string? Problem,bool Missing=false);
    public async Task<string?> InstallAsync(Release release,string exe,CancellationToken cancel)=>
        (await TryInstallAsync(release,exe,cancel)).Problem;

    /// <summary>
    /// Downloads a release next to <paramref name="exe"/> and swaps it in: the running file is renamed aside
    /// (Windows allows that), the new one takes its name. Returns the problem, or null when it's in place.
    /// </summary>
    private async Task<InstallAttempt> TryInstallAsync(Release release, string exe, CancellationToken cancel)
    {
        var folder = Path.GetDirectoryName(exe)!;
        var incoming = exe + ".new";
        var aside = exe + ".old";
        try
        {
            using (var response = await http.GetAsync("downloads/" + release.File, HttpCompletionOption.ResponseHeadersRead, cancel))
            {
                if (!response.IsSuccessStatusCode) return new("That update download is no longer available. Check for the latest release and try again.",response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone);
                if (response.Content.Headers.ContentLength > MaxDownloadBytes) return new("The update was larger than expected, so it wasn't used.");
                await using var source = await response.Content.ReadAsStreamAsync(cancel);
                await using var target = new FileStream(incoming, FileMode.Create, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancel)) > 0)
                {
                    total += read;
                    if (total > MaxDownloadBytes) return new("The update was larger than expected, so it wasn't used.");
                    await target.WriteAsync(buffer.AsMemory(0, read), cancel);
                }
            }
            string actual;
            await using (var check = File.OpenRead(incoming)) actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(check, cancel));
            if (actual != release.Sha256) return new("The download didn't match the signed release, so it wasn't used.");
            if (File.Exists(aside)) File.Delete(aside);
            File.Move(exe, aside);
            try { File.Move(incoming, exe); }
            catch { if (!File.Exists(exe) && File.Exists(aside)) File.Move(aside, exe); throw; }
            return new(null);
        }
        catch (UnauthorizedAccessException) { return new($"Windows won't let StarDad Companion update itself in {folder}. Download it from the StarDad website instead."); }
        catch (IOException exception) { return new($"Couldn't update: {exception.Message}"); }
        catch (HttpRequestException) { return new("Couldn't reach the StarDad website. Try again in a moment."); }
        finally
        {
            try { if (File.Exists(incoming)) File.Delete(incoming); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>The copy an update renamed aside, removed once the new one is running.</summary>
    public static void TidyUp(string exe)
    {
        try { if (File.Exists(exe + ".old")) File.Delete(exe + ".old"); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool IsPlainName(string file) =>
        file.Length is > 0 and <= 80 && file.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_') && file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
}
