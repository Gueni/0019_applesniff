using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using MediaDevices;

namespace AppleSniff;

public partial class MainWindow : Window
{
    private enum Stage { NoDevice, Locked, Scanning, Ready, Copying, Done }

    private static readonly Brush DotIdle = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A));
    private static readonly Brush DotWaiting = new SolidColorBrush(Color.FromRgb(0xF2, 0xA9, 0x00));
    private static readonly Brush DotOnline = new SolidColorBrush(Color.FromRgb(0x2E, 0xBD, 0x59));

    private readonly Settings _settings = Settings.Load();
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private readonly DispatcherTimer _copyTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly Queue<(long Ticks, long Bytes)> _speedSamples = new();
    private readonly bool _loaded;
    private readonly string? _demoFolder = Environment.GetEnvironmentVariable("APPLESNIFF_DEMO") is { Length: > 0 } demo && Directory.Exists(demo) ? demo : null;

    private Stage _stage = Stage.NoDevice;
    private MediaDevice? _device;
    private string _deviceId = "";
    private List<MediaItem> _items = new();
    private CopyPlan? _plan;
    private CopyProgress? _progress;
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _planCts;
    private CancellationTokenSource? _copyCts;
    private bool _polling;
    private bool _closeWhenDone;
    private string _errorLogPath = "";

    public MainWindow()
    {
        InitializeComponent();

        DestBox.Text = _settings.Destination ?? "";
        LayoutBox.SelectedIndex = (int)_settings.Layout;
        _loaded = true;

        _pollTimer.Tick += (_, _) => Poll();
        _copyTimer.Tick += (_, _) => RefreshCopyProgress();
        Loaded += (_, _) => { _pollTimer.Start(); Poll(); };
        Closing += OnClosing;
    }

    // ------------------------------------------------------------ detection

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Windows broadcasts WM_DEVICECHANGE when something is plugged in or removed: react right away.
        (PresentationSource.FromVisual(this) as HwndSource)?.AddHook((IntPtr _, int msg, IntPtr _, IntPtr _, ref bool _) =>
        {
            if (msg == 0x0219) Dispatcher.BeginInvoke(Poll);
            return IntPtr.Zero;
        });
    }

    private async void Poll()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            if (_demoFolder != null)
            {
                if (_stage != Stage.NoDevice) return;
                var folder = _demoFolder;
                DeviceName.Text = "Demo folder";
                SetStage(Stage.Scanning);
                _items = await Task.Run(() => PhoneService.ScanFolder(folder));
                ShowLibrary();
                await ReplanAsync();
            }
            else if (_stage is Stage.NoDevice or Stage.Locked)
            {
                var (found, name, id, device) = await Task.Run(() =>
                {
                    var d = PhoneService.OpenPhone();
                    if (d == null) return (false, "", "", (MediaDevice?)null);
                    var displayName = PhoneService.DisplayName(d);
                    var deviceId = d.DeviceId ?? "";
                    if (PhoneService.HasStorage(d)) return (true, displayName, deviceId, d);
                    d.Dispose();
                    return (true, displayName, deviceId, null);
                });

                if (_stage is not (Stage.NoDevice or Stage.Locked))
                {
                    device?.Dispose();
                    return;
                }

                if (!found)
                {
                    SetStage(Stage.NoDevice);
                }
                else if (device == null)
                {
                    DeviceName.Text = name;
                    SetStage(Stage.Locked);
                }
                else
                {
                    _device = device;
                    _deviceId = id;
                    DeviceName.Text = name;
                    _ = ScanAsync();
                }
            }
            else if (_stage == Stage.Ready)
            {
                var id = _deviceId;
                var present = await Task.Run(() => PhoneService.IsPresent(id));
                if (!present && _stage == Stage.Ready) ResetToNoDevice();
            }
        }
        catch
        {
            // Detection is retried on the next tick.
        }
        finally
        {
            _polling = false;
        }
    }

    private void DropDevice()
    {
        _scanCts?.Cancel();
        _planCts?.Cancel();
        var device = _device;
        _device = null;
        _items = new List<MediaItem>();
        _plan = null;
        if (device != null) Task.Run(() => { try { device.Dispose(); } catch { } });
    }

    private void ResetToNoDevice()
    {
        DropDevice();
        SetStage(Stage.NoDevice);
    }

    // ----------------------------------------------------------------- scan

    private async Task ScanAsync()
    {
        SetStage(Stage.Scanning);
        _scanCts?.Cancel();
        var cts = _scanCts = new CancellationTokenSource();
        var device = _device!;
        IProgress<int> progress = new Progress<int>(n =>
        {
            if (_stage == Stage.Scanning) WaitText.Text = $"{n:N0} photos and videos found so far…";
        });

        try
        {
            _items = await Task.Run(() => PhoneService.Scan(device, progress.Report, cts.Token));
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
            // Usually the phone locked or was unplugged mid-scan: start over once it is readable again.
            if (cts.IsCancellationRequested) return;
            DropDevice();
            SetStage(Stage.Locked);
            return;
        }

        if (cts.IsCancellationRequested) return;

        ShowLibrary();
        await ReplanAsync();
    }

    private void ShowLibrary()
    {
        StatPhotos.Text = _items.Count(i => !i.IsVideo).ToString("N0");
        StatVideos.Text = _items.Count(i => i.IsVideo).ToString("N0");
        StatSize.Text = FormatSize(_items.Sum(i => i.Size));
        SetStage(Stage.Ready);
    }

    // ----------------------------------------------------------------- plan

    private async Task ReplanAsync()
    {
        _planCts?.Cancel();
        _plan = null;
        StartButton.IsEnabled = false;

        if (_items.Count == 0)
        {
            StartButton.Content = "Nothing to copy";
            PlanText.Text = "No photos or videos were found. Make sure the iPhone is unlocked, then rescan.";
            return;
        }

        var destination = _settings.Destination;
        if (string.IsNullOrWhiteSpace(destination))
        {
            StartButton.Content = "Choose a folder to start";
            PlanText.Text = "";
            return;
        }

        StartButton.Content = "Checking the folder…";
        PlanText.Text = "";
        var cts = _planCts = new CancellationTokenSource();
        var items = _items;
        var layout = _settings.Layout;

        CopyPlan plan;
        try
        {
            plan = await Task.Run(() => PhoneService.BuildPlan(items, destination, layout, cts.Token));
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            StartButton.Content = "Choose another folder";
            PlanText.Text = "This folder can't be used: " + ex.Message;
            return;
        }

        if (cts.IsCancellationRequested || _stage != Stage.Ready) return;
        _plan = plan;

        var skipped = plan.AlreadyCopied > 0 ? $"{plan.AlreadyCopied:N0} already in this folder will be skipped." : "";
        if (plan.Jobs.Count == 0)
        {
            StartButton.Content = "Nothing new to copy";
            PlanText.Text = "Everything on the iPhone is already in this folder.";
        }
        else if (!plan.EnoughSpace)
        {
            StartButton.Content = "Not enough free space";
            PlanText.Text = $"This copy needs {FormatSize(plan.Bytes)} but the drive only has {FormatSize(plan.FreeBytes)} free.";
        }
        else
        {
            StartButton.Content = $"Copy {plan.Jobs.Count:N0} {(plan.Jobs.Count == 1 ? "item" : "items")}  ·  {FormatSize(plan.Bytes)}";
            StartButton.IsEnabled = true;
            PlanText.Text = skipped;
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose where to copy your photos and videos" };
        if (Directory.Exists(_settings.Destination)) dialog.InitialDirectory = _settings.Destination;
        if (dialog.ShowDialog(this) != true) return;

        _settings.Destination = dialog.FolderName;
        _settings.Save();
        DestBox.Text = dialog.FolderName;
        _ = ReplanAsync();
    }

    private void Layout_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_loaded || LayoutBox.SelectedIndex < 0) return;
        _settings.Layout = (FolderLayout)LayoutBox.SelectedIndex;
        _settings.Save();
        if (_stage == Stage.Ready) _ = ReplanAsync();
    }

    private void Rescan_Click(object sender, RoutedEventArgs e)
    {
        // Reopen the phone from scratch so the new scan sees photos taken since the last one.
        ResetToNoDevice();
        Poll();
    }

    // ----------------------------------------------------------------- copy

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        var plan = _plan;
        var destination = _settings.Destination;
        if (plan == null || plan.Jobs.Count == 0 || string.IsNullOrWhiteSpace(destination) || _stage != Stage.Ready) return;

        var cts = _copyCts = new CancellationTokenSource();
        var progress = _progress = new CopyProgress();
        var deviceId = _deviceId;
        _speedSamples.Clear();
        CancelButton.IsEnabled = true;
        CancelButton.Content = "Cancel";
        SetStage(Stage.Copying);
        RefreshCopyProgress();
        _copyTimer.Start();
        SetThreadExecutionState(EsContinuous | EsSystemRequired);

        var canceled = false;
        string? fatal = null;
        try
        {
            await Task.Run(() => PhoneService.Copy(plan, destination, deviceId, progress, cts.Token));
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }
        catch (Exception ex)
        {
            fatal = ex.Message;
        }
        finally
        {
            SetThreadExecutionState(EsContinuous);
            _copyTimer.Stop();
        }

        if (_closeWhenDone)
        {
            Close();
            return;
        }

        ShowResult(plan, progress, destination, canceled, fatal);
    }

    private void RefreshCopyProgress()
    {
        var plan = _plan;
        var progress = _progress;
        if (plan == null || progress == null) return;

        var bytes = Interlocked.Read(ref progress.BytesDone);
        var fraction = plan.Bytes > 0 ? Math.Clamp((double)bytes / plan.Bytes, 0, 1) : 0;
        var handled = progress.FilesDone + progress.FilesFailed;

        CopyBar.Value = fraction;
        CopyPercent.Text = $"{Math.Floor(fraction * 100):0}%";
        CopyCounts.Text = $"{handled:N0} of {plan.Jobs.Count:N0} items  ·  {FormatSize(bytes)} of {FormatSize(plan.Bytes)}";
        CopyFile.Text = progress.CurrentName;
        Taskbar.ProgressValue = fraction;

        // Speed over the last few seconds, so the estimate follows the cable rather than one big video.
        var now = Stopwatch.GetTimestamp();
        _speedSamples.Enqueue((now, bytes));
        while (_speedSamples.Count > 2 && Stopwatch.GetElapsedTime(_speedSamples.Peek().Ticks, now).TotalSeconds > 5) _speedSamples.Dequeue();
        var (firstTicks, firstBytes) = _speedSamples.Peek();
        var seconds = Stopwatch.GetElapsedTime(firstTicks, now).TotalSeconds;
        if (seconds < 1)
        {
            CopySpeed.Text = "Starting…";
            return;
        }

        var speed = (bytes - firstBytes) / seconds;
        if (speed < 1)
        {
            CopySpeed.Text = "Waiting for the iPhone…";
            return;
        }

        var remaining = TimeSpan.FromSeconds((plan.Bytes - bytes) / speed);
        CopySpeed.Text = $"{FormatSize((long)speed)}/s  ·  {FormatDuration(remaining)} left";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _copyCts?.Cancel();
        CancelButton.IsEnabled = false;
        CancelButton.Content = "Stopping…";
    }

    private void ShowResult(CopyPlan plan, CopyProgress progress, string destination, bool canceled, string? fatal)
    {
        var copied = progress.FilesDone;
        var failed = progress.FilesFailed;
        var copiedText = copied == 1 ? "1 item was copied" : $"{copied:N0} items were copied";
        var problem = true;

        if (fatal != null)
        {
            DoneTitle.Text = "The copy stopped";
            DoneText.Text = $"{copiedText} before this error: {fatal}";
        }
        else if (progress.DeviceLost)
        {
            DoneTitle.Text = "iPhone disconnected";
            DoneText.Text = $"{copiedText} before the connection was lost. Plug the iPhone back in and start again: it continues where it stopped.";
        }
        else if (canceled)
        {
            DoneTitle.Text = "Copy stopped";
            DoneText.Text = $"{copiedText}. Start again any time: it continues where it stopped.";
        }
        else if (failed > 0)
        {
            DoneTitle.Text = "Finished with problems";
            DoneText.Text = $"{copiedText}, but {failed:N0} could not be read from the iPhone.";
        }
        else
        {
            problem = false;
            DoneTitle.Text = "All done";
            DoneText.Text = $"{copiedText} to {destination}"
                + (plan.AlreadyCopied > 0 ? $"\n{plan.AlreadyCopied:N0} were already there and were skipped." : "");
        }

        DoneIcon.Text = problem ? "" : "";

        List<string> errors;
        lock (progress.Errors) errors = progress.Errors.ToList();
        if (errors.Count > 0)
        {
            DoneTip.Text = "Tip: on the iPhone, open Settings → Apps → Photos and set “Transfer to Mac or PC” to “Keep Originals”, "
                         + "then keep the phone unlocked and run the copy again. Only the missing items will be copied.";
            DoneTip.Visibility = Visibility.Visible;
            try
            {
                _errorLogPath = Path.Combine(Settings.AppDataDir, "last-errors.txt");
                File.WriteAllLines(_errorLogPath, errors);
                ErrorsButton.Visibility = Visibility.Visible;
            }
            catch
            {
                ErrorsButton.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            DoneTip.Visibility = Visibility.Collapsed;
            ErrorsButton.Visibility = Visibility.Collapsed;
        }

        SetStage(Stage.Done);
        Taskbar.ProgressState = problem ? TaskbarItemProgressState.Error : TaskbarItemProgressState.None;
        if (!IsActive) FlashWindow(new WindowInteropHelper(this).Handle, true);
    }

    private void Errors_Click(object sender, RoutedEventArgs e) => OpenInShell(_errorLogPath);

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(_settings.Destination)) OpenInShell(_settings.Destination!);
    }

    private async void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_stage != Stage.Done) return;
        var id = _deviceId;
        var present = _demoFolder != null || (_device != null && await Task.Run(() => PhoneService.IsPresent(id)));
        if (_stage != Stage.Done) return;

        if (!present)
        {
            ResetToNoDevice();
            Poll();
            return;
        }

        SetStage(Stage.Ready);
        await ReplanAsync();
    }

    // ------------------------------------------------------------------- ui

    private void SetStage(Stage stage)
    {
        _stage = stage;
        PanelWait.Visibility = stage is Stage.NoDevice or Stage.Locked or Stage.Scanning ? Visibility.Visible : Visibility.Collapsed;
        PanelReady.Visibility = stage == Stage.Ready ? Visibility.Visible : Visibility.Collapsed;
        PanelCopy.Visibility = stage == Stage.Copying ? Visibility.Visible : Visibility.Collapsed;
        PanelDone.Visibility = stage == Stage.Done ? Visibility.Visible : Visibility.Collapsed;
        WaitBar.Visibility = stage == Stage.Scanning ? Visibility.Visible : Visibility.Collapsed;
        Taskbar.ProgressState = stage switch
        {
            Stage.Scanning => TaskbarItemProgressState.Indeterminate,
            Stage.Copying => TaskbarItemProgressState.Normal,
            _ => TaskbarItemProgressState.None,
        };

        switch (stage)
        {
            case Stage.NoDevice:
                DeviceName.Text = "No iPhone";
                DeviceDot.Fill = DotIdle;
                WaitIcon.Text = "";
                WaitTitle.Text = "Connect your iPhone";
                WaitText.Text = "Plug it into this PC with a USB cable. It will be detected automatically.";
                break;
            case Stage.Locked:
                DeviceDot.Fill = DotWaiting;
                WaitIcon.Text = "";
                WaitTitle.Text = "Unlock your iPhone";
                WaitText.Text = "Unlock it and tap “Allow” (or “Trust”) when it asks whether this computer may access photos and videos.";
                break;
            case Stage.Scanning:
                DeviceDot.Fill = DotOnline;
                WaitIcon.Text = "";
                WaitTitle.Text = "Reading your library";
                WaitText.Text = "Looking for photos and videos…";
                break;
            default:
                DeviceDot.Fill = DotOnline;
                break;
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_stage == Stage.Copying && !_closeWhenDone)
        {
            // Let the file in flight be cleaned up before the window goes away.
            e.Cancel = true;
            _closeWhenDone = true;
            Cancel_Click(this, new RoutedEventArgs());
            return;
        }

        _pollTimer.Stop();
        _scanCts?.Cancel();
        _planCts?.Cancel();
    }

    private static void OpenInShell(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            // Nothing useful to do if Explorer refuses to open it.
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):0.0} GB";
        if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):0} MB";
        if (bytes >= 1L << 10) return $"{bytes / (double)(1L << 10):0} KB";
        return $"{bytes} B";
    }

    private static string FormatDuration(TimeSpan t)
    {
        if (t.TotalHours >= 1) return $"about {(int)t.TotalHours} h {t.Minutes} min";
        if (t.TotalMinutes >= 1) return $"about {Math.Ceiling(t.TotalMinutes):0} min";
        return "less than a minute";
    }

    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);

    [DllImport("user32.dll")]
    private static extern bool FlashWindow(IntPtr hwnd, bool invert);
}
