using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using MediaDevices;

namespace Sniffer;

public partial class MainWindow : Window
{
    /// <summary>Connected: the phone is readable but its library has not been scanned (scanning only starts when asked).</summary>
    private enum Stage { NoDevice, Locked, Connected, Scanning, Ready, Copying, Done }

    private static readonly Brush DotIdle = new SolidColorBrush(Color.FromRgb(0x6E, 0x6A, 0x7E));
    private static readonly Brush DotWaiting = new SolidColorBrush(Color.FromRgb(0xF5, 0xB8, 0x41));
    private static readonly Brush DotOnline = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));

    private readonly Settings _settings = Settings.Load();
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private readonly DispatcherTimer _copyTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly Queue<(long Ticks, long Bytes)> _speedSamples = new();
    private readonly bool _loaded;
    private readonly string? _demoFolder = Environment.GetEnvironmentVariable("SNIFFER_DEMO") is { Length: > 0 } demo && Directory.Exists(demo) ? demo : null;

    private Stage _stage = Stage.NoDevice;
    private List<PhoneInfo> _phones = new();
    private PhoneInfo? _phone;          // the phone being shown (connected, locked or being read)
    private string? _chosenId;          // picked by hand from the header when several are plugged in
    private MediaDevice? _device;
    private string _deviceId = "";
    private List<MediaItem> _items = new();
    private CopyPlan? _plan;
    private int _selectedCount;
    private CopyProgress? _progress;
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _planCts;
    private CancellationTokenSource? _copyCts;
    private bool _polling;
    private bool _autoStarted;
    private bool _scanRequested;        // the user asked to scan: do it as soon as the phone is readable
    private bool _closeWhenDone;
    private string _errorLogPath = "";
    private bool _sending;              // the running transfer goes PC -> phone rather than phone -> PC
    private int _runFiles;              // totals of the running transfer, for the progress screen
    private long _runBytes;

    public MainWindow()
    {
        InitializeComponent();

        DestBox.Text = _settings.Destination ?? "";
        LayoutBox.SelectedIndex = (int)_settings.Layout;
        PhotosCheck.IsChecked = _settings.IncludePhotos;
        VideosCheck.IsChecked = _settings.IncludeVideos;
        DateBox.SelectedIndex = Math.Clamp(_settings.DateFilter, 0, 4);
        SincePicker.SelectedDate = _settings.Since;
        SinceRow.Visibility = _settings.DateFilter == 4 ? Visibility.Visible : Visibility.Collapsed;
        AutoCheck.IsChecked = _settings.AutoStart;
        _loaded = true;

        _pollTimer.Tick += (_, _) => Poll();
        _copyTimer.Tick += (_, _) => RefreshCopyProgress();
        Loaded += (_, _) => { _pollTimer.Start(); Poll(); };
        Closing += OnClosing;
        SetStage(Stage.NoDevice);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        // Graphite title bar with light text, matching the window (Windows 11; ignored elsewhere).
        int on = 1, caption = 0x001C1617, text = 0x00F7F2F3;
        DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));
        DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));
        DwmSetWindowAttribute(hwnd, 36, ref text, sizeof(int));
        // Windows broadcasts WM_DEVICECHANGE when something is plugged in or removed: react right away.
        (PresentationSource.FromVisual(this) as HwndSource)?.AddHook((IntPtr _, int msg, IntPtr _, IntPtr _, ref bool _) =>
        {
            if (msg == 0x0219) Dispatcher.BeginInvoke(Poll);
            return IntPtr.Zero;
        });
    }

    // ------------------------------------------------------------ detection

    private async void Poll()
    {
        if (_polling || _stage is Stage.Copying or Stage.Done) return;
        _polling = true;
        try
        {
            if (_demoFolder != null)
            {
                if (_stage != Stage.NoDevice) return;
                var folder = _demoFolder;
                _phone = new PhoneInfo("demo", "Demo folder", false);
                DeviceName.Text = _phone.Name;
                if (!_scanRequested && !_settings.AutoStart)
                {
                    SetStage(Stage.Connected);
                    return;
                }
                _scanRequested = false;
                SetStage(Stage.Scanning);
                _items = await Task.Run(() => PhoneService.ScanFolder(folder));
                ShowLibrary();
                await ReplanAsync();
                return;
            }

            // While a phone is being allowed, Windows reinstalls its driver and calls can hang:
            // never let that block detection, just try again on the next tick.
            var phones = await Task.Run(PhoneService.ListPhones).WaitAsync(TimeSpan.FromSeconds(15));
            UpdatePhoneList(phones);

            if (_stage is Stage.Ready or Stage.Scanning or Stage.Connected)
            {
                if (!phones.Any(p => p.Id == _deviceId))
                {
                    Log.Write($"'{_phone?.Name}' unplugged");
                    ResetToNoDevice();
                }
                return;
            }
            if (_stage is Stage.Copying or Stage.Done) return;

            var target = phones.FirstOrDefault(p => p.Id == _chosenId)
                      ?? phones.FirstOrDefault(p => p.Id == _settings.LastDeviceId)
                      ?? phones.FirstOrDefault();
            if (target == null)
            {
                if (_stage != Stage.NoDevice) Log.Write("No phone connected");
                _phone = null;
                SetStage(Stage.NoDevice);
                return;
            }

            var probe = Task.Run(() =>
            {
                var d = PhoneService.Open(target.Id);
                if (d == null || PhoneService.HasMedia(d, target.IsApple)) return d;
                d.Dispose();
                return null;
            });

            MediaDevice? device;
            try
            {
                device = await probe.WaitAsync(TimeSpan.FromSeconds(15));
            }
            catch (TimeoutException)
            {
                Log.Write("Device probe timed out, retrying");
                _ = probe.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result?.Dispose(); });
                return;
            }

            if (_stage is not (Stage.NoDevice or Stage.Locked))
            {
                device?.Dispose();
                return;
            }

            _phone = target;
            DeviceName.Text = target.Name;
            if (device == null)
            {
                if (_stage != Stage.Locked) Log.Write($"'{target.Name}' connected but its photos are not readable yet");
                SetStage(Stage.Locked);
                return;
            }

            _device = device;
            _deviceId = target.Id;
            _settings.LastDeviceId = target.Id;
            _settings.Save();

            // Only read the library when the user asked for it (or opted into automatic copying).
            if (_scanRequested || _settings.AutoStart)
            {
                Log.Write($"'{target.Name}' readable (apple={target.IsApple}), scanning");
                _ = ScanAsync();
            }
            else
            {
                Log.Write($"'{target.Name}' readable (apple={target.IsApple}), waiting for the user to scan");
                SetStage(Stage.Connected);
            }
        }
        catch (TimeoutException)
        {
            Log.Write("Device list timed out, retrying");
        }
        catch (Exception ex)
        {
            Log.Error("Poll", ex);
        }
        finally
        {
            _polling = false;
        }
    }

    private void UpdatePhoneList(List<PhoneInfo> phones)
    {
        _phones = phones;
        var several = phones.Count > 1;
        DeviceChevron.Visibility = several ? Visibility.Visible : Visibility.Collapsed;
        DevicePill.Tag = several ? "pick" : null;
        DevicePill.ToolTip = several ? "Choose which phone to copy from" : null;
    }

    private void DevicePill_Click(object sender, RoutedEventArgs e)
    {
        if (_phones.Count < 2 || _stage == Stage.Copying) return;

        var menu = new ContextMenu { PlacementTarget = DevicePill, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var phone in _phones)
        {
            var item = new MenuItem { Header = phone.Name, IsCheckable = true, IsChecked = phone.Id == _phone?.Id };
            item.Click += (_, _) => SwitchTo(phone);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void SwitchTo(PhoneInfo phone)
    {
        if (phone.Id == _phone?.Id || _stage == Stage.Copying) return;
        Log.Write($"Switching to '{phone.Name}'");
        _chosenId = phone.Id;
        ResetToNoDevice();
        Poll();
    }

    private void DropDevice()
    {
        _scanCts?.Cancel();
        _planCts?.Cancel();
        var device = _device;
        _device = null;
        _deviceId = "";
        _items = new List<MediaItem>();
        _plan = null;
        _autoStarted = false;
        if (device != null) Task.Run(() => { try { device.Dispose(); } catch { } });
    }

    private void ResetToNoDevice()
    {
        DropDevice();
        // A scan the user asked for survives the phone locking mid-scan (DropDevice alone), not a new connection.
        _scanRequested = false;
        SetStage(Stage.NoDevice);
    }

    // ----------------------------------------------------------------- scan

    private async Task ScanAsync()
    {
        SetStage(Stage.Scanning);
        _scanCts?.Cancel();
        var cts = _scanCts = new CancellationTokenSource();
        var device = _device!;
        var isApple = _phone?.IsApple ?? false;
        IProgress<int> progress = new Progress<int>(n =>
        {
            if (_stage == Stage.Scanning) WaitText.Text = $"{n:N0} photos and videos found so far…";
        });

        try
        {
            _items = await Task.Run(() => PhoneService.Scan(device, isApple, progress.Report, cts.Token));
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            // Usually the phone locked or was unplugged mid-scan: start over once it is readable again.
            if (cts.IsCancellationRequested) return;
            Log.Error("Scan", ex);
            DropDevice();
            SetStage(Stage.Locked);
            return;
        }

        if (cts.IsCancellationRequested) return;

        _scanRequested = false;
        Log.Write($"Scan done: {_items.Count} items, {FormatSize(_items.Sum(i => i.Size))}");
        if (_items.Count == 0)
        {
            // Nothing visible almost always means the phone has not fully granted access yet: keep waiting.
            DropDevice();
            SetStage(Stage.Locked);
            return;
        }

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

    private DateTime? SinceDate() => _settings.DateFilter switch
    {
        1 => DateTime.Today.AddDays(-7),
        2 => DateTime.Today.AddDays(-30),
        3 => DateTime.Today.AddYears(-1),
        4 => _settings.Since?.Date,
        _ => null,
    };

    /// <summary>The items the filters let through. Items without a known date are kept: better copied than missed.</summary>
    private List<MediaItem> SelectedItems()
    {
        var since = SinceDate();
        return _items.Where(i => (i.IsVideo ? _settings.IncludeVideos : _settings.IncludePhotos)
                              && (since == null || i.Date == null || i.Date >= since)).ToList();
    }

    private async Task ReplanAsync()
    {
        _planCts?.Cancel();
        _plan = null;
        StartButton.IsEnabled = false;

        var destination = _settings.Destination;
        if (string.IsNullOrWhiteSpace(destination))
        {
            StartButton.Content = "Choose a folder to start";
            PlanText.Text = "";
            return;
        }

        if (!_settings.IncludePhotos && !_settings.IncludeVideos)
        {
            StartButton.Content = "Choose photos, videos or both";
            PlanText.Text = "";
            return;
        }

        var items = SelectedItems();
        _selectedCount = items.Count;
        var filtered = items.Count != _items.Count;
        if (items.Count == 0)
        {
            StartButton.Content = "Nothing to copy";
            PlanText.Text = "No photos or videos match these choices.";
            return;
        }

        StartButton.Content = "Checking the folder…";
        PlanText.Text = "";
        var cts = _planCts = new CancellationTokenSource();
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

        var notes = new List<string>();
        if (filtered) notes.Add($"{items.Count:N0} of {_items.Count:N0} items match your choices.");
        if (plan.AlreadyCopied > 0 && plan.Jobs.Count > 0) notes.Add($"{plan.AlreadyCopied:N0} already in this folder will be skipped.");

        if (plan.Jobs.Count == 0)
        {
            StartButton.Content = "Nothing new to copy";
            notes.Add(filtered ? "All of them are already in this folder." : "Everything on the phone is already in this folder.");
        }
        else if (!plan.EnoughSpace)
        {
            StartButton.Content = "Not enough free space";
            notes.Add($"This copy needs {FormatSize(plan.Bytes)} but the drive only has {FormatSize(plan.FreeBytes)} free.");
        }
        else
        {
            StartButton.Content = $"Copy {plan.Jobs.Count:N0} {(plan.Jobs.Count == 1 ? "item" : "items")}  ·  {FormatSize(plan.Bytes)}";
            StartButton.IsEnabled = true;
        }
        PlanText.Text = string.Join(" ", notes);

        // Auto mode: once per connection, so "Back" after a copy does not start another one by itself.
        if (_settings.AutoStart && !_autoStarted && StartButton.IsEnabled)
        {
            _autoStarted = true;
            Log.Write("Auto-starting copy");
            Start_Click(this, new RoutedEventArgs());
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
        if (_stage == Stage.Ready) _ = ReplanAsync();
    }

    /// <summary>Any of the options on the ready screen changed: remember it and recompute what will be copied.</summary>
    private void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;

        _settings.Layout = (FolderLayout)Math.Max(0, LayoutBox.SelectedIndex);
        _settings.IncludePhotos = PhotosCheck.IsChecked == true;
        _settings.IncludeVideos = VideosCheck.IsChecked == true;
        _settings.DateFilter = Math.Max(0, DateBox.SelectedIndex);
        _settings.AutoStart = AutoCheck.IsChecked == true;

        SinceRow.Visibility = _settings.DateFilter == 4 ? Visibility.Visible : Visibility.Collapsed;
        if (_settings.DateFilter == 4 && SincePicker.SelectedDate == null) SincePicker.SelectedDate = DateTime.Today.AddMonths(-1);
        _settings.Since = SincePicker.SelectedDate;
        _settings.Save();

        // Ticking "automatic" is not a request to start right now.
        if (ReferenceEquals(sender, AutoCheck)) _autoStarted = true;
        if (_stage == Stage.Ready) _ = ReplanAsync();
    }

    private void Rescan_Click(object sender, RoutedEventArgs e)
    {
        // Reopen the phone from scratch so the new scan sees photos taken since the last one.
        ResetToNoDevice();
        _scanRequested = true;
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
        _sending = false;
        _runFiles = plan.Jobs.Count;
        _runBytes = plan.Bytes;
        _speedSamples.Clear();
        CancelButton.IsEnabled = true;
        CancelButton.Content = "Cancel";
        SetStage(Stage.Copying);
        RefreshCopyProgress();
        _copyTimer.Start();
        SetThreadExecutionState(EsContinuous | EsSystemRequired);

        Log.Write($"Copy started: {plan.Jobs.Count} items, {FormatSize(plan.Bytes)} -> {destination} ({_settings.Layout})");
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

        Log.Write($"Copy ended: copied={progress.FilesDone} failed={progress.FilesFailed} canceled={canceled} deviceLost={progress.DeviceLost} fatal={fatal ?? "-"}");
        lock (progress.Errors) foreach (var err in progress.Errors.Take(50)) Log.Write("  failed: " + err);

        if (_closeWhenDone)
        {
            Close();
            return;
        }

        ShowResult(plan, progress, destination, canceled, fatal);
    }

    private void RefreshCopyProgress()
    {
        var progress = _progress;
        if (progress == null) return;

        var bytes = Interlocked.Read(ref progress.BytesDone);
        var fraction = _runBytes > 0 ? Math.Clamp((double)bytes / _runBytes, 0, 1) : 0;
        var handled = progress.FilesDone + progress.FilesFailed + progress.FilesSkipped;

        CopyBar.Value = fraction;
        CopyPercent.Text = $"{Math.Floor(fraction * 100):0}%";
        CopyCounts.Text = $"{handled:N0} of {_runFiles:N0} {(_sending ? "files sent" : "items")}  ·  {FormatSize(bytes)} of {FormatSize(_runBytes)}";
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
            CopySpeed.Text = "Waiting for the phone…";
            return;
        }

        var remaining = TimeSpan.FromSeconds((_runBytes - bytes) / speed);
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
        var isApple = _phone?.IsApple ?? false;
        var problem = true;

        if (fatal != null)
        {
            DoneTitle.Text = "The copy stopped";
            DoneText.Text = $"{copiedText} before this error: {fatal}";
        }
        else if (progress.DeviceLost)
        {
            DoneTitle.Text = "Phone disconnected";
            DoneText.Text = $"{copiedText} before the connection was lost. Plug the phone back in and start again: it continues where it stopped.";
        }
        else if (canceled)
        {
            DoneTitle.Text = "Copy stopped";
            DoneText.Text = $"{copiedText}. Start again any time: it continues where it stopped.";
        }
        else if (failed > 0)
        {
            DoneTitle.Text = "Finished with problems";
            DoneText.Text = $"{copiedText}, but {failed:N0} could not be read from the phone.";
        }
        else
        {
            problem = false;
            DoneTitle.Text = "All done";
            DoneText.Text = $"{copiedText} to {destination}"
                + (plan.AlreadyCopied > 0 ? $"\n{plan.AlreadyCopied:N0} were already there and were skipped." : "");
        }

        DoneIcon.Text = problem ? "" : "";
        DoneIcon.Foreground = (Brush)FindResource(problem ? "Amber" : "PurpleLight");
        DoneBadge.Background = (Brush)FindResource(problem ? "AmberSoft" : "PurpleSoft");

        List<string> errors;
        lock (progress.Errors) errors = progress.Errors.ToList();
        if (errors.Count > 0 || progress.DeviceLost)
        {
            DoneTip.Text = isApple
                ? "Tip: on the iPhone, open Settings → Apps → Photos and set “Transfer to Mac or PC” to “Keep Originals”, "
                  + "then keep the phone unlocked and run the copy again. Only the missing items will be copied."
                : "Tip: keep the phone unlocked with “File transfer” selected for USB, then run the copy again. "
                  + "Only the missing items will be copied.";
            DoneTip.Visibility = Visibility.Visible;
        }
        else
        {
            DoneTip.Visibility = Visibility.Collapsed;
        }

        OpenFolderButton.Visibility = Visibility.Visible;
        ShowErrors(errors);
        SetStage(Stage.Done);
        Taskbar.ProgressState = problem ? TaskbarItemProgressState.Error : TaskbarItemProgressState.None;
        if (!IsActive) FlashWindow(new WindowInteropHelper(this).Handle, true);
    }

    private void ShowErrors(List<string> errors)
    {
        ErrorsButton.Visibility = Visibility.Collapsed;
        if (errors.Count > 0)
        {
            try
            {
                _errorLogPath = Path.Combine(Settings.AppDataDir, "last-errors.txt");
                File.WriteAllLines(_errorLogPath, errors);
                ErrorsButton.Visibility = Visibility.Visible;
            }
            catch
            {
                // No list to show; the summary above still says how many failed.
            }
        }
    }

    // ------------------------------------------------------- send to phone

    private bool CanSend => _stage is Stage.Ready or Stage.Connected && _demoFolder == null && _phone is { IsApple: false } && _deviceId.Length > 0;

    private void Send_Click(object sender, RoutedEventArgs e)
    {
        if (!CanSend) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Choose files to send to the phone", Multiselect = true };
        if (dialog.ShowDialog(this) == true) _ = SendAsync(dialog.FileNames);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = CanSend && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (CanSend && e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0) _ = SendAsync(paths);
    }

    private async Task SendAsync(string[] paths)
    {
        if (!CanSend) return;
        List<SendJob> jobs;
        try
        {
            jobs = await Task.Run(() => PhoneService.CollectFiles(paths));
        }
        catch (Exception ex)
        {
            PlanText.Text = "Those files can't be read: " + ex.Message;
            return;
        }
        if (jobs.Count == 0 || !CanSend) return;

        var cts = _copyCts = new CancellationTokenSource();
        var progress = _progress = new CopyProgress();
        var deviceId = _deviceId;
        _sending = true;
        _runFiles = jobs.Count;
        _runBytes = jobs.Sum(j => j.Size);
        _speedSamples.Clear();
        CancelButton.IsEnabled = true;
        CancelButton.Content = "Cancel";
        SetStage(Stage.Copying);
        RefreshCopyProgress();
        _copyTimer.Start();
        SetThreadExecutionState(EsContinuous | EsSystemRequired);

        Log.Write($"Send started: {jobs.Count} files, {FormatSize(_runBytes)} -> phone");
        var canceled = false;
        string? fatal = null;
        var phoneFolder = PhoneService.SendFolder;
        try
        {
            phoneFolder = await Task.Run(() => PhoneService.Send(deviceId, jobs, progress, cts.Token));
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

        Log.Write($"Send ended: sent={progress.FilesDone} skipped={progress.FilesSkipped} failed={progress.FilesFailed} canceled={canceled} deviceLost={progress.DeviceLost} fatal={fatal ?? "-"}");
        lock (progress.Errors) foreach (var err in progress.Errors.Take(50)) Log.Write("  failed: " + err);

        if (_closeWhenDone)
        {
            Close();
            return;
        }

        var sent = progress.FilesDone;
        var sentText = sent == 1 ? "1 file was sent" : $"{sent:N0} files were sent";
        var problem = true;
        if (fatal != null)
        {
            DoneTitle.Text = "Sending stopped";
            DoneText.Text = $"{sentText} before this error: {fatal}";
        }
        else if (progress.DeviceLost)
        {
            DoneTitle.Text = "Phone disconnected";
            DoneText.Text = $"{sentText} before the connection was lost. Plug it back in and send again: files already there are skipped.";
        }
        else if (canceled)
        {
            DoneTitle.Text = "Sending stopped";
            DoneText.Text = $"{sentText}. Send again any time: files already on the phone are skipped.";
        }
        else if (progress.FilesFailed > 0)
        {
            DoneTitle.Text = "Finished with problems";
            DoneText.Text = $"{sentText}, but {progress.FilesFailed:N0} could not be written to the phone.";
        }
        else
        {
            problem = false;
            DoneTitle.Text = "Sent to phone";
            DoneText.Text = $"{sentText} to {phoneFolder} on the phone."
                + (progress.FilesSkipped > 0 ? $"\n{progress.FilesSkipped:N0} were already there and were skipped." : "");
        }

        DoneIcon.Text = problem ? "" : "";
        DoneIcon.Foreground = (Brush)FindResource(problem ? "Amber" : "PurpleLight");
        DoneBadge.Background = (Brush)FindResource(problem ? "AmberSoft" : "PurpleSoft");
        DoneTip.Text = "Tip: keep the phone unlocked with “File transfer” selected for USB. A USB 3 port and cable make sending much faster.";
        DoneTip.Visibility = problem ? Visibility.Visible : Visibility.Collapsed;
        OpenFolderButton.Visibility = Visibility.Collapsed;

        List<string> errors;
        lock (progress.Errors) errors = progress.Errors.ToList();
        ShowErrors(errors);
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
        var present = _demoFolder != null
                   || (_device != null && await Task.Run(() => PhoneService.ListPhones().Any(p => p.Id == id)));
        if (_stage != Stage.Done) return;

        if (!present)
        {
            ResetToNoDevice();
            Poll();
            return;
        }

        // After sending from the "connected" screen there is no library yet: go back there instead.
        if (_items.Count == 0)
        {
            SetStage(Stage.Connected);
            return;
        }

        SetStage(Stage.Ready);
        await ReplanAsync();
    }

    // ------------------------------------------------------------------- ui

    private void SetStage(Stage stage)
    {
        _stage = stage;
        PanelWait.Visibility = stage is Stage.NoDevice or Stage.Locked or Stage.Connected or Stage.Scanning ? Visibility.Visible : Visibility.Collapsed;
        ConnectedActions.Visibility = stage == Stage.Connected ? Visibility.Visible : Visibility.Collapsed;
        PanelReady.Visibility = stage == Stage.Ready ? Visibility.Visible : Visibility.Collapsed;
        PanelCopy.Visibility = stage == Stage.Copying ? Visibility.Visible : Visibility.Collapsed;
        PanelDone.Visibility = stage == Stage.Done ? Visibility.Visible : Visibility.Collapsed;
        WaitBar.Visibility = stage == Stage.Scanning ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.Visibility = stage == Stage.Locked ? Visibility.Visible : Visibility.Collapsed;
        Taskbar.ProgressState = stage switch
        {
            Stage.Scanning => TaskbarItemProgressState.Indeterminate,
            Stage.Copying => TaskbarItemProgressState.Normal,
            _ => TaskbarItemProgressState.None,
        };

        var isApple = _phone?.IsApple ?? false;
        switch (stage)
        {
            case Stage.NoDevice:
                DeviceName.Text = "No phone";
                DeviceDot.Fill = DotIdle;
                WaitIcon.Text = "";
                WaitTitle.Text = "Connect your phone";
                WaitText.Text = "Plug your iPhone or Android phone into this PC with a USB cable. It will be detected automatically.\n"
                              + "On Android, pick “File transfer” in the USB notification.";
                break;
            case Stage.Locked:
                DeviceDot.Fill = DotWaiting;
                WaitIcon.Text = "";
                WaitTitle.Text = "Unlock your phone";
                WaitText.Text = (isApple
                        ? "Unlock the iPhone and tap “Allow” (or “Trust”) when it asks whether this computer may access photos and videos."
                        : "Unlock the phone. If it asks what to use USB for, choose “File transfer” (sometimes “Transferring files / Android Auto”).")
                    + " This screen moves on by itself once the photos can be read.";
                break;
            case Stage.Connected:
                DeviceDot.Fill = DotOnline;
                WaitIcon.Text = "";
                WaitTitle.Text = "Phone connected";
                WaitText.Text = "Nothing is read from the phone until you ask. Scan it to copy its photos and videos to this PC"
                              + (CanSend ? ", or send files to it." : ".");
                ConnectedSendButton.Visibility = CanSend ? Visibility.Visible : Visibility.Collapsed;
                break;
            case Stage.Scanning:
                DeviceDot.Fill = DotOnline;
                WaitIcon.Text = "";
                WaitTitle.Text = "Reading your library";
                WaitText.Text = "Looking for photos and videos…";
                break;
            default:
                DeviceDot.Fill = DotOnline;
                // iPhones only expose their camera roll read-only over USB, so sending is Android-only.
                SendButton.Visibility = CanSend ? Visibility.Visible : Visibility.Collapsed;
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

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool FlashWindow(IntPtr hwnd, bool invert);
}
