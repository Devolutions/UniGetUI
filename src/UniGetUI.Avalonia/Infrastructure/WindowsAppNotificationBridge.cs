using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageOperations;

namespace UniGetUI.Avalonia.Infrastructure;

internal static class WindowsAppNotificationBridge
{
    private const string ScenarioDefault = "Default";
    private const string ScenarioUrgent = "Urgent";

    private static readonly object _registrationLock = new();
    private static bool _registrationAttempted;
    private static bool _isRegistered;
    private static object? _managerDefault;
    private static Type? _builderType;
    private static Type? _scenarioType;
    private static Type? _buttonType;
    private static MethodInfo? _removeByTagAsyncMethod;
    private static MethodInfo? _showMethod;

    // ── B2: action callback ────────────────────────────────────────────────
    /// <summary>Invoked on a thread-pool thread when a toast notification button is clicked.</summary>
    public static event Action<string>? NotificationActivated;

    private static readonly string[] _assemblyCandidates =
    {
        "Microsoft.Windows.AppNotifications",
        "Microsoft.WindowsAppSDK",
    };

    public static bool ShowProgress(AbstractOperation operation)
    {
        if (!EnsureRegistered())
            return false;

        try
        {
            string title = operation.Metadata.Title.Length > 0
                ? operation.Metadata.Title
                : CoreTools.Translate("Operation in progress");

            string message = operation.Metadata.Status.Length > 0
                ? operation.Metadata.Status
                : CoreTools.Translate("Please wait...");

            return ShowTextToast(
                tag: operation.Metadata.Identifier + "progress",
                scenario: ScenarioDefault,
                title: title,
                message: message,
                suppressDisplay: true);
        }
        catch (Exception ex)
        {
            Logger.Warn("Windows toast progress notification failed");
            Logger.Warn(ex);
            return false;
        }
    }

    public static bool ShowSuccess(AbstractOperation operation)
    {
        if (!EnsureRegistered())
            return false;

        try
        {
            string title = operation.Metadata.SuccessTitle.Length > 0
                ? operation.Metadata.SuccessTitle
                : CoreTools.Translate("Success!");

            string message = operation.Metadata.SuccessMessage.Length > 0
                ? operation.Metadata.SuccessMessage
                : CoreTools.Translate("Success!");

            return ShowTextToast(
                tag: operation.Metadata.Identifier,
                scenario: ScenarioDefault,
                title: title,
                message: message,
                suppressDisplay: false);
        }
        catch (Exception ex)
        {
            Logger.Warn("Windows toast success notification failed");
            Logger.Warn(ex);
            return false;
        }
    }

    public static bool ShowError(AbstractOperation operation)
    {
        if (!EnsureRegistered())
            return false;

        try
        {
            string title = operation.Metadata.FailureTitle.Length > 0
                ? operation.Metadata.FailureTitle
                : CoreTools.Translate("Failed");

            string message = operation.Metadata.FailureMessage.Length > 0
                ? operation.Metadata.FailureMessage
                : CoreTools.Translate("An error occurred while processing this package");

            return ShowTextToast(
                tag: operation.Metadata.Identifier,
                scenario: ScenarioUrgent,
                title: title,
                message: message,
                suppressDisplay: false);
        }
        catch (Exception ex)
        {
            Logger.Warn("Windows toast error notification failed");
            Logger.Warn(ex);
            return false;
        }
    }

    public static void RemoveProgress(AbstractOperation operation)
    {
        if (!EnsureRegistered())
            return;

        try
        {
            _removeByTagAsyncMethod?.Invoke(_managerDefault, [operation.Metadata.Identifier + "progress"]);
        }
        catch (Exception ex)
        {
            Logger.Warn("Windows toast progress cleanup failed");
            Logger.Warn(ex);
        }
    }

    // ── B2: updates-available notification ────────────────────────────────

    /// <summary>
    /// Shows a Windows toast notification listing available package updates, with action buttons
    /// ("Open UniGetUI" / "Update all").  No-op on non-Windows or when toast registration fails.
    /// </summary>
    public static void ShowUpdatesAvailableNotification(IReadOnlyList<IPackage> upgradable)
    {
        if (!EnsureRegistered()) return;
        if (Settings.AreUpdatesNotificationsDisabled()) return;

        bool sendNotification = upgradable.Any(p =>
            !Settings.GetDictionaryItem<string, bool>(
                Settings.K.DisabledPackageManagerNotifications, p.Manager.Name));
        if (!sendNotification) return;

        try
        {
            _removeByTagAsyncMethod?.Invoke(_managerDefault,
                [CoreData.UpdatesAvailableNotificationTag.ToString()]);

            if (upgradable.Count == 1)
            {
                ShowTextToast(
                    tag: CoreData.UpdatesAvailableNotificationTag.ToString(),
                    scenario: ScenarioDefault,
                    title: CoreTools.Translate("An update was found!"),
                    message: CoreTools.Translate("{0} can be updated to version {1}",
                        upgradable[0].Name, upgradable[0].NewVersionString),
                    suppressDisplay: false,
                    defaultAction: NotificationArguments.ShowOnUpdatesTab,
                    buttons:
                    [
                        (CoreTools.Translate("View on UniGetUI").Replace("'", "\u00b4"),
                            NotificationArguments.ShowOnUpdatesTab),
                        (CoreTools.Translate("Update").Replace("'", "\u00b4"),
                            NotificationArguments.UpdateAllPackages),
                    ]);
            }
            else
            {
                string attribution = string.Join(", ", upgradable
                    .Where(p => !Settings.GetDictionaryItem<string, bool>(
                        Settings.K.DisabledPackageManagerNotifications, p.Manager.Name))
                    .Select(p => p.Name));

                ShowTextToast(
                    tag: CoreData.UpdatesAvailableNotificationTag.ToString(),
                    scenario: ScenarioDefault,
                    title: CoreTools.Translate("Updates found!"),
                    message: CoreTools.Translate("{0} packages can be updated", upgradable.Count),
                    suppressDisplay: false,
                    defaultAction: NotificationArguments.ShowOnUpdatesTab,
                    buttons:
                    [
                        (CoreTools.Translate("Open UniGetUI").Replace("'", "\u00b4"),
                            NotificationArguments.ShowOnUpdatesTab),
                        (CoreTools.Translate("Update all").Replace("'", "\u00b4"),
                            NotificationArguments.UpdateAllPackages),
                    ]);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not show updates-available notification");
            Logger.Warn(ex);
        }
    }

    /// <summary>
    /// Shows a Windows toast offering a UniGetUI self-update with an "Update now" button.
    /// </summary>
    public static void ShowSelfUpdateAvailableNotification(string newVersion)
    {
        if (!EnsureRegistered()) return;
        try
        {
            _removeByTagAsyncMethod?.Invoke(_managerDefault,
                [CoreData.UniGetUICanBeUpdated.ToString()]);

            ShowTextToast(
                tag: CoreData.UniGetUICanBeUpdated.ToString(),
                scenario: ScenarioDefault,
                title: CoreTools.Translate("{0} can be updated to version {1}", "UniGetUI", newVersion),
                message: CoreTools.Translate("You have currently version {0} installed", CoreData.VersionName),
                suppressDisplay: false,
                defaultAction: NotificationArguments.Show,
                buttons:
                [
                    (CoreTools.Translate("Update now").Replace("'", "\u00b4"),
                        NotificationArguments.ReleaseSelfUpdateLock),
                ]);
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not show self-update notification");
            Logger.Warn(ex);
        }
    }

    /// <summary>
    /// Shows a Windows toast notification after new desktop shortcuts are detected.
    /// </summary>
    public static void ShowNewShortcutsNotification(IReadOnlyList<string> shortcuts)
    {
        if (!EnsureRegistered()) return;
        if (Settings.AreNotificationsDisabled()) return;

        try
        {
            _removeByTagAsyncMethod?.Invoke(_managerDefault,
                [CoreData.NewShortcutsNotificationTag.ToString()]);

            string title;
            string message;

            if (shortcuts.Count == 1)
            {
                title = CoreTools.Translate("Desktop shortcut created");
                message = CoreTools.Translate(
                    "UniGetUI has detected a new desktop shortcut that can be deleted automatically.")
                    + "\n" + shortcuts[0].Split('\\')[^1];
            }
            else
            {
                string names = string.Join(", ", shortcuts.Select(s => s.Split('\\')[^1]));
                title = CoreTools.Translate("{0} desktop shortcuts created", shortcuts.Count);
                message = CoreTools.Translate(
                    "UniGetUI has detected {0} new desktop shortcuts that can be deleted automatically.",
                    shortcuts.Count) + "\n" + names;
            }

            ShowTextToast(
                tag: CoreData.NewShortcutsNotificationTag.ToString(),
                scenario: ScenarioDefault,
                title: title,
                message: message,
                suppressDisplay: false,
                defaultAction: NotificationArguments.Show,
                buttons:
                [
                    (CoreTools.Translate("Open UniGetUI").Replace("'", "\u00b4"),
                        NotificationArguments.Show),
                ]);
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not show new shortcuts notification");
            Logger.Warn(ex);
        }
    }

    private static bool EnsureRegistered()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        lock (_registrationLock)
        {
            if (_registrationAttempted)
                return _isRegistered;

            _registrationAttempted = true;
            try
            {
                var managerType = ResolveType(
                    "Microsoft.Windows.AppNotifications.AppNotificationManager",
                    "Microsoft.Windows.AppNotifications.Builder.AppNotificationManager");
                _builderType = ResolveType(
                    "Microsoft.Windows.AppNotifications.Builder.AppNotificationBuilder");
                _scenarioType = ResolveType(
                    "Microsoft.Windows.AppNotifications.Builder.AppNotificationScenario");
                _buttonType = ResolveType(
                    "Microsoft.Windows.AppNotifications.Builder.AppNotificationButton");

                if (managerType is null || _builderType is null || _scenarioType is null)
                {
                    _isRegistered = false;
                    return _isRegistered;
                }

                _managerDefault = managerType.GetProperty("Default", BindingFlags.Public | BindingFlags.Static)
                    ?.GetValue(null);

                if (_managerDefault is null)
                {
                    _isRegistered = false;
                    return _isRegistered;
                }

                managerType.GetMethod("Register", BindingFlags.Public | BindingFlags.Instance)
                    ?.Invoke(_managerDefault, null);

                _removeByTagAsyncMethod = managerType.GetMethod(
                    "RemoveByTagAsync",
                    BindingFlags.Public | BindingFlags.Instance,
                    binder: null,
                    types: [typeof(string)],
                    modifiers: null);

                _showMethod = managerType.GetMethod("Show", BindingFlags.Public | BindingFlags.Instance);

                // ── B2: subscribe to NotificationInvoked via Expression lambda ──────
                var notifEventInfo = managerType.GetEvent("NotificationInvoked");
                if (notifEventInfo is not null)
                {
                    var handlerType = notifEventInfo.EventHandlerType!;
                    var invokeParams = handlerType.GetMethod("Invoke")!
                        .GetParameters()
                        .Select(p => Expression.Parameter(p.ParameterType, p.Name))
                        .ToArray();
                    var handlerMi = typeof(WindowsAppNotificationBridge)
                        .GetMethod(nameof(OnNotificationInvoked),
                            BindingFlags.Static | BindingFlags.NonPublic)!;
                    var body = Expression.Call(
                        handlerMi,
                        Expression.Convert(invokeParams[0], typeof(object)),
                        Expression.Convert(invokeParams[1], typeof(object)));
                    var lambda = Expression.Lambda(handlerType, body, invokeParams);
                    notifEventInfo.AddEventHandler(_managerDefault, lambda.Compile());
                }

                _isRegistered = _showMethod is not null;
            }
            catch (Exception ex)
            {
                Logger.Warn("Windows app notification registration failed in Avalonia host");
                Logger.Warn(ex);
                _isRegistered = false;
            }

            return _isRegistered;
        }
    }

    private static bool ShowTextToast(
        string tag,
        string scenario,
        string title,
        string message,
        bool suppressDisplay,
        string? defaultAction = null,
        IReadOnlyList<(string label, string action)>? buttons = null)
    {
        if (_managerDefault is null || _builderType is null || _scenarioType is null || _showMethod is null)
            return false;

        _removeByTagAsyncMethod?.Invoke(_managerDefault, [tag]);

        object builder = Activator.CreateInstance(_builderType)
            ?? throw new InvalidOperationException("Could not construct AppNotificationBuilder via reflection.");

        object? scenarioValue = Enum.Parse(_scenarioType, scenario, ignoreCase: true);

        builder = InvokeFluent(builder, "SetScenario", scenarioValue);
        builder = InvokeFluent(builder, "SetTag", tag);
        builder = InvokeFluent(builder, "AddText", title);
        builder = InvokeFluent(builder, "AddText", message);

        if (defaultAction is not null)
            builder = InvokeFluent(builder, "AddArgument", "action", defaultAction);

        // ── B2: add action buttons ─────────────────────────────────────────
        if (buttons is not null && _buttonType is not null)
        {
            foreach (var (label, action) in buttons)
            {
                try
                {
                    object? btn = Activator.CreateInstance(_buttonType, label);
                    if (btn is null) continue;
                    // btn.AddArgument("action", action) returns AppNotificationButton
                    btn = _buttonType
                        .GetMethod("AddArgument", BindingFlags.Public | BindingFlags.Instance,
                            null, [typeof(string), typeof(string)], null)
                        ?.Invoke(btn, ["action", action]) ?? btn;
                    builder = InvokeFluent(builder, "AddButton", btn);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Could not add notification button '{label}': {ex.Message}");
                }
            }
        }

        object? notification = builder
            .GetType()
            .GetMethod("BuildNotification", BindingFlags.Public | BindingFlags.Instance)
            ?.Invoke(builder, null);

        if (notification is null)
            return false;

        SetPropertyIfPresent(notification, "ExpiresOnReboot", true);
        SetPropertyIfPresent(notification, "SuppressDisplay", suppressDisplay);
        _showMethod.Invoke(_managerDefault, [notification]);
        return true;
    }

    private static object InvokeFluent(object instance, string methodName, params object?[] args)
    {
        return instance.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance)
            ?.Invoke(instance, args)
            ?? instance;
    }

    private static void SetPropertyIfPresent(object instance, string propertyName, object value)
    {
        instance.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)
            ?.SetValue(instance, value);
    }

    private static Type? ResolveType(params string[] possibleTypeNames)
    {
        foreach (var typeName in possibleTypeNames)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(typeName, throwOnError: false);
                if (t is not null)
                    return t;
            }

            foreach (var asmName in _assemblyCandidates)
            {
                try
                {
                    var asm = Assembly.Load(asmName);
                    var t = asm.GetType(typeName, throwOnError: false);
                    if (t is not null)
                        return t;
                }
                catch
                {
                    // Keep probing candidates.
                }
            }
        }

        return null;
    }

    // ── B2: notification activation handler ───────────────────────────────

    private static void OnNotificationInvoked(object _, object args)
    {
        try
        {
            var arguments = args.GetType()
                .GetProperty("Arguments", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(args) as IDictionary<string, string>;

            if (arguments?.TryGetValue("action", out var action) == true && action is not null)
            {
                NotificationActivated?.Invoke(action);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("NotificationInvoked handler error");
            Logger.Warn(ex);
        }
    }
}
