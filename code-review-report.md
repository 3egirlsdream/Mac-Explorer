# 代码审查报告：长时间打开界面崩溃修复

**审查日期**: 2026-06-01  
**审查范围**: 工作区未提交的改动（涉及 19 个文件）  
**审查方法**: 7 角度 × 6 候选 → 1-vote verify（recall-biased）→ ≤10 findings

---

## 一、变更概述

本次修改旨在修复**应用长时间打开后界面崩溃回到首页**的问题。主要变更包括：

1. **async void 回调加固**：所有 `PropertyChanged`/`CollectionChanged` 回调加 try-catch，捕获 `ObjectDisposedException`，防止组件销毁后回调崩溃
2. **目录工作取消机制**：引入 `DirectoryWork` 生成号模式 + `CancellationTokenSource`，导航切换时取消过期的后台任务（图标/缩略图/Git 状态/AI 分析）
3. **FSEvents 监听统一管理**：通过 `NavigationViewModel.SetWatchedDirectory()` 统一管理 watch/unwatch，修复旧代码中 `GoHome`/`NavigateToCollectionAsync` 的 unwatch no-op bug
4. **资源泄漏修复**：CTS disposal、NSUrl disposal、JS 事件监听器清理、CFString/CFArray 在 finally 中释放
5. **线程安全改进**：`BackgroundTaskManager`/`MacVolumeMonitorService` 事件逐订阅者 try-catch、`MacThumbnailService` 缓存加锁、`NativeDragDropHelper` 加锁保护
6. **缓存管理改进**：`MacThumbnailService` 内存缓存字节级限制 + 磁盘缓存定期清理 + 缩略图生成并发限制

---

## 二、审查发现

### 🔴 严重（建议修复后再合入）

#### Finding 1：FileGridView.DisposeAsync 信号量等待无超时

- **文件**: [FileGridView.razor:263](src/MacExplorer/Components/FileList/FileGridView.razor#L263)
- **状态**: ✅ CONFIRMED
- **描述**: `DisposeAsync` 在 `_blobUrlGate.WaitAsync()` 上没有传入 `CancellationToken` 也没有设超时。如果 `CreateBlobUrlsForEntriesAsync` 持有信号量且卡在 `JSRuntime.InvokeAsync` 调用上，`DisposeAsync` 会永远阻塞。
- **失败场景**: 组件销毁时，`CreateBlobUrlsForEntriesAsync` 正持有 `_blobUrlGate` 信号量并等待 `JSRuntime.InvokeAsync("createBlobUrl", ...)`。由于 Blazor 渲染器正在关闭，JSRuntime 变得不可用，JS 调用挂起不返回。`DisposeAsync` 永远等不到信号量释放，组件无法完成销毁，导致内存泄漏和页面卡死。
- **修复建议**: 改为 `await _blobUrlGate.WaitAsync(TimeSpan.FromSeconds(5))` 或传入一个 linked CancellationToken：
  ```csharp
  // 方案 A：超时
  if (!await _blobUrlGate.WaitAsync(TimeSpan.FromSeconds(5)))
      return; // 超时跳过清理

  // 方案 B：CancellationToken（需在 DisposeAsync 中创建）
  using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
  await _blobUrlGate.WaitAsync(cts.Token);
  ```

---

### 🟡 中等（建议择机修复）

#### Finding 2：ScheduleConfirmationScan 在 ThreadPool 线程调用 Foundation API

- **文件**: [MacVolumeMonitorService.cs:267](src/MacExplorer/Platforms/MacCatalyst/Services/MacVolumeMonitorService.cs#L267)
- **状态**: ⚠️ PLAUSIBLE
- **描述**: `ScheduleConfirmationScan` 使用 `Task.Run` 在 ThreadPool 线程上执行 `HandleVolumesChanged()`，而 `ScanExternalVolumes` 内部调用 `NSUrl.CreateFileUrl` 和 `NSUrl.GetResourceValues`。这些 Foundation API 通常被认为是线程安全的，但原有代码路径（FSEvents 回调）始终在主线程执行，新路径改变了线程上下文。
- **失败场景**: 外置磁盘短暂拔出后重新插入，500ms 后确认扫描在 ThreadPool 线程执行 `ScanExternalVolumes`。某些 macOS 版本下 Foundation API 在非主线程调用可能返回错误数据。
- **修复建议**: 改为 `MainThread.BeginInvokeOnMainThread(() => HandleVolumesChanged())`：
  ```csharp
  _ = Task.Run(async () =>
  {
      try
      {
          await Task.Delay(500);
          if (!_disposed)
              MainThread.BeginInvokeOnMainThread(() => HandleVolumesChanged());
      }
      ...
  });
  ```

#### Finding 3：NativeDragDropHelper DiscoverNSWindowForWebView TOCTOU 窗口

- **文件**: [NativeDragDropHelper.cs:157](src/MacExplorer/Platforms/MacCatalyst/Handlers/NativeDragDropHelper.cs#L157)
- **状态**: ⚠️ PLAUSIBLE
- **描述**: `DiscoverNSWindowForWebView` 在 lock 内更新 `_webViewNSWindowMap` 后释放 lock，再在 lock 外调用 `DropOverlayHelper.RegisterWebView`。与 `DetachFromWebView` 之间存在 TOCTOU 窗口。当前因两操作均在主线程执行而缓解，但结构性风险仍在。
- **失败场景**: 重试回调在 lock 内更新 `_webViewNSWindowMap` 后释放锁，`DisconnectHandler` 抢占执行 `DetachFromWebView`（UnregisterWebView 正常执行）。然后重试回调恢复，调用 `RegisterWebView` 写入 `_windowToWebView`——此后无人清理该注册。
- **修复建议**: 将 `RegisterWebView` 调用移入 lock 内部，或在 `DetachFromWebView` 中增加防御性清理逻辑。

---

### 🟢 低风险（可选修复）

#### Finding 4：MacThumbnailService 缓存计数器与实际使用不一致

- **文件**: [MacThumbnailService.cs:270](src/MacExplorer/Platforms/MacCatalyst/Services/MacThumbnailService.cs#L270)
- **状态**: ✅ CONFIRMED（影响极低）
- **描述**: `AddToMemoryCache` 的 `_memoryCacheBytes` 在 `_memoryCacheLock` 内更新，但 `GetThumbnailAsync` 的 `TryGetValue` 在 lock 外读取。当 eviction 发生时，已被驱逐的条目仍可能被外部线程持有引用，导致 `_memoryCacheBytes` 瞬间低于实际"逻辑使用中"的字节数。
- **实际影响**: 不会崩溃或数据损坏，仅缓存计数器瞬间不精确，eviction 阈值判断有微小偏差。
- **修复建议**: 可接受当前实现。如需严格一致，将 `GetThumbnailAsync` 的 `TryGetValue` 也放入 lock 内（会增加锁竞争，需权衡）。

---

## 三、已排除的误报

| 候选 | 结果 | 理由 |
|------|------|------|
| CancelDirectoryWork 中 CTS Dispose 竞态 | ❌ REFUTED | `Cancel()` 先于 `Dispose()`，标准 .NET API 在 token 已取消时走快速路径返回 `OperationCanceledException`，不触发 `Register()`，不会抛出 `ObjectDisposedException` |
| ResolveGitStatusAsync 竞态覆写新目录条目 | ❌ REFUTED | `BeginDirectoryWork()` 在 `LoadDirectoryContentsAsync` 的第一个同步语句执行（在任何 await 之前），旧 work 的 token 和 generation 已失效，`IsCurrentDirectoryWork` 检查会拒绝过期任务 |
| Home.DisposeAsync 事件取消订阅顺序 | ❌ REFUTED | `DisposeAsync` 中 `StopDirectoryWork()` 和 `PropertyChanged -=` 之间无 await，同步执行无间隙。且 `StopDirectoryWork` 在取消订阅之前执行，已确保旧任务被取消 |

---

## 四、整体评价

### ✅ 做得好的部分

1. **根因修复正确**：核心问题是 Blazor 组件的 `async void` 事件回调在组件销毁后仍然调用 `InvokeAsync(StateHasChanged)`，通过 try-catch 包裹所有回调，有效防止了 `ObjectDisposedException` 崩溃
2. **DirectoryWork 生成号模式**：通过 generation + CancellationToken 双重检查，优雅地解决了目录切换时过期后台任务的问题
3. **FSEvents 监听统一管理**：`SetWatchedDirectory()` 替代了分散的 Watch/Unwatch 调用，修复了旧代码中 `GoHome`/`NavigateToCollectionAsync` 的 unwatch no-op bug
4. **资源泄漏修复全面**：CTS、NSUrl、CFString/CFArray、JS 事件监听器等资源均有正确的清理路径
5. **事件通知健壮性**：`RaiseTasksChanged`/`RaiseVolumesChanged` 逐订阅者 try-catch 防止一个订阅者异常影响其他订阅者

### ⚠️ 需要注意的部分

1. **FileGridView 信号量无超时**（Finding 1）是唯一可能导致新崩溃/卡死的问题，建议优先修复
2. **MacVolumeMonitorService 线程上下文**（Finding 2）虽然后台 API 通常安全，但改变了原有的线程假设，建议保持一致
3. **缓存管理改进**（MacThumbnailService）引入了较多新代码，建议关注是否有回归

---

## 五、修改文件清单

| 文件 | 变更类型 |
|------|----------|
| AiViewHost.razor | async void 回调加 try-catch |
| TextSearchView.razor | async void 回调加 try-catch |
| FileGridView.razor | disposed 标志 + blob URL 信号量保护 |
| Home.razor | 移除 FSEventsWatcher 注入 + 初始导航恢复 + 键盘事件清理 |
| PreviewPane.razor | async void 回调加 try-catch + generation 计数器修复 |
| FinderSidebar.razor | async void 回调加 try-catch |
| AppDelegate.cs | 导航回调加 try-catch |
| DropOverlayHelper.cs | 新增 UnregisterWebView 方法 |
| NativeDragDropHelper.cs | 线程安全重构 + DetachFromWebView |
| TransparentWebViewHandler.cs | 新增 DisconnectHandler |
| SceneDelegate.cs | 导航回调加 try-catch |
| MacDragDropBridge.cs | 外部/内部 drop 回调加 try-catch |
| MacFSEventsWatcher.cs | CFString/CFArray 在 finally 中释放 |
| MacThumbnailService.cs | 缓存字节限制 + 磁盘清理 + 并发限制 |
| MacVolumeMonitorService.cs | 两阶段确认扫描 + 事件健壮性 + IDisposable |
| BackgroundTaskManager.cs | 事件逐订阅者 try-catch + CTS 清理 |
| AiViewModel.cs | CancellationToken 传播 + 取消任务清理 |
| FileListViewModel.cs | DirectoryWork 生成号模式 + 目录工作取消 |
| NavigationViewModel.cs | SetWatchedDirectory 统一管理 |
| SearchViewModel.cs | CTS 清理 |
| keyboard.js | 事件监听器 dispose 方法 |
