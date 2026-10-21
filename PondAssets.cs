using System.Windows.Media.Imaging;

namespace CodexPond;

/// <summary>Loads the project's original, locally bundled pixel-art assets.</summary>
public static class PondAssets
{
    private static readonly Dictionary<PondTheme, BitmapImage> Themes = new();
    private static readonly Dictionary<string, BitmapImage> Fish = new(StringComparer.Ordinal);
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
