using System.Text.Json;
using MacExplorer.PluginSdk;

namespace MacExplorer.Services.Plugins;

public sealed partial class PluginManager
{
    private const string TrialKey = "plugin_trials_v1";
    private readonly object _trialGate = new();
    private readonly Dictionary<string, PluginAccessResult> _access = new(StringComparer.Ordinal);

    public PluginAccessResult? GetAccess(string id)
    {
        lock (_trialGate) return _access.GetValueOrDefault(id);
    }

    public void SetAccess(string id, PluginAccessResult result)
    {
        lock (_trialGate) _access[id] = result;
        Changed?.Invoke();
    }

    public PluginTrial GetTrial(string id, bool start = false, DateTimeOffset? now = null)
    {
        lock (_trialGate)
        {
            // A damaged ledger must not silently grant another trial.
            var records = JsonSerializer.Deserialize<Dictionary<string, PluginTrial>>(
                _settings.Get(TrialKey) ?? "{}", PluginProtocol.Json) ?? throw new InvalidDataException("试用记录损坏。");
            var record = records.GetValueOrDefault(id) ?? new PluginTrial();
            var checkedAt = now ?? DateTimeOffset.UtcNow;
            if (record.CheckedAt > checkedAt) checkedAt = record.CheckedAt.Value;
            if (start && record.StartedAt == null)
            {
                var plugin = Plugins.Single(p => p.Manifest.Id == id && p.Enabled && !p.Removed);
                if (!plugin.Manifest.Paid || plugin.Manifest.TrialDays <= 0) throw new InvalidOperationException("此插件不提供试用。");
                record = record with { StartedAt = checkedAt, ExpiresAt = checkedAt.AddDays(plugin.Manifest.TrialDays) };
            }
            record = record with { CheckedAt = checkedAt };
            records[id] = record;
            _settings.Set(TrialKey, JsonSerializer.Serialize(records, PluginProtocol.Json));
            return record;
        }
    }
}
