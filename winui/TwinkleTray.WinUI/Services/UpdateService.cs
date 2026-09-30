using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using TwinkleTray.Core;

namespace TwinkleTray.WinUI.Services;

internal sealed record AvailableUpdate(string Version, string Notes, Uri Page, Uri Archive, Uri Checksums, string FileName);

internal sealed class UpdateService : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _downloadRoot;
    internal static string CurrentVersion => (Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.3.5").Split('+')[0];
    public AvailableUpdate? Available { get; private set; }

    public UpdateService() : this(new HttpClient { Timeout = TimeSpan.FromMinutes(10) },
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TwinkleTray.WinUI.Updates")) { }

    internal UpdateService(HttpClient http, string downloadRoot)
    {
        _http = http;
        _downloadRoot = Path.GetFullPath(downloadRoot);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("TwinkleTray-Native/" + CurrentVersion);
    }

    public async Task<AvailableUpdate?> CheckAsync(bool includePrerelease, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync("https://api.github.com/repos/BK927/twinkle-tray-native/releases?per_page=30", cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        string architecture = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "ARM64" : "x64";
        Available = null;
        foreach (var release in json.RootElement.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean() || (!includePrerelease && release.GetProperty("prerelease").GetBoolean())) continue;
            string tag = release.GetProperty("tag_name").GetString() ?? "";
            if (!tag.StartsWith("winui-v", StringComparison.OrdinalIgnoreCase)) continue;
            string version = tag[7..];
            if (!SemanticVersion.TryCompare(version, CurrentVersion, out var newer) || newer <= 0) continue;
            var assets = release.GetProperty("assets").EnumerateArray().ToArray();
            // Prefer the current public name while accepting assets retained from releases before the rename.
            string name = $"TwinkleTray-Native-{version}-{architecture}.zip";
            var archive = assets.FirstOrDefault(x => x.GetProperty("name").GetString() == name);
            if (archive.ValueKind == JsonValueKind.Undefined)
            {
                name = $"TwinkleTray-WinUI3-{version}-{architecture}.zip";
                archive = assets.FirstOrDefault(x => x.GetProperty("name").GetString() == name);
            }
            var sums = assets.FirstOrDefault(x => x.GetProperty("name").GetString() == "SHA256SUMS.txt");
            if (archive.ValueKind == JsonValueKind.Undefined || sums.ValueKind == JsonValueKind.Undefined) continue;
            if (Available is not null && SemanticVersion.Compare(version, Available.Version) <= 0) continue;
            Available = new AvailableUpdate(version, release.GetProperty("body").GetString() ?? "", new Uri(release.GetProperty("html_url").GetString()!),
                AssetUri(archive, tag), AssetUri(sums, tag), name);
        }
        return Available;
    }

    private static Uri AssetUri(JsonElement asset, string tag)
    {
        var uri = new Uri(asset.GetProperty("browser_download_url").GetString()!);
        string name = asset.GetProperty("name").GetString()!;
        string path = uri.GetComponents(UriComponents.Path, UriFormat.Unescaped);
        string suffix = $"/releases/download/{tag}/{name}";
        bool trustedPath = path.Equals("BK927/twinkle-tray-native" + suffix, StringComparison.Ordinal) ||
            path.Equals("BK927/twinkle-tray" + suffix, StringComparison.Ordinal);
        if (uri.Scheme != "https" || uri.Host != "github.com" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 || !trustedPath)
            throw new InvalidDataException("Unexpected update asset location.");
        return uri;
    }

    public async Task<string> DownloadAsync(CancellationToken cancellationToken)
    {
        var update = Available ?? throw new InvalidOperationException("Check for updates first.");
        string root = Path.Combine(_downloadRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string archive = Path.Combine(root, update.FileName);
        string checksums = await _http.GetStringAsync(update.Checksums, cancellationToken);
        string? expected = checksums.Split('\n').Select(line => line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length >= 2 && parts[^1].TrimStart('*') == update.FileName).Select(parts => parts[0]).SingleOrDefault();
        if (expected is null || expected.Length != 64 || !expected.All(Uri.IsHexDigit)) throw new InvalidDataException("The release has no valid SHA256 checksum for this architecture.");
        using (var response = await _http.GetAsync(update.Archive, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var destination = File.Create(archive);
            await source.CopyToAsync(destination, cancellationToken);
        }
        await using (var stream = File.OpenRead(archive))
        {
            string actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Update checksum verification failed. The installed application was not modified.");
        }
        string staging = Path.Combine(root, "app");
        ZipFile.ExtractToDirectory(archive, staging);
        if (!File.Exists(Path.Combine(staging, "TwinkleTray.WinUI.exe")) || !File.Exists(Path.Combine(staging, "TwinkleTray.WinUI.deps.json")))
            throw new InvalidDataException("The update does not contain a complete Twinkle Tray Native application.");
        return staging;
    }

    public static void LaunchInstaller(string staging)
    {
        var start = new ProcessStartInfo(Path.Combine(staging, "TwinkleTray.WinUI.exe")) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = staging };
        start.ArgumentList.Add("--apply-update"); start.ArgumentList.Add(Environment.ProcessId.ToString()); start.ArgumentList.Add(AppContext.BaseDirectory);
        _ = Process.Start(start) ?? throw new IOException("Could not start the update installer.");
    }

    public static int ApplyStagedUpdate(string[] arguments)
    {
        if (arguments.Length != 3 || !int.TryParse(arguments[1], out int parentId)) throw new ArgumentException("Invalid update request.");
        string source = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
        string target = Path.GetFullPath(arguments[2]).TrimEnd(Path.DirectorySeparatorChar);
        if (target == source || target == Path.GetPathRoot(target)?.TrimEnd(Path.DirectorySeparatorChar) ||
            !File.Exists(Path.Combine(target, "TwinkleTray.WinUI.exe")) || !File.Exists(Path.Combine(target, "TwinkleTray.WinUI.deps.json")))
            throw new InvalidOperationException("The update destination is not an existing Twinkle Tray Native installation.");
        try
        {
            using var parent = Process.GetProcessById(parentId);
            if (!parent.WaitForExit(30000)) throw new TimeoutException("Twinkle Tray Native did not close for the update.");
        }
        catch (ArgumentException) { /* The parent already exited. */ }
        string backup = Path.Combine(Directory.GetParent(source)!.FullName, "backup");
        InstallFiles(source, target, backup);
        Process.Start(new ProcessStartInfo(Path.Combine(target, "TwinkleTray.WinUI.exe")) { UseShellExecute = true, WorkingDirectory = target });
        return 0;
    }

    internal static void InstallFiles(string source, string target, string backup)
    {
        source = Path.GetFullPath(source); target = Path.GetFullPath(target); backup = Path.GetFullPath(backup);
        var roots = new[] { source, target, backup }.Select(path => path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar).ToArray();
        for (int first = 0; first < roots.Length; first++)
            for (int second = 0; second < roots.Length; second++)
                if (first != second && roots[first].StartsWith(roots[second], StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Update source, target and backup must be separate, non-nested directories.");
        foreach (var root in new[] { source, target, backup })
        {
            if (Directory.Exists(root) && (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new IOException("Update directories cannot be symbolic links.");
            if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Any(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
                throw new IOException("Update directories cannot contain symbolic links.");
        }
        var copied = new List<(string Target, string? Backup)>();
        try
        {
            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(source, file);
                string destination = Path.Combine(target, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                string? previous = null;
                if (File.Exists(destination))
                {
                    previous = Path.Combine(backup, relative); Directory.CreateDirectory(Path.GetDirectoryName(previous)!); File.Copy(destination, previous, true);
                }
                copied.Add((destination, previous)); File.Copy(file, destination, true);
            }
        }
        catch
        {
            foreach (var item in copied.AsEnumerable().Reverse())
                try { if (item.Backup is not null) File.Copy(item.Backup, item.Target, true); else File.Delete(item.Target); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { Program.Log(exception); }
            throw;
        }
    }

    public void Dispose() => _http.Dispose();
}
