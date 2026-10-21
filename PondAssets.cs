using System.Windows.Media.Imaging;
using System.Windows.Media;

namespace CodexPond;

/// <summary>Loads the project's original, locally bundled pixel-art assets.</summary>
public static class PondAssets
{
    private static readonly Dictionary<PondTheme, BitmapImage> Themes = new();
    private static readonly Dictionary<string, BitmapImage> Fish = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, BitmapSource> Silhouettes = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    public static BitmapImage Theme(PondTheme theme)
    {
        lock (Gate)
        {
            if (!Themes.TryGetValue(theme, out var image))
            {
                var fileName = theme switch
                {
                    PondTheme.A => "theme-a",
                    PondTheme.B => "theme-b",
                    PondTheme.C => "theme-c",
                    PondTheme.D => "theme-d",
                    PondTheme.E => "theme-e",
                    _ => "theme-fusionce"
                };
                image = Load($"Assets/Themes/{fileName}.png");
                Themes[theme] = image;
            }
            return image;
        }
    }

    public static BitmapImage? FishSprite(string? fishId)
    {
        if (string.IsNullOrWhiteSpace(fishId) || !FishCatalog.All.Any(item => item.Id == fishId)) return null;
        lock (Gate)
        {
            if (!Fish.TryGetValue(fishId, out var image))
            {
                image = Load($"Assets/Fish/{fishId}.png");
                Fish[fishId] = image;
            }
            return image;
        }
    }

    public static BitmapSource? FishSilhouette(string? fishId)
    {
        var source = FishSprite(fishId);
        if (source is null || string.IsNullOrWhiteSpace(fishId)) return null;
        lock (Gate)
        {
            if (Silhouettes.TryGetValue(fishId, out var cached)) return cached;
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            var stride = converted.PixelWidth * 4;
            var pixels = new byte[stride * converted.PixelHeight];
            converted.CopyPixels(pixels, stride, 0);
            const byte blue = 62;
            const byte green = 82;
            const byte red = 60;
            for (var index = 0; index < pixels.Length; index += 4)
            {
                if (pixels[index + 3] == 0) continue;
                pixels[index] = blue;
                pixels[index + 1] = green;
                pixels[index + 2] = red;
            }
            var silhouette = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96,
                PixelFormats.Bgra32, null, pixels, stride);
            silhouette.Freeze();
            Silhouettes[fishId] = silhouette;
            return silhouette;
        }
    }

    private static BitmapImage Load(string relativePath)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri($"pack://application:,,,/CodexPond;component/{relativePath}", UriKind.Absolute);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
