using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Pen = System.Windows.Media.Pen;

namespace CodexPond;

public sealed class PondRenderer : FrameworkElement
{
    private static readonly Dictionary<string, Brush> BrushesByHex = new(StringComparer.OrdinalIgnoreCase);
    private FishingStage _stage = FishingStage.Idle;
    private double _animationSeconds;
    private DateTimeOffset? _lastTickAt;
    private DateTimeOffset _stageStartedAt = DateTimeOffset.Now;
    private DateTimeOffset _rippleStartedAt;
    private DateTimeOffset _rippleEndsAt;
    private DateTimeOffset _activityEndsAt;
    private Point _ripplePoint = new(248, 191);
    private int _activityScore;

    public FishingStage Stage
    {
        get => _stage;
        set
        {
            if (_stage == value) return;
            _stage = value;
            _stageStartedAt = DateTimeOffset.Now;
            InvalidateVisual();
        }
    }

    public PondTheme Theme { get; set; } = PondTheme.FusionCE;
    public PondEnvironment Environment { get; set; } = PondEnvironment.At(DateTime.Now);
    public PondEvent? ActiveEvent { get; set; }
    public string EquippedHatId { get; set; } = "straw-hat";
    public string? LeftDecorId { get; set; }
    public string? RightDecorId { get; set; }
    public string CatchColor { get; set; } = "#E8B85F";
    public string CatchFishId { get; set; } = "sunny";

    public void Tick(DateTimeOffset? now = null)
    {
        var instant = now ?? DateTimeOffset.Now;
        if (_lastTickAt is { } last)
            _animationSeconds += Math.Clamp((instant - last).TotalSeconds, 0, 2);
        _lastTickAt = instant;
        InvalidateVisual();
    }

    public void CreateRipple(Point point)
    {
        _ripplePoint = point;
        _rippleStartedAt = DateTimeOffset.Now;
        _rippleEndsAt = _rippleStartedAt.AddSeconds(1.4);
        InvalidateVisual();
    }

    public void RegisterActivity(int score)
    {
        _activityScore = Math.Clamp(score, 0, 180);
        _activityEndsAt = DateTimeOffset.Now.AddSeconds(1.6);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        DrawTimeAndSeasonOverlay(dc);
        DrawFishingLine(dc);
        DrawStageEffects(dc);
        DrawEnvironmentEffects(dc);
        DrawAccessories(dc);
    }

    private void DrawTimeAndSeasonOverlay(DrawingContext dc)
    {
        var tint = Environment.Phase switch
        {
            DayPhase.Dawn => "#12F3C98F",
            DayPhase.Day => "#08F5EEA8",
            DayPhase.Dusk => "#18E77D62",
            _ => "#25323D70"
        };
        dc.DrawRectangle(Brush(tint), null, new Rect(0, 0, 500, 313));

        if (Environment.Weather is PondWeather.Rain or PondWeather.Snow)
        {
            for (var i = 0; i < 7; i++)
            {
                var x = 24 + (i * 61 + _animationSeconds * 22) % 450;
                var y = (i * 31 + _animationSeconds * 34) % 300;
                DrawPixel(dc, x, y, Environment.Weather == PondWeather.Snow ? 2 : 1, 3,
                    Environment.Weather == PondWeather.Snow ? "#B8E5E4" : "#6F9BA1");
            }
        }

        if (Environment.Phase == DayPhase.Night)
        {
            for (var i = 0; i < 5; i++)
            {
                var x = 34 + i * 88;
                var y = 22 + (i % 2) * 13;
                if (x is > 115 and < 315) continue;
                var sparkle = ((int)(_animationSeconds * 2) + i) % 3 == 0;
                DrawPixel(dc, x, y, sparkle ? 3 : 2, 2, "#D5D89A");
            }
        }
    }

    private void DrawFishingLine(DrawingContext dc)
    {
        var (rodBase, rodTip, bobberHome) = Theme switch
        {
            PondTheme.B => (new Point(397, 151), new Point(365, 137), new Point(302, 181)),
            PondTheme.D => (new Point(110, 190), new Point(137, 166), new Point(222, 188)),
            PondTheme.FusionCE => (new Point(398, 204), new Point(365, 170), new Point(298, 191)),
            PondTheme.E => (new Point(106, 190), new Point(139, 166), new Point(220, 190)),
            PondTheme.C => (new Point(112, 174), new Point(144, 153), new Point(220, 188)),
            _ => (new Point(111, 175), new Point(145, 153), new Point(222, 186))
        };
        var rodTipX = rodTip.X;
        var rodTipY = rodTip.Y;
        var phase = _animationSeconds * 2.8;
        var stageSeconds = Math.Max(0, (DateTimeOffset.Now - _stageStartedAt).TotalSeconds);
        double bobberX;
        double bobberY;

        switch (Stage)
        {
            case FishingStage.Casting:
                var cast = Math.Clamp(stageSeconds / 1.2, 0, 1);
                bobberX = rodTipX + (bobberHome.X - rodTipX) * cast;
                bobberY = rodTipY + (bobberHome.Y - rodTipY) * cast;
                break;
            case FishingStage.Biting:
                bobberX = bobberHome.X + Math.Sin(phase * 1.1) * 2;
                bobberY = bobberHome.Y + Math.Sin(phase * 1.8) * 3;
                break;
            case FishingStage.Fighting:
                bobberX = bobberHome.X + Math.Sin(phase * 1.5) * (12 + Math.Min(_activityScore / 20, 8));
                bobberY = bobberHome.Y + Math.Cos(phase * 1.9) * 5;
                break;
            case FishingStage.WaitingForApproval:
                bobberX = bobberHome.X;
                bobberY = bobberHome.Y;
                break;
            case FishingStage.Catching:
                bobberX = bobberHome.X;
                bobberY = bobberHome.Y - 26 - Math.Abs(Math.Sin(phase)) * 9;
                break;
            case FishingStage.Retracting:
                var retract = Math.Clamp(stageSeconds / 1.2, 0, 1);
                bobberX = bobberHome.X + (rodTipX - bobberHome.X) * retract;
                bobberY = bobberHome.Y + (rodTipY - bobberHome.Y) * retract;
                break;
            case FishingStage.ConnectionLost:
                bobberX = bobberHome.X;
                bobberY = bobberHome.Y;
                break;
            default:
                bobberX = bobberHome.X + Math.Sin(phase * 0.35) * 2;
                bobberY = bobberHome.Y + Math.Sin(phase * 0.5) * 1.5;
                break;
        }

        var linePen = new Pen(Brush(Stage == FishingStage.ConnectionLost ? "#8A7562" : "#F0E3BA"), 0.8);
        var rodPen = new Pen(Brush("#9A683B"), 1.7);
        dc.DrawLine(rodPen, rodBase, rodTip);
        if (Stage == FishingStage.ConnectionLost)
        {
            dc.DrawLine(linePen, rodTip, new Point(rodTipX + 28, rodTipY + 32));
            DrawPixel(dc, rodTipX + 28, rodTipY + 32, 3, 2, "#C66D4C");
            return;
        }

        dc.DrawLine(linePen, rodTip, new Point(bobberX, bobberY - 3));
        if (Stage is FishingStage.Idle or FishingStage.Casting or FishingStage.Retracting)
            DrawPixel(dc, bobberX - 5, bobberY + 7, 10, 1, "#77B3A0");
        if (Stage is FishingStage.Biting or FishingStage.Fighting or FishingStage.WaitingForApproval)
        {
            for (var i = 0; i < 3; i++)
            {
                var width = 10 + i * 7 + Math.Sin(phase + i) * 2;
            DrawPixel(dc, bobberX - width / 2, bobberY + 6 + i * 2, width, 1, i == 0 ? "#8EC4AF" : "#528E88");
            }
        }
        DrawPixel(dc, bobberX - 2, bobberY - 4, 4, 4, "#F6D67D");
        DrawPixel(dc, bobberX - 2, bobberY, 4, 5, "#D86654");
        DrawPixel(dc, bobberX - 1, bobberY + 4, 2, 2, "#F4E8C6");
    }

    private void DrawStageEffects(DrawingContext dc)
    {
        var home = Theme switch
        {
            PondTheme.B => new Point(302, 181),
            PondTheme.D => new Point(222, 188),
            PondTheme.FusionCE => new Point(298, 191),
            PondTheme.E => new Point(220, 190),
            PondTheme.C => new Point(220, 188),
            _ => new Point(222, 186)
        };
        var phase = _animationSeconds * 2.2;
        var activityVisible = DateTimeOffset.Now < _activityEndsAt;
        if (Stage is FishingStage.Biting or FishingStage.Fighting)
        {
            var activeMotion = activityVisible ? Math.Min(10, _activityScore / 18.0) : 0;
            var x = Stage == FishingStage.Fighting ? home.X - 25 + Math.Sin(phase) * (14 + activeMotion) : home.X - 25;
            var y = home.Y + 17 + Math.Cos(phase * 1.3) * 4;
            DrawFishSprite(dc, x, y, CatchFishId, 50, 30);
            if (activityVisible)
            {
                for (var i = 0; i < 3; i++)
                    DrawPixel(dc, x + 3 + i * 7, y - 10 - i % 2 * 4, 3, 2, "#E7D88C");
            }
        }
        else if (Stage == FishingStage.WaitingForApproval)
        {
            DrawPixel(dc, home.X + 4, home.Y - 24, 8, 9, "#F2D991");
            DrawPixel(dc, home.X + 7, home.Y - 22, 2, 3, "#3A3930");
            DrawPixel(dc, home.X + 7, home.Y - 17, 2, 2, "#3A3930");
        }
        else if (Stage == FishingStage.Catching)
        {
            var lift = 23 + Math.Abs(Math.Sin(phase)) * 13;
            var x = home.X - 25 + Math.Sin(phase * .7) * 8;
            var y = home.Y - lift;
            DrawFishSprite(dc, x, y, CatchFishId, 50, 30);
            for (var i = 0; i < 4; i++)
            {
                var sparkleX = home.X - 35 + i * 13;
                var sparkleY = home.Y - 49 + i % 2 * 11;
                DrawPixel(dc, sparkleX, sparkleY, 2, 7, "#F9E6A5");
                DrawPixel(dc, sparkleX - 2, sparkleY + 2, 6, 2, "#F9E6A5");
            }
        }
    }

    private void DrawEnvironmentEffects(DrawingContext dc)
    {
        var effectActive = ActiveEvent?.IsActive(DateTimeOffset.Now) == true;
        var season = Environment.Season;
        if (effectActive && ActiveEvent is { } activeEvent)
        {
            DrawEventParticles(dc, activeEvent.Definition.VisualEffect);
        }
        var count = effectActive ? 8 : season switch
        {
            PondSeason.Spring => 2,
            PondSeason.Summer => 2,
            PondSeason.Autumn => 2,
            _ => 3
        };
        for (var i = 0; i < count; i++)
        {
            var x = (42 + i * 67 + (season == PondSeason.Autumn ? _animationSeconds * 14 : 0)) % 480;
            var y = (18 + i * 29 + _animationSeconds * (i % 2 == 0 ? 8 : 3)) % 295;
            switch (season)
            {
                case PondSeason.Spring:
                    DrawPixel(dc, x, y, 3, 2, effectActive ? "#F2B4BB" : "#E69FA8");
                    DrawPixel(dc, x + 2, y + 1, 2, 2, "#F5D2B5");
                    break;
                case PondSeason.Summer:
                    var glow = ((int)(_animationSeconds * 1.5) + i) % 2 == 0;
                    DrawPixel(dc, x, y, glow ? 3 : 2, glow ? 3 : 2, glow ? "#E6E594" : "#C6D280");
                    break;
                case PondSeason.Autumn:
                    DrawPixel(dc, x, y, 4, 2, effectActive ? "#D4773E" : "#C99455");
                    DrawPixel(dc, x + 2, y + 2, 2, 2, "#B8703F");
                    break;
                default:
                    DrawPixel(dc, x, y, 2, 3, effectActive ? "#D8EAEB" : "#B8D5D8");
                    break;
            }
        }

        var rippleRemaining = (_rippleEndsAt - DateTimeOffset.Now).TotalSeconds;
        if (rippleRemaining > 0)
        {
            var progress = Math.Clamp(1 - rippleRemaining / 1.4, 0, 1);
            var radius = 4 + progress * 19;
            var pen = new Pen(Brush("#A5E4D5"), 1);
            dc.DrawEllipse(null, pen, _ripplePoint, radius, Math.Max(2, radius * .38));
            if (progress < .65)
                dc.DrawEllipse(null, new Pen(Brush("#71BDAE"), 1), _ripplePoint, radius * .55, Math.Max(2, radius * .22));
            if (progress > .3)
            {
                DrawPixel(dc, _ripplePoint.X + radius * .9, _ripplePoint.Y - radius * .35, 3, 3, "#173F48");
                DrawPixel(dc, _ripplePoint.X + radius * .9 + 3, _ripplePoint.Y - radius * .35 - 2, 4, 2, "#275B5D");
            }
        }
    }

    private void DrawEventParticles(DrawingContext dc, string effect)
    {
        for (var i = 0; i < 5; i++)
        {
            var x = (36 + i * 91 + _animationSeconds * (i % 2 == 0 ? 4 : -3) + 480) % 480;
            var y = (45 + i * 43 + Math.Sin(_animationSeconds + i) * 5 + 295) % 295;
            switch (effect)
            {
                case "dragonflies":
                    DrawPixel(dc, x, y, 5, 1, "#C5E9D3");
                    DrawPixel(dc, x + 2, y - 2, 2, 5, "#A7D6C5");
                    break;
                case "dew":
                    DrawPixel(dc, x, y, 2, 4, "#C8EEE3");
                    DrawPixel(dc, x + 1, y, 1, 2, "#EEF4D5");
                    break;
                case "reed-wind":
                    DrawPixel(dc, x, y, 7, 1, "#D9C28A");
                    DrawPixel(dc, x + 3, y + 2, 5, 1, "#AFBC83");
                    break;
                case "ice-glint":
                    DrawPixel(dc, x + 1, y - 2, 2, 7, "#D7EFF0");
                    DrawPixel(dc, x - 1, y, 6, 2, "#D7EFF0");
                    break;
            }
        }
    }

    private void DrawAccessories(DrawingContext dc)
    {
        var isFusion = Theme == PondTheme.FusionCE;
        if (EquippedHatId == "straw-hat")
        {
            var x = isFusion ? 103 : 111;
            var y = isFusion ? 131 : 117;
            DrawPixel(dc, x, y + 7, 21, 3, "#9A6735");
            DrawPixel(dc, x + 4, y, 13, 7, "#D8AE62");
            DrawPixel(dc, x + 6, y - 1, 10, 2, "#E7C77C");
        }
        else if (EquippedHatId == "moonlit-hat")
        {
            var x = isFusion ? 103 : 111;
            var y = isFusion ? 131 : 117;
            DrawPixel(dc, x + 2, y + 7, 20, 3, "#76869A");
            DrawPixel(dc, x + 7, y + 2, 12, 5, "#AFC7D0");
            DrawPixel(dc, x + 9, y, 8, 3, "#DDE7D5");
            DrawPixel(dc, x + 11, y + 2, 2, 2, "#F5E6A8");
        }
        if (LeftDecorId == "grass-block")
        {
            DrawPixel(dc, 18, 255, 20, 9, "#896343");
            DrawPixel(dc, 18, 251, 20, 5, "#668E4E");
            DrawPixel(dc, 20, 249, 7, 3, "#8AAF5A");
            DrawPixel(dc, 29, 249, 7, 3, "#749D50");
        }
        if (RightDecorId == "four-season-mobile")
        {
            DrawPixel(dc, 469, 157, 1, 16, "#A67942");
            DrawPixel(dc, 462, 160, 2, 2, "#E6A1A6");
            DrawPixel(dc, 468, 164, 2, 2, "#E4D48D");
            DrawPixel(dc, 474, 160, 2, 2, "#D47C4B");
            DrawPixel(dc, 468, 171, 2, 2, "#C7E2E3");
        }
        else if (RightDecorId is "spring-bloom" or "summer-shell" or "autumn-leaf" or "winter-star" or "firefly-jar")
        {
            var color = RightDecorId switch
            {
                "spring-bloom" => "#EDB1B3",
                "summer-shell" => "#DCCB8D",
                "autumn-leaf" => "#D1814C",
                "winter-star" => "#C6E2E3",
                _ => "#DDE78B"
            };
            DrawPixel(dc, 465, 170, 8, 8, "#765339");
            DrawPixel(dc, 467, 161, 4, 9, "#A67942");
            DrawPixel(dc, 465, 158, 8, 4, color);
            if (RightDecorId == "firefly-jar") DrawPixel(dc, 468, 165, 3, 3, "#F3EAA0");
        }
    }

    private static void DrawFishSprite(DrawingContext dc, double x, double y, string fishId, double width, double height)
    {
        var sprite = PondAssets.FishSprite(fishId);
        if (sprite is not null)
        {
            dc.DrawImage(sprite, new Rect(Math.Round(x), Math.Round(y), width, height));
            return;
        }
        DrawPixel(dc, x, y, width * .72, height * .45, "#D8AE62");
        DrawPixel(dc, x + width * .68, y + height * .16, width * .24, height * .32, "#9A6735");
        DrawPixel(dc, x - width * .12, y + height * .16, width * .18, height * .32, "#E7C77C");
    }

    private static void DrawPixel(DrawingContext dc, double x, double y, double width, double height, string color) =>
        dc.DrawRectangle(Brush(color), null,
            new Rect(Math.Round(x), Math.Round(y), Math.Max(1, Math.Ceiling(width)), Math.Max(1, Math.Ceiling(height))));

    private static Brush Brush(string hex)
    {
        if (BrushesByHex.TryGetValue(hex, out var brush)) return brush;
        var color = (Color)ColorConverter.ConvertFromString(hex);
        brush = new SolidColorBrush(color);
        brush.Freeze();
        BrushesByHex[hex] = brush;
        return brush;
    }
}
