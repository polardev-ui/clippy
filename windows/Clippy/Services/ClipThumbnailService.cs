using Clippy.Models;

namespace Clippy.Services;

public static class ClipThumbnailService
{
    public static string ThumbnailDirectory
    {
        get
        {
            var dir = Path.Combine(ClipStorage.AppDataDirectory, "Thumbnails");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string PathFor(Guid clipId) => Path.Combine(ThumbnailDirectory, $"{clipId:N}.jpg");

    public static async Task GenerateAsync(Guid clipId, string videoPath)
    {
        if (!File.Exists(videoPath) || FfmpegLocator.Path == null)
        {
            return;
        }

        var output = PathFor(clipId);
        try
        {
            if (File.Exists(output))
            {
                File.Delete(output);
            }

            var args = $"-y -ss 0.5 -i \"{videoPath}\" -vframes 1 -q:v 3 \"{output}\"";
            await ClipExporter.RunFfmpegAsync(args);
        }
        catch (Exception ex)
        {
            ClippyDebugLog.Instance.Log("Clip", $"Thumbnail failed: {ex.Message}");
            try { if (File.Exists(output)) File.Delete(output); } catch { }
        }
    }
}
