using System.Text.Json;
using System.Text.Json.Nodes;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;

namespace UniGetUI.Interface;

public enum IpcCliExitCode
{
    Success = 0,
    Failed = 1,
    InvalidParameter = 2,
    IpcUnavailable = 3,
    UnknownCommand = 4,
}

public static class IpcCliCommandRunner
{
    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        TextWriter output,
        TextWriter error
    )
    {
        IpcCliParseResult parseResult = IpcCliSyntax.Parse(args);
        if (parseResult.Status == IpcCliParseStatus.Help)
        {
            await output.WriteLineAsync(IpcCliSyntax.GetHelpText());
            return (int)IpcCliExitCode.Success;
        }

        if (
            parseResult.Status != IpcCliParseStatus.Success
            || parseResult.Command is null
            || parseResult.EffectiveArgs is null
        )
        {
            return await WriteErrorAsync(
                output,
                parseResult.Message ?? "A valid command was not provided.",
                IpcCliExitCode.InvalidParameter
            );
        }

        args = parseResult.EffectiveArgs;
        string subcommand = parseResult.Command.Trim().ToLowerInvariant();

        try
        {
            using var client = IpcClient.CreateForCli(args);
            return subcommand switch
            {
                "status" => await WriteJsonAsync(output, await client.GetStatusAsync()),
                "get-app-state" => await WriteWrappedJsonAsync(
                    output,
                    "app",
                    await client.GetAppInfoAsync()
                ),
                "show-app" => await WriteJsonAsync(output, await client.ShowAppAsync()),
                "navigate-app" => await WriteJsonAsync(
                    output,
                    await client.NavigateAppAsync(BuildAppNavigateRequest(args))
                ),
                "quit-app" => await WriteJsonAsync(output, await client.QuitAppAsync()),
                "list-operations" => await WriteWrappedJsonAsync(
                    output,
                    "operations",
                    await client.ListOperationsAsync()
                ),
                "get-operation" => await WriteWrappedJsonAsync(
                    output,
                    "operation",
                    await client.GetOperationAsync(
                        GetRequiredArgument(
                            args,
                            "--operation-id",
                            "operation get requires --id."
                        )
                    )
                ),
                "get-operation-output" => await WriteWrappedJsonAsync(
                    output,
                    "output",
                    await client.GetOperationOutputAsync(
                        GetRequiredArgument(
                            args,
                            "--operation-id",
                            "operation output requires --id."
                        ),
                        GetOptionalIntArgument(args, "--tail")
                    )
                ),
                "wait-operation" => await WriteWrappedJsonAsync(
                    output,
                    "operation",
                    await client.WaitForOperationAsync(
                        GetRequiredArgument(
                            args,
                            "--operation-id",
                            "operation wait requires --id."
                        ),
                        GetOptionalIntArgument(args, "--timeout") ?? 300,
                        ((GetOptionalIntArgument(args, "--delay") ?? 1) * 1000)
                    )
                ),
                "cancel-operation" => await WriteJsonAsync(
                    output,
                    await client.CancelOperationAsync(
                        GetRequiredArgument(
                            args,
                            "--operation-id",
                            "operation cancel requires --id."
                        )
                    )
                ),
                "retry-operation" => await WriteJsonAsync(
                    output,
                    await client.RetryOperationAsync(
                        GetRequiredArgument(
                            args,
                            "--operation-id",
                            "operation retry requires --id."
                        ),
                        GetOptionalArgument(args, "--mode")
                    )
                ),
                "reorder-operation" => await WriteJsonAsync(
                    output,
                    await client.ReorderOperationAsync(
                        GetRequiredArgument(
                            args,
                            "--operation-id",
                            "operation reorder requires --id."
                        ),
                        GetRequiredArgument(
                            args,
                            "--action",
                            "operation reorder requires --action."
                        )
                    )
                ),
                "forget-operation" => await WriteJsonAsync(
                    output,
                    await client.ForgetOperationAsync(
                        GetRequiredArgument(
                            args,
                            "--operation-id",
                            "operation forget requires --id."
                        )
                    )
                ),
                "list-managers" => await WriteWrappedJsonAsync(
                    output,
                    "managers",
                    await client.ListManagersAsync()
                ),
                "get-manager-maintenance" => await WriteWrappedJsonAsync(
                    output,
                    "maintenance",
                    await client.GetManagerMaintenanceAsync(
                        GetRequiredArgument(
                            args,
                            "--manager",
                            "manager maintenance requires --manager."
                        )
                    )
                ),
                "reload-manager" => await WriteJsonAsync(
                    output,
                    await client.ReloadManagerAsync(BuildManagerMaintenanceRequest(args))
                ),
                "set-manager-executable" => await WriteJsonAsync(
                    output,
                    await client.SetManagerExecutablePathAsync(
                        BuildManagerMaintenanceRequest(args, requirePath: true)
                    )
                ),
                "clear-manager-executable" => await WriteJsonAsync(
                    output,
                    await client.ClearManagerExecutablePathAsync(BuildManagerMaintenanceRequest(args))
                ),
                "run-manager-action" => await WriteJsonAsync(
                    output,
                    await client.RunManagerActionAsync(
                        BuildManagerMaintenanceRequest(args, requireAction: true)
                    )
                ),
                "list-sources" => await WriteWrappedJsonAsync(
                    output,
                    "sources",
                    await client.ListSourcesAsync(GetOptionalArgument(args, "--manager"))
                ),
                "add-source" => await WriteJsonAsync(
                    output,
                    await client.AddSourceAsync(BuildSourceRequest(args))
                ),
                "remove-source" => await WriteJsonAsync(
                    output,
                    await client.RemoveSourceAsync(BuildSourceRequest(args))
                ),
                "list-settings" => await WriteWrappedJsonAsync(
                    output,
                    "settings",
                    await client.ListSettingsAsync()
                ),
                "list-secure-settings" => await WriteWrappedJsonAsync(
                    output,
                    "settings",
                    await client.ListSecureSettingsAsync(GetOptionalArgument(args, "--user"))
                ),
                "get-secure-setting" => await WriteWrappedJsonAsync(
                    output,
                    "setting",
                    await client.GetSecureSettingAsync(
                        GetRequiredArgument(
                            args,
                            "--key",
                            "settings secure get requires --key."
                        ),
                        GetOptionalArgument(args, "--user")
                    )
                ),
                "set-secure-setting" => await WriteWrappedJsonAsync(
                    output,
                    "setting",
                    await client.SetSecureSettingAsync(BuildSecureSettingRequest(args))
                ),
                "get-setting" => await WriteWrappedJsonAsync(
                    output,
                    "setting",
                    await client.GetSettingAsync(
                        GetRequiredArgument(
                            args,
                            "--key",
                            "settings get requires --key."
                        )
                    )
                ),
                "set-setting" => await WriteWrappedJsonAsync(
                    output,
                    "setting",
                    await client.SetSettingAsync(BuildSettingRequest(args))
                ),
                "clear-setting" => await WriteWrappedJsonAsync(
                    output,
                    "setting",
                    await client.ClearSettingAsync(
                        GetRequiredArgument(
                            args,
                            "--key",
                            "settings clear requires --key."
                        )
                    )
                ),
                "set-manager-enabled" => await WriteWrappedJsonAsync(
                    output,
                    "manager",
                    await client.SetManagerEnabledAsync(BuildManagerToggleRequest(args))
                ),
                "set-manager-update-notifications" => await WriteWrappedJsonAsync(
                    output,
                    "manager",
                    await client.SetManagerUpdateNotificationsAsync(
                        BuildManagerToggleRequest(args)
                    )
                ),
                "reset-settings" => await WriteJsonAsync(
                    output,
                    await client.ResetSettingsAsync()
                ),
                "list-desktop-shortcuts" => await WriteWrappedJsonAsync(
                    output,
                    "shortcuts",
                    await client.ListDesktopShortcutsAsync()
                ),
                "set-desktop-shortcut" => await WriteJsonAsync(
                    output,
                    await client.SetDesktopShortcutAsync(BuildDesktopShortcutRequest(args, requireStatus: true))
                ),
                "reset-desktop-shortcut" => await WriteJsonAsync(
                    output,
                    await client.ResetDesktopShortcutAsync(
                        GetRequiredArgument(
                            args,
                            "--path",
                            "shortcut reset requires --path."
                        )
                    )
                ),
                "reset-desktop-shortcuts" => await WriteJsonAsync(
                    output,
                    await client.ResetDesktopShortcutsAsync()
                ),
                "get-app-log" => await WriteWrappedJsonAsync(
                    output,
                    "entries",
                    await client.GetAppLogAsync(GetOptionalIntArgument(args, "--level") ?? 4)
                ),
                "get-operation-history" => await WriteWrappedJsonAsync(
                    output,
                    "history",
                    await client.GetOperationHistoryAsync()
                ),
                "get-manager-log" => await WriteWrappedJsonAsync(
                    output,
                    "managers",
                    await client.GetManagerLogAsync(
                        GetOptionalArgument(args, "--manager"),
                        args.Contains("--verbose")
                    )
                ),
                "get-backup-status" => await WriteWrappedJsonAsync(
                    output,
                    "backup",
                    await client.GetBackupStatusAsync()
                ),
                "create-local-backup" => await WriteJsonAsync(
                    output,
                    await client.CreateLocalBackupAsync()
                ),
                "start-github-sign-in" => await WriteJsonAsync(
                    output,
                    await client.StartGitHubDeviceFlowAsync(BuildGitHubDeviceFlowRequest(args))
                ),
                "complete-github-sign-in" => await WriteJsonAsync(
                    output,
                    await client.CompleteGitHubDeviceFlowAsync()
                ),
                "sign-out-github" => await WriteJsonAsync(
                    output,
                    await client.SignOutGitHubAsync()
                ),
                "list-cloud-backups" => await WriteWrappedJsonAsync(
                    output,
                    "backups",
                    await client.ListCloudBackupsAsync()
                ),
                "create-cloud-backup" => await WriteJsonAsync(
                    output,
                    await client.CreateCloudBackupAsync()
                ),
                "download-cloud-backup" => await WriteJsonAsync(
                    output,
                    await client.DownloadCloudBackupAsync(BuildCloudBackupRequest(args))
                ),
                "restore-cloud-backup" => await WriteJsonAsync(
                    output,
                    await client.RestoreCloudBackupAsync(BuildCloudBackupRequest(args))
                ),
                "get-bundle" => await WriteWrappedJsonAsync(
                    output,
                    "bundle",
                    await client.GetBundleAsync()
                ),
                "reset-bundle" => await WriteJsonAsync(
                    output,
                    await client.ResetBundleAsync()
                ),
                "import-bundle" => await WriteJsonAsync(
                    output,
                    await client.ImportBundleAsync(BuildBundleImportRequest(args))
                ),
                "export-bundle" => await WriteJsonAsync(
                    output,
                    await client.ExportBundleAsync(BuildBundleExportRequest(args))
                ),
                "add-bundle-package" => await WriteJsonAsync(
                    output,
                    await client.AddBundlePackageAsync(BuildBundlePackageRequest(args))
                ),
                "remove-bundle-package" => await WriteJsonAsync(
                    output,
                    await client.RemoveBundlePackageAsync(BuildBundlePackageRequest(args))
                ),
                "install-bundle" => await WriteJsonAsync(
                    output,
                    await client.InstallBundleAsync(BuildBundleInstallRequest(args))
                ),
                "get-version" => await WriteWrappedJsonAsync(
                    output,
                    "build",
                    await client.GetVersionAsync()
                ),
                "get-updates" => await WriteWrappedJsonAsync(
                    output,
                    "updates",
                    await client.ListUpgradablePackagesAsync(
                        GetOptionalArgument(args, "--manager")
                    )
                ),
                "list-installed" => await WriteWrappedJsonAsync(
                    output,
                    "packages",
                    await client.ListInstalledPackagesAsync(
                        GetOptionalArgument(args, "--manager")
                    )
                ),
                "search-packages" => await WriteWrappedJsonAsync(
                    output,
                    "packages",
                    await client.SearchPackagesAsync(
                        GetRequiredArgument(
                            args,
                            "--query",
                            "package search requires --query."
                        ),
                        GetOptionalArgument(args, "--manager"),
                        GetOptionalIntArgument(args, "--max-results")
                    )
                ),
                "package-details" => await WriteWrappedJsonAsync(
                    output,
                    "package",
                    await client.GetPackageDetailsAsync(BuildPackageActionRequest(args))
                ),
                "package-versions" => await WriteWrappedJsonAsync(
                    output,
                    "versions",
                    await client.GetPackageVersionsAsync(BuildPackageActionRequest(args))
                ),
                "list-ignored-updates" => await WriteWrappedJsonAsync(
                    output,
                    "ignoredUpdates",
                    await client.ListIgnoredUpdatesAsync()
                ),
                "ignore-package" => await WriteJsonAsync(
                    output,
                    await client.IgnorePackageUpdateAsync(BuildPackageActionRequest(args))
                ),
                "unignore-package" => await WriteJsonAsync(
                    output,
                    await client.RemoveIgnoredUpdateAsync(BuildPackageActionRequest(args))
                ),
                "install-package" => await WriteJsonAsync(
                    output,
                    await client.InstallPackageAsync(BuildPackageActionRequest(args))
                ),
                "download-package" => await WriteJsonAsync(
                    output,
                    await client.DownloadPackageAsync(BuildPackageActionRequest(args))
                ),
                "reinstall-package" => await WriteJsonAsync(
                    output,
                    await client.ReinstallPackageAsync(BuildPackageActionRequest(args))
                ),
                "update-package" => await WriteJsonAsync(
                    output,
                    await client.UpdatePackageAsync(BuildPackageActionRequest(args))
                ),
                "uninstall-package" => await WriteJsonAsync(
                    output,
                    await client.UninstallPackageAsync(BuildPackageActionRequest(args))
                ),
                "uninstall-then-reinstall-package" => await WriteJsonAsync(
                    output,
                    await client.UninstallThenReinstallPackageAsync(BuildPackageActionRequest(args))
                ),
                "open-window" => await WriteJsonAsync(output, await client.OpenWindowAsync()),
                "open-updates" => await WriteJsonAsync(output, await client.OpenUpdatesAsync()),
                "show-package" => await WriteJsonAsync(
                    output,
                    await client.ShowPackageAsync(
                        GetRequiredArgument(
                            args,
                            "--package-id",
                            "package show requires --id."
                        ),
                        GetRequiredArgument(
                            args,
                            "--package-source",
                            "package show requires --source."
                        )
                    )
                ),
                "update-all" => await WriteJsonAsync(output, await client.UpdateAllAsync()),
                "update-manager" => await WriteJsonAsync(
                    output,
                    await client.UpdateManagerAsync(
                        GetRequiredArgument(
                            args,
                            "--manager",
                            "package update-manager requires --manager."
                        )
                    )
                ),
                _ => await WriteErrorAsync(
                    output,
                    $"Unknown command \"{subcommand}\".",
                    IpcCliExitCode.UnknownCommand
                ),
            };
        }
        catch (InvalidOperationException ex)
        {
            return await WriteErrorAsync(output, ex.Message, IpcCliExitCode.InvalidParameter);
        }
        catch (HttpRequestException ex)
        {
            return await WriteErrorAsync(
                output,
                ex.Message,
                IpcCliExitCode.IpcUnavailable
            );
        }
        catch (IOException ex)
        {
            return await WriteErrorAsync(
                output,
                ex.Message,
                IpcCliExitCode.IpcUnavailable
            );
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return await WriteErrorAsync(output, ex.Message, IpcCliExitCode.Failed);
        }
    }

    private static IpcPackageActionRequest BuildPackageActionRequest(IReadOnlyList<string> args)
    {
        return new IpcPackageActionRequest
        {
            PackageId = GetRequiredArgument(
                args,
                "--package-id",
                "This command requires --id."
            ),
            ManagerName = GetOptionalArgument(args, "--manager"),
            PackageSource = GetOptionalArgument(args, "--package-source"),
            Version = GetOptionalArgument(args, "--version"),
            Scope = GetOptionalArgument(args, "--scope"),
            PreRelease = args.Contains("--pre-release") ? true : null,
            Elevated = GetOptionalBoolArgument(args, "--elevated"),
            Interactive = GetOptionalBoolArgument(args, "--interactive"),
            SkipHash = GetOptionalBoolArgument(args, "--skip-hash"),
            RemoveData = GetOptionalBoolArgument(args, "--remove-data"),
            WaitForCompletion = args.Contains("--detach")
                ? false
                : GetOptionalBoolArgument(args, "--wait"),
            Architecture = GetOptionalArgument(args, "--architecture"),
            InstallLocation = GetOptionalArgument(args, "--location"),
            OutputPath = GetOptionalArgument(args, "--output"),
        };
    }

    private static IpcAppNavigateRequest BuildAppNavigateRequest(IReadOnlyList<string> args)
    {
        return new IpcAppNavigateRequest
        {
            Page = GetRequiredArgument(
                args,
                "--page",
                "app navigate requires --page."
            ),
            ManagerName = GetOptionalArgument(args, "--manager"),
            HelpAttachment = GetOptionalArgument(args, "--help-attachment"),
        };
    }

    private static IpcSourceRequest BuildSourceRequest(IReadOnlyList<string> args)
    {
        return new IpcSourceRequest
        {
            ManagerName = GetRequiredArgument(
                args,
                "--manager",
                "This command requires --manager."
            ),
            SourceName = GetRequiredArgument(
                args,
                "--name",
                "This command requires --name."
            ),
            SourceUrl = GetOptionalArgument(args, "--url"),
        };
    }

    private static IpcManagerMaintenanceRequest BuildManagerMaintenanceRequest(
        IReadOnlyList<string> args,
        bool requireAction = false,
        bool requirePath = false
    )
    {
        return new IpcManagerMaintenanceRequest
        {
            ManagerName = GetRequiredArgument(
                args,
                "--manager",
                "This command requires --manager."
            ),
            Action = requireAction
                ? GetRequiredArgument(args, "--action", "This command requires --action.")
                : GetOptionalArgument(args, "--action"),
            Path = requirePath
                ? GetRequiredArgument(args, "--path", "This command requires --path.")
                : GetOptionalArgument(args, "--path"),
            Confirm = args.Contains("--confirm"),
        };
    }

    private static IpcSecureSettingRequest BuildSecureSettingRequest(
        IReadOnlyList<string> args
    )
    {
        return new IpcSecureSettingRequest
        {
            SettingKey = GetRequiredArgument(args, "--key", "This command requires --key."),
            UserName = GetOptionalArgument(args, "--user"),
            Enabled = GetRequiredBoolArgument(args, "--enabled"),
        };
    }

    private static IpcManagerToggleRequest BuildManagerToggleRequest(IReadOnlyList<string> args)
    {
        return new IpcManagerToggleRequest
        {
            ManagerName = GetRequiredArgument(
                args,
                "--manager",
                "This command requires --manager."
            ),
            Enabled = GetRequiredBoolArgument(args, "--enabled"),
        };
    }

    private static IpcDesktopShortcutRequest BuildDesktopShortcutRequest(
        IReadOnlyList<string> args,
        bool requireStatus
    )
    {
        return new IpcDesktopShortcutRequest
        {
            Path = GetRequiredArgument(args, "--path", "This command requires --path."),
            Status = requireStatus
                ? GetRequiredArgument(
                    args,
                    "--status",
                    "This command requires --status."
                )
                : GetOptionalArgument(args, "--status"),
        };
    }

    private static IpcBundleImportRequest BuildBundleImportRequest(
        IReadOnlyList<string> args
    )
    {
        return new IpcBundleImportRequest
        {
            Path = GetOptionalArgument(args, "--path"),
            Content = GetOptionalArgument(args, "--content"),
            Format = GetOptionalArgument(args, "--format"),
            Append = args.Contains("--append"),
        };
    }

    private static IpcGitHubDeviceFlowRequest BuildGitHubDeviceFlowRequest(
        IReadOnlyList<string> args
    )
    {
        return new IpcGitHubDeviceFlowRequest
        {
            LaunchBrowser = args.Contains("--launch-browser"),
        };
    }

    private static IpcCloudBackupRequest BuildCloudBackupRequest(IReadOnlyList<string> args)
    {
        return new IpcCloudBackupRequest
        {
            Key = GetRequiredArgument(
                args,
                "--key",
                "This command requires --key."
            ),
            Append = args.Contains("--append"),
        };
    }

    private static IpcBundleExportRequest BuildBundleExportRequest(
        IReadOnlyList<string> args
    )
    {
        return new IpcBundleExportRequest { Path = GetOptionalArgument(args, "--path") };
    }

    private static IpcBundlePackageRequest BuildBundlePackageRequest(
        IReadOnlyList<string> args
    )
    {
        return new IpcBundlePackageRequest
        {
            PackageId = GetRequiredArgument(
                args,
                "--package-id",
                "This command requires --id."
            ),
            ManagerName = GetOptionalArgument(args, "--manager"),
            PackageSource = GetOptionalArgument(args, "--package-source"),
            Version = GetOptionalArgument(args, "--version"),
            Scope = GetOptionalArgument(args, "--scope"),
            PreRelease = args.Contains("--pre-release") ? true : null,
            Selection = GetOptionalArgument(args, "--selection"),
        };
    }

    private static IpcBundleInstallRequest BuildBundleInstallRequest(
        IReadOnlyList<string> args
    )
    {
        return new IpcBundleInstallRequest
        {
            IncludeInstalled = GetOptionalBoolArgument(args, "--include-installed"),
            Elevated = GetOptionalBoolArgument(args, "--elevated"),
            Interactive = GetOptionalBoolArgument(args, "--interactive"),
            SkipHash = GetOptionalBoolArgument(args, "--skip-hash"),
        };
    }

    private static IpcSettingValueRequest BuildSettingRequest(IReadOnlyList<string> args)
    {
        bool? enabled = null;
        string? enabledValue = GetOptionalArgument(args, "--enabled");
        if (enabledValue is not null)
        {
            if (!bool.TryParse(enabledValue, out bool parsedEnabled))
            {
                throw new InvalidOperationException(
                    "The value supplied to --enabled must be either true or false."
                );
            }

            enabled = parsedEnabled;
        }

        return new IpcSettingValueRequest
        {
            SettingKey = GetRequiredArgument(
                args,
                "--key",
                "This command requires --key."
            ),
            Enabled = enabled,
            Value = GetOptionalArgument(args, "--value"),
        };
    }

    private static string GetRequiredArgument(
        IReadOnlyList<string> arguments,
        string argumentName,
        string errorMessage
    )
    {
        int index = arguments.ToList().IndexOf(argumentName);
        if (index < 0 || index + 1 >= arguments.Count)
        {
            throw new InvalidOperationException(errorMessage);
        }

        return arguments[index + 1].Trim('"').Trim('\'');
    }

    private static string? GetOptionalArgument(
        IReadOnlyList<string> arguments,
        string argumentName
    )
    {
        int index = arguments.ToList().IndexOf(argumentName);
        if (index < 0 || index + 1 >= arguments.Count)
        {
            return null;
        }

        return arguments[index + 1].Trim('"').Trim('\'');
    }

    private static int? GetOptionalIntArgument(
        IReadOnlyList<string> arguments,
        string argumentName
    )
    {
        string? value = GetOptionalArgument(arguments, argumentName);
        if (value is null)
        {
            return null;
        }

        if (int.TryParse(value, out int result))
        {
            return result;
        }

        throw new InvalidOperationException(
            $"The value supplied to {argumentName} must be an integer."
        );
    }

    private static bool? GetOptionalBoolArgument(
        IReadOnlyList<string> arguments,
        string argumentName
    )
    {
        string? value = GetOptionalArgument(arguments, argumentName);
        if (value is null)
        {
            return null;
        }

        if (bool.TryParse(value, out bool result))
        {
            return result;
        }

        throw new InvalidOperationException(
            $"The value supplied to {argumentName} must be either true or false."
        );
    }

    private static bool GetRequiredBoolArgument(IReadOnlyList<string> arguments, string argumentName)
    {
        bool? value = GetOptionalBoolArgument(arguments, argumentName);
        if (!value.HasValue)
        {
            throw new InvalidOperationException(
                $"This command requires {argumentName} with a value of true or false."
            );
        }

        return value.Value;
    }

    private static async Task<int> WriteJsonAsync<T>(TextWriter output, T value)
    {
        await output.WriteLineAsync(
            IpcJson.Serialize(value)
        );
        return (int)IpcCliExitCode.Success;
    }

    private static async Task<int> WriteWrappedJsonAsync<T>(
        TextWriter output,
        string propertyName,
        T value
    )
    {
        var response = new JsonObject
        {
            ["status"] = "success",
            [propertyName] = JsonNode.Parse(IpcJson.Serialize(value)),
        };
        await output.WriteLineAsync(response.ToJsonString(IpcJson.Options));
        return (int)IpcCliExitCode.Success;
    }

    private static async Task<int> WriteErrorAsync(
        TextWriter output,
        string message,
        IpcCliExitCode exitCode
    )
    {
        await output.WriteLineAsync(
            IpcJson.Serialize(new IpcCommandResult { Status = "error", Message = message })
        );
        return (int)exitCode;
    }
}
