# About Panel + App Update Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an "About" tab to SettingsDialog with version display, update checking against a backend API, and one-click app update with download progress and auto-restart.

**Architecture:** New `IAppUpdateService`/`AppUpdateService` backed by `HttpClient` handles version checking and download. A Mac-specific shell script replaces the .app bundle while the process is terminated. `SettingsDialog.razor` gains an "about" tab with a state machine driving the UI.

**Tech Stack:** C#, .NET MAUI MacCatalyst, Blazor, System.Text.Json, System.IO.Compression, CSS

---

## File Structure

| Action | File | Responsibility |
|---|---|---|
| Create | `Models/VersionInfo.cs` | API response DTO with JSON mapping |
| Create | `Services/IAppUpdateService.cs` | Interface: CheckVersionAsync, DownloadAndInstallAsync |
| Create | `Services/Impl/AppUpdateService.cs` | HTTP logic, download+extract, version compare, Mac restart |
| Modify | `MauiProgram.cs` | Register HttpClient + IAppUpdateService in DI |
| Modify | `wwwroot/css/app.css` | About panel styles (append) |
| Modify | `Components/Dialogs/SettingsDialog.razor` | New "about" tab UI + code-behind state machine |

---

### Task 1: Create VersionInfo model

**Files:**
- Create: `src/MacExplorer/Models/VersionInfo.cs`

- [ ] **Step 1: Write the model classes**

```csharp
using System.Text.Json.Serialization;

namespace MacExplorer.Models;

public class VersionCheckResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("data")]
    public VersionInfo? Data { get; set; }
}

public class VersionInfo
{
    [JsonPropertyName("CLIENT")]
    public string Client { get; set; } = "";

    [JsonPropertyName("DATETIME")]
    public string DateTime { get; set; } = "";

    [JsonPropertyName("ID")]
    public string Id { get; set; } = "";

    [JsonPropertyName("MEMO")]
    public string Memo { get; set; } = "";

    [JsonPropertyName("PATH")]
    public string Path { get; set; } = "";

    [JsonPropertyName("VERSION")]
    public string Version { get; set; } = "";
}
```

- [ ] **Step 2: Build to verify compilation**

```bash
dotnet build src/MacExplorer/MacExplorer.csproj --framework net10.0-maccatalyst
```

Expected: Build succeeds (model has no dependencies on other new code).

- [ ] **Step 3: Commit**

```bash
git add src/MacExplorer/Models/VersionInfo.cs
git commit -m "feat: add VersionInfo and VersionCheckResponse models for app update"
```

---

### Task 2: Create IAppUpdateService interface

**Files:**
- Create: `src/MacExplorer/Services/IAppUpdateService.cs`

- [ ] **Step 1: Write the interface**

```csharp
using MacExplorer.Models;

namespace MacExplorer.Services;

public interface IAppUpdateService
{
    /// <summary>
    /// Queries the backend for a newer version.
    /// Returns VersionInfo if an update is available, null otherwise.
    /// </summary>
    Task<VersionInfo?> CheckVersionAsync(CancellationToken ct = default);

    /// <summary>
    /// Downloads the update zip, extracts it, and triggers install+restart.
    /// Reports progress as (percentage 0-100, status text).
    /// </summary>
    Task DownloadAndInstallAsync(
        VersionInfo versionInfo,
        IProgress<(double Progress, string Status)>? progress = null,
        CancellationToken ct = default);
}
```

- [ ] **Step 2: Build to verify**

```bash
dotnet build src/MacExplorer/MacExplorer.csproj --framework net10.0-maccatalyst
```

Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/MacExplorer/Services/IAppUpdateService.cs
git commit -m "feat: add IAppUpdateService interface"
```

---

### Task 3: Implement AppUpdateService

**Files:**
- Create: `src/MacExplorer/Services/Impl/AppUpdateService.cs`

- [ ] **Step 1: Write the implementation**

```csharp
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Json;
using Foundation;
using MacExplorer.Models;

namespace MacExplorer.Services.Impl;

public class AppUpdateService : IAppUpdateService
{
    private readonly HttpClient _http;
    private const string VersionApiUrl =
        "http://thankful.top:4396/api/CloudSync/GetVersion?Client=MacExplorer";

    public AppUpdateService(HttpClient http)
    {
        _http = http;
    }

    public async Task<VersionInfo?> CheckVersionAsync(CancellationToken ct = default)
    {
        var response = await _http.GetFromJsonAsync<VersionCheckResponse>(
            VersionApiUrl, ct);

        if (response?.Success != true || response.Data == null)
            return null;

        if (string.IsNullOrWhiteSpace(response.Data.Version))
            return null;

        var current = GetCurrentVersion();
        if (!Version.TryParse(response.Data.Version, out var latest))
            return null;

        return latest > current ? response.Data : null;
    }

    public async Task DownloadAndInstallAsync(
        VersionInfo versionInfo,
        IProgress<(double Progress, string Status)>? progress = null,
        CancellationToken ct = default)
    {
        var tempDir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "MacExplorer_Update");
        Directory.CreateDirectory(tempDir);

        var zipPath = System.IO.Path.Combine(tempDir, "update.zip");
        var extractDir = System.IO.Path.Combine(tempDir, "extracted");

        // ── Download ──
        using var response = await _http.GetAsync(
            versionInfo.Path, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1L;
        await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = File.Create(zipPath);

        var buffer = new byte[8192];
        var downloaded = 0L;
        int read;
        while ((read = await contentStream.ReadAsync(buffer, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
            downloaded += read;
            if (total > 0)
            {
                var pct = (double)downloaded / total * 100;
                var mbDown = downloaded / 1024.0 / 1024.0;
                var mbTotal = total / 1024.0 / 1024.0;
                progress?.Report((pct, $"下载中 {mbDown:F1} / {mbTotal:F1} MB"));
            }
        }

        // ── Extract ──
        progress?.Report((100, "正在解压..."));
        if (Directory.Exists(extractDir))
            Directory.Delete(extractDir, true);
        ZipFile.ExtractToDirectory(zipPath, extractDir);

        var appBundle = Directory.GetDirectories(
            extractDir, "*.app", SearchOption.AllDirectories).FirstOrDefault();
        if (appBundle == null)
            throw new InvalidOperationException("更新包中未找到 .app 文件");

        // ── Stage the new .app next to the current one ──
        var currentAppPath = NSBundle.MainBundle.BundlePath;
        var stagedPath = currentAppPath + ".new";

        if (Directory.Exists(stagedPath))
            Directory.Delete(stagedPath, true);
        CopyDirectory(appBundle, stagedPath);

        // ── Write and launch replace script ──
        var scriptPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "mac_explorer_update.sh");

        var script = $@"#!/bin/bash
sleep 1
rm -rf ""{currentAppPath}""
mv ""{stagedPath}"" ""{currentAppPath}""
open ""{currentAppPath}""
rm -f ""{scriptPath}""
rm -rf ""{tempDir}""
";

        File.WriteAllText(scriptPath, script);

        using var chmod = Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/chmod",
            Arguments = $"+x \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        chmod?.WaitForExit();

        Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/bash",
            Arguments = $"\"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        });

        // ── Quit immediately ──
        AppKit.NSApplication.SharedApplication.Terminate(null);
    }

    private static Version GetCurrentVersion()
    {
        var str = NSBundle.MainBundle.InfoDictionary["CFBundleShortVersionString"]
            ?.ToString() ?? "1.0";
        return Version.TryParse(str, out var v) ? v : new Version(1, 0);
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
        {
            var destFile = System.IO.Path.Combine(dest, System.IO.Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: true);
        }
        foreach (var dir in Directory.GetDirectories(source))
        {
            var destDir = System.IO.Path.Combine(dest, System.IO.Path.GetFileName(dir));
            CopyDirectory(dir, destDir);
        }
    }
}
```

- [ ] **Step 2: Build to verify compilation**

```bash
dotnet build src/MacExplorer/MacExplorer.csproj --framework net10.0-maccatalyst
```

Expected: Build succeeds (will have warnings about unused service until Task 4 registers it).

- [ ] **Step 3: Commit**

```bash
git add src/MacExplorer/Services/Impl/AppUpdateService.cs
git commit -m "feat: implement AppUpdateService with download, extract, and Mac restart"
```

---

### Task 4: Register services in DI

**Files:**
- Modify: `src/MacExplorer/MauiProgram.cs`

- [ ] **Step 1: Add HttpClient and IAppUpdateService registrations**

In `MauiProgram.cs`, after the existing `builder.Services.AddSingleton<IDefaultAppService, ...>` line (line ~163), add:

```csharp
// App Update
builder.Services.AddSingleton<HttpClient>(_ => new HttpClient
{
    Timeout = TimeSpan.FromMinutes(10)
});
builder.Services.AddSingleton<IAppUpdateService, Services.Impl.AppUpdateService>();
```

- [ ] **Step 2: Build to verify**

```bash
dotnet build src/MacExplorer/MacExplorer.csproj --framework net10.0-maccatalyst
```

Expected: Build succeeds with 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/MacExplorer/MauiProgram.cs
git commit -m "feat: register HttpClient and IAppUpdateService in DI"
```

---

### Task 5: Add about panel CSS styles

**Files:**
- Modify: `src/MacExplorer/wwwroot/css/app.css`

- [ ] **Step 1: Append about panel styles at the end of app.css**

Append before the final `}` or at end of file:

```css
/* ═══════════════════════════════════════
   About Panel (SettingsDialog)
   ═══════════════════════════════════════ */

.about-panel {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 16px;
    padding: 24px 16px 16px;
    text-align: center;
}

.about-app-icon {
    width: 72px;
    height: 72px;
    border-radius: 18px;
    object-fit: contain;
    box-shadow: 0 4px 12px rgba(0, 0, 0, 0.12);
    background: var(--color-bg-content);
}

.about-app-name {
    font-size: 18px;
    font-weight: 700;
    color: var(--color-text-primary);
    letter-spacing: -0.3px;
}

.about-version {
    font-size: 13px;
    color: var(--color-text-tertiary);
    margin-top: -8px;
    font-variant-numeric: tabular-nums;
}

.about-version-badge {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    padding: 4px 12px;
    background: var(--color-bg-hover);
    border-radius: 20px;
    font-size: 12px;
    color: var(--color-text-secondary);
}

.about-version-badge .new-dot {
    width: 7px;
    height: 7px;
    border-radius: 50%;
    background: var(--color-accent);
    animation: pulse-dot 2s ease-in-out infinite;
}

@keyframes pulse-dot {
    0%, 100% { opacity: 1; transform: scale(1); }
    50% { opacity: 0.5; transform: scale(1.3); }
}

.about-update-btn {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    gap: 8px;
    min-width: 160px;
    padding: 10px 24px;
    border: none;
    border-radius: 8px;
    font-size: 14px;
    font-weight: 600;
    cursor: pointer;
    color: white;
    background: var(--color-accent);
    transition: opacity 0.15s ease, transform 0.15s ease, background 0.15s ease;
}

.about-update-btn:hover {
    opacity: 0.88;
    transform: translateY(-1px);
}

.about-update-btn:active {
    transform: translateY(0);
}

.about-update-btn.checking {
    background: var(--color-bg-hover-strong);
    color: var(--color-text-secondary);
    cursor: wait;
    pointer-events: none;
}

.about-update-btn.downloading {
    background: var(--color-accent);
    cursor: progress;
    pointer-events: none;
}

.about-update-btn .btn-spinner {
    width: 16px;
    height: 16px;
    border: 2px solid rgba(255, 255, 255, 0.3);
    border-top-color: white;
    border-radius: 50%;
    animation: spin 0.7s linear infinite;
}

@keyframes spin {
    to { transform: rotate(360deg); }
}

.about-status {
    font-size: 12px;
    color: var(--color-text-secondary);
    min-height: 18px;
    transition: color 0.2s ease;
}

.about-status.error {
    color: var(--color-danger);
}

.about-status.success {
    color: #2d8a4e;
}

.about-progress-bar {
    width: 100%;
    max-width: 280px;
    height: 4px;
    background: var(--color-border-subtle);
    border-radius: 2px;
    overflow: hidden;
}

.about-progress-fill {
    height: 100%;
    background: var(--color-accent);
    border-radius: 2px;
    transition: width 0.3s ease;
}

.about-changelog {
    width: 100%;
    max-width: 320px;
    margin-top: 8px;
    border: 1px solid var(--color-border-subtle);
    border-radius: 10px;
    overflow: hidden;
}

.about-changelog-header {
    font-size: 11px;
    font-weight: 600;
    color: var(--color-text-tertiary);
    text-transform: uppercase;
    letter-spacing: 0.5px;
    padding: 10px 14px 6px;
    text-align: left;
}

.about-changelog-content {
    font-size: 12px;
    color: var(--color-text-secondary);
    line-height: 1.6;
    padding: 0 14px 14px;
    text-align: left;
    white-space: pre-line;
    max-height: 200px;
    overflow-y: auto;
}

.about-footer {
    font-size: 11px;
    color: var(--color-text-quaternary);
    padding-top: 8px;
    border-top: 1px solid var(--color-border-subtle);
    width: 100%;
    margin-top: 4px;
}
```

- [ ] **Step 2: Commit**

```bash
git add src/MacExplorer/wwwroot/css/app.css
git commit -m "feat: add about panel CSS styles for update UI"
```

---

### Task 6: Modify SettingsDialog.razor — add "about" tab and code-behind

**Files:**
- Modify: `src/MacExplorer/Components/Dialogs/SettingsDialog.razor`

This task has two parts: the template (Razor markup) and the code (`@code` block additions).

- [ ] **Step 1: Add the "about" tab button in the tab bar**

In the `.settings-tabs` div, after the "打开方式" tab, add:

```razor
<div class="settings-tab @(_activeTab == "about" ? "active" : "")" @onclick="@(() => _activeTab = "about")">关于</div>
```

- [ ] **Step 2: Add the about tab content**

In the `settings-tab-content` div, after the `else if (_activeTab == "openwith")` block, add:

```razor
else if (_activeTab == "about")
{
    <div class="about-panel">
        <img class="about-app-icon" src="/appicon.svg" alt="MacExplorer" />

        <div class="about-app-name">MacExplorer</div>

        <div class="about-version-badge">
            @if (_updateCheckState == UpdateCheckState.UpdateAvailable)
            {
                <span class="new-dot"></span>
            }
            版本 @_currentVersion
        </div>

        @if (_updateCheckState != UpdateCheckState.Downloading)
        {
            <button class="about-update-btn @GetUpdateBtnClass()"
                    @onclick="OnUpdateButtonClick"
                    disabled="@(_updateCheckState is UpdateCheckState.Checking or UpdateCheckState.Downloading or UpdateCheckState.Installing)">
                @if (_updateCheckState == UpdateCheckState.Checking)
                {
                    <span class="btn-spinner"></span>
                    <span>检查中...</span>
                }
                else if (_updateCheckState == UpdateCheckState.UpdateAvailable)
                {
                    <span>立即更新</span>
                }
                else
                {
                    <span>检查更新</span>
                }
            </button>
        }

        @if (_updateCheckState == UpdateCheckState.Downloading)
        {
            <div class="about-progress-bar">
                <div class="about-progress-fill" style="width: @((int)_downloadProgress)%"></div>
            </div>
            <div class="about-status">@_downloadStatusText</div>
        }

        @if (!string.IsNullOrEmpty(_updateStatusMessage))
        {
            <div class="about-status @GetUpdateStatusClass()">
                @_updateStatusMessage
            </div>
        }

        @if (_versionInfo != null && !string.IsNullOrWhiteSpace(_versionInfo.Memo))
        {
            <div class="about-changelog">
                <div class="about-changelog-header">更新日志</div>
                <div class="about-changelog-content">@_versionInfo.Memo</div>
            </div>
        }

        <div class="about-footer">Copyright 2024–2026 MacExplorer</div>
    </div>
}
```

- [ ] **Step 3: Add the @inject directive at the top of the file**

Add after the existing `@inject IOpenWithAppService OpenWithService` line:

```razor
@inject MacExplorer.Services.IAppUpdateService AppUpdateService
```

- [ ] **Step 4: Add the state enum, fields, and methods to the @code block**

Add the `UpdateCheckState` enum inside the `@code` block's namespace or as a private enum:

```csharp
private enum UpdateCheckState { Idle, Checking, NoUpdate, UpdateAvailable, Downloading, Installing, Error }
```

Add these fields alongside existing private fields (after `_isLoadingApps`):

```csharp
// About / Update state
private UpdateCheckState _updateCheckState = UpdateCheckState.Idle;
private VersionInfo? _versionInfo;
private string _updateStatusMessage = "";
private string _downloadStatusText = "";
private double _downloadProgress;
private string _currentVersion = "1.0";
private CancellationTokenSource? _updateCts;
```

Add initialization in `OnInitialized()` (before the closing brace):

```csharp
// Load current version for about panel
_currentVersion = GetCurrentAppVersion();
```

Add the helper methods and event handlers:

```csharp
private static string GetCurrentAppVersion()
{
    var str = Foundation.NSBundle.MainBundle.InfoDictionary["CFBundleShortVersionString"]
        ?.ToString() ?? "1.0";
    return str;
}

private string GetUpdateBtnClass() => _updateCheckState switch
{
    UpdateCheckState.Checking => "checking",
    UpdateCheckState.Downloading => "downloading",
    _ => ""
};

private string GetUpdateStatusClass() => _updateCheckState switch
{
    UpdateCheckState.Error => "error",
    UpdateCheckState.NoUpdate => "success",
    _ => ""
};

private async Task OnUpdateButtonClick()
{
    if (_updateCheckState == UpdateCheckState.UpdateAvailable && _versionInfo != null)
    {
        // Start download + install
        _updateCheckState = UpdateCheckState.Downloading;
        _updateStatusMessage = "";
        _downloadProgress = 0;
        _downloadStatusText = "准备下载...";
        StateHasChanged();

        _updateCts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<(double Progress, string Status)>(report =>
            {
                _downloadProgress = report.Progress;
                _downloadStatusText = report.Status;
                StateHasChanged();
            });

            await AppUpdateService.DownloadAndInstallAsync(_versionInfo, progress, _updateCts.Token);
        }
        catch (OperationCanceledException)
        {
            _updateCheckState = UpdateCheckState.Idle;
            _updateStatusMessage = "更新已取消";
            StateHasChanged();
        }
        catch (Exception ex)
        {
            _updateCheckState = UpdateCheckState.Error;
            _updateStatusMessage = $"下载失败: {ex.Message}";
            StateHasChanged();
        }
    }
    else
    {
        // Check for updates
        _updateCheckState = UpdateCheckState.Checking;
        _updateStatusMessage = "";
        _versionInfo = null;
        StateHasChanged();

        try
        {
            _versionInfo = await AppUpdateService.CheckVersionAsync();

            if (_versionInfo != null)
            {
                _updateCheckState = UpdateCheckState.UpdateAvailable;
                _updateStatusMessage = $"发现新版本 {_versionInfo.Version}";
            }
            else
            {
                _updateCheckState = UpdateCheckState.NoUpdate;
                _updateStatusMessage = "已是最新版本";
            }
        }
        catch (Exception ex)
        {
            _updateCheckState = UpdateCheckState.Error;
            _updateStatusMessage = $"检查失败: {ex.Message}";
        }

        StateHasChanged();
    }
}
```

- [ ] **Step 4: Build to verify compilation**

```bash
dotnet build src/MacExplorer/MacExplorer.csproj --framework net10.0-maccatalyst
```

Expected: Build succeeds with 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/MacExplorer/Components/Dialogs/SettingsDialog.razor
git commit -m "feat: add about tab with version check, update download, and changelog display"
```

---

## Verification Checklist

After all tasks are committed:

1. Open the Settings dialog → verify "关于" tab appears
2. Click "关于" → verify app icon, name, and current version display
3. Click "检查更新" → verify spinner shows, then either "已是最新版本" or "发现新版本"
4. When update available → verify changelog displays, button text changes to "立即更新"
5. Click "立即更新" → verify progress bar and status text update during download
6. After download → verify app terminates and restarts with the new version

Note: Steps 5-6 require the backend to return a newer version than current. For testing, temporarily mock the version check to return a higher version with a test zip URL.
