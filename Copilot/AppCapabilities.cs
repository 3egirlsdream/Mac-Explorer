using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO.Compression;
using MacExplorer.Models;
using MacExplorer.Indexing;
using MacExplorer.Services;
using MacExplorer.Services.Search;
using MacExplorer.Services.Impl;
using MacExplorer.Services.Plugins;
using MacExplorer.PluginSdk;
using MacExplorer.ViewModels;
using MacExplorer.Views;

namespace MacExplorer.Copilot;

public enum CapabilityImpact { Read, DiscloseContent, Change, Destructive, RunScript, InstallSoftware }

public sealed record AppCapability(
    string Id, string Name, string Description, string Arguments, string Owner,
    CapabilityImpact Impact, string Confirmation);

public sealed record CapabilityResult(bool Success, string Message, object? Data = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CapabilityPage? Page { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Coverage { get; init; }
}

public sealed record CapabilityPlan(
    string Id, AppCapability Capability, string ArgumentsJson, string Summary,
    IReadOnlyList<string> AffectedPaths, DateTimeOffset CreatedAt);

public interface IAppCapabilityRegistry
{
    IReadOnlyList<AppCapability> Catalog { get; }
    AppCapability? Find(string id);
    Task<CapabilityResult> ExecuteReadAsync(string id, string argumentsJson, FileListViewModel? pane);
    Task<CapabilityPlan> PreviewAsync(string id, string argumentsJson, FileListViewModel? pane);
    Task<CapabilityResult> ExecuteApprovedAsync(string planId, FileListViewModel? pane,
        IProgress<string>? progress = null);
    Task<CapabilityResult> ExecuteUiAsync(string id, string argumentsJson, FileListViewModel pane);
    void CancelPlan(string planId);
}

/// <summary>
/// The registry calls the same operation owner used by the file-list UI. A plan is
/// one-use and bound to the active pane and to the observed file state.
/// </summary>
public sealed class AppCapabilityRegistry(
    IFileService files, IPinnedFolderService pins, ISearchService search,
    IArchiveService archives, PluginManager plugins, PluginMarketClient market, FileDeliveryService delivery,
    IFileTagService tags, IDirectoryChangeNotifier changes, IRemoteConnectionService remoteConnections,
    ISettingsService appSettings, IThemeService themes, IInteractionStyleService interactionStyles,
    CopilotContentExtractor contentExtractor, HomeWorkspaceService homeWorkspace,
    HomeScriptRunner scriptRunner, FileDeliveryController deliveryController,
    IBatchRenameService batchRename, BatchRenameOperationService batchRenameOperation,
    SearchCatalog? indexedSearch = null, IMetadataService? metadata = null, IAiTagService? aiTags = null) : IAppCapabilityRegistry
{
    private sealed record FileStamp(string Path, bool Exists, long Size, DateTime Modified,
        DateTime Created, bool IsDirectory, bool IsSymbolicLink);
    private sealed record Pending(CapabilityPlan Plan, FileListViewModel? Pane, string PanePath,
        FileStamp[] Stamps, string? ScriptSnapshot, string? PluginSnapshot, string? TagSnapshot,
        PluginMarketItem? MarketItem, BatchRenamePlan? BatchRenamePreview,
        CopilotContentExtractor.Page? ContentPage);
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    private static readonly IReadOnlyList<AppCapability> BuiltInCatalog =
    [
        new("file.list", "列出文件", "分页列出目录中的文件和文件夹；每页最多 200 项，使用 Page.NextOffset 继续，列表变化后从 0 重新读取", "{\"path\":\"绝对路径\",\"offset\":0}", "IFileService", CapabilityImpact.Read, "无需确认"),
        new("file.info", "文件信息", "读取名称、类型、大小、创建与修改时间、Finder 标签，以及相机、镜头、曝光、尺寸、拍摄日期、GPS 等现有 EXIF 元数据和有效 AI 分类；不返回正文", "{\"path\":\"绝对路径\"}", "IFileService / IMetadataService / IAiTagService", CapabilityImpact.Read, "无需确认"),
        new("file.search", "搜索文件", "使用现有文件搜索，按名称及设置允许的 AI 标签匹配。支持 ext:pdf、path:目录关键词；结果依赖搜索索引，正文或元数据查询优先使用 file.search-index", "{\"path\":\"目录路径\",\"query\":\"名称关键词 ext:pdf\"}", "ISearchService", CapabilityImpact.Read, "无需确认"),
        new("file.search-fields", "数据库搜索字段", "查看数据库已有 AI 分析字段及文件属性过滤参数，包括 PDF/OCR 文字、图片语义、EXIF 相机、地点、日期、人物、标签和评分；未持久化的 EXIF 通过 file.info 查看", "{}", "SearchCatalog", CapabilityImpact.Read, "无需确认，不返回正文"),
        new("file.search-index", "搜索已有内容与元数据", "优先在现有数据库检索 PDF 正文、图片 OCR、分类、相机、地点、拍摄日期、人脸分组、Finder 标签和评分及文件属性。source=all|name|content|analysis；query 按完整字面片段匹配；tags 多项取交集，type 来自 file.search-fields。日期 from 包含、to 不包含；taken 仅用真实拍摄日期。每页100项，使用 Coverage.NextOffset 继续。返回路径和命中来源，不发送正文、不启动新分析；过期分析不作为命中。无命中不代表未分析文件没有内容", "{\"path\":\"目录绝对路径\",\"query\":\"应用场景概述\",\"source\":\"content\",\"extensions\":[\"pdf\"],\"tags\":[{\"type\":\"camera\",\"value\":\"Canon\"}],\"fileTag\":\"收藏\",\"person\":\"人物名\",\"minRating\":3,\"minSize\":0,\"maxSize\":10485760,\"createdFrom\":\"2026-01-01T00:00:00+08:00\",\"createdTo\":\"2027-01-01T00:00:00+08:00\",\"modifiedFrom\":\"2026-01-01T00:00:00+08:00\",\"modifiedTo\":\"2027-01-01T00:00:00+08:00\",\"takenFrom\":\"2026-01-01\",\"takenTo\":\"2027-01-01\",\"offset\":0}", "SearchCatalog / SQLite existing resources", CapabilityImpact.Read, "无需确认，只返回候选路径和命中来源；参数按需求选填"),
        new("ui.navigate", "打开位置", "在当前窗格打开文件夹或应用位置", "{\"path\":\"位置路径\"}", "FileListViewModel.NavigateToAsync", CapabilityImpact.Read, "无需确认"),
        new("ui.home", "打开首页", "在当前窗格显示首页", "{}", "FileListViewModel.GoHome", CapabilityImpact.Read, "无需确认"),
        new("ui.back", "后退", "回到当前窗格的上一个位置", "{}", "FileListViewModel.NavigateBackAsync", CapabilityImpact.Read, "无需确认"),
        new("ui.forward", "前进", "前往当前窗格的下一个位置", "{}", "FileListViewModel.NavigateForwardAsync", CapabilityImpact.Read, "无需确认"),
        new("ui.up", "上一级", "打开当前窗格的上一级目录", "{}", "FileListViewModel.NavigateUpAsync", CapabilityImpact.Read, "无需确认"),
        new("ui.new-tab", "新建标签页", "在当前窗口新建标签页", "{}", "MainWindow.OpenNewTabAsync", CapabilityImpact.Read, "无需确认"),
        new("ui.new-window", "新建窗口", "在当前路径新建主窗口", "{}", "App.OpenNewWindow", CapabilityImpact.Read, "无需确认"),
        new("ui.pane-layout", "设置窗格布局", "切换当前窗口的窗格布局", "{\"layout\":\"Single|TwoColumns|TwoRows|ThreeColumns|ThreeRows|FourGrid 等 PaneLayout 名称\"}", "MainWindow.SetPaneLayoutAsync", CapabilityImpact.Read, "无需确认"),
        new("ui.settings", "打开设置", "打开应用设置窗口", "{}", "MainWindow.OpenSettings", CapabilityImpact.Read, "无需确认"),
        new("ui.file-delivery", "打开文件速递", "显示已有的文件速递面板", "{}", "FileDeliveryController.ShowPanelAsync", CapabilityImpact.Read, "无需确认"),
        new("ui.refresh", "刷新", "刷新当前窗格", "{}", "FileListViewModel.RefreshAsync", CapabilityImpact.Read, "无需确认"),
        new("ui.search", "当前窗格搜索", "在当前窗格显示文件搜索结果", "{\"query\":\"搜索关键词\"}", "FileListViewModel.SearchAsync", CapabilityImpact.Read, "无需确认"),
        new("ui.view-mode", "切换视图", "设置当前窗格的列表、图标或树形视图", "{\"mode\":\"List|Grid|Tree\"}", "FileListViewModel.SetViewMode", CapabilityImpact.Read, "无需确认"),
        new("ui.sort", "排序", "设置当前窗格排序字段和方向", "{\"field\":\"Name|Modified|Size|Type\",\"ascending\":true}", "FileListViewModel.SetSort", CapabilityImpact.Read, "无需确认"),
        new("ui.preview-pane", "预览侧栏", "显示或隐藏当前窗格的预览面板", "{\"visible\":true}", "FileListViewModel.TogglePreviewPane", CapabilityImpact.Read, "无需确认"),
        new("ui.metadata-panel", "元数据面板", "显示或隐藏当前窗格的元数据面板", "{\"visible\":true}", "FileListViewModel.ToggleMetadataPanel", CapabilityImpact.Read, "无需确认"),
        new("ui.group", "文件分组", "设置当前窗格的分组方式", "{\"field\":\"None|Type|Modified|Size\"}", "FileListViewModel.GroupField", CapabilityImpact.Read, "无需确认"),
        new("ui.preview", "预览文件", "在系统快速预览中打开文件", "{\"path\":\"文件路径\"}", "FileListViewModel.QuickLookPathAsync", CapabilityImpact.Read, "无需确认"),
        new("ui.info-panel", "信息面板", "显示或隐藏当前窗格的文件信息面板，省略 visible 时显示", "{\"visible\":true}", "FileListViewModel.ToggleInfoPanel", CapabilityImpact.Read, "无需确认"),
        new("ui.task-panel", "显示任务中心", "打开已有后台任务面板", "{}", "FileListViewModel.RaiseRequestShowTaskPanel", CapabilityImpact.Read, "无需确认"),
        new("ui.batch-rename-dialog", "打开批量重命名", "生成可编辑规则链并打开专业工作台。selectionId 使用上下文中的选中项目快照，省略时使用当前选中项；也可指定 paths。规则支持 FindReplace、AddPrefix、AddSuffix、Sequence、Date、CaseConversion、InsertText、RemoveText、Template、Cleanup、Extension。模板字段 name、parent、n:000、created:yyyyMMdd、modified:yyyyMMdd、taken:yyyyMMdd；名称默认保留扩展名。", "{\"selectionId\":\"上下文中的快照 ID，可省略\",\"rules\":[{\"type\":\"Template\",\"templateText\":\"项目_{n:000}\"}],\"options\":{\"sort\":\"Input|Name|Created|Modified|PhotoTaken|Manual\",\"descending\":false,\"restartPerDirectory\":false}}", "FileListViewModel.RaiseRequestBatchRename", CapabilityImpact.Read, "用户在工作台预览、编辑并执行"),
        new("ui.compress-dialog", "打开压缩配置", "对当前选中项打开原有压缩配置窗口", "{}", "FileListViewModel.ShowCompressDialog", CapabilityImpact.Read, "由现有窗口收集格式和目标并确认"),
        new("archive.list", "查看压缩包", "列出压缩包中的条目", "{\"path\":\"压缩包路径\"}", "IArchiveService", CapabilityImpact.Read, "无需确认"),
        new("archive.extract-here", "解压到当前文件夹", "使用原有解压流程，在压缩包所在文件夹解压", "{\"path\":\"压缩包路径\"}", "ArchiveViewModel.ExtractHereAsync", CapabilityImpact.Change, "展示压缩包及目标文件夹后确认"),
        new("archive.extract-folder", "解压到同名文件夹", "使用原有解压流程，在同名文件夹解压", "{\"path\":\"压缩包路径\"}", "ArchiveViewModel.ExtractToNamedFolderAsync", CapabilityImpact.Change, "展示压缩包及新文件夹后确认"),
        new("archive.compress", "压缩文件", "打开原有压缩配置窗口供用户选择格式和输出", "{\"path\":\"文件路径\"}", "ArchiveViewModel.ShowCompressDialog", CapabilityImpact.Change, "展示来源后确认，再由现有窗口收集配置"),
        new("plugin.list", "列出插件", "查看已安装插件与命令", "{}", "PluginManager", CapabilityImpact.Read, "无需确认"),
        new("plugin.market-list", "搜索插件市场", "从现有插件市场查询插件信息", "{\"query\":\"关键词\",\"page\":0}", "PluginMarketClient.ListAsync", CapabilityImpact.Read, "无需确认"),
        new("sftp.servers", "列出远程连接", "查看已保存连接的名称、ID 和连接状态，不返回密码或密钥", "{}", "IRemoteConnectionService.GetSavedServers", CapabilityImpact.Read, "无需确认"),
        new("sftp.connect", "连接已保存服务器", "连接已有的 SFTP 服务器并在当前窗格打开默认位置", "{\"id\":\"已保存服务器 ID\"}", "FileListViewModel.ConnectToServerAsync", CapabilityImpact.Read, "无需确认；需要凭据时使用现有连接窗口"),
        new("sftp.connect-dialog", "打开远程连接窗口", "打开应用已有的远程连接窗口收集主机与凭据", "{}", "FileListViewModel.RaiseRequestRemoteConnection", CapabilityImpact.Read, "无需确认"),
        new("sftp.disconnect", "断开远程连接", "断开已有 SFTP 连接", "{\"id\":\"已保存服务器 ID\"}", "FileListViewModel.DisconnectServer", CapabilityImpact.Read, "无需确认"),
        new("script.list", "列出已有脚本命令", "只列出某脚本文件已经配置的命令名称和 ID", "{\"path\":\"脚本文件路径\"}", "HomeWorkspaceService.GetCommands", CapabilityImpact.Read, "无需确认"),
        new("delivery.list", "文件速递位置", "查看文件速递中的目录与收藏夹", "{}", "FileDeliveryService", CapabilityImpact.Read, "无需确认"),
        new("settings.list", "查看常用设置", "只查看安全白名单中的显示与操作偏好，不读取密钥或远程凭据", "{}", "FileListViewModel / IThemeService", CapabilityImpact.Read, "无需确认"),
        new("settings.toggle", "切换常用设置", "修改白名单中的显示与操作偏好", "{\"key\":\"hide-system|hide-dot-files|hide-dot-folders|confirm-trash|double-click-up|ai-analysis|folder-covers\",\"enabled\":true}", "FileListViewModel / ISettingsService", CapabilityImpact.Change, "展示设置名与新状态后确认"),
        new("settings.theme", "设置主题", "切换系统、浅色或深色主题", "{\"mode\":\"system|light|dark\"}", "IThemeService.SetThemeMode", CapabilityImpact.Change, "展示主题后确认"),
        new("tag.list", "列出标签", "查看侧栏标签", "{}", "IFileTagService", CapabilityImpact.Read, "无需确认"),
        new("tag.files", "列出收藏夹文件", "分页列出标签中的文件路径；使用 Page.NextOffset 继续，标签变化后从 0 重新读取", "{\"tag\":\"标签名称\",\"offset\":0}", "IFileTagService.FindFilePathsAsync", CapabilityImpact.Read, "无需确认"),
        new("file.tags", "文件标签", "查看文件已有标签", "{\"path\":\"文件路径\"}", "IFileTagService", CapabilityImpact.Read, "无需确认"),
        new("file.content", "读取文件正文", "必须 PreviewOperation → ExecuteApprovedPlan，不能 CallReadOnly。优先复用未过期的数据库 PDF/OCR 正文，缺失时本地提取文本、PDF、图片或 Office；每页最多50 KB，按 NextOffset 继续；Office 有解压资源与100万字符上限；offset 默认0", "{\"path\":\"本地文件路径\",\"offset\":0}", "CopilotContentExtractor", CapabilityImpact.DiscloseContent, "逐次展示发送范围并确认"),
        new("file.rating", "设置文件评分", "使用已有评分逻辑设置 0 到 5 星", "{\"path\":\"本地文件路径\",\"rating\":3}", "FileListViewModel.SetRatingAsync", CapabilityImpact.Change, "展示文件和评分后确认"),
        new("folder.pins", "列出收藏文件夹", "返回已固定的文件夹", "{}", "IPinnedFolderService", CapabilityImpact.Read, "无需确认"),
        new("folder.create", "新建文件夹", "在指定目录创建命名文件夹", "{\"path\":\"父目录\",\"name\":\"文件夹名称\"}", "IFileService.CreateFolderAsync", CapabilityImpact.Change, "展示目标路径后确认"),
        new("folder.create-unnamed", "新建未命名文件夹", "在活动窗格按现有自动命名规则创建文件夹并进入重命名", "{}", "FileOpsViewModel.CreateNewFolderAsync", CapabilityImpact.Change, "展示当前目录后确认"),
        new("file.create-text", "生成文本文件", "保存新的 .txt 或 .md 文件", "{\"path\":\"父目录\",\"name\":\"文件名\",\"content\":\"正文\"}", "IFileService.CreateFileWithContentAsync", CapabilityImpact.Change, "展示文件名、大小和正文预览后确认"),
        new("file.rename", "重命名", "重命名一个现有文件或文件夹", "{\"path\":\"绝对路径\",\"newName\":\"新名称\"}", "FileOpsViewModel.RenameEntryAsync", CapabilityImpact.Change, "展示旧名和新名后确认"),
        new("file.batch-rename", "批量重命名", "用规则链处理显式指定的本地项目，可跨目录。支持 rules 数组、排序 options；兼容旧单条 rule。自然语言生成方案优先使用 ui.batch-rename-dialog。", "{\"paths\":[\"文件路径\"],\"rules\":[{\"type\":\"AddPrefix\",\"prefixText\":\"前缀\"}],\"options\":{\"sort\":\"Input\"}}", "IBatchRenameService / BatchRenameOperationService", CapabilityImpact.Change, "逐项展示新旧文件名及冲突后确认"),
        new("file.move", "移动文件", "将文件移到目标文件夹", "{\"path\":\"绝对路径\",\"destination\":\"目标文件夹路径\"}", "FileOpsViewModel.MoveEntryAsync", CapabilityImpact.Change, "展示来源、目标和冲突后确认"),
        new("file.copy", "复制文件", "复制文件或文件夹到目标文件夹，沿用粘贴的进度与标签处理", "{\"path\":\"绝对路径\",\"destination\":\"目标文件夹路径\"}", "FileOperationService.CopyAsync", CapabilityImpact.Change, "展示来源、目标和冲突后确认"),
        new("file.trash", "移到废纸篓", "将文件移到废纸篓；远程文件由现有服务处理", "{\"path\":\"绝对路径\"}", "FileOpsViewModel.DeleteSelectedAsync", CapabilityImpact.Destructive, "单独确认"),
        new("file.delete-permanent", "永久删除", "永久删除本地文件或文件夹", "{\"path\":\"本地文件绝对路径\"}", "FileListViewModel.PermanentlyDeleteCheckedAsync", CapabilityImpact.Destructive, "醒目确认，操作不可恢复"),
        new("script.run", "运行已有脚本命令", "运行首页中已经配置的脚本命令", "{\"path\":\"脚本文件路径\",\"id\":\"已配置命令 ID\"}", "HomeScriptRunner.RunAsync", CapabilityImpact.RunScript, "展示脚本文件和命令名称，单独醒目确认"),
        new("folder.pin", "收藏文件夹", "将文件夹固定到侧栏", "{\"path\":\"绝对路径\"}", "FileOpsViewModel.PinFolderAsync", CapabilityImpact.Change, "展示文件夹后确认"),
        new("folder.unpin", "取消收藏", "取消侧栏中的固定文件夹", "{\"path\":\"绝对路径\"}", "FileOpsViewModel.UnpinFolderAsync", CapabilityImpact.Change, "展示文件夹后确认"),
        new("tag.create", "新建标签", "创建 Finder 标签", "{\"name\":\"标签名称\"}", "IFileTagService.CreateTagAsync", CapabilityImpact.Change, "展示名称后确认"),
        new("tag.rename", "重命名收藏夹", "重命名自定义标签或收藏夹", "{\"name\":\"原标签名\",\"newName\":\"新标签名\"}", "IFileTagService.RenameTagAsync", CapabilityImpact.Change, "展示旧名和新名后确认"),
        new("tag.delete", "删除收藏夹", "删除自定义标签或收藏夹，不删除原文件", "{\"name\":\"标签名称\"}", "IFileTagService.DeleteTagAsync", CapabilityImpact.Change, "展示受影响的文件数量后确认"),
        new("tag.pin", "固定标签", "在侧栏固定或取消固定标签", "{\"name\":\"标签名称\",\"pinned\":true}", "IFileTagService.SetTagPinnedAsync", CapabilityImpact.Change, "展示标签后确认"),
        new("tag.color", "设置标签颜色", "为自定义标签设置 Finder 颜色 ID 0 到 7", "{\"name\":\"标签名称\",\"colorId\":4}", "IFileTagService.SetTagColorAsync", CapabilityImpact.Change, "展示标签和新颜色后确认"),
        new("tag.apply", "设置文件标签", "为文件添加或移除一个已有标签", "{\"path\":\"文件路径\",\"tag\":\"标签名称\",\"applied\":true}", "IFileTagService.SetTagAsync", CapabilityImpact.Change, "展示文件及标签后确认"),
        new("plugin.enable", "启用或禁用插件", "切换已安装插件状态", "{\"id\":\"插件 ID\",\"enabled\":true}", "PluginManager.SetEnabledAsync", CapabilityImpact.Change, "展示插件和状态后确认"),
        new("plugin.install", "安装插件", "安装本地 .mexplug 插件包", "{\"path\":\"本地插件包路径\"}", "PluginManager.InstallAsync", CapabilityImpact.InstallSoftware, "展示插件来源、名称和版本，单独确认"),
        new("plugin.market-install", "安装或更新市场插件", "按市场目录中的插件 ID 和版本下载、校验并安装", "{\"id\":\"插件 ID\",\"version\":\"市场版本\"}", "PluginMarketClient.InstallAsync", CapabilityImpact.InstallSoftware, "展示来源、开发者、版本和校验信息后单独确认"),
        new("plugin.uninstall", "卸载插件", "卸载已安装的插件", "{\"id\":\"插件 ID\"}", "PluginManager.UninstallAsync", CapabilityImpact.Destructive, "展示插件及正在运行的任务后确认"),
        new("plugin.restore-built-in", "恢复内置插件", "恢复内置文件转换插件", "{}", "PluginManager.RestoreBuiltInAsync", CapabilityImpact.InstallSoftware, "展示内置插件后单独确认"),
        new("plugin.cancel", "取消插件任务", "中止正在运行的插件命令", "{\"id\":\"插件 ID\"}", "PluginManager.CancelAsync", CapabilityImpact.Change, "展示任务后确认"),
        new("delivery.add-folder", "加入文件速递", "将本地目录加入文件速递", "{\"path\":\"文件夹路径\"}", "FileDeliveryService.AddFolder", CapabilityImpact.Change, "展示文件夹后确认"),
        new("delivery.add-tag", "将标签加入文件速递", "将已有标签作为文件速递位置", "{\"tag\":\"标签名称\"}", "FileDeliveryService.AddTag", CapabilityImpact.Change, "展示标签后确认"),
        new("delivery.remove", "移除文件速递位置", "从文件速递移除页签，不删除原文件", "{\"id\":\"页签 ID\"}", "FileDeliveryService.Remove", CapabilityImpact.Change, "展示页签后确认"),
        new("delivery.move", "调整文件速递顺序", "将文件速递页签向前或向后移动一步", "{\"id\":\"页签 ID\",\"delta\":1}", "FileDeliveryService.Move", CapabilityImpact.Change, "展示页签和方向后确认"),
        new("delivery.enabled", "设置文件速递开关", "启用或停用文件速递", "{\"enabled\":true}", "FileDeliveryService.Enabled", CapabilityImpact.Change, "展示新状态后确认")
    ];

    private static readonly HashSet<string> PathlessCapabilities =
        ["folder.create-unnamed", "tag.create", "tag.rename", "tag.delete", "tag.pin", "tag.color",
            "plugin.enable", "plugin.uninstall", "plugin.restore-built-in", "plugin.cancel", "plugin.market-install",
            "settings.toggle", "settings.theme",
            "delivery.add-tag", "delivery.remove", "delivery.move", "delivery.enabled"];

    private static readonly HashSet<string> WritablePaneCapabilities =
        ["file.rename", "file.batch-rename", "file.move", "file.copy", "file.trash", "file.delete-permanent", "file.rating",
            "file.create-text", "folder.create", "folder.create-unnamed", "archive.extract-here", "archive.extract-folder",
            "archive.compress", "tag.apply"];

    public IReadOnlyList<AppCapability> Catalog => plugins == null ? ChannelCatalog :
        [..ChannelCatalog, ..plugins.Plugins.Where(p => !p.Removed).SelectMany(p => p.Manifest.Commands.Select(command =>
            new AppCapability(PluginCommandId(p.Manifest.Id, command.Id), command.Title,
                $"通过已安装插件 {p.Manifest.Name} 处理本地文件", "{\"paths\":[\"本地文件绝对路径\"]}",
                "FileListViewModel.ExecutePluginCommandAsync", CapabilityImpact.Change,
                "展示插件、命令、输入文件以及生成位置后确认")))];

    private static IReadOnlyList<AppCapability> ChannelCatalog => DistributionChannel.IsAppStore
        ? BuiltInCatalog.Where(capability => capability.Id != "script.run" && !capability.Id.StartsWith("plugin.", StringComparison.Ordinal)
            && !capability.Id.Contains("terminal", StringComparison.Ordinal)).ToArray() : BuiltInCatalog;

    public static string PluginCommandId(string pluginId, string commandId)
        => $"plugin.command:{Uri.EscapeDataString(pluginId)}:{Uri.EscapeDataString(commandId)}";

    private (string PluginId, PluginCommand Command) ResolvePluginCommand(string id)
    {
        foreach (var plugin in plugins.Plugins.Where(p => p.Enabled && !p.Removed))
            foreach (var command in plugin.Manifest.Commands)
                if (PluginCommandId(plugin.Manifest.Id, command.Id) == id)
                    return (plugin.Manifest.Id, command);
        throw new InvalidOperationException("插件命令已禁用或不再存在。");
    }

    /// <summary>Existing UI confirmation is the authorization for plugin commands. This route is never a model tool.</summary>
    public async Task<CapabilityResult> ExecuteUiAsync(
        string id, string argumentsJson, FileListViewModel pane)
    {
        if (id == "folder.create-unnamed") return await CreateUnnamedFolderAsync(pane);
        if (!id.StartsWith("plugin.command:", StringComparison.Ordinal))
            return await ExecuteReadAsync(id, argumentsJson, pane);
        using var document = JsonDocument.Parse(argumentsJson);
        return await InvokePluginCommandAsync(id, RequiredPaths(document.RootElement), pane);
    }

    private async Task<CapabilityResult> InvokePluginCommandAsync(
        string id, string[] paths, FileListViewModel pane)
    {
        var (pluginId, command) = ResolvePluginCommand(id);
        var selected = paths.Select(path => new PluginFile(path)).ToArray();
        if (!command.Match.Matches(selected) || paths.Any(path => !File.Exists(path)))
            throw new InvalidOperationException("插件命令与所选本地文件不再匹配。");
        var execution = await pane.ExecutePluginCommandAsync(pluginId, command, selected);
        return new(execution.Success, execution.Message,
            new { execution.Outputs, execution.Warnings });
    }

    private static async Task<CapabilityResult> CreateUnnamedFolderAsync(FileListViewModel pane)
    {
        if (pane.IsBrowseOnly) throw new InvalidOperationException("当前窗格只允许浏览。");
        if (string.IsNullOrWhiteSpace(pane.CurrentPath) || pane.IsSearchMode)
            throw new InvalidOperationException("请先在活动窗格打开目标文件夹。");
        await pane.FileOps.CreateNewFolderAsync(pane.CurrentPath, pane.Entries.ToList(),
            setStatus: message => pane.StatusText = message,
            refreshCallback: pane.RefreshAfterCreateAsync);
        return new(true, $"已在 {pane.CurrentPath} 创建文件夹：{pane.PendingSelectFileName}");
    }

    public AppCapability? Find(string id) => Catalog.FirstOrDefault(x => x.Id == id);

    public async Task<CapabilityResult> ExecuteReadAsync(string id, string argumentsJson, FileListViewModel? pane)
    {
        var capability = Find(id) ?? throw new ArgumentException($"未知能力：{id}", nameof(id));
        if (capability.Impact != CapabilityImpact.Read)
            throw new InvalidOperationException("此能力需要先预览并确认。");
        using var document = JsonDocument.Parse(argumentsJson);
        var args = document.RootElement;
        switch (id)
        {
            case "file.list":
            {
                var path = Required(args, "path");
                var entries = await files.GetDirectoryContentsAsync(path);
                return CapabilityPagination.Create(
                    entries.OrderBy(e => e.FullPath, StringComparer.Ordinal).ToArray(), args, e => new
                    {
                        e.FullPath, e.Name, e.IsDirectory, e.Size, e.LastModified
                    });
            }
            case "file.info":
            {
                var path = Required(args, "path");
                var entry = await files.GetEntryAsync(path);
                if (entry == null) return new(false, "文件不存在");
                var details = metadata == null ? null : await metadata.GetMetadataAsync(path);
                var image = details?.ImageInfo;
                var analysis = aiTags != null && File.Exists(path)
                    && await aiTags.IsFileAnalyzedAsync(path, File.GetLastWriteTime(path).Ticks)
                    ? (await aiTags.GetTagsForFileAsync(path)).Where(t => t.TagType is not ("text" or "text_summary"))
                        .Take(100).Select(t => new { t.TagType, t.TagValue, t.Confidence }).ToArray() : null;
                return new(true, "已读取文件信息与现有元数据（不含正文）", new
                {
                    entry.FullPath, entry.Name, entry.IsDirectory, entry.Size, entry.Created, entry.LastModified,
                    details?.LastAccessed, details?.Kind, details?.ContentType, details?.Tags,
                    Image = image == null ? null : new
                    {
                        image.PixelWidth, image.PixelHeight, image.ColorSpace, image.CameraMake, image.CameraModel,
                        image.LensModel, image.FocalLength, image.Aperture, image.ExposureTime, image.IsoSpeed,
                        image.WhiteBalance, image.Flash, image.ExposureProgram, image.MeteringMode,
                        image.PhotoTakenDate, image.Latitude, image.Longitude, image.Altitude
                    }, Analysis = analysis
                });
            }
            case "file.search-fields":
                if (indexedSearch == null) return new(false, "数据库搜索服务不可用。");
                return new(true, "已有数据库字段；所有筛选条件取交集，未持久化 EXIF 可通过 file.info 查看。", new
                {
                    AnalysisTagTypes = await indexedSearch.GetAnalysisFieldsAsync(),
                    Filters = new[] { "query", "source", "extensions", "tags[{type,value}]", "fileTag", "person",
                        "minRating", "minSize", "maxSize", "createdFrom", "createdTo", "modifiedFrom", "modifiedTo",
                        "takenFrom", "takenTo", "offset" },
                    Sources = new[] { "all", "name", "content", "analysis" },
                    DateRange = "from 包含，to 不包含；文件日期使用 ISO 8601 含时区，拍摄日期使用 yyyy-MM-dd。"
                });
            case "file.search-index":
            {
                if (indexedSearch == null) return new(false, "数据库搜索服务不可用。");
                var query = JsonSerializer.Deserialize<IndexedFileQuery>(argumentsJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                var options = new SearchOptions(appSettings?.Get("HideSystemFiles", true) ?? true,
                    appSettings?.Get("HideDotFiles", true) ?? true, appSettings?.Get("HideDotFolders", true) ?? true);
                var result = await indexedSearch.SearchResourcesAsync(Required(args, "path"), query, options);
                return new(true, $"现有数据库本页找到 {result.Items.Count} 项。只覆盖已索引属性与已分析内容；未分析、已过期或尚未索引的文件仍可能符合条件。", result.Items)
                {
                    Coverage = new { Scope = "existing-database", result.Offset, result.NextOffset, result.HasMore,
                        result.AnalyzedFiles, result.IndexStatus, Content = "仅匹配未过期的已有分析，不返回正文；无命中不能证明文件不存在。" }
                };
            }
            case "file.search":
            {
                var results = new List<object>();
                await foreach (var entry in search.SearchAsync(Required(args, "path"), Required(args, "query"), 100))
                    results.Add(new { entry.FullPath, entry.Name, entry.IsDirectory, entry.Size });
                return new(true, results.Count == 100
                    ? "返回前 100 项搜索结果，可能还有更多；请缩小搜索范围，不能将此结果视为全部文件。"
                    : $"找到 {results.Count} 项", results);
            }
            case "ui.navigate":
                if (pane == null) throw new InvalidOperationException("请先激活文件窗格。");
                await pane.NavigateToAsync(Required(args, "path"));
                return new(true, $"已打开：{pane.CurrentPath}");
            case "ui.home":
                if (pane == null) throw new InvalidOperationException("请先激活文件窗格。");
                pane.GoHome();
                return new(true, "已打开首页。");
            case "ui.back":
                if (pane == null || !pane.CanGoBack) return new(false, "当前窗格没有上一位置。");
                await pane.NavigateBackAsync();
                return new(true, $"已返回：{pane.CurrentPath}");
            case "ui.forward":
                if (pane == null || !pane.CanGoForward) return new(false, "当前窗格没有下一位置。");
                await pane.NavigateForwardAsync();
                return new(true, $"已前往：{pane.CurrentPath}");
            case "ui.up":
                if (pane == null) throw new InvalidOperationException("请先激活文件窗格。");
                await pane.NavigateUpAsync();
                return new(true, $"当前位置：{pane.CurrentPath}");
            case "ui.new-tab":
                await RequireMainWindow(pane).OpenNewTabAsync();
                return new(true, "已新建标签页。");
            case "ui.new-window":
                if (pane == null) throw new InvalidOperationException("请先激活文件窗格。");
                App.OpenNewWindow(Directory.Exists(pane.CurrentPath) ? pane.CurrentPath : null);
                return new(true, "已新建窗口。");
            case "ui.pane-layout":
                var layoutName = Required(args, "layout");
                if (!Enum.TryParse<PaneLayout>(layoutName, false, out var layout)
                    || !Enum.IsDefined(layout))
                    throw new ArgumentException("未知窗格布局；请使用 PaneLayout 名称。");
                await RequireMainWindow(pane).SetPaneLayoutAsync(layout);
                return new(true, $"已切换窗格布局：{layout}");
            case "ui.settings":
                RequireMainWindow(pane).OpenSettings();
                return new(true, "已打开设置窗口。");
            case "ui.file-delivery":
                if (!delivery.Enabled) return new(false, "文件速递已停用，请先在设置中启用。");
                await deliveryController.ShowPanelAsync();
                return new(true, "已打开文件速递面板。");
            case "ui.refresh":
                if (pane == null) throw new InvalidOperationException("请先激活文件窗格。");
                await pane.RefreshAsync();
                return new(true, "当前窗格已刷新。");
            case "ui.search":
                if (pane == null) throw new InvalidOperationException("请先激活文件窗格。");
                await pane.SearchAsync(Required(args, "query"));
                return new(true, "已在当前窗格搜索。", new { pane.CurrentPath, pane.StatusText });
            case "ui.view-mode":
                if (pane == null) throw new InvalidOperationException("请先激活文件窗格。");
                if (!Enum.TryParse<ViewMode>(Required(args, "mode"), false, out var mode)
                    || !Enum.IsDefined(mode)) throw new ArgumentException("未知视图模式。");
                pane.SetViewMode(mode);
                return new(true, $"已切换视图：{mode}");
            case "ui.sort":
                if (pane == null) throw new InvalidOperationException("请先激活文件窗格。");
                if (!Enum.TryParse<SortField>(Required(args, "field"), false, out var field)
                    || !Enum.IsDefined(field)) throw new ArgumentException("未知排序字段。");
                pane.SetSort(field, args.TryGetProperty("ascending", out var direction) ? Boolean(args, "ascending") : null);
                return new(true, $"已按 {field} 排序。");
            case "ui.preview-pane":
                if (pane == null) throw new InvalidOperationException("请先激活文件窗格。");
                if (pane.IsPreviewPaneVisible != Boolean(args, "visible")) pane.TogglePreviewPane();
                return new(true, pane.IsPreviewPaneVisible ? "已显示预览面板。" : "已隐藏预览面板。");
            case "ui.metadata-panel":
                if (pane == null) throw new InvalidOperationException("请先激活文件窗格。");
                if (pane.IsMetadataPanelVisible != Boolean(args, "visible")) pane.ToggleMetadataPanel();
                return new(true, pane.IsMetadataPanelVisible ? "已显示元数据面板。" : "已隐藏元数据面板。");
            case "ui.group":
                if (pane == null) throw new InvalidOperationException("请先激活文件窗格。");
                if (!Enum.TryParse<GroupField>(Required(args, "field"), false, out var group)
                    || !Enum.IsDefined(group)) throw new ArgumentException("未知分组方式。");
                pane.GroupField = group;
                return new(true, $"已设置分组：{group}");
            case "ui.info-panel":
                if (pane == null) throw new InvalidOperationException("请先激活文件窗格。");
                var visible = !args.TryGetProperty("visible", out _) || Boolean(args, "visible");
                if (pane.IsInfoPanelVisible != visible) pane.ToggleInfoPanel();
                return new(true, pane.IsInfoPanelVisible ? "已显示信息面板。" : "已隐藏信息面板。");
            case "ui.task-panel":
                if (pane == null) throw new InvalidOperationException("请先激活文件窗格。");
                pane.RaiseRequestShowTaskPanel();
                return new(true, "已显示任务中心。");
            case "ui.preview":
                if (pane == null) throw new InvalidOperationException("请先激活文件窗格。");
                var previewPath = Required(args, "path");
                if (!await files.ExistsAsync(previewPath)) throw new FileNotFoundException("文件不存在。", previewPath);
                await pane.QuickLookPathAsync(previewPath);
                return new(true, $"已预览：{previewPath}");
            case "ui.batch-rename-dialog":
                if (pane == null || pane.IsBrowseOnly || pane.IsArchiveView || pane.IsTrashActive)
                    throw new InvalidOperationException("当前窗格无法批量重命名。");
                var targets = args.TryGetProperty("selectionId", out var selectionId)
                    ? pane.GetBatchRenameSelection(selectionId.GetString() ?? "").Entries : pane.CreateBatchRenameRequest().Entries;
                if (args.TryGetProperty("paths", out _))
                {
                    var explicitEntries = await Task.WhenAll(RequiredPaths(args).Select(files.GetEntryAsync));
                    if (explicitEntries.Any(entry => entry == null)) throw new FileNotFoundException("源项目已不存在。");
                    targets = explicitEntries.Select(entry => entry!).ToArray();
                }
                if (targets.Count == 0) return new(false, "请先选择文件或文件夹。");
                if (targets.Any(entry => !FileListViewModel.IsBatchRenameTarget(entry)))
                    throw new InvalidOperationException("批量重命名仅支持本地文件和文件夹。");
                pane.RaiseRequestBatchRename(pane.CreateBatchRenameRequest(ParseBatchRenameRules(args, optional: true), ParseBatchRenameOptions(args), targets));
                return new(true, "已打开批量重命名窗口，请查看预览并确认。");
            case "ui.compress-dialog":
                if (pane == null || pane.IsBrowseOnly) throw new InvalidOperationException("当前窗格无法压缩文件。");
                if (pane.SelectedEntries.Count == 0) return new(false, "请先选择要压缩的文件或文件夹。");
                pane.ShowCompressDialog();
                return new(true, "已打开压缩配置窗口，请在窗口中完成格式和目标设置。");
            case "archive.list":
            {
                var entries = await archives.GetArchiveContentsAsync(Required(args, "path"));
                return new(true, $"压缩包中有 {entries.Count} 项",
                    entries.Take(200).Select(e => new { e.FullPath, e.Name, e.IsDirectory, e.Size }).ToArray());
            }
            case "plugin.list":
                return new(true, "已读取插件目录", plugins.Plugins.Select(p => new
                {
                    p.Manifest.Id, p.Manifest.Name, p.Enabled, p.Status, p.LastError,
                    Commands = p.Manifest.Commands.Select(c => new { c.Id, c.Title }).ToArray()
                }).ToArray());
            case "plugin.market-list":
            {
                var query = args.TryGetProperty("query", out var keyword) ? keyword.GetString() ?? "" : "";
                var pageIndex = args.TryGetProperty("page", out var pageValue) && pageValue.TryGetInt32(out var number)
                    ? number : 0;
                if (pageIndex is < 0 or > 20) throw new ArgumentException("市场页码须在 0 到 20 之间。");
                var page = await market.ListAsync(query, pageIndex, CancellationToken.None);
                return new(true, $"市场返回 {page.Items.Length} 项", new
                {
                    page.HasMore,
                    Items = page.Items.Select(item => new
                    {
                        item.Manifest.Id, item.Manifest.Name, item.Manifest.Version,
                        item.Manifest.Developer, item.Manifest.Description, item.Manifest.Paid,
                        item.Manifest.TrialDays, item.Size, item.Sha256
                    }).ToArray()
                });
            }
            case "sftp.servers":
                return new(true, "已读取远程连接列表", remoteConnections.GetSavedServers().Select(server => new
                {
                    server.Id, server.DisplayName, IsConnected = remoteConnections.IsConnected(server.Id)
                }).ToArray());
            case "sftp.connect-dialog":
                if (pane == null) throw new InvalidOperationException("请先激活文件窗格。");
                pane.RaiseRequestRemoteConnection();
                return new(true, "已打开远程连接窗口，请在窗口中填写凭据。");
            case "sftp.connect":
            {
                if (pane == null) throw new InvalidOperationException("请先激活文件窗格。");
                var server = remoteConnections.GetSavedServers().FirstOrDefault(x => x.Id == Required(args, "id"))
                    ?? throw new ArgumentException("服务器不存在。");
                await pane.ConnectToServerAsync(server);
                return new(remoteConnections.IsConnected(server.Id), pane.StatusText,
                    new { server.Id, server.DisplayName, pane.CurrentPath });
            }
            case "sftp.disconnect":
            {
                if (pane == null) throw new InvalidOperationException("请先激活文件窗格。");
                var serverId = Required(args, "id");
                if (!remoteConnections.GetSavedServers().Any(server => server.Id == serverId))
                    throw new ArgumentException("服务器不存在。");
                pane.DisconnectServer(serverId);
                return new(true, "已断开远程连接。");
            }
            case "script.list":
                return new(true, "已读取已有脚本命令",
                    homeWorkspace.GetCommands(Required(args, "path")).Select(c => new { c.Id, c.Name }).ToArray());
            case "delivery.list":
                return new(true, "已读取文件速递位置", delivery.Preferences.Entries.ToArray());
            case "settings.list":
                if (pane == null) throw new InvalidOperationException("请先激活文件窗格。");
                return new(true, "已读取常用设置", new
                {
                    Theme = themes.GetThemeMode(),
                    HideSystem = pane.HideSystemFiles, HideDotFiles = pane.HideDotFiles,
                    HideDotFolders = pane.HideDotFolders, ConfirmTrash = pane.ConfirmBeforeTrash,
                    DoubleClickUp = pane.DoubleClickEmptyAreaGoUp,
                    AiAnalysis = pane.IsAiAnalysisEnabled,
                    FolderCovers = appSettings.Get(FileListViewModel.FolderPhotoCoverSettingKey, false)
                });
            case "tag.list":
                return new(true, "已读取标签", await tags.GetSidebarTagsAsync());
            case "tag.files":
                var match = (await tags.GetSidebarTagsAsync()).FirstOrDefault(item => item.Name == Required(args, "tag"))
                    ?? throw new ArgumentException("标签不存在。");
                var taggedPaths = await tags.FindFilePathsAsync(match);
                return CapabilityPagination.Create(
                    taggedPaths.OrderBy(path => path, StringComparer.Ordinal).ToArray(), args, path => path);
            case "file.tags":
                return new(true, "已读取文件标签", await tags.GetFileTagsAsync(Required(args, "path")));
            case "folder.pins":
                return new(true, "已读取收藏文件夹", await pins.GetAllAsync());
            default:
                throw new InvalidOperationException("能力未实现。");
        }
    }

    public async Task<CapabilityPlan> PreviewAsync(string id, string argumentsJson, FileListViewModel? pane)
    {
        var capability = Find(id) ?? throw new ArgumentException($"未知能力：{id}", nameof(id));
        if (capability.Impact == CapabilityImpact.Read)
            throw new InvalidOperationException("只读能力无需审批。");
        if (pane == null) throw new InvalidOperationException("请先激活文件窗格。");
        if (pane.IsBrowseOnly && (WritablePaneCapabilities.Contains(id)
            || id.StartsWith("plugin.command:", StringComparison.Ordinal)))
            throw new InvalidOperationException("当前窗格只允许浏览。");
        using var document = JsonDocument.Parse(argumentsJson);
        var args = document.RootElement;
        var isPluginCommand = id.StartsWith("plugin.command:", StringComparison.Ordinal);
        var isBatchRename = id == "file.batch-rename";
        var operationPaths = isPluginCommand || isBatchRename ? RequiredPaths(args) : [];
        if (isBatchRename && (operationPaths.Length < 1
            || operationPaths.Distinct(StringComparer.Ordinal).Count() != operationPaths.Length))
            throw new ArgumentException("批量重命名需要不同的本地项目。");
        var path = PathlessCapabilities.Contains(id) ? string.Empty
            : isPluginCommand || isBatchRename ? operationPaths[0] : Required(args, "path");
        var source = string.IsNullOrEmpty(path) ? null : await files.GetEntryAsync(path);
        if (source == null && !PathlessCapabilities.Contains(id))
            throw new FileNotFoundException("源文件或父目录不存在。", path);
        if (id is "file.rename" or "file.move" or "file.copy" or "file.trash" or "file.delete-permanent"
            or "archive.extract-here" or "archive.extract-folder" or "archive.compress"
            or "file.batch-rename" || isPluginCommand)
        {
            var sourcePaths = isPluginCommand || isBatchRename ? operationPaths : [path];
            if (!isBatchRename && sourcePaths.Any(item => files.GetParentPath(item) != pane.CurrentPath))
                throw new InvalidOperationException("请先在活动窗格打开源文件所在文件夹，再预览操作。");
        }
        string summary;
        PluginMarketItem? marketItem = null;
        BatchRenamePlan? batchPreview = null;
        CopilotContentExtractor.Page? contentPage = null;
        FileStamp? contentSourceStamp = null;
        var observed = isPluginCommand || isBatchRename ? operationPaths.ToList()
            : string.IsNullOrEmpty(path) ? new List<string>() : new List<string> { path };
        switch (id)
        {
            case "folder.create-unnamed":
                var currentFolder = await files.GetEntryAsync(pane.CurrentPath);
                if (currentFolder?.IsDirectory != true)
                    throw new DirectoryNotFoundException("当前窗格未打开可写的文件夹。");
                observed.Add(pane.CurrentPath);
                summary = $"在当前目录 {pane.CurrentPath} 按现有自动命名规则创建文件夹，并进入重命名。";
                break;
            case "folder.create":
            case "file.create-text":
            {
                if (source!.IsDirectory != true) throw new InvalidOperationException("父目录不是文件夹。");
                var name = Required(args, "name");
                ValidateNewName(name);
                if (id == "file.create-text" && !name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                    && !name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("首版生成文件仅支持 .txt 和 .md。");
                var target = files.CombinePath(path, name);
                if (await files.ExistsAsync(target)) throw new IOException("目标名称已存在。");
                observed.Add(target);
                var content = id == "file.create-text" ? Required(args, "content") : null;
                if (content?.Length > 128_000) throw new ArgumentException("文件正文超过 128000 字符。");
                summary = id == "folder.create" ? $"新建文件夹：{target}"
                    : $"生成文件：{target}（{content!.Length} 字符）\n正文预览：{content[..Math.Min(content.Length, 300)]}";
                break;
            }
            case "file.rename":
            {
                var name = Required(args, "newName");
                ValidateNewName(name);
                var target = files.CombinePath(files.GetParentPath(path), name);
                if (await files.ExistsAsync(target)) throw new IOException("目标名称已存在。");
                observed.Add(target);
                summary = $"重命名：{path} → {name}";
                break;
            }
            case "file.batch-rename":
            {
                var entries = await Task.WhenAll(operationPaths.Select(files.GetEntryAsync));
                if (entries.Any(item => item == null || !FileListViewModel.IsBatchRenameTarget(item)))
                    throw new FileNotFoundException("批量重命名只支持存在的本地项目。");
                batchPreview = await batchRename.GeneratePreviewAsync(new BatchRenameRequest
                { Entries = entries.Select(item => item!).ToArray(), Rules = ParseBatchRenameRules(args), Options = ParseBatchRenameOptions(args) });
                if (!batchPreview.CanExecute)
                    throw new InvalidOperationException("重命名预览存在冲突、错误，或没有文件名变化。请调整规则。");
                observed.AddRange(batchPreview.Items.Where(item => item.IsChanged).Select(item => item.NewPath));
                summary = $"批量重命名 {batchPreview.Items.Count(item => item.IsChanged)} 项：\n"
                    + string.Join("\n", batchPreview.Items.Where(item => item.IsChanged).Take(30)
                        .Select(item => $"{item.OriginalName} → {item.NewName}"))
                    + (batchPreview.Items.Count(item => item.IsChanged) > 30 ? "\n…其余项目可在执行回执中查看。" : "");
                break;
            }
            case "file.move":
            case "file.copy":
            {
                var destination = Required(args, "destination");
                var folder = await files.GetEntryAsync(destination);
                if (folder?.IsDirectory != true) throw new DirectoryNotFoundException("目标文件夹不存在。");
                var target = files.CombinePath(destination, source!.Name);
                if (source.IsDirectory && (destination == path
                    || destination.StartsWith(path.TrimEnd('/') + "/", StringComparison.Ordinal)))
                    throw new InvalidOperationException("不能将文件夹移动到自身或子文件夹。");
                if (await files.ExistsAsync(target)) throw new IOException("目标位置已有同名文件。");
                observed.AddRange([destination, target]);
                summary = id == "file.move" ? $"移动：{path} → {destination}"
                    : $"复制：{path} → {destination}";
                break;
            }
            case "file.trash": summary = VirtualPath.IsRemotePath(path)
                ? $"远程文件将永久删除：{path}" : $"移到废纸篓：{path}"; break;
            case "file.delete-permanent":
                if (VirtualPath.IsRemotePath(path) || !Path.IsPathFullyQualified(path))
                    throw new InvalidOperationException("永久删除能力仅适用于本地文件。");
                summary = $"永久删除：{path}。此操作无法恢复。";
                break;
            case "script.run":
                var script = homeWorkspace.GetCommands(path).FirstOrDefault(c => c.Id == Required(args, "id"))
                    ?? throw new ArgumentException("此文件没有该已配置命令。");
                if (source!.IsDirectory || !File.Exists(path)) throw new FileNotFoundException("脚本文件不存在。", path);
                summary = $"运行已配置脚本命令「{script.Name}」：{path}。命令将在终端执行。";
                break;
            case "archive.extract-here":
            case "archive.extract-folder":
                if (!archives.IsArchiveFile(path)) throw new ArgumentException("不是支持的压缩包。");
                summary = id == "archive.extract-here"
                    ? $"解压：{path} → {Path.GetDirectoryName(path)}。同名内容由现有解压流程避让。"
                    : $"解压：{path} → 压缩包旁的同名文件夹。若文件夹存在会自动选择新名称。";
                break;
            case "archive.compress":
                if (VirtualPath.IsRemotePath(path)) throw new InvalidOperationException("请先下载远程文件再压缩。");
                summary = $"压缩来源：{path}。确认后打开压缩设置窗口。";
                break;
            case "file.content":
                if (VirtualPath.IsRemotePath(path) || !Path.IsPathFullyQualified(path)
                    || !File.Exists(path) || source!.IsDirectory || source.IsSymbolicLink
                    || !contentExtractor.Supports(path))
                    throw new NotSupportedException("该文件暂不支持正文提取。");
                var contentBytes = new FileInfo(path).Length;
                contentSourceStamp = await StampAsync(path);
                contentPage = await contentExtractor.ExtractPageAsync(path, ContentOffset(args));
                summary = $"从 {path}（{contentBytes} 字节）读取字符 {contentPage.Offset} 至 {contentPage.NextOffset}"
                    + $"（本次 {contentPage.Text.Length} 字符）并发送给当前模型。"
                    + (contentPage.HasMore ? $"后续可从 {contentPage.NextOffset} 继续。" : "已到内容末尾。");
                break;
            case "file.rating":
                if (source!.IsDirectory || VirtualPath.IsRemotePath(path))
                    throw new InvalidOperationException("只能给本地文件评分。");
                var rating = Rating(args);
                summary = $"设置评分：{path} → {rating} 星";
                break;
            case "folder.pin":
                if (!source!.IsDirectory) throw new InvalidOperationException("只能收藏文件夹。");
                summary = $"收藏文件夹：{path}";
                break;
            case "folder.unpin":
                if (!await pins.IsPinnedAsync(path)) throw new InvalidOperationException("文件夹尚未收藏。");
                summary = $"取消收藏：{path}";
                break;
            case "tag.create":
                var tagName = Required(args, "name");
                if (tagName.Length > 100) throw new ArgumentException("标签名称过长。");
                summary = $"新建标签：{tagName}";
                break;
            case "tag.rename":
            case "tag.delete":
            case "tag.pin":
            case "tag.color":
                var targetTag = await GetCustomTagAsync(Required(args, "name"));
                summary = id switch
                {
                    "tag.rename" => $"重命名收藏夹：{targetTag.Name} → {Required(args, "newName")}",
                    "tag.delete" => $"删除收藏夹：{targetTag.Name}。约 {targetTag.ItemCount} 个文件将失去此标签，原文件不会删除。",
                    "tag.pin" => $"{(Boolean(args, "pinned") ? "固定" : "取消固定")}标签：{targetTag.Name}",
                    _ => $"设置标签颜色：{targetTag.Name} → {ColorId(args)}"
                };
                break;
            case "tag.apply":
                var tag = Required(args, "tag");
                if (!(await tags.GetSidebarTagsAsync()).Any(item => item.Name == tag))
                    throw new ArgumentException("标签不存在。");
                summary = $"{(Boolean(args, "applied") ? "添加" : "移除")}标签 {tag}：{path}";
                break;
            case "plugin.enable":
                var pluginId = Required(args, "id");
                if (!plugins.Plugins.Any(p => p.Manifest.Id == pluginId))
                    throw new ArgumentException("插件不存在。");
                summary = $"{(Boolean(args, "enabled") ? "启用" : "禁用")}插件：{pluginId}";
                break;
            case "plugin.install":
                if (!Path.IsPathFullyQualified(path) || !File.Exists(path)
                    || !path.EndsWith(".mexplug", StringComparison.OrdinalIgnoreCase))
                    throw new FileNotFoundException("请选择本地 .mexplug 插件包。", path);
                var manifest = ReadPluginManifest(path);
                summary = $"安装插件：{manifest.Name} {manifest.Version}（{manifest.Id}）。来源：{path}";
                break;
            case "plugin.market-install":
                marketItem = await FindMarketItemAsync(Required(args, "id"), Required(args, "version"));
                PluginMarketClient.RequireHttps(marketItem.DownloadUrl);
                if (marketItem.Size is <= 0 or > 536870912
                    || marketItem.Sha256 is not { Length: 64 })
                    throw new InvalidDataException("市场插件下载信息无效。");
                var currentPlugin = plugins.Plugins.FirstOrDefault(p =>
                    p.Manifest.Id == marketItem.Manifest.Id && !p.Removed);
                if (currentPlugin?.Manifest.Version == marketItem.Manifest.Version)
                    throw new InvalidOperationException("此版本已经安装。");
                if (currentPlugin != null
                    && Version.TryParse(currentPlugin.Manifest.Version, out var currentVersion)
                    && Version.TryParse(marketItem.Manifest.Version, out var targetVersion)
                    && targetVersion <= currentVersion)
                    throw new InvalidOperationException("不能安装比当前版本更旧的插件。");
                summary = $"{(currentPlugin == null ? "安装" : "更新")}市场插件：{marketItem.Manifest.Name} "
                    + $"{marketItem.Manifest.Version}（{marketItem.Manifest.Id}）。开发者：{marketItem.Manifest.Developer}。"
                    + $"来源：{marketItem.DownloadUrl}。大小：{marketItem.Size} 字节；SHA-256：{marketItem.Sha256}。"
                    + (marketItem.Manifest.Paid ? "此插件标记为付费。" : "此插件标记为免费。");
                break;
            case "plugin.uninstall":
                var installed = plugins.Plugins.FirstOrDefault(p => p.Manifest.Id == Required(args, "id") && !p.Removed)
                    ?? throw new ArgumentException("插件未安装。");
                summary = $"卸载插件：{installed.Manifest.Name} {installed.Manifest.Version}"
                    + (installed.Running ? "。当前任务将等待结束。" : "。");
                break;
            case "plugin.restore-built-in":
                summary = "恢复内置文件转换插件。";
                break;
            case "plugin.cancel":
                var running = plugins.Plugins.FirstOrDefault(p => p.Manifest.Id == Required(args, "id") && p.Running)
                    ?? throw new InvalidOperationException("插件当前没有运行中的任务。");
                summary = $"取消插件任务：{running.Manifest.Name}";
                break;
            case "settings.toggle":
                var settingKey = Required(args, "key");
                ValidateToggle(settingKey);
                summary = $"设置 {settingKey}：{(Boolean(args, "enabled") ? "开启" : "关闭")}";
                break;
            case "settings.theme":
                var themeMode = Required(args, "mode");
                if (themeMode is not ("system" or "light" or "dark"))
                    throw new ArgumentException("主题只能为 system、light 或 dark。");
                summary = $"切换主题：{themeMode}";
                break;
            case "delivery.add-folder":
                if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path))
                    throw new DirectoryNotFoundException("文件速递只接受本地文件夹。");
                summary = $"加入文件速递：{path}";
                break;
            case "delivery.add-tag":
                var deliveryTag = Required(args, "tag");
                if (!(await tags.GetSidebarTagsAsync()).Any(item => item.Name == deliveryTag))
                    throw new ArgumentException("标签不存在。");
                summary = $"将标签加入文件速递：{deliveryTag}";
                break;
            case "delivery.remove":
            case "delivery.move":
                var tab = delivery.Preferences.Entries.FirstOrDefault(item => item.Id == Required(args, "id"))
                    ?? throw new ArgumentException("页签不存在。");
                summary = id == "delivery.remove"
                    ? $"从文件速递移除页签：{tab.Name}。不会删除原位置。"
                    : $"调整文件速递页签顺序：{tab.Name}，移动 {Step(args)} 步。";
                break;
            case "delivery.enabled":
                summary = Boolean(args, "enabled") ? "启用文件速递" : "停用文件速递";
                break;
            default:
                if (!isPluginCommand) throw new InvalidOperationException("能力未实现。");
                var (commandPluginId, command) = ResolvePluginCommand(id);
                var selectedFiles = operationPaths.Select(p => new PluginFile(p)).ToArray();
                if (!command.Match.Matches(selectedFiles))
                    throw new InvalidOperationException("所选文件不符合插件命令的文件类型或数量要求。");
                if (operationPaths.Any(p => !Path.IsPathFullyQualified(p) || !File.Exists(p)))
                    throw new FileNotFoundException("插件命令只接受存在的本地文件。");
                summary = $"运行插件 {commandPluginId} / {command.Title}：{string.Join("、", operationPaths)}。输出由插件生成并经应用提交到源目录。";
                break;
        }
        var plan = new CapabilityPlan(Guid.NewGuid().ToString("N"), capability,
            args.GetRawText(), summary, observed, DateTimeOffset.UtcNow);
        var stamps = await Task.WhenAll(observed.Select(StampAsync));
        if (contentSourceStamp != null && stamps.First(stamp => stamp.Path == path) != contentSourceStamp)
            throw new IOException("文件在正文预览时发生变化，请重新预览。");
        var scriptSnapshot = id == "script.run"
            ? JsonSerializer.Serialize(homeWorkspace.GetCommands(path).First(c => c.Id == Required(args, "id")))
            : null;
        var pluginSnapshot = id.StartsWith("plugin.", StringComparison.Ordinal)
            ? CurrentPluginSnapshot() : null;
        var tagSnapshot = id is "tag.rename" or "tag.delete" or "tag.pin" or "tag.color"
            ? JsonSerializer.Serialize(await GetCustomTagAsync(Required(args, "name"))) : null;
        lock (_gate)
        {
            foreach (var expired in _pending.Where(item =>
                         DateTimeOffset.UtcNow - item.Value.Plan.CreatedAt > TimeSpan.FromMinutes(5))
                     .Select(item => item.Key).ToArray())
                _pending.Remove(expired);
            _pending.Add(plan.Id, new Pending(plan, pane, pane.CurrentPath,
                stamps, scriptSnapshot, pluginSnapshot, tagSnapshot, marketItem, batchPreview, contentPage));
        }
        return plan;
    }

    public async Task<CapabilityResult> ExecuteApprovedAsync(string planId, FileListViewModel? pane,
        IProgress<string>? progress = null)
    {
        Pending pending;
        lock (_gate)
        {
            if (!_pending.Remove(planId, out pending!))
                throw new InvalidOperationException("计划不存在或已执行。");
        }
        if (!ReferenceEquals(pending.Pane, pane))
            throw new InvalidOperationException("活动窗格已切换，请重新预览。");
        if (pane?.CurrentPath != pending.PanePath)
            throw new InvalidOperationException("窗格位置已切换，请重新预览。");
        if (DateTimeOffset.UtcNow - pending.Plan.CreatedAt > TimeSpan.FromMinutes(5))
            throw new InvalidOperationException("计划已过期，请重新预览。");
        foreach (var stamp in pending.Stamps)
            if (stamp != await StampAsync(stamp.Path))
                throw new InvalidOperationException($"文件状态已变化，请重新预览：{stamp.Path}");

        using var document = JsonDocument.Parse(pending.Plan.ArgumentsJson);
        var args = document.RootElement;
        var id = pending.Plan.Capability.Id;
        var isPluginCommand = id.StartsWith("plugin.command:", StringComparison.Ordinal);
        var isBatchRename = id == "file.batch-rename";
        var path = PathlessCapabilities.Contains(id) ? string.Empty
            : isPluginCommand || isBatchRename ? string.Empty : Required(args, "path");
        var entry = string.IsNullOrEmpty(path) ? null : await files.GetEntryAsync(path);
        if (id == "script.run" && pending.ScriptSnapshot != JsonSerializer.Serialize(
                homeWorkspace.GetCommands(path).FirstOrDefault(c => c.Id == Required(args, "id"))))
            throw new InvalidOperationException("脚本配置已变化，请重新预览。");
        if (pending.PluginSnapshot != null && pending.PluginSnapshot != CurrentPluginSnapshot())
            throw new InvalidOperationException("插件状态已变化，请重新预览。");
        if (pending.TagSnapshot != null && pending.TagSnapshot != JsonSerializer.Serialize(
                await GetCustomTagAsync(Required(args, "name"))))
            throw new InvalidOperationException("收藏夹状态已变化，请重新预览。");
        switch (pending.Plan.Capability.Id)
        {
            case "folder.create-unnamed":
                return await CreateUnnamedFolderAsync(pane!);
            case "folder.create":
                var createdFolder = await files.CreateFolderAsync(path, Required(args, "name"));
                changes.NotifyChanged([path], null);
                await pane!.RefreshAsync();
                return new(true, $"已创建文件夹：{createdFolder}", createdFolder);
            case "file.create-text":
                var createdFile = await files.CreateFileWithContentAsync(path, Required(args, "name"),
                    System.Text.Encoding.UTF8.GetBytes(Required(args, "content")));
                changes.NotifyChanged([path], null);
                await pane!.RefreshAsync();
                return new(true, $"已生成文件：{createdFile}", createdFile);
            case "file.rename":
                if (entry == null || !await pane!.RenameEntryAsync(entry, Required(args, "newName")))
                    throw new IOException("重命名失败，请查看文件列表状态。");
                break;
            case "file.batch-rename":
            {
                if (pending.BatchRenamePreview == null) throw new InvalidOperationException("请重新预览。");
                var currentPreview = await batchRename.GeneratePreviewAsync(pending.BatchRenamePreview.Request);
                if (JsonSerializer.Serialize(currentPreview.Items.Select(item => new
                    {
                        item.OriginalPath, item.NewPath, item.HasError, item.HasConflict
                    })) != JsonSerializer.Serialize(pending.BatchRenamePreview.Items.Select(item => new
                    {
                        item.OriginalPath, item.NewPath, item.HasError, item.HasConflict
                    })))
                    throw new InvalidOperationException("批量重命名预览已变化，请重新确认。");
                var result = await batchRenameOperation.ExecuteAsync(currentPreview);
                await pane!.RefreshAsync();
                return new(result.FailedCount == 0 && !result.WasCancelled,
                    $"批量重命名：成功 {result.SuccessCount}，跳过 {result.SkippedCount}，失败 {result.FailedCount}",
                    new { result.SuccessfulItems, result.Errors, result.Warnings, result.WasCancelled });
            }
            case "file.move":
                var folder = await files.GetEntryAsync(Required(args, "destination"))
                    ?? throw new DirectoryNotFoundException("目标文件夹不存在。");
                var moved = await pane!.MoveEntryCheckedDetailedAsync(entry ?? throw new FileNotFoundException(), folder);
                return new(true, moved.Warnings.Count == 0
                    ? $"已移动到：{string.Join("、", moved.CompletedPaths)}"
                    : $"已移动到：{string.Join("、", moved.CompletedPaths)}；{string.Join("；", moved.Warnings)}", moved);
            case "file.copy":
                var copyTarget = await files.GetEntryAsync(Required(args, "destination"))
                    ?? throw new DirectoryNotFoundException("目标文件夹不存在。");
                var copied = await pane!.CopyEntryCheckedDetailedAsync(entry ?? throw new FileNotFoundException(), copyTarget);
                return new(true, copied.Warnings.Count == 0
                    ? $"已复制到：{string.Join("、", copied.CompletedPaths)}"
                    : $"已复制到：{string.Join("、", copied.CompletedPaths)}；{string.Join("；", copied.Warnings)}", copied);
            case "file.trash":
                await pane!.DeleteEntriesCheckedAsync([entry ?? throw new FileNotFoundException()]);
                break;
            case "file.delete-permanent":
                await pane!.PermanentlyDeleteCheckedAsync([entry ?? throw new FileNotFoundException()]);
                break;
            case "script.run":
                var scriptCommand = homeWorkspace.GetCommands(path).FirstOrDefault(c => c.Id == Required(args, "id"))
                    ?? throw new InvalidOperationException("脚本配置已变化，请重新预览。");
                await scriptRunner.RunAsync(path, scriptCommand);
                return new(true, $"已启动脚本命令：{scriptCommand.Name}");
            case "archive.extract-here":
                pane!.ExtractHere(entry ?? throw new FileNotFoundException());
                return new(true, "解压任务已开始。进度和失败原因显示在应用任务面板。");
            case "archive.extract-folder":
                pane!.ExtractToNamedFolder(entry ?? throw new FileNotFoundException());
                return new(true, "解压任务已开始。进度和失败原因显示在应用任务面板。");
            case "archive.compress":
                pane!.SelectEntry(entry ?? throw new FileNotFoundException());
                pane.ShowCompressDialog();
                return new(true, "压缩配置窗口已打开，请在窗口中完成格式和目标设置。");
            case "file.content":
                var page = pending.ContentPage ?? throw new InvalidOperationException("正文预览已失效，请重新预览。");
                return new(true, $"已读取字符 {page.Offset} 至 {page.NextOffset}：{path}"
                    + (page.HasMore ? $"；下一次使用 offset={page.NextOffset}" : "；已到内容末尾"), page);
            case "file.rating":
                await pane!.SetRatingAsync(path, Rating(args));
                break;
            case "folder.pin":
                await pane!.PinFolderAsync(path, entry?.Name ?? throw new FileNotFoundException());
                break;
            case "folder.unpin":
                await pane!.UnpinFolderAsync(path);
                break;
            case "tag.create":
                await tags.CreateTagAsync(Required(args, "name"));
                break;
            case "tag.rename":
                await tags.RenameTagAsync(await GetCustomTagAsync(Required(args, "name")),
                    Required(args, "newName"));
                break;
            case "tag.delete":
                await tags.DeleteTagAsync(await GetCustomTagAsync(Required(args, "name")));
                break;
            case "tag.pin":
                await tags.SetTagPinnedAsync(await GetCustomTagAsync(Required(args, "name")),
                    Boolean(args, "pinned"));
                break;
            case "tag.color":
                await tags.SetTagColorAsync(await GetCustomTagAsync(Required(args, "name")),
                    ColorId(args));
                break;
            case "tag.apply":
                var tag = (await tags.GetSidebarTagsAsync()).First(t => t.Name == Required(args, "tag"));
                await tags.SetTagAsync([path], tag, Boolean(args, "applied"));
                break;
            case "plugin.enable":
                await plugins.SetEnabledAsync(Required(args, "id"), Boolean(args, "enabled"));
                break;
            case "plugin.install":
                await plugins.InstallAsync(path);
                break;
            case "plugin.market-install":
                var approvedMarketItem = pending.MarketItem
                    ?? throw new InvalidOperationException("市场插件计划缺少来源信息。");
                var latestMarketItem = await FindMarketItemAsync(
                    approvedMarketItem.Manifest.Id, approvedMarketItem.Manifest.Version);
                if (latestMarketItem.DownloadUrl != approvedMarketItem.DownloadUrl
                    || latestMarketItem.Size != approvedMarketItem.Size
                    || !latestMarketItem.Sha256.Equals(approvedMarketItem.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("市场插件来源或校验值已变化，请重新预览。");
                await market.InstallAsync(approvedMarketItem, plugins,
                    new Progress<double?>(value => progress?.Report(
                        $"下载 {approvedMarketItem.Manifest.Name}：{value:0}%")), CancellationToken.None);
                return new(true, $"已安装市场插件：{approvedMarketItem.Manifest.Name} {approvedMarketItem.Manifest.Version}");
            case "plugin.uninstall":
                await plugins.UninstallAsync(Required(args, "id"));
                break;
            case "plugin.restore-built-in":
                await plugins.RestoreBuiltInAsync();
                break;
            case "plugin.cancel":
                await plugins.CancelAsync(Required(args, "id"));
                break;
            case "settings.toggle":
                var enabled = Boolean(args, "enabled");
                switch (Required(args, "key"))
                {
                    case "hide-system": pane!.HideSystemFiles = enabled; break;
                    case "hide-dot-files": pane!.HideDotFiles = enabled; break;
                    case "hide-dot-folders": pane!.HideDotFolders = enabled; break;
                    case "confirm-trash": pane!.ConfirmBeforeTrash = enabled; break;
                    case "double-click-up": pane!.DoubleClickEmptyAreaGoUp = enabled; break;
                    case "ai-analysis": pane!.IsAiAnalysisEnabled = enabled; break;
                    case "folder-covers": appSettings.Set(FileListViewModel.FolderPhotoCoverSettingKey, enabled); break;
                    default: throw new ArgumentException("不支持的设置项。");
                }
                break;
            case "settings.theme":
                var mode = Required(args, "mode");
                if (mode is not ("system" or "light" or "dark")) throw new ArgumentException("主题值无效。");
                appSettings.Set("theme_mode", mode);
                themes.SetThemeMode(mode);
                interactionStyles.ApplyCurrentTheme();
                break;
            case "delivery.add-folder":
                delivery.AddFolder(path);
                break;
            case "delivery.add-tag":
                var deliveryTag = (await tags.GetSidebarTagsAsync()).First(item => item.Name == Required(args, "tag"));
                delivery.AddTag(deliveryTag);
                break;
            case "delivery.remove":
                delivery.Remove(Required(args, "id"));
                break;
            case "delivery.move":
                delivery.Move(Required(args, "id"), Step(args));
                break;
            case "delivery.enabled":
                delivery.Enabled = Boolean(args, "enabled");
                break;
            default:
                if (!isPluginCommand) throw new InvalidOperationException("能力未实现。");
                return await InvokePluginCommandAsync(id, RequiredPaths(args), pane!);
        }
        return new(true, $"已完成：{pending.Plan.Summary}");
    }

    public void CancelPlan(string planId)
    {
        lock (_gate) _pending.Remove(planId);
    }

    private static MainWindow RequireMainWindow(FileListViewModel? pane)
        => pane?.OwnerWindow as MainWindow
           ?? throw new InvalidOperationException("此操作需要主窗口中的活动窗格。");

    private async Task<FileStamp> StampAsync(string path)
    {
        var entry = await files.GetEntryAsync(path);
        return entry == null ? new(path, false, 0, default, default, false, false)
            : new(path, true, entry.Size, entry.LastModified, entry.Created,
                entry.IsDirectory, entry.IsSymbolicLink);
    }

    private string CurrentPluginSnapshot() => JsonSerializer.Serialize(plugins.Plugins
        .OrderBy(p => p.Manifest.Id, StringComparer.Ordinal)
        .Select(p => new { p.Manifest.Id, p.Manifest.Version, p.Enabled, p.Removed, p.Running }));

    private async Task<PluginMarketItem> FindMarketItemAsync(string id, string version)
    {
        for (var page = 0; page < 10; page++)
        {
            var listing = await market.ListAsync(id, page, CancellationToken.None);
            var item = listing.Items.FirstOrDefault(candidate => candidate.Manifest != null
                && candidate.Manifest.Id == id
                && candidate.Manifest.Version == version);
            if (item != null) return item;
            if (!listing.HasMore) break;
        }
        throw new ArgumentException("市场中找不到指定 ID 和版本的插件。");
    }

    private static PluginManifest ReadPluginManifest(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("plugin.json") ?? throw new InvalidDataException("插件包缺少 plugin.json。");
        if (entry.Length > 1024 * 1024) throw new InvalidDataException("插件清单过大。");
        using var stream = entry.Open();
        return JsonSerializer.Deserialize<PluginManifest>(stream, PluginProtocol.Json)
            ?? throw new InvalidDataException("插件清单无效。");
    }

    private static string Required(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.GetString()))
            throw new ArgumentException($"缺少参数：{name}");
        return property.GetString()!;
    }

    private static bool Boolean(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ArgumentException($"缺少布尔参数：{name}");
        return value.GetBoolean();
    }

    private static long ContentOffset(JsonElement args)
    {
        if (!args.TryGetProperty("offset", out var value)) return 0;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var offset) || offset < 0)
            throw new ArgumentException("offset 必须是非负整数。");
        return offset;
    }

    private static string[] RequiredPaths(JsonElement args)
    {
        if (!args.TryGetProperty("paths", out var value) || value.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("缺少参数：paths");
        var paths = value.EnumerateArray().Select(item => item.GetString() ?? "").ToArray();
        if (paths.Length == 0 || paths.Length > 100 || paths.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("paths 需包含 1 到 100 个文件路径。");
        return paths;
    }

    private static IReadOnlyList<BatchRenameRule> ParseBatchRenameRules(JsonElement args, bool optional = false)
    {
        if (!args.TryGetProperty("rules", out var values))
            return args.TryGetProperty("rule", out _) ? [ParseBatchRenameRule(args)]
                : optional ? [] : throw new ArgumentException("缺少 rules 或 rule。");
        if (args.TryGetProperty("rule", out _)) throw new ArgumentException("rule 与 rules 不能同时提供。");
        if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() is < 1 or > 64)
            throw new ArgumentException("rules 需包含 1 到 64 条规则。");
        var rules = values.EnumerateArray().Select(value =>
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
                throw new ArgumentException("每条规则需要 type。");
            var rule = value.Deserialize<BatchRenameRule>(BatchRenameJsonOptions()) ?? throw new ArgumentException("规则无效。");
            BatchRenameRuleEngine.ValidateRule(rule);
            return rule;
        }).ToArray();
        return rules;
    }

    private static JsonSerializerOptions BatchRenameJsonOptions()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static BatchRenameOptions ParseBatchRenameOptions(JsonElement args)
    {
        var options = args.TryGetProperty("options", out var value)
            ? value.Deserialize<BatchRenameOptions>(BatchRenameJsonOptions()) ?? new() : new BatchRenameOptions();
        if (!Enum.IsDefined(options.Sort)) throw new ArgumentException("排序选项无效。");
        return options;
    }

    private static BatchRenameRule ParseBatchRenameRule(JsonElement args)
    {
        if (!args.TryGetProperty("rule", out var value) || value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
            throw new ArgumentException("缺少批量重命名规则及 type。");
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new JsonStringEnumConverter());
        var rule = value.Deserialize<BatchRenameRule>(options)
            ?? throw new ArgumentException("批量重命名规则无效。");
        if (!Enum.IsDefined(rule.Type) || !Enum.IsDefined(rule.CaseMode) || !rule.IsEnabled)
            throw new ArgumentException("批量重命名规则类型或大小写模式无效。");
        if (rule.Type == BatchRenameRuleType.FindReplace && string.IsNullOrEmpty(rule.FindText))
            throw new ArgumentException("查找并替换规则需要非空的查找内容。");
        if (rule.SequencePadding is < 0 or > 12 || rule.SequenceStep is < 1 or > 10000)
            throw new ArgumentException("序号规则超出允许范围。");
        return rule;
    }

    private static int Step(JsonElement args)
    {
        if (!args.TryGetProperty("delta", out var value) || !value.TryGetInt32(out var delta)
            || delta is not (-1 or 1))
            throw new ArgumentException("delta 只能为 -1 或 1。");
        return delta;
    }

    private static int Rating(JsonElement args)
    {
        if (!args.TryGetProperty("rating", out var value) || !value.TryGetInt32(out var rating)
            || rating is < 0 or > 5)
            throw new ArgumentException("评分必须为 0 到 5。");
        return rating;
    }

    private static void ValidateToggle(string key)
    {
        if (key is not ("hide-system" or "hide-dot-files" or "hide-dot-folders"
            or "confirm-trash" or "double-click-up" or "ai-analysis" or "folder-covers"))
            throw new ArgumentException("设置项不在允许的白名单中。");
    }

    private async Task<FileTag> GetCustomTagAsync(string name)
    {
        var tag = (await tags.GetSidebarTagsAsync()).FirstOrDefault(item => item.Name == name)
            ?? throw new ArgumentException("标签不存在。");
        if (!tag.IsCustom) throw new InvalidOperationException("此操作仅适用于自定义标签。");
        return tag;
    }

    private static int ColorId(JsonElement args)
    {
        if (!args.TryGetProperty("colorId", out var value) || !value.TryGetInt32(out var colorId)
            || colorId is < 0 or > 7)
            throw new ArgumentException("colorId 必须在 0 到 7 之间。");
        return colorId;
    }

    private static void ValidateNewName(string name)
    {
        if (name is "." or ".." || name.Contains('/') || name.Contains('\\') || string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("文件名无效。");
    }
}
