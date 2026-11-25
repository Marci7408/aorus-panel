using System;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DrawingColor = System.Drawing.Color;
using Forms = System.Windows.Forms;

namespace AorusPanel;

public partial class MainWindow : Window
{
    private sealed record ModeChoice(PanelMode Mode, string Label)
    {
        public override string ToString() => Label;
    }

    private static readonly ModeChoice[] Modes =
    [
        new(PanelMode.Enthusiast1, "Enthusiast 1"),
        new(PanelMode.Enthusiast2, "Enthusiast 2"),
        new(PanelMode.Enthusiast3, "Enthusiast 3"),
        new(PanelMode.Chibi, "AORUS Chibi"),
        new(PanelMode.Image, "Stored image"),
        new(PanelMode.Text, "Stored text"),
        new(PanelMode.Gif, "Stored GIF"),
        new(PanelMode.Carousel, "Stored carousel")
    ];

    private static readonly string[] MetricNames =
    [
        "GPU temperature", "GPU clock", "GPU utilization", "Fan speed",
        "VRAM clock", "VRAM usage", "Frame rate", "Board power"
    ];

    private readonly CheckBox[] metrics = new CheckBox[8];
    private readonly DispatcherTimer animation = new();
    private PanelImage? selectedImage;
    private PanelMedia? textMedia;
    private ImageFraming framing = new(false, 1, 0, 0);
    private Point dragStart;
    private ImageFraming dragFraming;
    private DrawingColor ink = DrawingColor.White;
    private DrawingColor backdrop = DrawingColor.Black;
    private PanelState? state;
    private int frame;
    private bool initialized;
    private bool busy;

    public MainWindow()
    {
        InitializeComponent();
        Preset.ItemsSource = Modes;
        for (int i = 0; i < metrics.Length; i++)
        {
            metrics[i] = new CheckBox { Content = MetricNames[i] };
            MetricGrid.Children.Add(metrics[i]);
        }
        for (int seconds = 1; seconds <= 10; seconds++)
            Interval.Items.Add($"{seconds} s");
        Interval.SelectedIndex = 0;
        animation.Tick += (_, _) =>
        {
            if (selectedImage is not { } image || TextChoice.IsChecked == true)
                return;
            frame = (frame + 1) % image.Preview.Count;
            FramedImage.Source = image.Preview[frame];
        };
        initialized = true;
        UpdateButtons();
        Loaded += async (_, _) => await Refresh();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await Refresh();

    private Task Refresh() => Run("Reading the LCD…", async () =>
    {
        try
        {
            var result = await Task.Run(() =>
            {
                var panel = Program.WithPanel(device => device.Status());
                using var service = new LcdService(allowChanges: false);
                return (panel, service.Running);
            });
            ShowStatus(result.panel, resetSelection: true);
            TelemetryNote.Text = result.Running
                ? "Live readouts use the AORUS LCD background service."
                : "The AORUS LCD service is stopped. Live readouts need the service.";
            Feedback.Text = "";
        }
        catch
        {
            state = null;
            ConnectionTitle.Text = "LCD unavailable";
            ConnectionDetails.Text = "";
            ModeLabel.Text = "Refresh to reconnect";
            throw;
        }
    });

    private async void Power_Click(object sender, RoutedEventArgs e)
    {
        if (state is null)
            return;
        bool on = !state.On;
        await Run(on ? "Turning the LCD on…" : "Turning the LCD off…", async () =>
        {
            ShowStatus(await Task.Run(() => Program.WithPanel(panel => panel.Power(on))));
            Feedback.Text = on ? "LCD turned on." : "LCD turned off.";
        });
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (Preset.SelectedItem is not ModeChoice selection)
            return;
        byte? selectedMetrics = null;
        byte interval = (byte)(Interval.SelectedIndex + 1);
        if (IsEnthusiast(selection.Mode))
        {
            byte flags = 0;
            for (int i = 0; i < metrics.Length; i++)
                if (metrics[i].IsChecked == true)
                    flags |= (byte)(1 << i);
            selectedMetrics = flags;
        }

        await Run("Applying display settings…", async () =>
        {
            ShowStatus(await Task.Run(() =>
                Program.WithPanel(panel => panel.Display(selection.Mode, selectedMetrics, interval))));
            Feedback.Text = $"Applied {selection.Label}.";
        });
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose content for the LCD",
            Filter = "Supported images|*.png;*.jpg;*.jpeg;*.bmp;*.gif",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
            await LoadImage(dialog.FileName);
    }

    private Task LoadImage(string path) =>
        Run("Preparing the LCD preview…", async () =>
        {
            selectedImage = await Task.Run(() => PanelImage.Load(path));
            FileLabel.Text = Path.GetFileName(path);
            FileLabel.ToolTip = path;
            SetFraming(new ImageFraming(false, 1, 0, 0));
            ShowPreview();
            Feedback.Text = selectedImage.Mode == PanelMode.Gif
                ? $"{selectedImage.Preview.Count} frames · {selectedImage.Delay} ms per frame"
                : "";
        });

    private void SetFraming(ImageFraming value)
    {
        if (selectedImage is not { } image)
            return;

        framing = value.Constrain(image.Width, image.Height);
        ZoomSlider.Value = framing.Zoom;
        ZoomValue.Text = $"{framing.Zoom * 100:0}%";
        var bounds = framing.Bounds(image.Width, image.Height);
        Canvas.SetLeft(FramedImage, bounds.X);
        Canvas.SetTop(FramedImage, bounds.Y);
        FramedImage.Width = bounds.Width;
        FramedImage.Height = bounds.Height;
    }

    private void ZoomChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (initialized && e.NewValue != framing.Zoom)
            SetFraming(framing with { Zoom = e.NewValue });
    }

    private void Fit_Click(object sender, RoutedEventArgs e) => SetFraming(new ImageFraming(false, 1, 0, 0));
    private void Fill_Click(object sender, RoutedEventArgs e) => SetFraming(new ImageFraming(true, 1, 0, 0));
    private void Center_Click(object sender, RoutedEventArgs e) => SetFraming(framing with { X = 0, Y = 0 });

    private bool IsFramingImage => selectedImage is not null && TextChoice.IsChecked != true && !busy;

    private void Preview_DragStart(object sender, MouseButtonEventArgs e)
    {
        if (!IsFramingImage)
            return;

        PreviewArea.Focus();
        dragStart = e.GetPosition(PreviewArea);
        dragFraming = framing;
        PreviewArea.CaptureMouse();
        e.Handled = true;
    }

    private void Preview_DragMove(object sender, MouseEventArgs e)
    {
        if (!PreviewArea.IsMouseCaptured || !IsFramingImage)
            return;

        Vector delta = e.GetPosition(PreviewArea) - dragStart;
        SetFraming(dragFraming with { X = dragFraming.X + delta.X, Y = dragFraming.Y + delta.Y });
    }

    private void Preview_DragEnd(object sender, MouseButtonEventArgs e)
    {
        if (!PreviewArea.IsMouseCaptured)
            return;

        Preview_DragMove(sender, e);
        PreviewArea.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void Preview_Wheel(object sender, MouseWheelEventArgs e)
    {
        if (!IsFramingImage || PreviewArea.IsMouseCaptured)
            return;

        double zoom = Math.Clamp(framing.Zoom * Math.Pow(1.15, e.Delta / 120d),
            ImageFraming.MinZoom, ImageFraming.MaxZoom);
        double ratio = zoom / framing.Zoom;
        Point pointer = e.GetPosition(PreviewArea);
        double x = pointer.X - PanelMedia.Width / 2d;
        double y = pointer.Y - PanelMedia.Height / 2d;
        SetFraming(framing with { Zoom = zoom, X = x + (framing.X - x) * ratio, Y = y + (framing.Y - y) * ratio });
        e.Handled = true;
    }

    private void Preview_KeyDown(object sender, KeyEventArgs e)
    {
        if (!IsFramingImage)
            return;
        int step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
        switch (e.Key)
        {
            case Key.Left: SetFraming(framing with { X = framing.X - step }); break;
            case Key.Right: SetFraming(framing with { X = framing.X + step }); break;
            case Key.Up: SetFraming(framing with { Y = framing.Y - step }); break;
            case Key.Down: SetFraming(framing with { Y = framing.Y + step }); break;
            default: return;
        }
        e.Handled = true;
    }

    private void ContentChanged(object sender, RoutedEventArgs e)
    {
        if (!initialized)
            return;
        bool text = TextChoice.IsChecked == true;
        TextControls.Visibility = text ? Visibility.Visible : Visibility.Collapsed;
        FileControls.Visibility = text ? Visibility.Collapsed : Visibility.Visible;
        if (text)
            UpdateText();
        else
            ShowPreview();
    }

    private void TextEdited(object sender, TextChangedEventArgs e)
    {
        if (initialized && TextChoice.IsChecked == true)
            UpdateText();
    }

    private void Ink_Click(object sender, RoutedEventArgs e) => ChooseColor(foreground: true);
    private void Background_Click(object sender, RoutedEventArgs e) => ChooseColor(foreground: false);

    private void ChooseColor(bool foreground)
    {
        using var dialog = new Forms.ColorDialog { Color = foreground ? ink : backdrop, FullOpen = true };
        if (dialog.ShowDialog() != Forms.DialogResult.OK)
            return;
        if (foreground)
            ink = dialog.Color;
        else
            backdrop = dialog.Color;
        UpdateText();
    }

    private void UpdateText()
    {
        textMedia = string.IsNullOrWhiteSpace(MessageText.Text) ? null : PanelMedia.FromText(MessageText.Text, ink, backdrop);
        ShowPreview();
    }

    private void ShowPreview()
    {
        animation.Stop();
        bool text = TextChoice.IsChecked == true;
        PanelImage? image = text ? null : selectedImage;
        frame = 0;
        PreviewScreen.Source = text ? textMedia?.Preview[0] : null;
        FramedImage.Source = image?.Preview[0];
        EmptyPreview.Visibility = image is null && !text ? Visibility.Visible : Visibility.Collapsed;
        FramingControls.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
        PreviewArea.Focusable = image is not null;
        PreviewArea.Cursor = image is null ? Cursors.Arrow : Cursors.SizeAll;
        if (image is { Preview.Count: > 1 })
        {
            animation.Interval = TimeSpan.FromMilliseconds(image.Delay);
            animation.Start();
        }
        UpdateButtons();
    }

    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        bool text = TextChoice.IsChecked == true;
        PanelImage? image = text ? null : selectedImage;
        PanelMedia? draft = text ? textMedia : null;
        PanelMode? mode = image?.Mode ?? draft?.Mode;
        if (mode is not { } contentMode)
            return;
        if (MessageBox.Show(this, $"Replace the {MediaName(contentMode)} stored on the LCD?",
            "Send to LCD", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        ImageFraming placement = framing;
        await Run("Preparing the LCD upload…", async () =>
        {
            PanelMedia media = image is null ? draft! : await Task.Run(() => image.Render(placement));
            Feedback.Text = "Sending to the LCD. Keep this window open…";
            Progress.Visibility = Visibility.Visible;
            Progress.Value = 0;
            Progress.IsIndeterminate = true;
            IProgress<double> report = new Progress<double>(value =>
            {
                Progress.IsIndeterminate = false;
                Progress.Value = value * 100;
            });
            PanelState updated = await Program.SendMedia(media, report.Report, () =>
                Feedback.Text = "Approve the Windows prompt to pause telemetry and upload. Large GIFs can take several minutes.");
            ShowStatus(updated, resetSelection: true);
            Feedback.Text = $"The LCD switched to your {MediaName(media.Mode)}.";
        });
    }

    private static string MediaName(PanelMode mode) => mode switch
    {
        PanelMode.Gif => "GIF",
        PanelMode.Text => "text",
        _ => "image"
    };

    private async Task Run(string message, Func<Task> action)
    {
        if (busy)
            return;
        busy = true;
        Editor.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        Feedback.Foreground = (Brush)FindResource("Muted");
        Feedback.Text = message;
        try { await action(); }
        catch (Exception error)
        {
            Feedback.Foreground = new SolidColorBrush(Color.FromRgb(255, 175, 155));
            Feedback.Text = error is OutOfMemoryException
                ? "Windows could not decode the image. Check the file and try again."
                : error.Message;
        }
        finally
        {
            Progress.Visibility = Visibility.Collapsed;
            busy = false;
            Editor.IsEnabled = true;
            RefreshButton.IsEnabled = true;
            UpdateButtons();
        }
    }

    private void ShowStatus(PanelState current, bool resetSelection = false)
    {
        state = current;
        ConnectionTitle.Text = "LCD connected";
        ConnectionDetails.Text = $"Firmware {current.Firmware}";
        ModeChoice? mode = Array.Find(Modes, item => item.Mode == current.Mode);
        ModeLabel.Text = $"LCD {(current.On ? "on" : "off")} · {mode?.Label ?? $"Mode {(byte)current.Mode}"}";
        PowerButton.Content = current.On ? "Turn _off" : "Turn _on";
        if (resetSelection || Preset.SelectedItem is null)
        {
            Preset.SelectedItem = mode ?? Modes[0];
            for (int i = 0; i < metrics.Length; i++)
                metrics[i].IsChecked = (current.Metrics & (1 << i)) != 0;
            Interval.SelectedIndex = Math.Clamp((int)current.Interval, 1, 10) - 1;
        }
    }

    private static bool IsEnthusiast(PanelMode mode) =>
        mode is >= PanelMode.Enthusiast1 and <= PanelMode.Enthusiast3;

    private void PresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (initialized)
            UpdateButtons();
    }

    private void UpdateButtons()
    {
        PowerButton.IsEnabled = state is not null;
        ApplyButton.IsEnabled = state is not null && Preset.SelectedItem is ModeChoice;
        UploadButton.IsEnabled = state is not null && (TextChoice.IsChecked == true ? textMedia is not null : selectedImage is not null);
        MetricsSettings.Visibility = Preset.SelectedItem is ModeChoice item && IsEnthusiast(item.Mode)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (busy)
            e.Cancel = true;
        base.OnClosing(e);
    }
}
