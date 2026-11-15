using System.ComponentModel;
using System.Runtime.CompilerServices;
using RotrTrainer.Core;

namespace RotrTrainer;

/// <summary>One feature line in the window; the view binds to it and the tick refreshes it.</summary>
internal sealed class FeatureRow(IFeature feature, string hotkeyLabel) : INotifyPropertyChanged
{
    private bool _isOn;
    private bool _waitsForArming;
    private bool _hotkeyUnavailable;
    private string _description = feature.Description;

    public event PropertyChangedEventHandler? PropertyChanged;

    public IFeature Feature { get; } = feature;
    public string HotkeyLabel { get; } = hotkeyLabel;
    public string Name => Feature.Name;
    public bool IsAlwaysOn => !Feature.NeedsArming;

    /// <summary>The feature's one-line description, or why it could not be applied.</summary>
    public string Description
    {
        get => _description;
        set
        {
            if (_description == value)
                return;
            _description = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Description)));
        }
    }

    public bool IsOn
    {
        get => _isOn;
        set => Set(ref _isOn, value);
    }

    public bool WaitsForArming
    {
        get => _waitsForArming;
        set => Set(ref _waitsForArming, value);
    }

    /// <summary>Another program owns this key; the switch still works by mouse.</summary>
    public bool HotkeyUnavailable
    {
        get => _hotkeyUnavailable;
        set => Set(ref _hotkeyUnavailable, value);
    }

    public void Toggle() => Feature.Enabled = !Feature.Enabled;

    private void Set(ref bool field, bool value, [CallerMemberName] string? property = null)
    {
        if (field == value)
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
}
