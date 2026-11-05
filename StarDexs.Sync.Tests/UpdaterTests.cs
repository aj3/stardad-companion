using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using StarDexs.Sync.Core;
using Xunit;

namespace StarDexs.Sync.Tests;

public class UpdaterTests
{
    private static readonly byte[] NewExe = Encoding.UTF8.GetBytes("pretend this is StarDad Companion 0.3.0");
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private string PublicKey => Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());

    private string Manifest(string version = "0.3.0", byte[]? exe = null, ECDsa? signer = null, string? sha = null)
    {
        sha ??= Convert.ToHexStringLower(SHA256.HashData(exe ?? NewExe));
        var signature = (signer ?? _key).SignData(Updater.SignedText(version, sha), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return new JsonObject { ["version"] = version, ["file"] = $"StarDexsCompanion-{version}.exe", ["sha256"] = sha, ["signature"] = Convert.ToBase64String(signature) }.ToJsonString();
    }

    private Updater For(HttpMessageHandler site) => new(new HttpClient(site) { BaseAddress = new Uri("https://stardexs.test/") }, PublicKey);

    [Fact]
    public void Accepts_only_a_newer_release_signed_with_the_release_key()
    {
        var updater = For(new Files(new()));
        Assert.Equal(new Version(0, 3, 0), updater.Check(Manifest(), new Version(0, 2, 0))!.Version);
        Assert.Null(updater.Check(Manifest(), new Version(0, 3, 0)));                        // not newer
        Assert.Null(updater.Check(Manifest("0.1.0"), new Version(0, 2, 0)));                 // older: no going back
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Null(updater.Check(Manifest(signer: stranger), new Version(0, 2, 0)));         // someone else's key
        var tampered = JsonNode.Parse(Manifest())!;
        tampered["version"] = "9.9.9";
        Assert.Null(updater.Check(tampered.ToJsonString(), new Version(0, 2, 0)));           // signature covers the version
        var path = JsonNode.Parse(Manifest())!;
        path["file"] = "../../evil.exe";
        Assert.Null(updater.Check(path.ToJsonString(), new Version(0, 2, 0)));
        Assert.Null(updater.Check("not json", new Version(0, 2, 0)));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("{\"version\":42}")]
    [InlineData("{\"version\":\"9.9.9\",\"file\":[]}")]
    public void Malformed_manifest_types_are_refused_without_crashing(string json)
    {
        Assert.Null(For(new Files(new())).Check(json, new Version(0, 2, 0)));
    }

    [Fact]
    public void The_built_in_key_is_a_valid_public_key()
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(Updater.ReleaseKey), out var read);
        Assert.True(read > 0);
    }

    [Fact]
    public async Task Swaps_in_a_release_that_matches_its_signed_fingerprint()
    {
        using var install = new FakeInstall();
        var exe = Path.Combine(install.Root, "StarDexsCompanion.exe");
        File.WriteAllText(exe, "old");
        var updater = For(new Files(new() { ["/downloads/StarDexsCompanion-0.3.0.exe"] = NewExe }));
        var release = updater.Check(Manifest(), new Version(0, 2, 0))!;

        Assert.Null(await updater.InstallAsync(release, exe, CancellationToken.None));
        Assert.Equal(NewExe, File.ReadAllBytes(exe));
        Assert.Equal("old", File.ReadAllText(exe + ".old"));
        Updater.TidyUp(exe);
        Assert.False(File.Exists(exe + ".old"));
    }

    [Fact]
    public async Task Refuses_a_download_that_does_not_match()
    {
        using var install = new FakeInstall();
        var exe = Path.Combine(install.Root, "StarDexsCompanion.exe");
        File.WriteAllText(exe, "old");
        var updater = For(new Files(new() { ["/downloads/StarDexsCompanion-0.3.0.exe"] = Encoding.UTF8.GetBytes("something else") }));
        var release = updater.Check(Manifest(), new Version(0, 2, 0))!;

        Assert.Contains("didn't match", await updater.InstallAsync(release, exe, CancellationToken.None));
        Assert.Equal("old", File.ReadAllText(exe));
        Assert.False(File.Exists(exe + ".new"));
    }

    [Fact]
    public async Task Update_click_fetches_latest_signed_release_instead_of_a_cached_offer()
    {
        using var install=new FakeInstall();var exe=Path.Combine(install.Root,"Companion.exe");File.WriteAllText(exe,"original");
        var requests=new List<string>();var site=new Dynamic(request=>{
            requests.Add(request.RequestUri!.AbsolutePath);
            if(request.RequestUri.AbsolutePath.EndsWith("companion.json")){Assert.True(request.Headers.CacheControl?.NoCache);Assert.True(request.Headers.CacheControl?.NoStore);return Text(Manifest("0.5.0"));}
            return new(HttpStatusCode.OK){Content=new ByteArrayContent(NewExe)};
        });
        var result=await For(site).InstallNewestAsync(new(0,2,0),exe,null,CancellationToken.None);
        Assert.True(result.Installed);Assert.Equal(new Version(0,5,0),result.Release!.Version);
        Assert.Equal(new[]{"/downloads/companion.json","/downloads/StarDexsCompanion-0.5.0.exe"},requests);
        Assert.Equal(NewExe,File.ReadAllBytes(exe));
    }
    [Fact]
    public async Task Removed_download_rechecks_once_and_accepts_only_a_newer_signed_replacement()
    {
        using var install=new FakeInstall();var exe=Path.Combine(install.Root,"Companion.exe");File.WriteAllText(exe,"original");var checks=0;
        var site=new Dynamic(request=>request.RequestUri!.AbsolutePath.EndsWith("companion.json")?Text(Manifest(++checks==1?"0.3.0":"0.4.0")):
            request.RequestUri.AbsolutePath.EndsWith("0.3.0.exe")?new(HttpStatusCode.NotFound):new(HttpStatusCode.OK){Content=new ByteArrayContent(NewExe)});
        var result=await For(site).InstallNewestAsync(new(0,2,0),exe,null,CancellationToken.None);
        Assert.True(result.Installed);Assert.Equal(2,checks);Assert.Equal(new Version(0,4,0),result.Release!.Version);Assert.Equal("original",File.ReadAllText(exe+".old"));
    }
    [Fact]
    public async Task Invalid_latest_signature_never_downloads_and_never_claims_up_to_date()
    {
        using var install=new FakeInstall();var exe=Path.Combine(install.Root,"Companion.exe");File.WriteAllText(exe,"original");using var stranger=ECDsa.Create(ECCurve.NamedCurves.nistP256);var requests=0;
        var result=await For(new Dynamic(request=>{requests++;return Text(Manifest("9.9.9",signer:stranger));})).InstallNewestAsync(new(0,2,0),exe,null,CancellationToken.None);
        Assert.False(result.Installed);Assert.Contains("could not be verified",result.Problem);Assert.Equal(1,requests);Assert.Equal("original",File.ReadAllText(exe));
    }
    [Fact]
    public async Task Fingerprint_failure_is_not_retried_or_replaced_with_an_unverified_fallback()
    {
        using var install=new FakeInstall();var exe=Path.Combine(install.Root,"Companion.exe");File.WriteAllText(exe,"original");var manifests=0;
        var result=await For(new Dynamic(request=>request.RequestUri!.AbsolutePath.EndsWith("companion.json")?Text(Manifest(++manifests==1?"0.3.0":"0.4.0")):new(HttpStatusCode.OK){Content=new ByteArrayContent(Encoding.UTF8.GetBytes("tampered"))})).InstallNewestAsync(new(0,2,0),exe,null,CancellationToken.None);
        Assert.False(result.Installed);Assert.Contains("didn't match",result.Problem);Assert.Equal(1,manifests);Assert.Equal("original",File.ReadAllText(exe));Assert.False(File.Exists(exe+".new"));
    }
    [Fact]
    public async Task Removed_offer_does_not_fall_back_to_an_older_release_or_loop()
    {
        using var install=new FakeInstall();var exe=Path.Combine(install.Root,"Companion.exe");File.WriteAllText(exe,"original");var checks=0;var downloads=0;
        var result=await For(new Dynamic(request=>{if(request.RequestUri!.AbsolutePath.EndsWith("companion.json"))return Text(Manifest(++checks==1?"0.4.0":"0.3.0"));downloads++;return new(HttpStatusCode.Gone);})).InstallNewestAsync(new(0,2,0),exe,null,CancellationToken.None);
        Assert.False(result.Installed);Assert.NotNull(result.Problem);Assert.Equal(2,checks);Assert.Equal(1,downloads);Assert.Equal("original",File.ReadAllText(exe));
    }
    [Fact]
    public async Task Current_release_and_unavailable_manifest_are_distinct()
    {
        using var install=new FakeInstall();var exe=Path.Combine(install.Root,"Companion.exe");File.WriteAllText(exe,"original");
        var current=await For(new Dynamic(_=>Text(Manifest("0.3.0")))).InstallNewestAsync(new(0,3,0),exe,null,CancellationToken.None);
        Assert.False(current.Installed);Assert.Null(current.Problem);
        var unavailable=await For(new Dynamic(_=>new(HttpStatusCode.ServiceUnavailable))).InstallNewestAsync(new(0,2,0),exe,null,CancellationToken.None);
        Assert.False(unavailable.Installed);Assert.NotNull(unavailable.Problem);Assert.Equal("original",File.ReadAllText(exe));
    }
    private static HttpResponseMessage Text(string body)=>new(HttpStatusCode.OK){Content=new StringContent(body,Encoding.UTF8,"application/json")};
    private sealed class Dynamic(Func<HttpRequestMessage,HttpResponseMessage> respond):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancel)=>Task.FromResult(respond(request));
    }
    private sealed class Files(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel) =>
            Task.FromResult(files.TryGetValue(request.RequestUri!.AbsolutePath, out var data)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
