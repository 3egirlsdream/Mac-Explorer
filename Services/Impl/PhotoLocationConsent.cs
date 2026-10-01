using System.Diagnostics;

namespace MacExplorer.Services.Impl;

/// <summary>A revocable lease checked by the native helper immediately before geocoding.</summary>
public sealed class PhotoLocationConsent
{
    public const string SettingKey = "privacy.photo-location-network";
    private readonly ISettingsService _settings;
    private readonly string _path;
    private readonly object _gate = new();
    private string _lease = "";

    public PhotoLocationConsent(ISettingsService settings)
        : this(settings, Path.Combine(RuntimePaths.DataDirectory, "photo-location-consent")) { }

    internal PhotoLocationConsent(ISettingsService settings, string path)
    {
        _settings = settings;
        _path = path;
        // Each process creates a fresh lease; old helpers cannot regain permission.
        var allowed = settings.Get(SettingKey) == "true" && File.Exists(path) && File.ReadAllText(path).Length > 0;
        WriteLease(allowed ? Guid.NewGuid().ToString("N") : "");
    }

    public bool Enabled { get { lock (_gate) return _lease.Length > 0; } }

    public void SetAllowed(bool allowed)
    {
        lock (_gate)
        {
            WriteLease(allowed ? Guid.NewGuid().ToString("N") : "");
            _settings.Set(SettingKey, allowed ? "true" : "false");
        }
    }

    internal void ConfigureHelper(ProcessStartInfo startInfo)
    {
        lock (_gate)
        {
            if (_lease.Length == 0) return;
            startInfo.ArgumentList.Add("--location-consent");
            startInfo.ArgumentList.Add(_path);
            startInfo.ArgumentList.Add(_lease);
        }
    }

    private void WriteLease(string lease)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        try
        {
            File.WriteAllText(temporary, lease);
            File.Move(temporary, _path, true);
            _lease = lease;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
