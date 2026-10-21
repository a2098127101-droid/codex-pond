using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace CodexPond;

public sealed record FishBookEntry(string FishName, string Description, string MetaLabel, ImageSource? Sprite, string TurnLabel, string CaughtAtLabel);

public partial class FishBookWindow : Window
{
    public FishBookWindow(Window owner, FishBookStore store)
    {
        InitializeComponent();
        Owner = owner;
        DataContext = this;
        Refresh(store);
    }

    public ObservableCollection<FishBookEntry> Catches { get; } = [];

    public void Refresh(FishBookStore store)
    {
        Catches.Clear();
        var read = store.ReadResult();
        if (!read.CanAppend)
        {
            CatchCountText.Text = "鱼册暂时无法读取";
            StorageWarningText.Text = read.Error ?? "原文件已保留；修复文件后可继续查看。";
            StorageWarningText.Visibility = Visibility.Visible;
            EmptyText.Visibility = Visibility.Collapsed;
            return;
        }
        StorageWarningText.Text = "";
        StorageWarningText.Visibility = Visibility.Collapsed;
        var species = FishCatalog.All.ToDictionary(item => item.Id, StringComparer.Ordinal);
        foreach (var record in read.Records)
        {
            var description = species.TryGetValue(record.FishId, out var item)
                ? item.Description
                : "池塘里的一位神秘来客。";
            var turn = record.TurnId.Length > 16 ? record.TurnId[..16] + "…" : record.TurnId;
            var rarity = record.Rarity switch
            {
                FishRarity.Common => "常见",
                FishRarity.Uncommon => "少见",
                FishRarity.Rare => "稀有",
                _ => "历史鱼获"
            };
            var season = record.Season switch
            {
                PondSeason.Spring => "春",
                PondSeason.Summer => "夏",
                PondSeason.Autumn => "秋",
                PondSeason.Winter => "冬",
                _ => "旧记录"
            };
            var eventLabel = record.EventId is null ? "" : " · 限时事件";
            Catches.Add(new FishBookEntry(
                record.FishName,
                description,
                $"{rarity} · {season}{eventLabel}",
                PondAssets.FishSprite(record.FishId),
                "turn · " + turn,
                record.CaughtAt.ToLocalTime().ToString("MM/dd HH:mm")));
        }
        CatchCountText.Text = $"已经收获 {Catches.Count} 条鱼";
        EmptyText.Visibility = Catches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Hide();

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); } catch (InvalidOperationException) { }
    }
}
