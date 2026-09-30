using System.Text.Json;

namespace TwinkleTray.Core;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly object gate = new();
    public string FilePath { get; }

    public SettingsStore(string? path = null)
    {
        FilePath = Path.GetFullPath(path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TwinkleTray.WinUI", "settings.json"));
    }

    public AppSettings Load()
    {
        lock (gate)
        {
            if (!File.Exists(FilePath))
                return new AppSettings();

            try
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions)
                    ?? throw new JsonException("The settings document is null.");
                return SettingsNormalizer.Normalize(settings);
            }
            catch (JsonException exception)
            {
                // Keep the original file in place as well as a recovery copy. Never silently reset it.
                string? backupPath = FilePath + $".corrupt-{DateTime.UtcNow:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}.json";
                Exception? backupError = null;
                try
                {
                    File.Copy(FilePath, backupPath, overwrite: false);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    backupPath = null;
                    backupError = error;
                }
                throw new SettingsLoadException(FilePath, backupPath, exception, backupError);
            }
        }
    }

    public void Save(AppSettings settings)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(SettingsNormalizer.Normalize(settings), JsonOptions);
        lock (gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temporaryPath = FilePath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    4096, FileOptions.WriteThrough))
                {
                    stream.Write(data);
                    stream.Flush(flushToDisk: true);
                }

                // The temporary file shares the target volume, so replacement is atomic.
                if (File.Exists(FilePath))
                    File.Replace(temporaryPath, FilePath, destinationBackupFileName: null);
                else
                    File.Move(temporaryPath, FilePath);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }
    }
}

public sealed class SettingsLoadException : IOException
{
    public string FilePath { get; }
    public string? BackupPath { get; }
    public Exception? BackupError { get; }

    public SettingsLoadException(string filePath, string? backupPath, Exception innerException, Exception? backupError = null)
        : base(backupPath is null
            ? $"Settings could not be read. The original file is unchanged at '{filePath}'."
            : $"Settings could not be read. The original file is unchanged and a recovery copy was saved to '{backupPath}'.", innerException)
    {
        FilePath = filePath;
        BackupPath = backupPath;
        BackupError = backupError;
    }
}
