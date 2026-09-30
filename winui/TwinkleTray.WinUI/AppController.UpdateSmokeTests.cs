using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TwinkleTray.WinUI.Services;

namespace TwinkleTray.WinUI;

internal sealed partial class AppController
{
    private static async Task VerifyUpdateDownloadAsync(Action<bool, string> check)
    {
        string fixtures = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "test-fixtures"));
        string root = Path.Combine(fixtures, "update-download-" + Guid.NewGuid().ToString("N"));
        var handler = new UpdateFixtureHandler();
        using var updater = new UpdateService(new HttpClient(handler), root);
        try
        {
            var stable = await updater.CheckAsync(false, CancellationToken.None);
            var preview = await updater.CheckAsync(true, CancellationToken.None);
            check(stable?.Version == "998.0.0" && preview?.Version == "999.0.0-beta.2" &&
                preview.FileName.StartsWith("TwinkleTray-Native-", StringComparison.Ordinal) &&
                preview.Archive.AbsolutePath.StartsWith("/BK927/twinkle-tray-native/", StringComparison.Ordinal) && handler.NativeApiRequests == 2,
                "Update release selection honors stable and preview channels at the Native repository");
            string staging = await updater.DownloadAsync(CancellationToken.None);
            check(File.ReadAllText(Path.Combine(staging, "TwinkleTray.WinUI.exe")) == "fixture-only-not-an-executable" &&
                File.Exists(Path.Combine(staging, "TwinkleTray.WinUI.deps.json")), "Update download verifies checksum and extracts a complete staged archive");

            handler.BadChecksum = true;
            bool rejected = false;
            try { await updater.DownloadAsync(CancellationToken.None); } catch (InvalidDataException) { rejected = true; }
            check(rejected && Directory.GetDirectories(root).Count(path => Directory.Exists(Path.Combine(path, "app"))) == 1,
                "Update checksum mismatch is rejected before extraction");

            handler.BadChecksum = false;
            handler.IncludeLegacyAlias = true;
            var bothNames = await updater.CheckAsync(false, CancellationToken.None);
            check(bothNames?.FileName.StartsWith("TwinkleTray-Native-", StringComparison.Ordinal) == true,
                "Update selection prefers the Native asset when a legacy alias appears first");

            handler.IncludeLegacyAlias = false; handler.UseLegacyAssetNames = true;
            var renamedLegacy = await updater.CheckAsync(false, CancellationToken.None);
            staging = await updater.DownloadAsync(CancellationToken.None);
            check(renamedLegacy?.FileName.StartsWith("TwinkleTray-WinUI3-", StringComparison.Ordinal) == true &&
                renamedLegacy.Archive.AbsolutePath.StartsWith("/BK927/twinkle-tray-native/", StringComparison.Ordinal) &&
                File.Exists(Path.Combine(staging, "TwinkleTray.WinUI.exe")),
                "Legacy WinUI3 assets retained in the renamed repository verify and stage successfully");

            handler.UseLegacyRepository = true;
            var oldLocation = await updater.CheckAsync(false, CancellationToken.None);
            staging = await updater.DownloadAsync(CancellationToken.None);
            check(oldLocation?.Archive.AbsolutePath.StartsWith("/BK927/twinkle-tray/releases/download/", StringComparison.Ordinal) == true &&
                File.Exists(Path.Combine(staging, "TwinkleTray.WinUI.exe")),
                "An explicitly retained legacy repository asset remains compatible with the new updater");

            handler.UseLegacyRepository = false; handler.UseLegacyAssetNames = false;
            foreach (string invalid in new[] { "foreign-owner", "similar-repository", "wrong-tag", "wrong-file", "http", "port", "credentials", "query", "foreign-checksum" })
            {
                handler.UntrustedAsset = invalid;
                rejected = false;
                try { await updater.CheckAsync(true, CancellationToken.None); } catch (InvalidDataException) { rejected = true; }
                if (!rejected) throw new InvalidOperationException("Update fixture accepted an unsafe asset location: " + invalid);
            }
            check(true, "Update release assets reject foreign repositories and mismatched or unsafe download locations");
        }
        finally
        {
            string resolved = Path.GetFullPath(root);
            if (resolved.StartsWith(fixtures + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolved))
                Directory.Delete(resolved, true);
        }
    }

    private sealed class UpdateFixtureHandler : HttpMessageHandler
    {
        private readonly byte[] _archive;
        private readonly string _architecture = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "ARM64" : "x64";
        public bool BadChecksum { get; set; }
        public bool UseLegacyAssetNames { get; set; }
        public bool IncludeLegacyAlias { get; set; }
        public bool UseLegacyRepository { get; set; }
        public string UntrustedAsset { get; set; } = "";
        public int NativeApiRequests { get; private set; }

        public UpdateFixtureHandler()
        {
            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            {
                using (var writer = new StreamWriter(zip.CreateEntry("TwinkleTray.WinUI.exe").Open())) writer.Write("fixture-only-not-an-executable");
                using (var writer = new StreamWriter(zip.CreateEntry("TwinkleTray.WinUI.deps.json").Open())) writer.Write("{}");
            }
            _archive = stream.ToArray();
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = request.RequestUri ?? throw new InvalidOperationException("Missing update request URI.");
            HttpContent content;
            if (uri.Host == "api.github.com" && uri.AbsolutePath == "/repos/BK927/twinkle-tray-native/releases")
            {
                NativeApiRequests++;
                object Release(string version, bool prerelease, bool draft = false)
                {
                    string repository = UseLegacyRepository ? "twinkle-tray" : "twinkle-tray-native";
                    string prefix = $"https://github.com/BK927/{repository}/releases/download/winui-v{version}/";
                    object Asset(string name)
                    {
                        string url = prefix + name;
                        url = UntrustedAsset switch
                        {
                            "foreign-owner" => url.Replace("/BK927/", "/untrusted/", StringComparison.Ordinal),
                            "similar-repository" => url.Replace("/twinkle-tray-native/", "/twinkle-tray-native-unsafe/", StringComparison.Ordinal),
                            "wrong-tag" => url.Replace("/winui-v" + version + "/", "/winui-v997.0.0/", StringComparison.Ordinal),
                            "wrong-file" => prefix + "other.zip",
                            "http" => url.Replace("https://", "http://", StringComparison.Ordinal),
                            "port" => url.Replace("github.com/", "github.com:8443/", StringComparison.Ordinal),
                            "credentials" => url.Replace("https://", "https://untrusted@", StringComparison.Ordinal),
                            "query" => url + "?asset=other",
                            "foreign-checksum" when name == "SHA256SUMS.txt" => url.Replace("/BK927/", "/untrusted/", StringComparison.Ordinal),
                            _ => url
                        };
                        return new { name, browser_download_url = url };
                    }
                    var assets = new List<object>();
                    if (IncludeLegacyAlias) assets.Add(Asset($"TwinkleTray-WinUI3-{version}-{_architecture}.zip"));
                    assets.Add(Asset($"{(UseLegacyAssetNames ? "TwinkleTray-WinUI3" : "TwinkleTray-Native")}-{version}-{_architecture}.zip"));
                    assets.Add(Asset("SHA256SUMS.txt"));
                    return new { draft, prerelease, tag_name = "winui-v" + version, body = "Fixture release", html_url = "https://github.com/BK927/twinkle-tray-native/releases", assets };
                }
                content = new StringContent(JsonSerializer.Serialize(new[] { Release("999.0.0-beta.2", true), Release("998.0.0", false), Release("1000.0.0", false, true) }), Encoding.UTF8, "application/json");
            }
            else if (uri.Host == "github.com" && (uri.AbsolutePath.StartsWith("/BK927/twinkle-tray/releases/download/", StringComparison.Ordinal) ||
                uri.AbsolutePath.StartsWith("/BK927/twinkle-tray-native/releases/download/", StringComparison.Ordinal)))
            {
                string version = uri.Segments[^2].TrimEnd('/')[7..];
                content = uri.AbsolutePath.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal)
                    ? new StringContent($"{(BadChecksum ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(_archive)))}  {(UseLegacyAssetNames ? "TwinkleTray-WinUI3" : "TwinkleTray-Native")}-{version}-{_architecture}.zip\n")
                    : new ByteArrayContent(_archive);
            }
            else throw new InvalidOperationException("Unexpected fixture request: " + uri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
