# About Panel + App Update Design

**Date**: 2026-06-03
**Status**: Approved

## Overview

Add an "About" tab to the Settings dialog that shows the current app version, checks for updates from a backend API, displays update logs, and supports downloading + background-replacing the .app bundle to update the application.

## API

```
GET http://thankful.top:4396/api/CloudSync/GetVersion?Client=MacExplorer
```

Response:
```json
{
  "success": true,
  "code": 200,
  "data": {
    "CLIENT": "MacExplorer",
    "DATETIME": "2024-03-09T07:54:39",
    "ID": "191B94CAA5BF4CF480137AA277CA952D",
    "MEMO": "v3.0.0.0\n...changelog...",
    "PATH": "https://cdn.thankful.top/...zip",
    "VERSION": "3.0.0.0"
  }
}
```

## Architecture

```
SettingsDialog.razor (about tab)
    │
    ▼
IAppUpdateService
  ├── CheckVersionAsync() → VersionInfo? (null = no update)
  └── DownloadAndInstallAsync(VersionInfo, progress, ct) → Task
    │
    ▼
HttpClient → GET version check + download .zip
    │
    ▼
MacAppUpdateService (platform-specific)
  └── ReplaceAndRestart(zipPath) → launches shell script, kills self
```

## Files

### New

| File | Responsibility |
|---|---|
| `Models/VersionInfo.cs` | API response DTO with VERSION, MEMO, PATH, DATETIME |
| `Services/IAppUpdateService.cs` | CheckVersionAsync, DownloadAndInstallAsync |
| `Services/Impl/AppUpdateService.cs` | Cross-platform logic, downloads, version comparison |
| `Platforms/MacCatalyst/Services/MacAppUpdateService.cs` | replace.sh generation, process termination, restart |

### Modified

| File | Change |
|---|---|
| `SettingsDialog.razor` | Add "about" tab with version info, check/update button, changelog |
| `SettingsDialog.razor.cs` | State machine for update flow |
| `MauiProgram.cs` | Register IAppUpdateService, HttpClient |
| `wwwroot/css/app.css` | About panel styles |

## UI State Machine

```
idle
  │ [click "Check for Updates"]
  ▼
checking (spinner)
  ├── no_update → idle (show "Already up to date" + check button)
  └── update_available (show MEMO + "Update Now" button)
        │ [click "Update Now"]
        ▼
      downloading (progress bar + current file name)
        │
        ▼
      installing (message: "Installing, app will restart...")
        │
        ▼
      done → app restarts automatically
```

## Version Comparison

- Current version from `NSBundle.MainBundle.InfoDictionary["CFBundleShortVersionString"]`
- API returns `VERSION` as string (e.g. "3.0.0.0")
- Compare using `System.Version` class for proper SemVer ordering
- New version available if API version > current version

## Download & Install Flow

1. Download .zip to temp directory (show progress via `IBackgroundTaskManager` pattern)
2. Extract .zip to temp directory
3. Generate a shell script (`replace.sh`) that:
   - Waits for the current process to exit (polling)
   - Copies the new .app bundle over the old .app bundle path
   - Launches the new .app from the updated bundle
4. Execute the shell script via `NSTask` (detached, nohup)
5. Call `NSApplication.SharedApplication.Terminate(this)` to exit immediately

## Edge Cases

- Network error: show error message, button reverts to "Check for Updates"
- Download timeout/cancellation: clean up temp files, revert to idle
- Disk full: catch IOException, show user-friendly error
- App in read-only location (e.g. /Applications): helper script uses `osascript` for admin privileges if needed
- Same version: show "Already up to date" immediately
