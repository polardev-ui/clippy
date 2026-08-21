using System.Collections.ObjectModel;
using System.Text.Json;
using Clippy.Models;

namespace Clippy.Services;

public sealed class ClipManager
{
    private static ClipManager? _instance;
    public static ClipManager Instance => _instance ??= new ClipManager();

    public ObservableCollection<Clip> Clips { get; } = new();

    private ClipManager()
    {
        LoadClips();
    }

    public void LoadClips()
    {
        Clips.Clear();
        if (!File.Exists(ClipStorage.IndexPath)) return;

        try
        {
            var json = File.ReadAllText(ClipStorage.IndexPath);
            var decoded = JsonSerializer.Deserialize<List<Clip>>(json) ?? new List<Clip>();
            foreach (var clip in decoded
                         .Where(c => File.Exists(c.FilePath))
                         .OrderByDescending(c => c.CreatedAt))
            {
                Clips.Add(clip);
            }
        }
        catch
        {
        }
    }

    public async Task<Clip> AddClipAsync(string sourcePath, double duration)
    {
        if (!await ClipExporter.IsPlayableVideoAsync(sourcePath))
        {
            throw new InvalidOperationException("The exported clip could not be verified as playable video.");
        }

        var fileName = $"clip_{Guid.NewGuid():N}.mov";
        var destination = Path.Combine(ClipStorage.LibraryDirectory, fileName);
        if (File.Exists(destination)) File.Delete(destination);
        File.Copy(sourcePath, destination, overwrite: true);

        if (!await ClipExporter.IsPlayableVideoAsync(destination))
        {
            File.Delete(destination);
            throw new InvalidOperationException("The exported clip could not be verified as playable video.");
        }

        var clip = new Clip
        {
            Duration = duration,
            FileName = fileName,
            Title = Clip.DefaultTitle(DateTime.Now)
        };

        // Clips are produced on a background thread; the collection belongs to the UI.
        await UiDispatcher.RunAsync(() =>
        {
            Clips.Insert(0, clip);
            SaveIndex();
        });

        await ClipThumbnailService.GenerateAsync(clip.Id, destination);
        return clip;
    }

    public void DeleteClip(Clip clip)
    {
        if (File.Exists(clip.FilePath)) File.Delete(clip.FilePath);
        var thumbPath = ClipThumbnailService.PathFor(clip.Id);
        if (File.Exists(thumbPath)) File.Delete(thumbPath);
        var existing = Clips.FirstOrDefault(c => c.Id == clip.Id);
        if (existing != null) Clips.Remove(existing);
        SaveIndex();
    }

    public void RenameClip(Clip clip, string title)
    {
        var index = -1;
        for (var i = 0; i < Clips.Count; i++)
        {
            if (Clips[i].Id == clip.Id) { index = i; break; }
        }

        if (index < 0) return;

        Clips[index].Title = title;

        // Clip has no change notification, so re-seat the item to make the list redraw.
        Clips[index] = Clips[index];
        SaveIndex();
    }

    public void RevealInExplorer(Clip clip)
    {
        if (!File.Exists(clip.FilePath)) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{clip.FilePath}\"",
            UseShellExecute = true
        });
    }

    public async Task ExportClipAsync(Clip clip)
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow!);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.VideosLibrary;
        picker.SuggestedFileName = clip.FileName;
        picker.FileTypeChoices.Add("QuickTime Movie", new List<string> { ".mov" });

        var file = await picker.PickSaveFileAsync();
        if (file == null) return;
        File.Copy(clip.FilePath, file.Path, overwrite: true);
    }

    private void SaveIndex()
    {
        var json = JsonSerializer.Serialize(Clips.ToList(), new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ClipStorage.IndexPath, json);
    }
}
