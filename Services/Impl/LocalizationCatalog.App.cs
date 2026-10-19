namespace MacExplorer.Services.Impl;

internal static partial class LocalizationCatalog
{
    private static readonly IReadOnlyDictionary<string, (string Chinese, string English)> App =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["app.store_name"] = ("文件资源管理器", "文件资源管理器"),
            ["app.menu_about"] = ("关于 {0}", "About {0}"),
            ["app.menu_quit"] = ("退出 {0}", "Quit {0}"),
            ["app.delivery"] = ("文件速递 · {0}", "File Delivery · {0}"),
            ["subscription.unverified"] = ("暂时无法验证订阅", "Unable to verify subscription"),
            ["subscription.required"] = ("全部文件管理功能需年度订阅（App 内购买）", "All file management features require an annual subscription (in-app purchase)"),
            ["subscription.loading_price"] = ("正在获取 App Store 价格…", "Loading App Store price…"),
            ["subscription.no_price"] = ("订阅价格暂不可用", "Subscription price unavailable"),
            ["subscription.yearly_price"] = ("{0} / 年", "{0} / year"),
            ["subscription.trial_terms"] = ("符合资格可免费试用 {0} 天，之后按 {1}/年自动续订。可在 Apple 账户中取消。", "Eligible users receive a {0}-day free trial, then {1}/year, automatically renewing. Cancel in your Apple account."),
            ["subscription.renewal_terms"] = ("年度自动续订，可在 Apple 账户中取消。取消后仍可使用至当前有效期结束。", "Renews annually. Cancel in your Apple account; access continues until the current period ends."),
            ["subscription.trial_button"] = ("免费试用 {0} 天", "Try free for {0} days"),
            ["subscription.subscribe"] = ("订阅", "Subscribe"),
            ["subscription.load_error"] = ("暂时无法加载订阅价格，请重试。", "Unable to load the subscription price. Please retry."),
            ["subscription.pending"] = ("购买等待批准，批准后将自动解锁。", "Purchase awaiting approval. Access unlocks after approval."),
            ["subscription.cancelled"] = ("已取消购买。", "Purchase cancelled."),
            ["subscription.action_error"] = ("操作未完成，请检查 App Store 账户或网络后重试。", "Could not complete the action. Check your App Store account or network and retry."),
            ["subscription.locked"] = ("订阅未生效，请先订阅或恢复购买。", "No active subscription. Subscribe or restore purchases to continue."),
            ["app.localsend_network_error"] = ("无法使用本地网络，请在系统设置中允许 {0} 访问本地网络后重试。", "Local network unavailable. Allow {0} to access the local network in System Settings, then retry."),
        };
}
