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
            check(stable?.Version == "998.0.0" && preview?.Version == "999.0.0-beta.2", "Update release selection honors stable and preview channels");
            string staging = await updater.DownloadAsync(CancellationToken.None);
            check(File.ReadAllText(Path.Combine(staging, "TwinkleTray.WinUI.exe")) == "fixture-only-not-an-executable" &&
                File.Exists(Path.Combine(staging, "TwinkleTray.WinUI.deps.json")), "Update download verifies checksum and extracts a complete staged archive");

            handler.BadChecksum = true;
            bool rejected = false;
            try { await updater.DownloadAsync(CancellationToken.None); } catch (InvalidDataException) { rejected = true; }
            check(rejected && Directory.GetDirectories(root).Count(path => Directory.Exists(Path.Combine(path, "app"))) == 1,
                "Update checksum mismatch is rejected before extraction");

            handler.BadChecksum = false;
            handler.UntrustedAsset = true;
            rejected = false;
            try { await updater.CheckAsync(true, CancellationToken.None); } catch (InvalidDataException) { rejected = true; }
            check(rejected, "Update release assets outside the fork are rejected");
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
        public bool UntrustedAsset { get; set; }

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
            if (uri.Host == "api.github.com" && uri.AbsolutePath == "/repos/BK927/twinkle-tray/releases")
            {
                object Release(string version, bool prerelease, bool draft = false)
                {
                    string prefix = $"https://github.com/{(UntrustedAsset ? "untrusted" : "BK927")}/twinkle-tray/releases/download/winui-v{version}/";
                    return new { draft, prerelease, tag_name = "winui-v" + version, body = "Fixture release", html_url = "https://github.com/BK927/twinkle-tray/releases", assets = new[] {
                        new { name = $"TwinkleTray-WinUI3-{version}-{_architecture}.zip", browser_download_url = prefix + $"TwinkleTray-WinUI3-{version}-{_architecture}.zip" },
                        new { name = "SHA256SUMS.txt", browser_download_url = prefix + "SHA256SUMS.txt" } } };
                }
                content = new StringContent(JsonSerializer.Serialize(new[] { Release("999.0.0-beta.2", true), Release("998.0.0", false), Release("1000.0.0", false, true) }), Encoding.UTF8, "application/json");
            }
            else if (uri.Host == "github.com" && uri.AbsolutePath.StartsWith("/BK927/twinkle-tray/releases/download/", StringComparison.Ordinal))
            {
                string version = uri.Segments[^2].TrimEnd('/')[7..];
                content = uri.AbsolutePath.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal)
                    ? new StringContent($"{(BadChecksum ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(_archive)))}  TwinkleTray-WinUI3-{version}-{_architecture}.zip\n")
                    : new ByteArrayContent(_archive);
            }
            else throw new InvalidOperationException("Unexpected fixture request: " + uri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
