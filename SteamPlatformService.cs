using System.Text;
using System.Text.Json;
using Steamworks;

namespace CodexPond;

/// <summary>Steam cloud data is deliberately whitelisted. It contains only game progression and presentation settings.</summary>
public sealed record PondCloudSettings(
    DateTimeOffset UpdatedAt,
    PondTheme Theme,
    string EquippedHatId,
    string? LeftDecorId,
    string? RightDecorId,
    IReadOnlyList<string> UnlockedAccessoryIds,
    PondMode LastMode);

public sealed record PondCloudSave(int SchemaVersion, DateTimeOffset SavedAt, IReadOnlyList<FishRecord> Catches, PondCloudSettings Settings);

public interface ISteamPlatformService : IDisposable
{
    bool IsAvailable { get; }
    string Status { get; }
    void Initialize();
    void PumpCallbacks();
    void SyncLocalSave(PondSettings settings, PondSettingsStore settingsStore, FishBookStore fishBook);
    void OnFishCaught(int totalFish, int discoveredSpecies);
}

/// <summary>Steamworks.NET implementation. Native/API absence is a supported offline state.</summary>
public sealed class SteamPlatformService : ISteamPlatformService
{
    private const string CloudFileName = "codex-pond-save-v1.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private Callback<UserStatsReceived_t>? _statsReceived;
    private bool _initialized;
    private bool _statsReady;
    private int _pendingFishCount;
    private int _pendingSpeciesCount;
    private string _status = "Steam 未连接；本地存档正常使用。";

    public bool IsAvailable => _initialized;
    public string Status => _status;

    public void Initialize()
    {
        if (_initialized) return;
        try
        {
            if (!SteamAPI.IsSteamRunning())
            {
                _status = "Steam 未运行；使用本地存档。";
                return;
            }
            if (!SteamAPI.Init())
            {
                _status = "Steamworks 初始化未成功；使用本地存档。";
                return;
            }
            _initialized = true;
            _status = "Steamworks 已连接。";
            _statsReceived = Callback<UserStatsReceived_t>.Create(OnStatsReceived);
            SteamUserStats.RequestCurrentStats();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException)
        {
            _initialized = false;
            _status = "Steamworks 运行库不可用；本地存档正常使用。";
        }
    }

    private void OnStatsReceived(UserStatsReceived_t result)
    {
        _statsReady = result.m_eResult == EResult.k_EResultOK;
        _status = _statsReady ? "Steam 成就与云存档已就绪。" : "Steam 成就暂不可用；本地存档正常使用。";
        if (_statsReady && _pendingFishCount > 0) WriteProgressStats(_pendingFishCount, _pendingSpeciesCount);
    }

    public void PumpCallbacks()
    {
        if (!_initialized) return;
        try { SteamAPI.RunCallbacks(); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            _initialized = false;
            _status = "Steam 连接不可用；继续使用本地存档。";
        }
    }

    public void SyncLocalSave(PondSettings settings, PondSettingsStore settingsStore, FishBookStore fishBook)
    {
        if (!_initialized) return;
        try
        {
            if (!SteamRemoteStorage.IsCloudEnabledForAccount() || !SteamRemoteStorage.IsCloudEnabledForApp())
            {
                _status = "Steam 云存档未启用；使用本地存档。";
                return;
            }

            PondCloudSave? remote = TryReadCloudSave();
            var localRead = fishBook.ReadResult();
            if (!localRead.CanAppend)
            {
                _status = "本地鱼册需要检查；为保护原文件，跳过云端合并。";
                return;
            }
            if (remote is not null)
            {
                fishBook.MergeRemoteRecords(remote.Catches);
                if (remote.Settings.UpdatedAt > settings.SettingsUpdatedAt)
                {
                    ApplyCloudSettings(settings, remote.Settings);
                    settingsStore.Save(settings);
                }
            }

            var currentRead = fishBook.ReadResult();
            if (!currentRead.CanAppend) return;
            WriteCloudSave(new PondCloudSave(1, DateTimeOffset.UtcNow, currentRead.Records, CaptureCloudSettings(settings)));
            _status = "Steam 云存档已合并；鱼册按记录 ID 去重。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            _status = $"Steam 同步失败；本地数据仍保留。({ex.GetType().Name})";
        }
    }

    public void OnFishCaught(int totalFish, int discoveredSpecies)
    {
        _pendingFishCount = Math.Max(_pendingFishCount, totalFish);
        _pendingSpeciesCount = Math.Max(_pendingSpeciesCount, discoveredSpecies);
        if (!_initialized || !_statsReady) return;
        WriteProgressStats(_pendingFishCount, _pendingSpeciesCount);
    }

    private void WriteProgressStats(int totalFish, int discoveredSpecies)
    {
        try
        {
            SteamUserStats.SetStat("POND_FISH_CAUGHT", totalFish);
            SteamUserStats.SetStat("POND_SPECIES_DISCOVERED", discoveredSpecies);
            if (totalFish >= 1) SetAchievement("POND_FIRST_CATCH");
            if (totalFish >= 20) SetAchievement("POND_CATCH_20");
            if (discoveredSpecies >= FishCatalog.All.Count) SetAchievement("POND_ALL_FISH");
            SteamUserStats.StoreStats();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            _status = "Steam 统计暂不可用；本地鱼册仍已保存。";
        }
    }

    private static void SetAchievement(string achievementId)
    {
        if (SteamUserStats.GetAchievement(achievementId, out var achieved) && !achieved)
            SteamUserStats.SetAchievement(achievementId);
    }

    private static PondCloudSettings CaptureCloudSettings(PondSettings settings) => new(
        settings.SettingsUpdatedAt,
        settings.Theme,
        settings.EquippedHatId,
        settings.LeftDecorId,
        settings.RightDecorId,
        settings.UnlockedAccessoryIds.Distinct(StringComparer.Ordinal).ToArray(),
        settings.LastMode);

    internal static void ApplyCloudSettings(PondSettings local, PondCloudSettings cloud)
    {
        if (Enum.IsDefined(typeof(PondTheme), cloud.Theme)) local.Theme = cloud.Theme;
        local.EquippedHatId = cloud.EquippedHatId ?? "straw-hat";
        local.LeftDecorId = cloud.LeftDecorId;
        local.RightDecorId = cloud.RightDecorId;
        local.UnlockedAccessoryIds = cloud.UnlockedAccessoryIds?.Distinct(StringComparer.Ordinal).ToList() ?? ["straw-hat"];
        if (!local.UnlockedAccessoryIds.Contains("straw-hat", StringComparer.Ordinal)) local.UnlockedAccessoryIds.Add("straw-hat");
        if (Enum.IsDefined(typeof(PondMode), cloud.LastMode)) local.LastMode = cloud.LastMode;
        local.SettingsUpdatedAt = cloud.UpdatedAt;
    }

    internal static void ApplyCloudSettingsForTest(PondSettings local, PondCloudSettings cloud) => ApplyCloudSettings(local, cloud);

    private static PondCloudSave? TryReadCloudSave()
    {
        if (!SteamRemoteStorage.FileExists(CloudFileName)) return null;
        var size = SteamRemoteStorage.GetFileSize(CloudFileName);
        if (size <= 0 || size > 8 * 1024 * 1024) return null;
        var bytes = new byte[size];
        var read = SteamRemoteStorage.FileRead(CloudFileName, bytes, size);
        if (read <= 0) return null;
        var save = JsonSerializer.Deserialize<PondCloudSave>(Encoding.UTF8.GetString(bytes, 0, read), JsonOptions);
        return save?.SchemaVersion == 1 ? save : null;
    }

    private static void WriteCloudSave(PondCloudSave save)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(save, JsonOptions));
        if (!SteamRemoteStorage.FileWrite(CloudFileName, bytes, bytes.Length))
            throw new IOException("SteamRemoteStorage.FileWrite 返回失败。");
    }

    public void Dispose()
    {
        _statsReceived?.Dispose();
        _statsReceived = null;
        if (!_initialized) return;
        try { SteamAPI.Shutdown(); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException) { }
        _initialized = false;
    }
}

/// <summary>Deterministic in-memory platform fake used by the smoke harness.</summary>
public sealed class FakeSteamPlatformService : ISteamPlatformService
{
    private readonly bool _availableOnInitialize;
    public FakeSteamPlatformService(bool availableOnInitialize = true, PondCloudSave? cloudSave = null)
    {
        _availableOnInitialize = availableOnInitialize;
        CloudSave = cloudSave;
        Status = availableOnInitialize ? "模拟 Steam 未初始化" : "离线本地模式";
    }

    public bool IsAvailable { get; private set; }
    public string Status { get; private set; }
    public PondCloudSave? CloudSave { get; set; }
    public HashSet<string> Achievements { get; } = new(StringComparer.Ordinal);
    public void Initialize()
    {
        IsAvailable = _availableOnInitialize;
        Status = IsAvailable ? "模拟 Steam 已连接" : "离线本地模式";
    }
    public void PumpCallbacks() { }
    public void SyncLocalSave(PondSettings settings, PondSettingsStore settingsStore, FishBookStore fishBook)
    {
        if (!IsAvailable) { Status = "离线本地模式；云同步未执行"; return; }
        var read = fishBook.ReadResult();
        if (!read.CanAppend) return;
        if (CloudSave is not null)
        {
            fishBook.MergeRemoteRecords(CloudSave.Catches);
            if (CloudSave.Settings.UpdatedAt > settings.SettingsUpdatedAt)
            {
                SteamPlatformService.ApplyCloudSettingsForTest(settings, CloudSave.Settings);
                settingsStore.Save(settings);
            }
        }
        var finalRead = fishBook.ReadResult();
        if (finalRead.CanAppend)
            CloudSave = new PondCloudSave(1, DateTimeOffset.UtcNow, finalRead.Records,
                new PondCloudSettings(settings.SettingsUpdatedAt, settings.Theme, settings.EquippedHatId, settings.LeftDecorId,
                    settings.RightDecorId, settings.UnlockedAccessoryIds.ToArray(), settings.LastMode));
        Status = "模拟云存档已合并";
    }
    public void OnFishCaught(int totalFish, int discoveredSpecies)
    {
        if (totalFish >= 1) Achievements.Add("POND_FIRST_CATCH");
        if (totalFish >= 20) Achievements.Add("POND_CATCH_20");
        if (discoveredSpecies >= FishCatalog.All.Count) Achievements.Add("POND_ALL_FISH");
    }
    public void Dispose() => IsAvailable = false;
}
