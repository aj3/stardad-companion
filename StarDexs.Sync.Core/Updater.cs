using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace StarDexs.Sync.Core;

/// <summary>A release stardexs.com offers, after its signature checked out.</summary>
public sealed record Release(Version Version, string File, string Sha256);

/// <summary>
/// Updating StarDexs Companion, only when the player presses Update. stardexs.com/downloads/companion.json
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
        JsonNode? body;
        try { body = JsonNode.Parse(manifest); }
        catch (System.Text.Json.JsonException) { return null; }
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

    /// <summary>The newest signed release, or null when there's none newer (or the manifest doesn't check out).</summary>
    public async Task<Release?> NewestAsync(Version current, CancellationToken cancel)
    {
        using var response = await http.GetAsync(ManifestPath, cancel);
        if (!response.IsSuccessStatusCode) return null;
        return Check(await response.Content.ReadAsStringAsync(cancel), current);
    }

    /// <summary>
    /// Downloads a release next to <paramref name="exe"/> and swaps it in: the running file is renamed aside
    /// (Windows allows that), the new one takes its name. Returns the problem, or null when it's in place.
    /// </summary>
    public async Task<string?> InstallAsync(Release release, string exe, CancellationToken cancel)
    {
        var folder = Path.GetDirectoryName(exe)!;
        var incoming = exe + ".new";
        var aside = exe + ".old";
        try
        {
            using (var response = await http.GetAsync("downloads/" + release.File, HttpCompletionOption.ResponseHeadersRead, cancel))
            {
                if (!response.IsSuccessStatusCode) return "The update isn't on stardexs.com right now. Try again later.";
                if (response.Content.Headers.ContentLength > MaxDownloadBytes) return "The update was larger than expected, so it wasn't used.";
                await using var source = await response.Content.ReadAsStreamAsync(cancel);
                await using var target = new FileStream(incoming, FileMode.Create, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancel)) > 0)
                {
                    total += read;
                    if (total > MaxDownloadBytes) return "The update was larger than expected, so it wasn't used.";
                    await target.WriteAsync(buffer.AsMemory(0, read), cancel);
                }
            }
            string actual;
            await using (var check = File.OpenRead(incoming)) actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(check, cancel));
            if (actual != release.Sha256) return "The download didn't match the signed release, so it wasn't used.";
            if (File.Exists(aside)) File.Delete(aside);
            File.Move(exe, aside);
            File.Move(incoming, exe);
            return null;
        }
        catch (UnauthorizedAccessException) { return $"Windows won't let StarDexs Companion update itself in {folder}. Download it from stardexs.com instead."; }
        catch (IOException exception) { return $"Couldn't update: {exception.Message}"; }
        catch (HttpRequestException) { return "Couldn't reach stardexs.com. Try again in a moment."; }
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
