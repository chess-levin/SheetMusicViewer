using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics;
using System.Reflection;
using System.Windows.Input;
using Json.Schema;

namespace SheetMusicViewer;

/// <summary>
/// Stored in %AppData%\\SheetMusicViewer\\settings.json.
/// The JSON object uses the properties NextKey, PreviousKey, ZoomInKey, ZoomOutKey,
/// Zoom and RecentFiles. Keys are WPF <see cref="Key"/> names (for example
/// <c>"Right"</c> or <c>"Add"</c>), never numeric enum values.
/// </summary>
public sealed class ViewerSettings
{
    private const double MinimumZoom = 0.5;
    private const double MaximumZoom = 3.0;
    private const int MaximumRecentFiles = 8;
    private static readonly SemaphoreSlim SaveGate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    private static readonly JsonSchema SettingsSchema = LoadSchema();
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SheetMusicViewer", "settings.json");
    private static long _nextSaveRevision;
    private static long _lastSavedRevision;

    // Modifier keys are deliberately not accepted for navigation; use the bare arrow keys.
    public Key NextKey { get; set; } = Key.Right;
    public Key PreviousKey { get; set; } = Key.Left;
    public Key ZoomInKey { get; set; } = Key.Add;
    public Key ZoomOutKey { get; set; } = Key.Subtract;
    public double Zoom { get; set; } = 1.0;
    public List<string> RecentFiles { get; set; } = new();

    public IReadOnlyList<string> AvailableRecentFiles()
    {
        RecentFiles ??= new List<string>();
        var recentFiles = RecentFiles
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaximumRecentFiles)
            .ToList();
        if (!recentFiles.SequenceEqual(RecentFiles, StringComparer.OrdinalIgnoreCase))
        {
            RecentFiles = recentFiles;
        }
        return recentFiles;
    }

    public void RememberFile(string path)
    {
        RecentFiles ??= new List<string>();
        RecentFiles.RemoveAll(file => string.Equals(file, path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, path);
        RecentFiles = RecentFiles.Take(MaximumRecentFiles).ToList();
    }

    public void ForgetFile(string path)
    {
        RecentFiles ??= new List<string>();
        RecentFiles.RemoveAll(file => string.Equals(file, path, StringComparison.OrdinalIgnoreCase));
    }

    public static ViewerSettings Load()
    {
        try
        {
            var json = File.ReadAllText(SettingsPath);
            ValidateJson(json);
            var settings = JsonSerializer.Deserialize<ViewerSettings>(json, JsonOptions) ?? new ViewerSettings();
            settings.Normalize();
            return settings;
        }
        catch { return new ViewerSettings(); }
    }

    /// <summary>
    /// Persists a snapshot without blocking the caller. The file is replaced only after
    /// its complete contents have been written to a temporary file in the same directory.
    /// </summary>
    public Task SaveAsync()
    {
        Normalize();
        var json = JsonSerializer.Serialize(this, JsonOptions);
        var revision = Interlocked.Increment(ref _nextSaveRevision);
        return SaveSnapshotAsync(json, revision);
    }

    private void Normalize()
    {
        Zoom = double.IsFinite(Zoom) ? Math.Clamp(Zoom, MinimumZoom, MaximumZoom) : 1.0;

        NextKey = IsNavigationKey(NextKey) ? NextKey : Key.Right;
        PreviousKey = IsNavigationKey(PreviousKey) && PreviousKey != NextKey
            ? PreviousKey
            : NextKey == Key.Left ? Key.Right : Key.Left;
        ZoomInKey = IsZoomKey(ZoomInKey) && ZoomInKey != NextKey && ZoomInKey != PreviousKey ? ZoomInKey : Key.Add;
        ZoomOutKey = IsZoomKey(ZoomOutKey) && ZoomOutKey != NextKey && ZoomOutKey != PreviousKey && ZoomOutKey != ZoomInKey
            ? ZoomOutKey
            : Key.Subtract;

        RecentFiles = (RecentFiles ?? new List<string>())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaximumRecentFiles)
            .ToList();
    }

    private static bool IsNavigationKey(Key key) => key is
        Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End;

    private static bool IsZoomKey(Key key) => key is Key.Add or Key.Subtract or Key.OemPlus or Key.OemMinus;

    private static JsonSchema LoadSchema()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SheetMusicViewer.settings.schema.json")
            ?? throw new InvalidOperationException("The embedded viewer settings schema is missing.");
        using var reader = new StreamReader(stream);
        return JsonSchema.FromText(reader.ReadToEnd());
    }

    private static void ValidateJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!SettingsSchema.Evaluate(document.RootElement).IsValid)
        {
            Trace.TraceWarning("Viewer settings do not conform to settings.schema.json; invalid values will be normalized.");
        }
    }

    private static async Task SaveSnapshotAsync(string json, long revision)
    {
        await SaveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (revision <= Volatile.Read(ref _lastSavedRevision)) return;

            await Task.Run(() => WriteSnapshot(json)).ConfigureAwait(false);
            Volatile.Write(ref _lastSavedRevision, revision);
        }
        catch (Exception exception)
        {
            // Settings are non-essential; I/O failures must not bring down the viewer.
            Trace.TraceError("Could not save viewer settings: {0}", exception);
        }
        finally
        {
            SaveGate.Release();
        }
    }

    private static void WriteSnapshot(string json)
    {
        var directory = Path.GetDirectoryName(SettingsPath)!;
        var temporaryPath = Path.Combine(directory, $"settings.{Path.GetRandomFileName()}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch (Exception exception)
            {
                Trace.TraceWarning("Could not remove temporary viewer settings file: {0}", exception);
            }
        }
    }
}
