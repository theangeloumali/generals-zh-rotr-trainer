using System.Runtime.InteropServices;
using RotrTrainer.Core;

namespace RotrTrainer;

/// <summary>The whole UI: reads TrainerEngine.Status every tick and forwards user actions to the engine.</summary>
internal sealed class MainForm : Form
{
    private const int RefreshIntervalMs = 100;
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private const int ArmHotkeyId = 1;
    private const int FirstFeatureHotkeyId = 2;
    private const string OwnerRules =
        "Owner rules: single-player and skirmish only. Close the trainer and restart the game before any LAN " +
        "or online session. Do not share replays of trained matches.";

    private static readonly Keys[] FeatureHotkeys = [Keys.F6, Keys.F7, Keys.F8, Keys.F9, Keys.F10, Keys.Insert];
    private static readonly Color ArmedColor = Color.Firebrick;
    private static readonly Color DisarmedColor = Color.DarkGreen;

    private readonly TrainerEngine _engine;
    private readonly List<FeatureRow> _rows = [];
    private readonly Dictionary<int, string> _hotkeyFailures = [];
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = RefreshIntervalMs };
    private readonly Label _connectionLabel = CreateWrappingLabel();
    private readonly Label _armStateLabel = new() { AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 20f, FontStyle.Bold) };
    private readonly Label _guardLabel = CreateWrappingLabel();
    private readonly Label _gameValuesLabel = CreateWrappingLabel();
    private readonly Button _armButton = new() { AutoSize = true, AccessibleName = "Arm or disarm" };
    private readonly CheckBox _topmostCheckBox = new() { Text = "Always on top", AutoSize = true };

    public MainForm(TrainerEngine engine, IReadOnlyList<FeatureBinding> bindings)
    {
        _engine = engine;
        BuildLayout(bindings);
        _armButton.Click += (_, _) => ToggleArm();
        _topmostCheckBox.CheckedChanged += (_, _) => TopMost = _topmostCheckBox.Checked;
        _refreshTimer.Tick += (_, _) => RefreshDisplay();
        _refreshTimer.Start();
        RefreshDisplay();
    }

    private void BuildLayout(IReadOnlyList<FeatureBinding> bindings)
    {
        Text = "RoTR Trainer";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(460, 470);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(12),
            AutoScroll = false,
        };
        layout.Controls.Add(_connectionLabel);
        layout.Controls.Add(_armStateLabel);
        layout.Controls.Add(_guardLabel);
        layout.Controls.Add(_gameValuesLabel);
        layout.Controls.Add(_armButton);

        int featureIndex = 0;
        foreach (var binding in bindings)
        {
            var row = binding.Feature.NeedsArming
                ? FeatureRow.Toggleable(binding, FeatureHotkeys[featureIndex], FirstFeatureHotkeyId + featureIndex++, ToggleFeature)
                : FeatureRow.AlwaysOn(binding);
            _rows.Add(row);
            layout.Controls.Add(row.Panel);
        }

        layout.Controls.Add(_topmostCheckBox);
        layout.Controls.Add(new Label { Text = OwnerRules, AutoSize = true, MaximumSize = new Size(430, 0), Margin = new Padding(0, 12, 0, 0) });
        Controls.Add(layout);
    }

    private void ToggleArm()
    {
        if (_engine.Status.IsArmed)
            _engine.Disarm();
        else
            _engine.Arm();
        RefreshDisplay();
    }

    private void ToggleFeature(FeatureRow row)
    {
        row.Binding.Feature.Enabled = !row.Binding.Feature.Enabled;
        RefreshDisplay();
    }

    private void RefreshDisplay()
    {
        _engine.Tick();
        var status = _engine.Status;

        _connectionLabel.Text = DescribeConnection(status);
        _armStateLabel.Text = status.IsArmed ? "ARMED" : "DISARMED";
        _armStateLabel.ForeColor = status.IsArmed ? ArmedColor : DisarmedColor;
        _guardLabel.Text = "Guard: " + status.GuardReason;
        _gameValuesLabel.Text = DescribeGameValues(status);
        _armButton.Text = status.IsArmed ? "Disarm (F5)" : "Arm (F5)";
        _armButton.Enabled = status.IsAttached && status.Refusal is null;

        foreach (var row in _rows)
            row.Refresh(status, HotkeyNote(row.HotkeyId));
        if (_hotkeyFailures.TryGetValue(ArmHotkeyId, out string? armFailure))
            _armButton.Text += " - " + armFailure;
    }

    private static string DescribeConnection(TrainerStatus status) =>
        !status.IsAttached ? "Waiting for the game. " + status.GuardReason
        : status.Refusal is not null ? "Game attached, refused: " + status.Refusal
        : "Game attached: " + status.BuildName;

    private static string DescribeGameValues(TrainerStatus status) =>
        status.Money is null ? "Money, general's points and rank: not readable yet"
        : $"Money: {status.Money}   General's points: {status.SciencePurchasePoints}   Rank: {status.RankLevel}";

    private string? HotkeyNote(int? hotkeyId) =>
        hotkeyId is { } id && _hotkeyFailures.TryGetValue(id, out string? failure) ? failure : null;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RegisterHotkey(ArmHotkeyId, Keys.F5);
        foreach (var row in _rows.Where(row => row.HotkeyId is not null))
            RegisterHotkey(row.HotkeyId!.Value, row.Hotkey);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        UnregisterHotKey(Handle, ArmHotkeyId);
        foreach (var row in _rows.Where(row => row.HotkeyId is not null))
            UnregisterHotKey(Handle, row.HotkeyId!.Value);
        base.OnHandleDestroyed(e);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _refreshTimer.Stop();
        _engine.Dispose();
        base.OnFormClosing(e);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmHotkey)
            HandleHotkey(message.WParam.ToInt32());
        base.WndProc(ref message);
    }

    private void HandleHotkey(int hotkeyId)
    {
        if (hotkeyId == ArmHotkeyId)
            ToggleArm();
        else if (_rows.FirstOrDefault(row => row.HotkeyId == hotkeyId) is { } row)
            ToggleFeature(row);
    }

    private void RegisterHotkey(int hotkeyId, Keys key)
    {
        if (!RegisterHotKey(Handle, hotkeyId, ModNoRepeat, (uint)key))
            _hotkeyFailures[hotkeyId] = $"{key} is in use by another program; use the button";
    }

    private static Label CreateWrappingLabel() => new() { AutoSize = true, MaximumSize = new Size(430, 0) };

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr windowHandle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr windowHandle, int id);

    /// <summary>One feature line: a checkbox (or a plain label for always-on fixes) and its failure text.</summary>
    private sealed class FeatureRow
    {
        private readonly CheckBox? _toggle;
        private readonly Label _detail = CreateWrappingLabel();
        private readonly Label? _alwaysOnLabel;

        private FeatureRow(FeatureBinding binding, Keys hotkey, int? hotkeyId, CheckBox? toggle, Label? alwaysOnLabel)
        {
            Binding = binding;
            Hotkey = hotkey;
            HotkeyId = hotkeyId;
            _toggle = toggle;
            _alwaysOnLabel = alwaysOnLabel;
            Panel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            Panel.Controls.Add((Control?)toggle ?? alwaysOnLabel!);
            Panel.Controls.Add(_detail);
        }

        public FeatureBinding Binding { get; }
        public Keys Hotkey { get; }
        public int? HotkeyId { get; }
        public Control Panel { get; }

        public static FeatureRow Toggleable(FeatureBinding binding, Keys hotkey, int hotkeyId, Action<FeatureRow> onToggle)
        {
            var toggle = new CheckBox { AutoSize = true, AutoCheck = false, Text = $"{binding.Feature.Name} ({hotkey})" };
            var row = new FeatureRow(binding, hotkey, hotkeyId, toggle, null);
            toggle.Click += (_, _) => onToggle(row);
            return row;
        }

        public static FeatureRow AlwaysOn(FeatureBinding binding) =>
            new(binding, Keys.None, null, null, new Label { AutoSize = true, Text = $"{binding.Feature.Name}: always on" });

        public void Refresh(TrainerStatus status, string? hotkeyNote)
        {
            var featureStatus = status.Features.First(feature => feature.Name == Binding.Feature.Name);
            if (_toggle is not null)
                _toggle.Checked = featureStatus.Enabled;
            string? failure = Binding.Failure();
            _detail.Text = failure is not null ? "Failed: " + failure : hotkeyNote ?? string.Empty;
            _detail.Visible = _detail.Text.Length > 0;
        }
    }
}
