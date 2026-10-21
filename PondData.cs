using System.Text.Json;

namespace CodexPond;

public enum FishingStage
{
    Idle,
    Casting,
    Biting,
    Fighting,
    WaitingForApproval,
    Catching,
    Retracting,
    ConnectionLost
}

public enum CodexSignal
{
    TurnStarted,
    ItemStarted,
    ItemCompleted,
    ApprovalRequested,
    ApprovalResolved,
    TurnSucceeded,
    TurnFailed,
    TurnInterrupted,
    Disconnected
}

public enum PondSeason { Spring, Summer, Autumn, Winter }
public enum DayPhase { Dawn, Day, Dusk, Night }
public enum PondWeather { Clear, Cloudy, Rain, Mist, Snow }
public enum FishRarity { Common, Uncommon, Rare }
public enum PondTheme { FusionCE, A, B, C, D, E }
public enum AccessorySlot { Hat, LeftDecor, RightDecor }
public enum PondMode { AgentPond, IndependentFishing }

public sealed class FishingStateMachine
{
    public FishingStage Stage { get; private set; } = FishingStage.Idle;

    public FishingStage Apply(CodexSignal signal)
    {
        Stage = signal switch
        {
            CodexSignal.TurnStarted => FishingStage.Casting,
            CodexSignal.ItemStarted when Stage != FishingStage.WaitingForApproval => FishingStage.Biting,
            CodexSignal.ItemCompleted when Stage != FishingStage.WaitingForApproval => FishingStage.Fighting,
            CodexSignal.ApprovalRequested => FishingStage.WaitingForApproval,
            CodexSignal.ApprovalResolved => FishingStage.Biting,
            CodexSignal.TurnSucceeded => FishingStage.Catching,
            CodexSignal.TurnFailed or CodexSignal.TurnInterrupted => FishingStage.Retracting,
            CodexSignal.Disconnected => FishingStage.ConnectionLost,
            _ => Stage
        };
        return Stage;
    }

    public FishingStage Reset()
    {
        Stage = FishingStage.Idle;
        return Stage;
    }
}

public sealed record PondEnvironment(PondSeason Season, DayPhase Phase, PondWeather Weather, DateTime LocalTime)
{
    public static PondEnvironment At(DateTime localTime)
    {
        var season = localTime.Month switch
        {
            >= 3 and <= 5 => PondSeason.Spring,
            >= 6 and <= 8 => PondSeason.Summer,
            >= 9 and <= 11 => PondSeason.Autumn,
            _ => PondSeason.Winter
        };
        var phase = localTime.Hour switch
        {
            >= 5 and <= 7 => DayPhase.Dawn,
            >= 8 and <= 16 => DayPhase.Day,
            >= 17 and <= 19 => DayPhase.Dusk,
            _ => DayPhase.Night
        };

        // Weather is intentionally local and simulated. It is stable for the same local date.
        var dateSeed = unchecked(localTime.Year * 397 + localTime.DayOfYear * 17 + localTime.Month * 31 + localTime.Day);
        var weather = (PondWeather)(Math.Abs(dateSeed % 5));
        if (season != PondSeason.Winter && weather == PondWeather.Snow) weather = PondWeather.Cloudy;
        if (season == PondSeason.Winter && weather == PondWeather.Rain) weather = PondWeather.Snow;
        return new PondEnvironment(season, phase, weather, localTime);
    }

    public string SeasonLabel => Season switch
    {
        PondSeason.Spring => "春",
        PondSeason.Summer => "夏",
        PondSeason.Autumn => "秋",
        _ => "冬"
    };

    public string PhaseLabel => Phase switch
    {
        DayPhase.Dawn => "黎明",
        DayPhase.Day => "白日",
        DayPhase.Dusk => "黄昏",
        _ => "夜晚"
    };
}

public sealed record PondEventDefinition(string Id, string Name, PondSeason Season, string LimitedFishId);
public sealed record PondEvent(PondEventDefinition Definition, DateTimeOffset StartedAt, DateTimeOffset EndsAt)
{
    public string Id => Definition.Id;
    public string Name => Definition.Name;
    public bool IsActive(DateTimeOffset now) => now >= StartedAt && now < EndsAt;
}

public static class PondEventCatalog
{
    public static IReadOnlyList<PondEventDefinition> All { get; } =
    [
        new("spring-petals", "樱草花瓣飘过水面", PondSeason.Spring, "petal-carp"),
        new("summer-fireflies", "萤火虫在芦苇间亮起", PondSeason.Summer, "firefly-koi"),
        new("autumn-leaves", "金色落叶旋进池塘", PondSeason.Autumn, "maple-carp"),
        new("winter-frost", "池边结起一圈薄霜", PondSeason.Winter, "frost-fish")
    ];
}

/// <summary>Visible-time scheduler; hidden time is paused and does not consume an event interval.</summary>
public sealed class PondEventScheduler
{
    private readonly Random _random;
    private DateTimeOffset _nextEventAt;
    private DateTimeOffset? _hiddenAt;
    private PondEvent? _activeEvent;

    public PondEventScheduler(Random? random = null, DateTimeOffset? startAt = null)
    {
        _random = random ?? Random.Shared;
        _nextEventAt = (startAt ?? DateTimeOffset.Now).AddMinutes(_random.Next(15, 31));
    }

    public PondEvent? ActiveEvent => _activeEvent;
    public DateTimeOffset NextEventAt => _nextEventAt;

    public PondEvent? Update(DateTimeOffset now, bool visible, PondSeason season)
    {
        if (!visible)
        {
            _hiddenAt ??= now;
            return _activeEvent;
        }

        if (_hiddenAt is { } hiddenAt)
        {
            var paused = now - hiddenAt;
            _nextEventAt += paused;
            if (_activeEvent is not null)
                _activeEvent = _activeEvent with { StartedAt = _activeEvent.StartedAt + paused, EndsAt = _activeEvent.EndsAt + paused };
            _hiddenAt = null;
        }

        if (_activeEvent is not null && now >= _activeEvent.EndsAt)
            _activeEvent = null;

        if (_activeEvent is null && now >= _nextEventAt)
        {
            var events = PondEventCatalog.All.Where(item => item.Season == season).ToArray();
            if (events.Length > 0)
            {
                var definition = events[_random.Next(events.Length)];
                var duration = TimeSpan.FromSeconds(_random.Next(60, 121));
                _activeEvent = new PondEvent(definition, now, now + duration);
                _nextEventAt = now.AddMinutes(_random.Next(15, 31));
            }
        }
        return _activeEvent;
    }
}

/// <summary>Counts anonymous input pulses only in memory while an Agent turn is active.</summary>
public sealed class FishingActivityTracker
{
    private static readonly TimeSpan MinPulseInterval = TimeSpan.FromMilliseconds(200);
    private DateTimeOffset? _lastPulseAt;

    public FishingActivityTracker(bool enabled = true) => Enabled = enabled;

    public bool Enabled { get; set; }
    public bool IsTurnActive { get; private set; }
    public int Score { get; private set; }

    public void BeginTurn()
    {
        Score = 0;
        _lastPulseAt = null;
        IsTurnActive = true;
    }

    public void Reset() { Score = 0; _lastPulseAt = null; }

    public void EndTurn() => IsTurnActive = false;

    public bool TryPulse(DateTimeOffset now)
    {
        if (!Enabled || !IsTurnActive || Score >= 180) return false;
        if (_lastPulseAt is { } last && now - last < MinPulseInterval) return false;
        _lastPulseAt = now;
        Score = Math.Min(180, Score + 1);
        return true;
    }
}

public sealed record FishSpecies(
    string Id,
    string Name,
    string Color,
    string Description,
    FishRarity Rarity = FishRarity.Common,
    IReadOnlyList<PondSeason>? Seasons = null,
    IReadOnlyList<DayPhase>? Phases = null,
    string? EventId = null)
{
    public bool IsEventFish => !string.IsNullOrWhiteSpace(EventId);
    public bool IsAvailable(PondEnvironment environment, PondEvent? pondEvent) =>
        (Seasons is null || Seasons.Contains(environment.Season)) &&
        (Phases is null || Phases.Contains(environment.Phase)) &&
        (EventId is null || string.Equals(pondEvent?.Id, EventId, StringComparison.Ordinal));
}

public sealed record FishCatchContext(PondEnvironment Environment, PondEvent? Event, int ActivityScore, DateTimeOffset CaughtAt);

public sealed record FishRecord(
    string TurnId,
    string FishId,
    string FishName,
    string Color,
    DateTimeOffset CaughtAt,
    FishRarity? Rarity = null,
    PondSeason? Season = null,
    string? EventId = null)
{
    public bool IsHistorical => Rarity is null;
}

public enum FishBookReadStatus { Empty, Loaded, Corrupt, Unreadable }

public sealed record FishBookReadResult(FishBookReadStatus Status, IReadOnlyList<FishRecord> Records, string? Error = null)
{
    public bool CanAppend => Status is FishBookReadStatus.Empty or FishBookReadStatus.Loaded;
}

public sealed class FishBookStorageException : IOException
{
    public FishBookStorageException(FishBookReadStatus status, string message, Exception? innerException = null)
        : base(message, innerException) => Status = status;

    public FishBookReadStatus Status { get; }
}

public static class FishCatalog
{
    private static readonly PondSeason[] AllSeasons = Enum.GetValues<PondSeason>();
    private static readonly DayPhase[] AllPhases = Enum.GetValues<DayPhase>();

    public static IReadOnlyList<FishSpecies> All { get; } =
    [
        new("sunny", "暖阳鲫", "#E7A84F", "池边最常见的小鱼，鳞片像落日。"),
        new("mint", "青团鱼", "#78B99C", "喜欢躲在浮萍下面。", FishRarity.Uncommon, [PondSeason.Spring, PondSeason.Summer], [DayPhase.Dawn, DayPhase.Day]),
        new("blue", "溪蓝鳑鲏", "#6DA6C0", "水面安静时才会出现。", FishRarity.Common, AllSeasons, [DayPhase.Dawn, DayPhase.Day, DayPhase.Dusk]),
        new("rose", "桃花鲤", "#D77B72", "春天池塘里的温柔访客。", FishRarity.Uncommon, [PondSeason.Spring]),
        new("moon", "月光银鱼", "#B8C9C1", "银色背鳍会映出月光。", FishRarity.Rare, AllSeasons, [DayPhase.Night]),
        new("gold", "小金鲤", "#E7C55E", "圆滚滚的好运气。", FishRarity.Rare, AllSeasons, [DayPhase.Dawn, DayPhase.Day]),
        new("violet", "紫苔鱼", "#9B8AB7", "常在石头和苔藓之间巡游。", FishRarity.Uncommon, AllSeasons, [DayPhase.Dusk, DayPhase.Night]),
        new("ember", "晚霞鲈", "#D9824D", "黄昏时会游到浅水边。", FishRarity.Uncommon, AllSeasons, [DayPhase.Dusk]),
        new("reed", "芦苇鳅", "#A7A36A", "在春夏的浅水和芦苇根间穿行。", FishRarity.Common, [PondSeason.Spring, PondSeason.Summer], [DayPhase.Dawn, DayPhase.Dusk]),
        new("cloud", "云纹小鲦", "#A9C6C2", "阴天时常在水面下留下一道浅影。", FishRarity.Common, AllSeasons, [DayPhase.Day, DayPhase.Dusk]),
        new("lantern", "灯影鳊", "#D2A06A", "喜欢在岸灯亮起后靠近木栈桥。", FishRarity.Uncommon, AllSeasons, [DayPhase.Dusk, DayPhase.Night]),
        new("crystal", "溪石斑", "#8AACA2", "躲在石缝间，背鳍带着青绿色。", FishRarity.Common, AllSeasons, AllPhases),
        new("winter-ice", "冰晶银鱼", "#B8D7E2", "冬日清晨才游到薄冰边缘。", FishRarity.Uncommon, [PondSeason.Winter], [DayPhase.Dawn, DayPhase.Night]),
        new("autumn-carp", "枫影鲤", "#C77E54", "入秋后会追着落叶缓缓游动。", FishRarity.Uncommon, [PondSeason.Autumn], [DayPhase.Day, DayPhase.Dusk]),
        new("lotus", "莲心鲫", "#E6B17E", "夏天喜欢围着莲叶打转。", FishRarity.Uncommon, [PondSeason.Summer], [DayPhase.Dawn, DayPhase.Day]),
        new("stone", "苔石鲶", "#7F9182", "不挑时辰，总在池底的石头旁等候。", FishRarity.Common, AllSeasons, AllPhases),
        new("petal-carp", "花瓣锦鲤", "#E8979C", "花瓣事件中才会循着香气出现。", FishRarity.Rare, [PondSeason.Spring], AllPhases, "spring-petals"),
        new("firefly-koi", "萤火金鳞", "#D9D780", "萤火闪烁时，金鳞会映出微光。", FishRarity.Rare, [PondSeason.Summer], [DayPhase.Dusk, DayPhase.Night], "summer-fireflies"),
        new("maple-carp", "枫叶鲤", "#D77843", "落叶旋涡中偶尔游出一尾红鲤。", FishRarity.Rare, [PondSeason.Autumn], AllPhases, "autumn-leaves"),
        new("frost-fish", "霜铃鱼", "#B7DCE3", "薄霜出现时才会靠近岸边。", FishRarity.Rare, [PondSeason.Winter], [DayPhase.Dawn, DayPhase.Night], "winter-frost")
    ];

    public static IReadOnlyList<PondTheme> Themes { get; } = Enum.GetValues<PondTheme>();

    public static FishSpecies Choose(FishCatchContext context, Random? random = null)
    {
        var rng = random ?? Random.Shared;
        var candidates = All.Where(item => item.IsAvailable(context.Environment, context.Event)).ToArray();
        if (candidates.Length == 0) candidates = All.Where(item => !item.IsEventFish && item.IsAvailable(context.Environment, null)).ToArray();
        if (candidates.Length == 0) candidates = All.Where(item => !item.IsEventFish).ToArray();

        var roll = rng.Next(100);
        var weights = RarityWeightsForActivity(context.ActivityScore);
        var cumulative = 0;
        var desired = FishRarity.Common;
        foreach (var weight in weights)
        {
            cumulative += weight.Item2;
            if (roll < cumulative) { desired = weight.Item1; break; }
        }

        var selected = candidates.Where(item => item.Rarity == desired).ToArray();
        if (selected.Length == 0)
        {
            var availableRarities = candidates.Select(item => item.Rarity).Distinct().ToArray();
            var availableWeight = weights.Where(item => availableRarities.Contains(item.Item1)).ToArray();
            var total = availableWeight.Sum(item => item.Item2);
            var adjustedRoll = rng.Next(Math.Max(1, total));
            cumulative = 0;
            var fallback = availableRarities[0];
            foreach (var weight in availableWeight)
            {
                cumulative += weight.Item2;
                if (adjustedRoll < cumulative) { fallback = weight.Item1; break; }
            }
            selected = candidates.Where(item => item.Rarity == fallback).ToArray();
        }
        return selected[rng.Next(selected.Length)];
    }

    public static IReadOnlyList<(FishRarity Rarity, int Weight)> RarityWeightsForActivity(int score) => score switch
    {
        >= 120 => [(FishRarity.Common, 45), (FishRarity.Uncommon, 40), (FishRarity.Rare, 15)],
        >= 60 => [(FishRarity.Common, 60), (FishRarity.Uncommon, 32), (FishRarity.Rare, 8)],
        _ => [(FishRarity.Common, 70), (FishRarity.Uncommon, 25), (FishRarity.Rare, 5)]
    };
}

public sealed record AccessoryDefinition(string Id, string Name, AccessorySlot Slot, string UnlockHint, string? RequiredSeason = null, string? RequiredEvent = null);

public static class AccessoryCatalog
{
    public static IReadOnlyList<AccessoryDefinition> All { get; } =
    [
        new("straw-hat", "草帽", AccessorySlot.Hat, "开局可用"),
        new("grass-block", "苔草方块", AccessorySlot.LeftDecor, "累计钓起 3 条鱼"),
        new("spring-bloom", "春花灯", AccessorySlot.RightDecor, "钓到第一条春季鱼", "Spring"),
        new("summer-shell", "夏贝风铃", AccessorySlot.RightDecor, "钓到第一条夏季鱼", "Summer"),
        new("autumn-leaf", "枫叶标牌", AccessorySlot.RightDecor, "钓到第一条秋季鱼", "Autumn"),
        new("winter-star", "雪晶挂饰", AccessorySlot.RightDecor, "钓到第一条冬季鱼", "Winter"),
        new("firefly-jar", "萤火玻璃罐", AccessorySlot.RightDecor, "钓到事件限定鱼", null, "event")
    ];

    public static string ForSeason(PondSeason season) => season switch
    {
        PondSeason.Spring => "spring-bloom",
        PondSeason.Summer => "summer-shell",
        PondSeason.Autumn => "autumn-leaf",
        _ => "winter-star"
    };
}

public static class AccessoryUnlockRules
{
    public static bool Apply(PondSettings settings, IReadOnlyList<FishRecord> catches)
    {
        settings.UnlockedAccessoryIds ??= ["straw-hat"];
        var changed = false;
        void Unlock(string id)
        {
            if (settings.UnlockedAccessoryIds.Contains(id, StringComparer.Ordinal)) return;
            settings.UnlockedAccessoryIds.Add(id);
            changed = true;
        }

        if (catches.Count >= 3) Unlock("grass-block");
        foreach (var record in catches)
        {
            if (record.Season is { } season) Unlock(ForSeason(season));
            if (record.EventId is not null) Unlock("firefly-jar");
        }
        return changed;
    }

    private static string ForSeason(PondSeason season) => AccessoryCatalog.ForSeason(season);
}

public static class PondThemeCatalog
{
    public static string Label(PondTheme theme) => theme switch
    {
        PondTheme.FusionCE => "C + E · 池畔融合（默认）",
        PondTheme.A => "A · 岸边木牌",
        PondTheme.B => "B · 木栈桥",
        PondTheme.C => "C · 轻量状态栏",
        PondTheme.D => "D · 林间角落",
        _ => "E · 掌机鱼塘"
    };
}

public sealed class PondSettings
{
    public string LastWorkspace { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public string CodexExecutable { get; set; } = "codex";
    public string DeepSeekExecutable { get; set; } = "dsh";
    public string ClaudeExecutable { get; set; } = "claude";
    public string AgentProviderId { get; set; } = AgentProviderIds.Codex;
    public Dictionary<string, string> ProviderSessions { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> ProviderSessionWorkspaces { get; set; } = new(StringComparer.Ordinal);
    public string WorkBuddyDashboardUrl { get; set; } = "";
    public PondMode LastMode { get; set; } = PondMode.AgentPond;
    public DateTimeOffset SettingsUpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool HasChosenWorkspace { get; set; }
    public string? LastThreadId { get; set; }
    public string? LastThreadWorkspace { get; set; }
    public double? PondLeft { get; set; }
    public double? PondTop { get; set; }
    public PondTheme Theme { get; set; } = PondTheme.FusionCE;
    public bool TrackInputActivity { get; set; } = true;
    public bool ActivityTrackingNoticeSeen { get; set; }
    public string EquippedHatId { get; set; } = "straw-hat";
    public string? LeftDecorId { get; set; }
    public string? RightDecorId { get; set; }
    public List<string> UnlockedAccessoryIds { get; set; } = ["straw-hat"];
}

public sealed class PondSettingsStore
{
    private readonly string _path;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public PondSettingsStore(string? dataDirectory = null)
    {
        var root = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexPond");
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "settings.json");
    }

    public PondSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var settings = JsonSerializer.Deserialize<PondSettings>(File.ReadAllText(_path)) ?? new PondSettings();
                if (!Enum.IsDefined(typeof(PondTheme), settings.Theme)) settings.Theme = PondTheme.FusionCE;
                if (!Enum.IsDefined(typeof(PondMode), settings.LastMode)) settings.LastMode = PondMode.AgentPond;
                settings.AgentProviderId ??= AgentProviderIds.Codex;
                if (!AgentProviderCatalog.Available.Any(provider => provider.Id == settings.AgentProviderId)) settings.AgentProviderId = AgentProviderIds.Codex;
                settings.ProviderSessions ??= new Dictionary<string, string>(StringComparer.Ordinal);
                settings.ProviderSessionWorkspaces ??= new Dictionary<string, string>(StringComparer.Ordinal);
                settings.WorkBuddyDashboardUrl ??= "";
                settings.UnlockedAccessoryIds ??= ["straw-hat"];
                if (!settings.UnlockedAccessoryIds.Contains("straw-hat", StringComparer.Ordinal))
                    settings.UnlockedAccessoryIds.Add("straw-hat");
                return settings;
            }
        }
        catch (JsonException) { }
        catch (IOException) { }
        return new PondSettings();
    }

    public void Save(PondSettings settings)
    {
        settings.SettingsUpdatedAt = DateTimeOffset.UtcNow;
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
            if (File.Exists(_path)) File.Replace(temp, _path, null, ignoreMetadataErrors: true);
            else File.Move(temp, _path);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

public sealed class FishBookStore
{
    private static readonly object ProcessGate = new();
    private static readonly Mutex FileMutex = new(false, @"Local\CodexPond.FishBook");
    private readonly string _path;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public FishBookStore(string? dataDirectory = null)
    {
        var root = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexPond");
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "fish-book.json");
    }

    public FishBookReadResult ReadResult()
    {
        try { _ = File.GetAttributes(_path); }
        catch (FileNotFoundException) { return new FishBookReadResult(FishBookReadStatus.Empty, []); }
        catch (DirectoryNotFoundException) { return new FishBookReadResult(FishBookReadStatus.Empty, []); }
        catch (UnauthorizedAccessException ex) { return new FishBookReadResult(FishBookReadStatus.Unreadable, [], $"没有读取鱼册的权限：{ex.Message}"); }
        catch (IOException ex) { return new FishBookReadResult(FishBookReadStatus.Unreadable, [], $"鱼册文件不可读：{ex.Message}"); }
        try
        {
            var records = JsonSerializer.Deserialize<List<FishRecord>>(File.ReadAllText(_path));
            if (records is null)
                return new FishBookReadResult(FishBookReadStatus.Corrupt, [], "鱼册内容不是有效的记录列表。");
            if (records.Any(record => record is null ||
                string.IsNullOrWhiteSpace(record.TurnId) || string.IsNullOrWhiteSpace(record.FishId) ||
                string.IsNullOrWhiteSpace(record.FishName) || string.IsNullOrWhiteSpace(record.Color)))
                return new FishBookReadResult(FishBookReadStatus.Corrupt, [], "鱼册中有缺少必要字段的记录；原文件已保留。");
            return new FishBookReadResult(records.Count == 0 ? FishBookReadStatus.Empty : FishBookReadStatus.Loaded, records);
        }
        catch (JsonException ex) { return new FishBookReadResult(FishBookReadStatus.Corrupt, [], $"鱼册 JSON 无法读取：{ex.Message}"); }
        catch (IOException ex) { return new FishBookReadResult(FishBookReadStatus.Unreadable, [], $"鱼册文件不可读：{ex.Message}"); }
        catch (UnauthorizedAccessException ex) { return new FishBookReadResult(FishBookReadStatus.Unreadable, [], $"没有读取鱼册的权限：{ex.Message}"); }
    }

    public IReadOnlyList<FishRecord> ReadAll()
    {
        var result = ReadResult();
        if (!result.CanAppend)
            throw new FishBookStorageException(result.Status, result.Error ?? "鱼册不可用。");
        return result.Records;
    }

    public bool TryCatchCompletedTurn(string? status, string turnId, out FishRecord? record) =>
        TryCatchCompletedTurn(status, turnId, new FishCatchContext(PondEnvironment.At(DateTime.Now), null, 0, DateTimeOffset.Now), out record);

    public bool TryCatchCompletedTurn(string? status, string turnId, FishCatchContext context, out FishRecord? record)
    {
        if (!string.Equals(status, "completed", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(turnId))
        {
            record = null;
            return false;
        }
        return TryCatch(turnId, context, out record);
    }

    public bool TryCatch(string turnId, out FishRecord? record) =>
        TryCatch(turnId, new FishCatchContext(PondEnvironment.At(DateTime.Now), null, 0, DateTimeOffset.Now), out record);

    public bool TryCatchManual(FishCatchContext context, out FishRecord? record) =>
        TryCatch($"manual:{Guid.NewGuid():N}", context, out record);

    public IReadOnlyList<FishRecord> MergeRemoteRecords(IEnumerable<FishRecord> remoteRecords)
    {
        lock (ProcessGate)
        {
            var hasMutex = false;
            try
            {
                try { hasMutex = FileMutex.WaitOne(TimeSpan.FromSeconds(5)); }
                catch (AbandonedMutexException) { hasMutex = true; }
                if (!hasMutex) throw new TimeoutException("等待鱼册文件锁超时。");
                var read = ReadResult();
                if (!read.CanAppend) throw new FishBookStorageException(read.Status, read.Error ?? "鱼册不可用，未合并云端记录。");
                var merged = read.Records.ToDictionary(record => record.TurnId, StringComparer.Ordinal);
                foreach (var record in remoteRecords)
                {
                    if (record is null || string.IsNullOrWhiteSpace(record.TurnId) || string.IsNullOrWhiteSpace(record.FishId) ||
                        string.IsNullOrWhiteSpace(record.FishName) || string.IsNullOrWhiteSpace(record.Color)) continue;
                    merged.TryAdd(record.TurnId, record);
                }
                var records = merged.Values.OrderByDescending(record => record.CaughtAt).ToList();
                WriteAll(records);
                return records;
            }
            finally
            {
                if (hasMutex) FileMutex.ReleaseMutex();
            }
        }
    }

    public bool TryCatch(string turnId, FishCatchContext context, out FishRecord? record)
    {
        lock (ProcessGate)
        {
            var hasMutex = false;
            try
            {
                try { hasMutex = FileMutex.WaitOne(TimeSpan.FromSeconds(5)); }
                catch (AbandonedMutexException) { hasMutex = true; }
                if (!hasMutex) throw new TimeoutException("等待鱼册文件锁超时。");

                var read = ReadResult();
                if (!read.CanAppend)
                    throw new FishBookStorageException(read.Status, read.Error ?? "鱼册不可用；为保护原文件，未写入新记录。");
                var catches = read.Records.ToList();
                record = catches.FirstOrDefault(x => string.Equals(x.TurnId, turnId, StringComparison.Ordinal));
                if (record is not null) return false;

                var species = FishCatalog.Choose(context);
                record = new FishRecord(turnId, species.Id, species.Name, species.Color, context.CaughtAt,
                    species.Rarity, context.Environment.Season, context.Event?.Id);
                catches.Insert(0, record);
                WriteAll(catches);
                return true;
            }
            finally
            {
                if (hasMutex) FileMutex.ReleaseMutex();
            }
        }
    }

    private void WriteAll(IReadOnlyList<FishRecord> catches)
    {
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(catches, JsonOptions));
            if (File.Exists(_path)) File.Replace(temp, _path, null, ignoreMetadataErrors: true);
            else File.Move(temp, _path);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
