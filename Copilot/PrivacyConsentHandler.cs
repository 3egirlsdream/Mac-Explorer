namespace MacExplorer.Copilot;

/// <summary>Checks the receiver again at the final HTTP boundary, including tool-result continuations.</summary>
internal sealed class PrivacyConsentHandler(HttpMessageHandler transport, CopilotSettings settings,
    CopilotCredentialStore credentials, string endpoint, string model, string key) : DelegatingHandler(transport)
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (settings.Endpoint != endpoint || settings.Model != model || credentials.Read() != key
            || !settings.HasMetadataConsent(key))
            throw new InvalidOperationException("AI 分享许可已撤回或接收配置已变化，发送已停止，请重新确认接收方。");
        return base.SendAsync(request, cancellationToken);
    }
}
