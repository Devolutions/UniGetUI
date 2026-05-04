# UniGetUI background IPC API

This file documents the **local automation API** used by the UniGetUI CLI.

- For the public command-line interface built on top of this API, see [CLI.md](CLI.md).
- This API is designed for **local automation**, not for remote exposure.

## Overview

UniGetUI exposes a local HTTP API over one of two transports:

- **Named-pipe transport** (default)
  - Windows: Windows named pipe
  - Non-Windows: Unix domain socket
- **TCP transport** (optional)
  - Localhost only

All endpoints live under `/uniget/v1/...`.

## Transport defaults

| Setting | Value |
| --- | --- |
| Default transport | `named-pipe` |
| Default TCP port | `7058` |
| Default pipe name | `UniGetUI.IPC` |
| Default Unix socket directory | `/tmp` |

On non-Windows, a relative named-pipe name such as `UniGetUI.IPC` resolves to:

```text
/tmp/UniGetUI.IPC
```

An absolute path may also be supplied on non-Windows. On Windows, absolute pipe paths are rejected and UniGetUI falls back to the default pipe name.

## Server-side configuration

These options are read when UniGetUI starts its IPC API server.

| Argument | Environment variable | Meaning |
| --- | --- | --- |
| `--ipc-api-transport {named-pipe\|tcp}` | `UNIGETUI_IPC_API_TRANSPORT` | Selects the server transport. |
| `--ipc-api-port <port>` | `UNIGETUI_IPC_API_PORT` | Selects the TCP port when TCP is enabled. |
| `--ipc-api-pipe-name <name-or-path>` | `UNIGETUI_IPC_API_PIPE_NAME` | Selects the pipe name or Unix socket path when named-pipe transport is enabled. |

## Client-side configuration

These options are read by the CLI and `IpcClient`.

| Argument | Environment variable | Meaning |
| --- | --- | --- |
| `--transport {named-pipe\|tcp}` | `UNIGETUI_IPC_API_TRANSPORT` | Explicit client-side transport override. |
| `--tcp-port <port>` | `UNIGETUI_IPC_API_PORT` | Explicit client-side TCP port override. |
| `--pipe-name <name-or-path>` | `UNIGETUI_IPC_API_PIPE_NAME` | Explicit client-side pipe name or Unix socket override. |

## Session discovery

When the client does **not** receive an explicit transport override:

1. UniGetUI loads persisted endpoint registrations from the user configuration directory.
2. Registrations are ordered with this preference:
   1. headless sessions first
   2. newest persisted session first
3. The client probes for a live session and uses its persisted token automatically.

When the client **does** receive an explicit override:

- it connects to that transport choice instead of auto-selecting the newest session
- it waits up to 5 seconds for a matching persisted token to appear

## Authentication

| Endpoint | Auth |
| --- | --- |
| `GET /uniget/v1/status` | No token required |
| All other `/uniget/v1/*` endpoints | `token` query parameter required |

Authentication details:

- UniGetUI generates a per-session token at API startup.
- That token is persisted with the endpoint registration metadata.
- `IpcClient` automatically appends `token=<value>` to authenticated requests.

## Security notes

- The default design is **local-only automation**.
- TCP mode binds to `localhost`, not all interfaces.
- On non-Windows named-pipe transport, UniGetUI applies Unix socket mode:

```text
user-read + user-write
```

That is effectively `0600`-style same-user access on the socket file.

- On Windows named-pipe transport, UniGetUI uses Kestrel named-pipe hosting and does not expose a filesystem socket path.

## Error model

| Condition | Result |
| --- | --- |
| Missing or invalid token | HTTP `401` |
| Invalid query/body arguments | HTTP `400` with plain-text error message |
| Success | JSON response with camelCase property names |

Most successful command endpoints return either:

- a domain object wrapped in a `status: "success"` envelope, or
- a command/result JSON envelope, or
- another typed JSON payload documented by its fields rather than its CLR type name

## Request conventions

### Query-string endpoints

Most endpoints use query parameters, including:

- operations
- app navigation
- sources
- settings
- secure settings
- shortcuts
- logs
- package search/details/versions/installed/updates
- package actions

### JSON-body endpoints

These endpoint families consume JSON bodies:

| Endpoint family | Request shape |
| --- | --- |
| manager maintenance actions | manager maintenance request body |
| GitHub device-flow start | GitHub device-flow start request body |
| cloud backup download/restore | cloud backup request body |
| bundle import | bundle import request body |
| bundle export | bundle export request body |
| bundle add/remove | bundle package request body |
| bundle install | bundle install request body |

### JSON body field reference

All request bodies use **camelCase** JSON.

#### Manager maintenance request body

| Field | Type | Meaning |
| --- | --- | --- |
| `managerName` | string | Required stable manager id |
| `action` | string | Manager action name for `/action` |
| `path` | string | Custom executable path for `/executable/set` |
| `confirm` | boolean | Confirmation flag for destructive actions |

#### GitHub device-flow start request body

| Field | Type | Meaning |
| --- | --- | --- |
| `launchBrowser` | boolean | Whether UniGetUI should try to open the verification URL automatically |

#### Cloud backup request body

| Field | Type | Meaning |
| --- | --- | --- |
| `key` | string | Backup identifier |
| `append` | boolean | Append instead of replace when restoring/importing |

#### Bundle import request body

| Field | Type | Meaning |
| --- | --- | --- |
| `path` | string | Source file path |
| `content` | string | Raw bundle content |
| `format` | string | Bundle format such as `ubundle`, `json`, `yaml`, or `xml` |
| `append` | boolean | Append imported items to the current bundle |

#### Bundle export request body

| Field | Type | Meaning |
| --- | --- | --- |
| `path` | string | Optional output path |

#### Bundle package request body

| Field | Type | Meaning |
| --- | --- | --- |
| `packageId` | string | Package identifier |
| `managerName` | string | Stable manager id |
| `packageSource` | string | Source/feed name |
| `version` | string | Requested version |
| `scope` | string | Requested scope |
| `preRelease` | boolean | Include prerelease package metadata |
| `selection` | string | Bundle selection mode |

#### Bundle install request body

| Field | Type | Meaning |
| --- | --- | --- |
| `includeInstalled` | boolean | Whether already-installed packages should still be processed |
| `elevated` | boolean | Request elevated execution |
| `interactive` | boolean | Request interactive execution |
| `skipHash` | boolean | Skip hash validation when supported |

## Shared parameter sets

### Package action query parameters

These keys are used by package-related endpoints such as install, update, uninstall, details, versions, ignored updates, and download.

| Query key | Meaning |
| --- | --- |
| `packageId` | Package identifier |
| `manager` | Stable manager id |
| `packageSource` | Source/feed name |
| `version` | Requested version |
| `scope` | Install scope |
| `preRelease` | Boolean |
| `elevated` | Boolean |
| `interactive` | Boolean |
| `skipHash` | Boolean |
| `removeData` | Boolean |
| `wait` | Boolean |
| `architecture` | Architecture override |
| `location` | Install location override |
| `outputPath` | Download output path |

### App navigation query parameters

| Query key | Meaning |
| --- | --- |
| `page` | Target page name |
| `manager` | Optional manager context |
| `helpAttachment` | Optional help-page attachment |

### Operation query parameters

| Query key | Meaning |
| --- | --- |
| `tailLines` | Used by `GET /uniget/v1/operations/{operationId}/output` |
| `mode` | Retry mode for `POST /uniget/v1/operations/{operationId}/retry` |
| `action` | Queue action for `POST /uniget/v1/operations/{operationId}/reorder` |

## Endpoint reference

### Session and app

| Method | Path | Auth | Parameters/body | CLI equivalent | Notes |
| --- | --- | --- | --- | --- | --- |
| `GET` | `/uniget/v1/status` | No | None | `status`, `version` | Returns `running`, `transport`, `tcpPort`, `namedPipeName`, `namedPipePath`, `baseAddress`, `version`, and `buildNumber`. |
| `GET` | `/uniget/v1/app` | Yes | None | `app status` | Returns app/headless/window state. |
| `POST` | `/uniget/v1/app/show` | Yes | None | `app show` | UI-only in practice. |
| `POST` | `/uniget/v1/app/navigate` | Yes | Query: `page`, optional `manager`, optional `helpAttachment` | `app navigate` | UI-only in practice. |
| `POST` | `/uniget/v1/app/quit` | Yes | None | `app quit` | Shuts down the selected session. |

### Operations

| Method | Path | Auth | Parameters/body | CLI equivalent |
| --- | --- | --- | --- | --- |
| `GET` | `/uniget/v1/operations` | Yes | None | `operation list` |
| `GET` | `/uniget/v1/operations/{operationId}` | Yes | Route: `operationId` | `operation get` |
| `GET` | `/uniget/v1/operations/{operationId}/output` | Yes | Route: `operationId`, optional query `tailLines` | `operation output` |
| `POST` | `/uniget/v1/operations/{operationId}/cancel` | Yes | Route: `operationId` | `operation cancel` |
| `POST` | `/uniget/v1/operations/{operationId}/retry` | Yes | Route: `operationId`, optional query `mode` | `operation retry` |
| `POST` | `/uniget/v1/operations/{operationId}/reorder` | Yes | Route: `operationId`, query `action` | `operation reorder` |
| `POST` | `/uniget/v1/operations/{operationId}/forget` | Yes | Route: `operationId` | `operation forget` |

### Managers

| Method | Path | Auth | Parameters/body | CLI equivalent |
| --- | --- | --- | --- | --- |
| `GET` | `/uniget/v1/managers` | Yes | None | `manager list` |
| `GET` | `/uniget/v1/managers/maintenance` | Yes | Query `manager` | `manager maintenance` |
| `POST` | `/uniget/v1/managers/maintenance/reload` | Yes | JSON body: manager maintenance request body | `manager reload` |
| `POST` | `/uniget/v1/managers/maintenance/executable/set` | Yes | JSON body: manager maintenance request body | `manager set-executable` |
| `POST` | `/uniget/v1/managers/maintenance/executable/clear` | Yes | JSON body: manager maintenance request body | `manager clear-executable` |
| `POST` | `/uniget/v1/managers/maintenance/action` | Yes | JSON body: manager maintenance request body | `manager action` |
| `POST` | `/uniget/v1/managers/set-enabled` | Yes | Query `manager`, `enabled` | `manager enable`, `manager disable` |
| `POST` | `/uniget/v1/managers/set-update-notifications` | Yes | Query `manager`, `enabled` | `manager notifications enable`, `manager notifications disable` |

### Sources

| Method | Path | Auth | Parameters/body | CLI equivalent |
| --- | --- | --- | --- | --- |
| `GET` | `/uniget/v1/sources` | Yes | Optional query `manager` | `source list` |
| `POST` | `/uniget/v1/sources/add` | Yes | Query `manager`, `name`, optional `url` | `source add` |
| `POST` | `/uniget/v1/sources/remove` | Yes | Query `manager`, `name`, optional `url` | `source remove` |

### Settings

| Method | Path | Auth | Parameters/body | CLI equivalent |
| --- | --- | --- | --- | --- |
| `GET` | `/uniget/v1/settings` | Yes | None | `settings list` |
| `GET` | `/uniget/v1/settings/item` | Yes | Query `key` | `settings get` |
| `POST` | `/uniget/v1/settings/set` | Yes | Query `key`, optional `enabled`, optional `value` | `settings set` |
| `POST` | `/uniget/v1/settings/clear` | Yes | Query `key` | `settings clear` |
| `POST` | `/uniget/v1/settings/reset` | Yes | None | `settings reset` |

### Secure settings

| Method | Path | Auth | Parameters/body | CLI equivalent |
| --- | --- | --- | --- | --- |
| `GET` | `/uniget/v1/secure-settings` | Yes | Optional query `user` | `settings secure list` |
| `GET` | `/uniget/v1/secure-settings/item` | Yes | Query `key`, optional `user` | `settings secure get` |
| `POST` | `/uniget/v1/secure-settings/set` | Yes | Query `key`, `enabled`, optional `user` | `settings secure set` |

### Desktop shortcuts

| Method | Path | Auth | Parameters/body | CLI equivalent |
| --- | --- | --- | --- | --- |
| `GET` | `/uniget/v1/desktop-shortcuts` | Yes | None | `shortcut list` |
| `POST` | `/uniget/v1/desktop-shortcuts/set` | Yes | Query `path`, `status` | `shortcut set` |
| `POST` | `/uniget/v1/desktop-shortcuts/reset` | Yes | Query `path` | `shortcut reset` |
| `POST` | `/uniget/v1/desktop-shortcuts/reset-all` | Yes | None | `shortcut reset-all` |

### Logs

| Method | Path | Auth | Parameters/body | CLI equivalent |
| --- | --- | --- | --- | --- |
| `GET` | `/uniget/v1/logs/app` | Yes | Optional query `level` | `log app` |
| `GET` | `/uniget/v1/logs/history` | Yes | None | `log operations` |
| `GET` | `/uniget/v1/logs/manager` | Yes | Optional query `manager`, optional query `verbose` | `log manager` |

### Backups

| Method | Path | Auth | Parameters/body | CLI equivalent | Notes |
| --- | --- | --- | --- | --- | --- |
| `GET` | `/uniget/v1/backups/status` | Yes | None | `backup status` | Includes local backup settings and GitHub auth state. |
| `POST` | `/uniget/v1/backups/local/create` | Yes | None | `backup local create` | Creates a local backup bundle. |
| `POST` | `/uniget/v1/backups/github/sign-in/start` | Yes | JSON body: GitHub device-flow start request body | `backup github login start` | Starts GitHub device flow. |
| `POST` | `/uniget/v1/backups/github/sign-in/complete` | Yes | None | `backup github login complete` | Completes device flow. |
| `POST` | `/uniget/v1/backups/github/sign-out` | Yes | None | `backup github logout` | Signs out of GitHub backup integration. |
| `GET` | `/uniget/v1/backups/cloud` | Yes | None | `backup cloud list` | Lists cloud backups. |
| `POST` | `/uniget/v1/backups/cloud/create` | Yes | None | `backup cloud create` | Uploads a cloud backup. |
| `POST` | `/uniget/v1/backups/cloud/download` | Yes | JSON body: cloud backup request body | `backup cloud download` | Downloads backup content. |
| `POST` | `/uniget/v1/backups/cloud/restore` | Yes | JSON body: cloud backup request body | `backup cloud restore` | Restores/imports a cloud backup. |

### Bundles

| Method | Path | Auth | Parameters/body | CLI equivalent |
| --- | --- | --- | --- | --- |
| `GET` | `/uniget/v1/bundles` | Yes | None | `bundle get` |
| `POST` | `/uniget/v1/bundles/reset` | Yes | None | `bundle reset` |
| `POST` | `/uniget/v1/bundles/import` | Yes | JSON body: bundle import request body | `bundle import` |
| `POST` | `/uniget/v1/bundles/export` | Yes | JSON body: bundle export request body | `bundle export` |
| `POST` | `/uniget/v1/bundles/add` | Yes | JSON body: bundle package request body | `bundle add` |
| `POST` | `/uniget/v1/bundles/remove` | Yes | JSON body: bundle package request body | `bundle remove` |
| `POST` | `/uniget/v1/bundles/install` | Yes | JSON body: bundle install request body | `bundle install` |

### Packages

| Method | Path | Auth | Parameters/body | CLI equivalent | Notes |
| --- | --- | --- | --- | --- | --- |
| `GET` | `/uniget/v1/packages/search` | Yes | Query `query`, optional `manager`, optional `maxResults` | `package search` | Search endpoint. |
| `GET` | `/uniget/v1/packages/installed` | Yes | Optional query `manager` | `package installed` | Installed packages. |
| `GET` | `/uniget/v1/packages/updates` | Yes | Optional query `manager` | `package updates` | Upgradable packages. |
| `GET` | `/uniget/v1/packages/details` | Yes | Package action query set | `package details` | Details payload. |
| `GET` | `/uniget/v1/packages/versions` | Yes | Package action query set | `package versions` | Installable versions. |
| `GET` | `/uniget/v1/packages/ignored` | Yes | None | `package ignored list` | Ignored-update rules. |
| `POST` | `/uniget/v1/packages/ignore` | Yes | Package action query set | `package ignored add` | Adds ignored-update rule. |
| `POST` | `/uniget/v1/packages/unignore` | Yes | Package action query set | `package ignored remove` | Removes ignored-update rule. |
| `POST` | `/uniget/v1/packages/download` | Yes | Package action query set | `package download` | Starts or performs download. |
| `POST` | `/uniget/v1/packages/install` | Yes | Package action query set | `package install` | Starts or performs install. |
| `POST` | `/uniget/v1/packages/reinstall` | Yes | Package action query set | `package reinstall` | Reinstalls package. |
| `POST` | `/uniget/v1/packages/update` | Yes | Package action query set | `package update` | Updates one package. |
| `POST` | `/uniget/v1/packages/uninstall` | Yes | Package action query set | `package uninstall` | Uninstalls package. |
| `POST` | `/uniget/v1/packages/uninstall-then-reinstall` | Yes | Package action query set | `package repair` | Repair flow. |
| `POST` | `/uniget/v1/packages/show` | Yes | Query `packageId`, `packageSource` | `package show` | UI-oriented package-details flow. |
| `POST` | `/uniget/v1/packages/update-all` | Yes | None | `package update-all` | Requires `OnUpgradeAll` handler to be wired. |
| `POST` | `/uniget/v1/packages/update-manager` | Yes | Query `manager` | `package update-manager` | Requires `OnUpgradeAllForManager` handler to be wired. |

## Headless-specific limitations

In headless sessions:

- `POST /uniget/v1/app/show` fails because there is no window to show.
- `POST /uniget/v1/app/navigate` fails because there is no UI page stack to navigate.
- `POST /uniget/v1/packages/update-all` fails unless a host wires `OnUpgradeAll`.
- `POST /uniget/v1/packages/update-manager` fails unless a host wires `OnUpgradeAllForManager`.

These failures are intentional and surfaced as HTTP `400` with a descriptive message.

## Practical testing tip

If you want to inspect the IPC API manually with generic tools such as `curl`, the easiest route is to start UniGetUI in **TCP mode**:

```powershell
UniGetUI.exe --headless --ipc-api-transport tcp --ipc-api-port 7058
```

Then:

```powershell
curl http://localhost:7058/uniget/v1/status
```

For authenticated endpoints, you must also supply the session token as the `token` query parameter. The built-in CLI and `IpcClient` resolve that automatically.
