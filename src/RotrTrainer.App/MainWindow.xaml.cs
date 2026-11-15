using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using RotrTrainer.Core;

namespace RotrTrainer;

/// <summary>The trainer window. It only shows TrainerDisplay and forwards arm and toggle requests to the engine.</summary>
public partial class MainWindow : Window
{
    private const int ExtendedStyleIndex = -20;
    private const int NoActivate = 0x08000000;
    private const int AppWindow = 0x00040000;
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(100);
    private static readonly (Key Key, string Label)[] FeatureHotkeys =
        [(Key.F6, "F6"), (Key.F7, "F7"), (Key.F8, "F8"), (Key.F9, "F9"), (Key.F10, "F10"), (Key.Insert, "Ins")];

    private readonly TrainerEngine _engine;
    private readonly List<FeatureRow> _rows;
    private readonly DispatcherTimer _timer = new() { Interval = TickInterval };
    private readonly string _version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "";
    private GlobalHotkeys? _hotkeys;

    public MainWindow(TrainerEngine engine, IReadOnlyList<IFeature> features)
    {
        InitializeComponent();
        _engine = engine;
        _rows = features
            .Select((feature, index) => new FeatureRow(feature, index < FeatureHotkeys.Length ? FeatureHotkeys[index].Label : ""))
            .ToList();
        FeatureList.ItemsSource = _rows;
        _timer.Tick += (_, _) => Refresh();
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(nint window, int index, int value);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var source = (HwndSource)PresentationSource.FromVisual(this);

        // Clicking the trainer must not take focus from the game: this old engine blanks its picture when it loses focus.
        SetWindowLong(source.Handle, ExtendedStyleIndex, GetWindowLong(source.Handle, ExtendedStyleIndex) | NoActivate | AppWindow);

        _hotkeys = new GlobalHotkeys(source);
        if (!_hotkeys.Register(Key.F5, ToggleArm))
            ArmKeyText.TextDecorations = TextDecorations.Strikethrough;
        for (int index = 0; index < _rows.Count && index < FeatureHotkeys.Length; index++)
        {
            var row = _rows[index];
            if (!row.IsAlwaysOn)
                row.HotkeyUnavailable = !_hotkeys.Register(FeatureHotkeys[index].Key, row.Toggle);
        }

        Refresh();
        _timer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _hotkeys?.Dispose();
        base.OnClosed(e);
    }

    private void Refresh()
    {
        _engine.Tick();
        var display = TrainerDisplay.From(_engine.Status);

        StateWord.Text = display.StateWord;
        StateDetail.Text = display.StateDetail;
        ArmActionText.Text = display.ArmActionText;
        ArmButton.IsEnabled = display.CanToggleArm;
        MoneyText.Text = display.Money;
        PointsText.Text = display.GeneralPoints;
        RankText.Text = display.Rank;
        FooterText.Text = $"{display.Footer} · v{_version}";

        var stateBrush = (Brush)FindResource(display.IsArmed ? "Gold" : "Quiet");
        var threadBrush = (Brush)FindResource(display.IsArmed ? "Gold" : "Rule");
        StateWord.Foreground = stateBrush;
        Thread.Stroke = threadBrush;
        Ring.Stroke = threadBrush;

        for (int index = 0; index < _rows.Count; index++)
        {
            _rows[index].IsOn = display.Features[index].IsOn;
            _rows[index].WaitsForArming = display.Features[index].WaitsForArming;
            _rows[index].Description = display.Features[index].Description;
        }
    }

    private void ToggleArm()
    {
        if (_engine.Status.IsArmed)
            _engine.Disarm();
        else
            _engine.Arm();
        Refresh();
    }

    private void OnArmClicked(object sender, RoutedEventArgs e) => ToggleArm();

    private void OnFeatureToggled(object sender, RoutedEventArgs e)
    {
        if (((ToggleButton)sender).DataContext is FeatureRow row)
            row.Toggle();
        Refresh();
    }

    private void OnMinimizeClicked(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();
}
