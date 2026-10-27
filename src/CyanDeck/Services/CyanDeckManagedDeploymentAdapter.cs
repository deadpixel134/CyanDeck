using System.Text.Json;
using CyanDeck.ManagedDeployment;
using CyanDeck.Models;
using CyanDeck.Automation;

namespace CyanDeck.Services;

/// <summary>
/// Explicit allowlist adapter between the generic managed-deployment engine and
/// CyanDeck's current settings model. Deliberately avoids reflection so a policy
/// file cannot reach arbitrary state properties.
/// </summary>
public sealed class CyanDeckManagedDeploymentAdapter : IManagedDeploymentAdapter
{
    private static readonly HashSet<string> SettingsAllowlist = new(StringComparer.Ordinal)
    {
        // The two *ActivationMode keys remain accepted and stored so existing policy files keep
        // validating, but they are compatibility-only: CyanDeck registers no global hotkeys,
        // so they have no runtime effect (see DeckSettings legacy hotkey comment).
        "General.PeekActivationMode",
        "General.DesktopDisplayActivationMode",
        "QuickHide.Enabled",
        "QuickHide.AlwaysShowIconsAtStartup",
        "General.QuickHideMode",
        "General.AutoImportDesktopItems",
        "General.CreateGroupMouseButtonMode",
        "Layout.UseSameSpacingInsideAndOutsideGroups",
        "Automation.UseDefaultDesktopPlacement",
        "Automation.DefaultDesktopPlacementKind",
        "Automation.DefaultDesktopPlacementGroupId",
        "Automation.DefaultDesktopPlacementTabId",
        "Automation.ActivateDefaultDestinationTab",
        "General.PageAnimationsEnabled",
        "General.PageAnimationDurationMs",
        "General.LayoutAdjustmentMode",
        "General.ManualSnapshotRetention",
        "General.AutoBackupRetentionDays",
        "General.PreserveMultiSelectionOnDrag",
        "General.HideInactiveScrollbars",
        "Performance.AsyncItemLoading",
        "Performance.AsyncItemThreshold",
        "Performance.IconCacheCapacity",
        "Safety.LargeRecycleConfirmThreshold",
        "Input.EnableTouchIconFlick",
        "Appearance.ShowTitlebarControlsInInactiveTabs",
        "Appearance.HideFileShortcutBadges",
        "Appearance.HideInternetShortcutBadges",
        "Appearance.ShowShortcutBadgesInFolderPortals",
        "Appearance.AnimateIconsWhenMoved",
        "Appearance.IconMovementAnimationDurationMs",
        "Performance.IconMovementAnimationMaxItems"
    };

    private static readonly HashSet<string> CapabilityAllowlist = new(StringComparer.Ordinal)
    {
        "Settings.EditGeneral",
        "Settings.EditGroups",
        "Layout.MoveResizeGroups",
        "Layout.CreateDeleteGroups",
        "Layout.SwapScreenContents",
        "Layout.IconSpacing.Edit",
        "Layout.RestoreSnapshots",
        "Automation.EditRules",
        "Appearance.Edit",
        "FolderPortal.Edit",
        "FolderPortal.CreateFromDesktopAltDrop",
        "ImportExport.Import",
        "Templates.Edit",
        "Input.TouchIconFlick"
    };

    private static readonly DeckSettings Defaults = new();
    private readonly Func<DeckState> _getState;
    private readonly Action _refreshManagedPolicyUi;
    private readonly Action _saveState;

    public CyanDeckManagedDeploymentAdapter(
        Func<DeckState> getState,
        Action refreshManagedPolicyUi,
        Action saveState)
    {
        _getState = getState ?? throw new ArgumentNullException(nameof(getState));
        _refreshManagedPolicyUi = refreshManagedPolicyUi ?? throw new ArgumentNullException(nameof(refreshManagedPolicyUi));
        _saveState = saveState ?? throw new ArgumentNullException(nameof(saveState));
    }

    public IReadOnlySet<string> KnownSettings => SettingsAllowlist;
    public IReadOnlySet<string> LockableSettings => SettingsAllowlist;
    public IReadOnlySet<string> KnownCapabilities => CapabilityAllowlist;

    public bool IsSettingAtDefault(string key)
        => string.Equals(CaptureSettingJson(key), CaptureDefaultSettingJson(key), StringComparison.Ordinal);

    public bool TryNormalizeSetting(string key, string rawJson, out string canonicalJson, out string? error)
    {
        canonicalJson = string.Empty;
        error = null;
        if (!SettingsAllowlist.Contains(key))
        {
            error = "unknown setting";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(rawJson, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4
            });
            var value = document.RootElement;
            canonicalJson = key switch
            {
                "General.PeekActivationMode" => NormalizeEnum<DeckHotkeyActivation>(value),
                "General.DesktopDisplayActivationMode" => NormalizeEnum<DeckHotkeyActivation>(value),
                "QuickHide.Enabled" => NormalizeBoolean(value),
                "QuickHide.AlwaysShowIconsAtStartup" => NormalizeBoolean(value),
                "General.QuickHideMode" => NormalizeEnum<DeckQuickHideMode>(value),
                "General.AutoImportDesktopItems" => NormalizeBoolean(value),
                "General.CreateGroupMouseButtonMode" => NormalizeEnum<DeckCreationMouseButtonMode>(value),
                "Layout.UseSameSpacingInsideAndOutsideGroups" => NormalizeBoolean(value),
                "Automation.UseDefaultDesktopPlacement" => NormalizeBoolean(value),
                "Automation.DefaultDesktopPlacementKind" => NormalizeEnum<DefaultDesktopPlacementKind>(value),
                "Automation.DefaultDesktopPlacementGroupId" => NormalizeNullableGuidString(value),
                "Automation.DefaultDesktopPlacementTabId" => NormalizeNullableGuidString(value),
                "Automation.ActivateDefaultDestinationTab" => NormalizeBoolean(value),
                "General.PageAnimationsEnabled" => NormalizeBoolean(value),
                "General.PageAnimationDurationMs" => NormalizeInteger(value, 80, 600),
                "General.LayoutAdjustmentMode" => NormalizeEnum<DeckLayoutAdjustmentMode>(value),
                "General.ManualSnapshotRetention" => NormalizeInteger(value, 1, 100),
                "General.AutoBackupRetentionDays" => NormalizeInteger(value, 1, 30),
                "General.PreserveMultiSelectionOnDrag" => NormalizeBoolean(value),
                "General.HideInactiveScrollbars" => NormalizeBoolean(value),
                "Performance.AsyncItemLoading" => NormalizeBoolean(value),
                "Performance.AsyncItemThreshold" => NormalizeInteger(value, 20, 500),
                "Performance.IconCacheCapacity" => NormalizeInteger(value, 128, 4096),
                "Safety.LargeRecycleConfirmThreshold" => NormalizeInteger(value, 5, 200),
                "Input.EnableTouchIconFlick" => NormalizeBoolean(value),
                "Appearance.ShowTitlebarControlsInInactiveTabs" => NormalizeBoolean(value),
                "Appearance.HideFileShortcutBadges" => NormalizeBoolean(value),
                "Appearance.HideInternetShortcutBadges" => NormalizeBoolean(value),
                "Appearance.ShowShortcutBadgesInFolderPortals" => NormalizeBoolean(value),
                "Appearance.AnimateIconsWhenMoved" => NormalizeBoolean(value),
                "Appearance.IconMovementAnimationDurationMs" => NormalizeInteger(value, 80, 300),
                "Performance.IconMovementAnimationMaxItems" => NormalizeInteger(value, 8, 256),
                _ => throw new InvalidOperationException("unknown setting")
            };
            return true;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentOutOfRangeException or InvalidOperationException)
        {
            error = ex.Message;
            canonicalJson = string.Empty;
            return false;
        }
    }

    public string CaptureSettingJson(string key) => CaptureSettingJson(_getState().Settings, key);

    public void ApplySettingJson(string key, string canonicalJson)
    {
        var settings = _getState().Settings;
        switch (key)
        {
            case "General.PeekActivationMode":
                settings.PeekActivation = ParseEnum<DeckHotkeyActivation>(canonicalJson);
                DiagnosticsLog.Info("Managed policy key General.PeekActivationMode is compatibility-only; CyanDeck registers no global hotkeys.");
                break;
            case "General.DesktopDisplayActivationMode":
                settings.DesktopDisplayActivation = ParseEnum<DeckHotkeyActivation>(canonicalJson);
                DiagnosticsLog.Info("Managed policy key General.DesktopDisplayActivationMode is compatibility-only; CyanDeck registers no global hotkeys.");
                break;
            case "QuickHide.Enabled":
                settings.EnableQuickHide = ParseBoolean(canonicalJson);
                break;
            case "QuickHide.AlwaysShowIconsAtStartup":
                settings.QuickHideAlwaysShowIconsAtStartup = ParseBoolean(canonicalJson);
                break;
            case "General.QuickHideMode":
                settings.QuickHideMode = ParseEnum<DeckQuickHideMode>(canonicalJson);
                break;
            case "General.AutoImportDesktopItems":
                settings.AutoImportDesktop = ParseBoolean(canonicalJson);
                break;
            case "General.CreateGroupMouseButtonMode":
                settings.CreateGroupMouseButtonMode = ParseEnum<DeckCreationMouseButtonMode>(canonicalJson);
                break;
            case "Layout.UseSameSpacingInsideAndOutsideGroups":
                settings.UseSameSpacingInsideAndOutsideGroups = ParseBoolean(canonicalJson);
                break;
            case "Automation.UseDefaultDesktopPlacement":
                settings.UseDefaultDesktopPlacement = ParseBoolean(canonicalJson);
                break;
            case "Automation.DefaultDesktopPlacementKind":
                settings.DefaultDesktopPlacementKind = ParseEnum<DefaultDesktopPlacementKind>(canonicalJson);
                break;
            case "Automation.DefaultDesktopPlacementGroupId":
                settings.DefaultDesktopPlacementGroupId = ParseNullableString(canonicalJson);
                break;
            case "Automation.DefaultDesktopPlacementTabId":
                settings.DefaultDesktopPlacementTabId = ParseNullableString(canonicalJson);
                break;
            case "Automation.ActivateDefaultDestinationTab":
                settings.ActivateDefaultDestinationTab = ParseBoolean(canonicalJson);
                break;
            case "General.PageAnimationsEnabled":
                settings.AnimatePageTransitions = ParseBoolean(canonicalJson);
                break;
            case "General.PageAnimationDurationMs":
                settings.PageTransitionDurationMs = ParseInteger(canonicalJson);
                break;
            case "General.LayoutAdjustmentMode":
                settings.LayoutAdjustmentMode = ParseEnum<DeckLayoutAdjustmentMode>(canonicalJson);
                break;
            case "General.ManualSnapshotRetention":
                settings.MaxLayoutSnapshots = ParseInteger(canonicalJson);
                break;
            case "General.AutoBackupRetentionDays":
                settings.AutomaticSnapshotRetentionDays = ParseInteger(canonicalJson);
                break;
            case "General.PreserveMultiSelectionOnDrag":
                settings.ProtectMultiSelectionDuringDrag = ParseBoolean(canonicalJson);
                break;
            case "General.HideInactiveScrollbars":
                settings.HideScrollbarsWhenInactive = ParseBoolean(canonicalJson);
                break;
            case "Performance.AsyncItemLoading":
                settings.EnableAsyncItemLoading = ParseBoolean(canonicalJson);
                break;
            case "Performance.AsyncItemThreshold":
                settings.AsyncItemLoadThreshold = ParseInteger(canonicalJson);
                break;
            case "Performance.IconCacheCapacity":
                settings.IconCacheMaxEntries = ParseInteger(canonicalJson);
                break;
            case "Safety.LargeRecycleConfirmThreshold":
                settings.LargeBatchOperationThreshold = ParseInteger(canonicalJson);
                break;
            case "Input.EnableTouchIconFlick":
                settings.EnableTouchIconFlick = ParseBoolean(canonicalJson);
                break;
            case "Appearance.ShowTitlebarControlsInInactiveTabs":
                settings.ShowTitlebarControlsInInactiveTabs = ParseBoolean(canonicalJson);
                break;
            case "Appearance.HideFileShortcutBadges":
                settings.HideFileShortcutBadges = ParseBoolean(canonicalJson);
                break;
            case "Appearance.HideInternetShortcutBadges":
                settings.HideInternetShortcutBadges = ParseBoolean(canonicalJson);
                break;
            case "Appearance.ShowShortcutBadgesInFolderPortals":
                settings.ShowShortcutBadgesInFolderPortals = ParseBoolean(canonicalJson);
                break;
            case "Appearance.AnimateIconsWhenMoved":
                settings.AnimateIconsWhenMoved = ParseBoolean(canonicalJson);
                break;
            case "Appearance.IconMovementAnimationDurationMs":
                settings.IconMovementAnimationDurationMs = ParseInteger(canonicalJson);
                break;
            case "Performance.IconMovementAnimationMaxItems":
                settings.IconMovementAnimationMaxItems = ParseInteger(canonicalJson);
                break;
            default:
                throw new InvalidOperationException($"Unknown managed setting: {key}");
        }
    }

    public void RefreshManagedPolicyUi() => _refreshManagedPolicyUi();
    public void SaveState() => _saveState();

    private static string CaptureDefaultSettingJson(string key) => CaptureSettingJson(Defaults, key);

    private static string CaptureSettingJson(DeckSettings settings, string key) => key switch
    {
        "General.PeekActivationMode" => JsonSerializer.Serialize(settings.PeekActivation.ToString()),
        "General.DesktopDisplayActivationMode" => JsonSerializer.Serialize(settings.DesktopDisplayActivation.ToString()),
        "QuickHide.Enabled" => JsonSerializer.Serialize(settings.EnableQuickHide),
        "QuickHide.AlwaysShowIconsAtStartup" => JsonSerializer.Serialize(settings.QuickHideAlwaysShowIconsAtStartup),
        "General.QuickHideMode" => JsonSerializer.Serialize(settings.QuickHideMode.ToString()),
        "General.AutoImportDesktopItems" => JsonSerializer.Serialize(settings.AutoImportDesktop),
        "General.CreateGroupMouseButtonMode" => JsonSerializer.Serialize(settings.CreateGroupMouseButtonMode.ToString()),
        "Layout.UseSameSpacingInsideAndOutsideGroups" => JsonSerializer.Serialize(settings.UseSameSpacingInsideAndOutsideGroups),
        "Automation.UseDefaultDesktopPlacement" => JsonSerializer.Serialize(settings.UseDefaultDesktopPlacement),
        "Automation.DefaultDesktopPlacementKind" => JsonSerializer.Serialize(settings.DefaultDesktopPlacementKind.ToString()),
        "Automation.DefaultDesktopPlacementGroupId" => JsonSerializer.Serialize(settings.DefaultDesktopPlacementGroupId),
        "Automation.DefaultDesktopPlacementTabId" => JsonSerializer.Serialize(settings.DefaultDesktopPlacementTabId),
        "Automation.ActivateDefaultDestinationTab" => JsonSerializer.Serialize(settings.ActivateDefaultDestinationTab),
        "General.PageAnimationsEnabled" => JsonSerializer.Serialize(settings.AnimatePageTransitions),
        "General.PageAnimationDurationMs" => JsonSerializer.Serialize(settings.PageTransitionDurationMs),
        "General.LayoutAdjustmentMode" => JsonSerializer.Serialize(settings.LayoutAdjustmentMode.ToString()),
        "General.ManualSnapshotRetention" => JsonSerializer.Serialize(settings.MaxLayoutSnapshots),
        "General.AutoBackupRetentionDays" => JsonSerializer.Serialize(settings.AutomaticSnapshotRetentionDays),
        "General.PreserveMultiSelectionOnDrag" => JsonSerializer.Serialize(settings.ProtectMultiSelectionDuringDrag),
        "General.HideInactiveScrollbars" => JsonSerializer.Serialize(settings.HideScrollbarsWhenInactive),
        "Performance.AsyncItemLoading" => JsonSerializer.Serialize(settings.EnableAsyncItemLoading),
        "Performance.AsyncItemThreshold" => JsonSerializer.Serialize(settings.AsyncItemLoadThreshold),
        "Performance.IconCacheCapacity" => JsonSerializer.Serialize(settings.IconCacheMaxEntries),
        "Safety.LargeRecycleConfirmThreshold" => JsonSerializer.Serialize(settings.LargeBatchOperationThreshold),
        "Input.EnableTouchIconFlick" => JsonSerializer.Serialize(settings.EnableTouchIconFlick),
        "Appearance.ShowTitlebarControlsInInactiveTabs" => JsonSerializer.Serialize(settings.ShowTitlebarControlsInInactiveTabs),
        "Appearance.HideFileShortcutBadges" => JsonSerializer.Serialize(settings.HideFileShortcutBadges),
        "Appearance.HideInternetShortcutBadges" => JsonSerializer.Serialize(settings.HideInternetShortcutBadges),
        "Appearance.ShowShortcutBadgesInFolderPortals" => JsonSerializer.Serialize(settings.ShowShortcutBadgesInFolderPortals),
        "Appearance.AnimateIconsWhenMoved" => JsonSerializer.Serialize(settings.AnimateIconsWhenMoved),
        "Appearance.IconMovementAnimationDurationMs" => JsonSerializer.Serialize(settings.IconMovementAnimationDurationMs),
        "Performance.IconMovementAnimationMaxItems" => JsonSerializer.Serialize(settings.IconMovementAnimationMaxItems),
        _ => throw new InvalidOperationException($"Unknown managed setting: {key}")
    };

    private static string NormalizeNullableGuidString(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return "null";
        if (value.ValueKind != JsonValueKind.String)
            throw new FormatException("value must be a GUID string or null");
        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text)) return "null";
        if (!Guid.TryParse(text, out var guid))
            throw new FormatException("value must be a GUID string or null");
        return JsonSerializer.Serialize(guid.ToString("D"));
    }

    private static string NormalizeBoolean(JsonElement value)
    {
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new FormatException("value must be true or false");
        return value.GetBoolean() ? "true" : "false";
    }

    private static string NormalizeInteger(JsonElement value, int minimum, int maximum)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
            throw new FormatException("value must be an integer");
        if (number < minimum || number > maximum)
            throw new ArgumentOutOfRangeException(nameof(value), $"value must be between {minimum} and {maximum}");
        return number.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string NormalizeEnum<TEnum>(JsonElement value) where TEnum : struct, Enum
    {
        if (value.ValueKind != JsonValueKind.String)
            throw new FormatException("value must be an enum name string");
        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text) || !Enum.TryParse<TEnum>(text, ignoreCase: true, out var parsed) || !Enum.IsDefined(typeof(TEnum), parsed))
            throw new FormatException($"unsupported {typeof(TEnum).Name} value");
        return JsonSerializer.Serialize(parsed.ToString());
    }

    private static string? ParseNullableString(string canonicalJson)
        => JsonSerializer.Deserialize<string?>(canonicalJson);

    private static bool ParseBoolean(string canonicalJson)
        => JsonSerializer.Deserialize<bool>(canonicalJson);

    private static int ParseInteger(string canonicalJson)
        => JsonSerializer.Deserialize<int>(canonicalJson);

    private static TEnum ParseEnum<TEnum>(string canonicalJson) where TEnum : struct, Enum
    {
        var text = JsonSerializer.Deserialize<string>(canonicalJson);
        if (string.IsNullOrWhiteSpace(text) || !Enum.TryParse<TEnum>(text, ignoreCase: false, out var parsed) || !Enum.IsDefined(typeof(TEnum), parsed))
            throw new FormatException($"Invalid canonical {typeof(TEnum).Name} value.");
        return parsed;
    }
}
