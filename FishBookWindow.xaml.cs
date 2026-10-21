using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CodexPond;

public sealed record FishBookEntry(string FishName, string Description, string MetaLabel, ImageSource? Sprite, string StateLabel, string CaughtAtLabel);
public sealed record FishCatchEntry(string FishName, string Description, string MetaLabel, ImageSource? Sprite, string TurnLabel, string CaughtAtLabel);

public partial class FishBookWindow : Window
{
    private readonly FishBookStore _store;

    public FishBookWindow(Window owner, FishBookStore store)
    {
        _store = store;
        InitializeComponent();
        Owner = owner;
        DataContext = this;
        Refresh(store);
    }

    public ObservableCollection<FishBookEntry> FishEntries { get; } = [];
    public ObservableCollection<FishCatchEntry> Catches { get; } = [];

    public void Refresh(FishBookStore store)
    {
        var read = store.ReadResult();
        FishEntries.Clear();
        Catches.Clear();
        StorageWarningText.Visibility = read.CanAppend ? Visibility.Collapsed : Visibility.Visible;
        StorageWarningText.Text = read.CanAppend ? "" : read.Error ?? "原文件已保留；修复文件后可继续查看。";
        if (!read.CanAppend)
        {
            CatchCountText.Text = "鱼册暂时无法读取";
            return;
        }

        var discoveries = FishDiscoveryCatalog.Build(read.Records);
        var discoveredCount = discoveries.Count(entry => entry.IsDiscovered);
        CatchCountText.Text = $"已发现 {discoveredCount} / {FishCatalog.All.Count} 种 · 收获 {read.Records.Count} 条";
        var filter = (FilterPicker.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
        foreach (var entry in discoveries.Where(entry => FishDiscoveryCatalog.MatchesFilter(entry, filter)))
        {
            var species = entry.Species;
            var seasonMeta = string.Join("、", species.Seasons?.Select(SeasonLabel) ?? Enum.GetValues<PondSeason>().Select(SeasonLabel));
            var timeMeta = string.Join("、", species.Phases?.Select(PhaseLabel) ?? Enum.GetValues<DayPhase>().Select(PhaseLabel));
            var rarity = species.Rarity switch { FishRarity.Common => "常见", FishRarity.Uncommon => "少见", _ => "稀有" };
            var displayName = entry.IsDiscovered ? species.Name : "？？？";
            var meta = entry.IsDiscovered
                ? $"{rarity} · 已钓 {entry.CatchCount} 次 · {seasonMeta} · {timeMeta}" + (species.IsEventFish ? " · 限定事件" : "")
                : $"未发现 · {seasonMeta} · {timeMeta}" + (species.IsEventFish ? " · 限定事件" : "");
            FishEntries.Add(new FishBookEntry(
                displayName,
                entry.IsDiscovered ? species.Description : entry.Clue,
                meta,
                entry.IsDiscovered ? PondAssets.FishSprite(species.Id) : PondAssets.FishSilhouette(species.Id),
                entry.IsDiscovered ? "已记录" : "待发现",
                entry.LatestCatch?.CaughtAt.ToLocalTime().ToString("MM/dd HH:mm") ?? ""));
        }

        var speciesById = FishCatalog.All.ToDictionary(item => item.Id, StringComparer.Ordinal);
        foreach (var record in read.Records.OrderByDescending(item => item.CaughtAt))
        {
            speciesById.TryGetValue(record.FishId, out var species);
            var rarityLabel = record.Rarity switch { FishRarity.Common => "常见", FishRarity.Uncommon => "少见", FishRarity.Rare => "稀有", _ => "历史鱼获" };
            var seasonLabel = record.Season is { } season ? SeasonLabel(season) : "旧记录";
            Catches.Add(new FishCatchEntry(record.FishName, species?.Description ?? "池塘里的一位神秘来客。",
                $"{rarityLabel} · {seasonLabel}" + (record.EventId is null ? "" : " · 事件记录"),
                PondAssets.FishSprite(record.FishId),
                record.TurnId.Length > 16 ? "turn · " + record.TurnId[..16] + "…" : "turn · " + record.TurnId,
                record.CaughtAt.ToLocalTime().ToString("MM/dd HH:mm")));
        }
    }

    private static string SeasonLabel(PondSeason value) => value switch { PondSeason.Spring => "春", PondSeason.Summer => "夏", PondSeason.Autumn => "秋", _ => "冬" };
    private static string PhaseLabel(DayPhase value) => value switch { DayPhase.Dawn => "黎明", DayPhase.Day => "白天", DayPhase.Dusk => "黄昏", _ => "夜晚" };
    private void FilterPicker_SelectionChanged(object sender, SelectionChangedEventArgs e) => Refresh(_store);
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Hide();

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); } catch (InvalidOperationException) { }
    }
}
