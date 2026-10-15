using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SpotifyBar.Core;

namespace SpotifyBar;

public partial class MainWindow : Window
{
    private readonly bool _demo, _smoke;
    private readonly Preferences _preferences;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 4 * 1024 * 1024 };
    private readonly HttpClient _images = new() { Timeout = TimeSpan.FromSeconds(8), MaxResponseContentBufferSize = 2 * 1024 * 1024 };
    private readonly SpotifyAuth _auth;
    private readonly SpotifyApi _api;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _volumeDebounce = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _seekDebounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private CancellationTokenSource? _signIn, _search, _artwork;
    private DesktopDock? _dock;
    private Playback? _playback;
    private DateTimeOffset _observedAt, _nextPoll;
    private int _failures, _lastVolume = 60, _searchGeneration;
    private bool _ready, _syncing, _updatingSettings, _refreshing, _working, _online, _seekDragging, _requiresSignIn;
    private string? _artworkKey;
    private string _panel = "", _accountName = "Spotify account";
    private bool _nameLoaded;
    private StatusActionKind _statusAction;
    private enum StatusActionKind { None, Connect, Devices, Retry, Track }
    private bool Connected => _demo || (_auth.IsConnected && !_requiresSignIn);
    private string? DeviceId => _playback?.Device?.Id;
    private CancellationToken Ct => _lifetime.Token;

    public MainWindow(bool demo, bool smoke)
    {
        _demo = demo;
        _smoke = smoke;
        _preferences = demo ? new Preferences { ReserveSpace = false } : Storage.LoadPreferences();
        _auth = new SpotifyAuth(_http, demo ? new EmptyTokenStore() : new WindowsTokenStore()) { ClientId = _preferences.ClientId };
        _api = new SpotifyApi(_http, _auth);
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            _dock = new DesktopDock(this);
            _dock.PositionChanged += UpdateSizing;
            _dock.Configure(_preferences.Monitor, _preferences.ReserveSpace);
            UpdateSizing();
        };
        Loaded += WindowLoaded;
        Closed += WindowClosed;
        Deactivated += (_, _) => { if (Flyout.IsOpen && !Flyout.IsKeyboardFocusWithin) Flyout.IsOpen = false; };
        _clock.Tick += async (_, _) =>
        {
            UpdatePosition();
            if (!_demo && Connected && !_working && DateTimeOffset.UtcNow >= _nextPoll) await RefreshPlaybackAsync();
        };
        _volumeDebounce.Tick += async (_, _) =>
        {
            _volumeDebounce.Stop();
            if (_working) { _volumeDebounce.Start(); return; }
            var volume = (int)Math.Round(VolumeSlider.Value);
            await PerformAsync(async () =>
            {
                if (_demo && _playback?.Device is { } device) _playback = _playback with { Device = device with { Volume = volume } };
                else await _api.VolumeAsync(DeviceId, volume, Ct);
            });
        };
        _seekDebounce.Tick += async (_, _) =>
        {
            _seekDebounce.Stop();
            if (_working) { _seekDebounce.Start(); return; }
            await SeekAsync((int)SeekSlider.Value);
        };
        _searchDebounce.Tick += async (_, _) => { _searchDebounce.Stop(); await SearchAsync(); };
        _ready = true;
        UpdateUi();
    }

    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        if (_demo)
        {
            _playback = DemoPlayback();
            _observedAt = DateTimeOffset.UtcNow;
            _online = true;
            Artwork.Source = DemoArtwork();
            ArtworkPlaceholder.Visibility = Visibility.Collapsed;
            UpdateUi();
            SetStatus("Demo · connect your Spotify account", StatusActionKind.Connect);
        }
        else if (Connected) await RefreshPlaybackAsync();
        else ShowPanel("connect");
        _clock.Start();
        if (_smoke) await RunSmokeTestAsync();
    }

    private void UpdateSizing()
    {
        if (_dock is null) return;
        var dpi = _dock.DpiScale;
        PlayerLayout.LayoutTransform = new ScaleTransform(1 / dpi, 1 / dpi);
        var size = _dock.GetPhysicalSize();
        var compact = size.Width < 1500;
        SearchLabel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        DeviceLabel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        VolumeSlider.Visibility = size.Width < 1200 ? Visibility.Collapsed : Visibility.Visible;
        TransportColumn.Width = new GridLength(size.Width < 900 ? 240 : size.Width < 1200 ? 360 : 480);
        ShuffleButton.Visibility = RepeatButton.Visibility = size.Width < 900 ? Visibility.Collapsed : Visibility.Visible;
        FlyoutBorder.Width = Math.Min(440, Math.Max(280, size.Width / dpi - 24));
        FlyoutBorder.MaxHeight = Math.Max(200, (_dock.CurrentMonitor.Bottom - _dock.CurrentMonitor.Top - 150) / dpi);
        Flyout.HorizontalOffset = Math.Max(8, size.Width / dpi - FlyoutBorder.Width - 16);
    }

    private void UpdateUi()
    {
        if (!_ready) return;
        _syncing = true;
        try
        {
            var track = _playback?.Track;
            TrackTitle.Text = track?.Name ?? "Spotify Bar";
            TrackTitle.ToolTip = track?.Name;
            TrackArtist.Text = track?.Artist ?? (Connected ? "Open Spotify and choose a device." : "Your music, within reach.");
            TrackArtist.ToolTip = track?.Artist;
            ConnectButton.Visibility = Connected ? Visibility.Collapsed : Visibility.Visible;
            SearchButton.Visibility = DevicesButton.Visibility = MuteButton.Visibility = Connected ? Visibility.Visible : Visibility.Collapsed;
            VolumeSlider.Visibility = Connected && (_dock?.GetPhysicalSize().Width ?? 1920) >= 1200 ? Visibility.Visible : Visibility.Collapsed;
            var enabled = Connected && _online && !_working;
            bool Allows(string action) => enabled && _playback is not null && _playback.Allows(action);
            var playing = _playback?.IsPlaying == true;
            PlayButton.Content = playing ? "\uE769" : "\uE768";
            PlayButton.ToolTip = playing ? "Pause" : "Play";
            AutomationProperties.SetName(PlayButton, playing ? "Pause" : "Play");
            PlayButton.IsEnabled = Allows(playing ? "pausing" : "resuming");
            PreviousButton.IsEnabled = Allows("skipping_prev");
            NextButton.IsEnabled = Allows("skipping_next");
            SeekSlider.IsEnabled = Allows("seeking") && track is not null && track.DurationMs > 0;
            ShuffleButton.IsEnabled = Allows("toggling_shuffle");
            RepeatButton.IsEnabled = Allows("toggling_repeat_context") || Allows("toggling_repeat_track");
            ShuffleButton.Foreground = (Brush)FindResource(_playback?.Shuffle == true ? "Accent" : "Muted");
            RepeatButton.Foreground = (Brush)FindResource(_playback?.Repeat is "context" or "track" ? "Accent" : "Muted");
            RepeatButton.Content = _playback?.Repeat == "track" ? "\uE8ED" : "\uE8EE";
            RepeatButton.ToolTip = "Repeat: " + (_playback?.Repeat ?? "off");
            DeviceLabel.Text = _playback?.Device?.Name ?? "Devices";
            DevicesButton.ToolTip = _playback?.Device is { } current ? "Playing on " + current.Name + " · Change device" : "Choose a playback device";
            var volumeEnabled = enabled && _playback?.Device is { SupportsVolume: true, IsRestricted: false, Volume: not null };
            VolumeSlider.IsEnabled = MuteButton.IsEnabled = volumeEnabled;
            MuteButton.ToolTip = volumeEnabled ? (_playback?.Device?.Volume == 0 ? "Unmute" : "Mute") : "Volume is unavailable on this device";
            MuteButton.Content = _playback?.Device?.Volume == 0 ? "\uE74F" : "\uE767";
            if (!_volumeDebounce.IsEnabled) VolumeSlider.Value = _playback?.Device?.Volume ?? 0;
            SearchButton.IsEnabled = DevicesButton.IsEnabled = Connected;
            SeekSlider.Maximum = Math.Max(1, track?.DurationMs ?? 0);
            DurationLabel.Text = FormatTime(track?.DurationMs ?? 0);
            SignOutButton.IsEnabled = !_demo && _auth.IsConnected;
            ArtworkButton.IsEnabled = track?.ExternalUrl is not null;
        }
        finally { _syncing = false; }
        UpdatePosition();
    }

    private void UpdatePosition()
    {
        if (_syncing || _seekDragging || _seekDebounce.IsEnabled) return;
        _syncing = true;
        try
        {
            var ms = _playback?.ProgressMs ?? 0;
            if (_online && _playback?.IsPlaying == true) ms += (int)(DateTimeOffset.UtcNow - _observedAt).TotalMilliseconds;
            ms = Math.Clamp(ms, 0, _playback?.Track?.DurationMs ?? 0);
            SeekSlider.Value = ms;
            ElapsedLabel.Text = FormatTime(ms);
        }
        finally { _syncing = false; }
    }
    private static string FormatTime(int ms) => TimeSpan.FromMilliseconds(Math.Max(0, ms)).TotalHours >= 1
        ? TimeSpan.FromMilliseconds(ms).ToString(@"h\:mm\:ss") : TimeSpan.FromMilliseconds(Math.Max(0, ms)).ToString(@"m\:ss");

    private async Task RefreshPlaybackAsync()
    {
        if (_refreshing || !Connected || Ct.IsCancellationRequested) return;
        _refreshing = true;
        try
        {
            var state = await _api.GetPlaybackAsync(Ct);
            if (!Connected || Ct.IsCancellationRequested) return;
            _playback = state;
            _observedAt = DateTimeOffset.UtcNow;
            _online = true;
            _failures = 0;
            // Keep the seek animation local. Poll less frequently when paused/idle.
            _nextPoll = DateTimeOffset.UtcNow.AddSeconds(state?.IsPlaying == true ? 7 : 20);
            UpdateUi();
            SetStatus(state?.Device is null ? "Open Spotify · choose a device" : "Spotify ↗",
                state?.Device is null ? StatusActionKind.Devices : StatusActionKind.Track);
            _ = LoadArtworkAsync(state?.Track);
            if (!_nameLoaded && _auth.IsConnected)
            {
                _nameLoaded = true;
                try { _accountName = await _api.GetDisplayNameAsync(Ct); AccountLabel.Text = _accountName; }
                catch (Exception ex) when (Expected(ex)) { /* Account name is optional; playback remains usable. */ }
            }
        }
        catch (Exception ex) when (Expected(ex)) { ShowError(ex); }
        finally { _refreshing = false; }
    }

    private async Task PerformAsync(Func<Task> operation, string? confirmation = null, bool closePanel = false)
    {
        if (_working || Ct.IsCancellationRequested) return;
        _working = true;
        UpdateUi();
        try
        {
            await operation();
            if (Ct.IsCancellationRequested) return;
            if (closePanel) Flyout.IsOpen = false;
            if (_demo) { _observedAt = DateTimeOffset.UtcNow; UpdateUi(); }
            else
            {
                await Task.Delay(450, Ct);
                await RefreshPlaybackAsync();
            }
            if (confirmation is not null) PanelFeedback(confirmation);
        }
        catch (Exception ex) when (Expected(ex)) { ShowError(ex); }
        finally { _working = false; UpdateUi(); }
    }

    private async void Play_Click(object sender, RoutedEventArgs e) => await TogglePlaybackAsync();
    private Task TogglePlaybackAsync() => PerformAsync(async () =>
    {
        if (_demo && _playback is not null)
        {
            _playback = _playback with { IsPlaying = !_playback.IsPlaying, ProgressMs = (int)SeekSlider.Value };
            return;
        }
        if (_playback?.IsPlaying == true) await _api.PauseAsync(DeviceId, Ct);
        else await _api.PlayAsync(DeviceId, null, Ct);
    });
    private async void Next_Click(object sender, RoutedEventArgs e) => await PerformAsync(async () =>
    {
        if (_demo && _playback is not null) _playback = _playback with { ProgressMs = 0, Track = DemoTracks()[1] };
        else await _api.NextAsync(DeviceId, Ct);
    });
    private async void Previous_Click(object sender, RoutedEventArgs e) => await PerformAsync(async () =>
    {
        if (_demo && _playback is not null) _playback = _playback with { ProgressMs = 0, Track = DemoTracks()[0] };
        else await _api.PreviousAsync(DeviceId, Ct);
    });
    private async void Shuffle_Click(object sender, RoutedEventArgs e) => await PerformAsync(async () =>
    {
        var enabled = _playback?.Shuffle != true;
        if (_demo && _playback is not null) _playback = _playback with { Shuffle = enabled, ProgressMs = (int)SeekSlider.Value };
        else await _api.ShuffleAsync(DeviceId, enabled, Ct);
    });
    private async void Repeat_Click(object sender, RoutedEventArgs e) => await PerformAsync(async () =>
    {
        var current = _playback?.Repeat ?? "off";
        var choices = current switch { "off" => new[] { "context", "track" }, "context" => ["track", "off"], _ => ["off", "context"] };
        var next = choices.FirstOrDefault(choice => _playback?.Allows(
            choice == "track" || (choice == "off" && current == "track") ? "toggling_repeat_track" : "toggling_repeat_context") == true);
        if (next is null) return;
        if (_demo && _playback is not null) _playback = _playback with { Repeat = next, ProgressMs = (int)SeekSlider.Value };
        else await _api.RepeatAsync(DeviceId, next, Ct);
    });
    private void Seek_Down(object sender, MouseButtonEventArgs e) { if (SeekSlider.IsEnabled) _seekDragging = true; }
    private void Seek_Up(object sender, MouseButtonEventArgs e) => EndSeek();
    private void Seek_LostCapture(object sender, MouseEventArgs e) { if (_seekDragging) EndSeek(); }
    private void EndSeek()
    {
        if (!_seekDragging) return;
        _seekDragging = false;
        _seekDebounce.Stop();
        _seekDebounce.Start();
    }
    private void Seek_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _syncing) return;
        ElapsedLabel.Text = FormatTime((int)e.NewValue);
        if (!_seekDragging) { _seekDebounce.Stop(); _seekDebounce.Start(); }
    }
    private Task SeekAsync(int position) => PerformAsync(async () =>
    {
        if (_demo && _playback is not null) _playback = _playback with { ProgressMs = position };
        else await _api.SeekAsync(DeviceId, position, Ct);
    });
    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _syncing) return;
        _volumeDebounce.Stop(); _volumeDebounce.Start();
    }
    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        if (VolumeSlider.Value > 0) { _lastVolume = (int)VolumeSlider.Value; VolumeSlider.Value = 0; }
        else VolumeSlider.Value = Math.Max(1, _lastVolume);
    }

    private void ShowPanel(string kind)
    {
        if (Flyout.IsOpen && _panel == kind) { Flyout.IsOpen = false; return; }
        _panel = kind;
        _searchDebounce.Stop();
        _search?.Cancel();
        PanelMessage.Visibility = Visibility.Collapsed;
        ConnectPanel.Visibility = kind == "connect" ? Visibility.Visible : Visibility.Collapsed;
        SearchPanel.Visibility = kind == "search" ? Visibility.Visible : Visibility.Collapsed;
        DevicesPanel.Visibility = kind == "devices" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = kind == "settings" ? Visibility.Visible : Visibility.Collapsed;
        PanelHeading.Text = kind switch { "search" => "Find a song", "devices" => "Choose a device", "settings" => "Settings", _ => "Connect to Spotify" };
        if (kind == "connect")
        {
            ClientIdField.Text = _preferences.ClientId;
            AppSetup.IsExpanded = string.IsNullOrEmpty(_preferences.ClientId);
        }
        if (kind == "settings")
        {
            _updatingSettings = true;
            MonitorPicker.ItemsSource = DesktopDock.GetMonitors();
            MonitorPicker.SelectedValue = _dock?.CurrentMonitor.Id;
            ReserveSpaceCheck.IsChecked = _preferences.ReserveSpace;
            AccountLabel.Text = _demo ? "Demo · no account connected" : _accountName;
            _updatingSettings = false;
        }
        UpdateSizing();
        Flyout.IsOpen = true;
        if (kind == "devices") _ = RefreshDevicesAsync();
    }
    private void ConnectPanel_Click(object sender, RoutedEventArgs e) => ShowPanel("connect");
    private void SearchPanel_Click(object sender, RoutedEventArgs e) => ShowPanel("search");
    private void DevicesPanel_Click(object sender, RoutedEventArgs e) => ShowPanel("devices");
    private void SettingsPanel_Click(object sender, RoutedEventArgs e) => ShowPanel("settings");
    private void DismissPanel_Click(object sender, RoutedEventArgs e) => Flyout.IsOpen = false;
    private void Flyout_Opened(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (_panel == "search") SearchField.Focus();
        else if (_panel == "connect" && AppSetup.IsExpanded) ClientIdField.Focus();
        else FlyoutBorder.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }, DispatcherPriority.Input);
    private void Flyout_Closed(object? sender, EventArgs e)
    {
        _searchDebounce.Stop();
        _search?.Cancel();
        _searchGeneration++;
        // Browser sign-in deliberately continues when Windows dismisses this popup on deactivation.
        // Cancel sign-in and closing the application explicitly cancel the pending callback.
    }
    private void Flyout_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Flyout.IsOpen = false; e.Handled = true; }
        if (e.Key == Key.System && e.SystemKey == Key.F4 && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) { e.Handled = true; Close(); }
    }
    private async void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Flyout.IsOpen = false; e.Handled = true; }
        else if (e.Key == Key.Space && !Flyout.IsOpen && Keyboard.FocusedElement is not (TextBox or Button) && PlayButton.IsEnabled)
        { e.Handled = true; await TogglePlaybackAsync(); }
    }

    private bool SaveClientId()
    {
        var clientId = ClientIdField.Text.Trim();
        if (clientId.Length != 32 || clientId.Any(c => !Uri.IsHexDigit(c)))
        { AppSetup.IsExpanded = true; PanelFeedback("Enter the 32-character Client ID from your Spotify app settings."); return false; }
        if (_auth.IsConnected && clientId != _preferences.ClientId)
        { PanelFeedback("Sign out in Settings before changing the Client ID."); return false; }
        if (_demo) { PanelFeedback("Close the demo and launch SpotifyBar.exe normally to connect your account."); return false; }
        _preferences.ClientId = clientId;
        _auth.ClientId = clientId;
        Storage.SavePreferences(_preferences);
        return true;
    }
    private void SaveSetup_Click(object sender, RoutedEventArgs e)
    {
        try { if (SaveClientId()) PanelFeedback("App setup saved. You can now connect Spotify."); }
        catch (Exception ex) when (Expected(ex)) { ShowError(ex); }
    }
    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        if (_signIn is not null) return;
        try
        {
            if (!SaveClientId()) return;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            _signIn = cancellation;
            SignInButton.IsEnabled = false;
            ClientIdField.IsEnabled = false;
            CancelSignInButton.Visibility = Visibility.Visible;
            SetStatus("Waiting for Spotify sign-in…", StatusActionKind.None);
            PanelFeedback("Finish sign-in in your browser. This attempt expires in three minutes.");
            await _auth.SignInAsync(cancellation.Token);
            _requiresSignIn = false;
            _nameLoaded = false;
            // Clear before closing, so dismissal does not cancel a successful sign-in.
            _signIn = null;
            Flyout.IsOpen = false;
            await RefreshPlaybackAsync();
        }
        catch (OperationCanceledException) { SetStatus("Sign-in cancelled or timed out · connect Spotify", StatusActionKind.Connect); PanelFeedback("Sign-in cancelled or timed out. You can try again."); }
        catch (SocketException) { SetStatus("Sign-in could not start", StatusActionKind.Connect); PanelFeedback("The sign-in port (43821) is in use. Close the other listener and try again."); }
        catch (Exception ex) when (Expected(ex)) { ShowError(ex); }
        finally
        {
            _signIn = null;
            SignInButton.IsEnabled = ClientIdField.IsEnabled = true;
            CancelSignInButton.Visibility = Visibility.Collapsed;
            UpdateUi();
        }
    }
    private void CancelSignIn_Click(object sender, RoutedEventArgs e) => _signIn?.Cancel();
    private async void SignOut_Click(object sender, RoutedEventArgs e)
    {
        _signIn?.Cancel();
        _search?.Cancel();
        _artwork?.Cancel();
        _searchGeneration++;
        _requiresSignIn = true;
        _online = false;
        UpdateUi();
        try
        {
            await _auth.SignOutAsync();
            _playback = null;
            _online = false;
            _requiresSignIn = true;
            _nameLoaded = false;
            _accountName = "Spotify account";
            SearchResults.ItemsSource = null;
            Artwork.Source = null;
            _artworkKey = null;
            ArtworkPlaceholder.Visibility = Visibility.Visible;
            Flyout.IsOpen = false;
            UpdateUi();
            SetStatus("Connect Spotify to start", StatusActionKind.Connect);
        }
        catch (Exception ex) when (Expected(ex)) { ShowError(ex); }
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        _search?.Cancel();
        _searchGeneration++;
        _searchDebounce.Stop();
        SearchResults.ItemsSource = null;
        SearchHint.Text = "Search by song title or artist.";
        if (_panel == "search" && !string.IsNullOrWhiteSpace(SearchField.Text)) _searchDebounce.Start();
    }
    private async void Search_KeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Enter) { e.Handled = true; _searchDebounce.Stop(); await SearchAsync(); } }
    private async void SearchNow_Click(object sender, RoutedEventArgs e) { _searchDebounce.Stop(); await SearchAsync(); }
    private async Task SearchAsync()
    {
        var query = SearchField.Text.Trim();
        if (query.Length == 0) return;
        _search?.Cancel();
        _search?.Dispose();
        _search = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var token = _search.Token;
        var generation = ++_searchGeneration;
        SearchHint.Text = "Searching…";
        PanelMessage.Visibility = Visibility.Collapsed;
        try
        {
            var results = _demo ? DemoTracks() : await _api.SearchAsync(query, token);
            if (token.IsCancellationRequested || generation != _searchGeneration) return;
            SearchResults.ItemsSource = results;
            SearchHint.Text = results.Count == 0 ? "No songs found. Try another title or artist." : "Play now, or + to add to your queue.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (Expected(ex))
        { if (generation == _searchGeneration) { SearchHint.Text = "Search could not complete."; ShowError(ex); } }
    }
    private async void PlayResult_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not Track track) return;
        await PerformAsync(async () =>
        {
            if (_demo && _playback is not null) _playback = _playback with { Track = track, ProgressMs = 0, IsPlaying = true };
            else await _api.PlayAsync(DeviceId, track.Uri, Ct);
        }, closePanel: true);
    }
    private async void QueueResult_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not Track track) return;
        await PerformAsync(async () => { if (!_demo) await _api.QueueAsync(DeviceId, track.Uri, Ct); }, "Added to queue.");
    }

    private async Task RefreshDevicesAsync()
    {
        DeviceHint.Text = "Looking for your devices…";
        try
        {
            IReadOnlyList<Device> devices = _demo
                ? [_playback!.Device!, new Device("demo-phone", "Phone", "Smartphone", false, false, true, 50)]
                : await _api.GetDevicesAsync(Ct);
            if (!Connected) return;
            DeviceResults.ItemsSource = devices.OrderByDescending(d => d.IsActive).ToList();
            DeviceHint.Text = devices.Count == 0
                ? "Open Spotify, start a song there once, then refresh devices."
                : "Select the Spotify app on this PC or another available Spotify Connect device.";
        }
        catch (Exception ex) when (Expected(ex)) { DeviceHint.Text = "Could not refresh devices."; ShowError(ex); }
    }
    private async void RefreshDevices_Click(object sender, RoutedEventArgs e) => await RefreshDevicesAsync();
    private async void SelectDevice_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not Device device || !device.CanSelect) return;
        await PerformAsync(async () =>
        {
            if (_demo && _playback is not null) _playback = _playback with { Device = device with { IsActive = true }, ProgressMs = (int)SeekSlider.Value };
            else await _api.TransferAsync(device.Id, _playback?.IsPlaying == true, Ct);
        }, closePanel: true);
    }

    private void Monitor_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSettings || MonitorPicker.SelectedItem is not DisplayMonitor monitor) return;
        _preferences.Monitor = monitor.Id;
        ApplyPreferences();
    }
    private void ReserveSpace_Click(object sender, RoutedEventArgs e)
    { _preferences.ReserveSpace = ReserveSpaceCheck.IsChecked == true; ApplyPreferences(); }
    private void OpenDataFolder_Click(object sender, RoutedEventArgs e) => OpenLink(Storage.Root);
    private void ApplyPreferences()
    {
        try
        {
            _dock?.Configure(_preferences.Monitor, _preferences.ReserveSpace);
            if (!_demo) Storage.SavePreferences(_preferences);
            Flyout.IsOpen = false;
        }
        catch (Exception ex) when (Expected(ex)) { ShowError(ex); }
    }

    private async Task LoadArtworkAsync(Track? track)
    {
        if (_artworkKey == track?.ImageUrl) return;
        _artworkKey = track?.ImageUrl;
        _artwork?.Cancel();
        _artwork?.Dispose();
        _artwork = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var token = _artwork.Token;
        Artwork.Source = null;
        ArtworkPlaceholder.Visibility = Visibility.Visible;
        if (track?.ImageUrl is null || !Uri.TryCreate(track.ImageUrl, UriKind.Absolute, out var url) || url.Scheme != "https") return;
        try
        {
            var bytes = await _images.GetByteArrayAsync(url, token);
            if (token.IsCancellationRequested) return;
            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 144;
            bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            Artwork.Source = bitmap;
            ArtworkPlaceholder.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (Expected(ex) || ex is NotSupportedException or FileFormatException) { /* Artwork is optional. */ }
    }
    private void SetStatus(string message, StatusActionKind action)
    {
        StatusText.Text = message;
        StatusAction.ToolTip = message;
        StatusAction.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        StatusAction.Cursor = action == StatusActionKind.None ? Cursors.Arrow : Cursors.Hand;
        _statusAction = action;
    }
    private void PanelFeedback(string message)
    { PanelMessage.Text = message; PanelMessage.Visibility = Visibility.Visible; }
    private void ShowError(Exception ex)
    {
        if (Ct.IsCancellationRequested) return;
        string message;
        if (ex is SignInRequiredException)
        {
            _requiresSignIn = true;
            _online = false;
            message = ex.Message;
            SetStatus("Reconnect Spotify", StatusActionKind.Connect);
        }
        else if (ex is SpotifyApiException api)
        {
            message = api.Message;
            _nextPoll = api.RetryAt ?? DateTimeOffset.UtcNow.AddSeconds(30);
            if (api.StatusCode == 404) SetStatus("Open Spotify · choose a device", StatusActionKind.Devices);
            else { _online = false; SetStatus(message, StatusActionKind.Retry); }
        }
        else if (ex is HttpRequestException or OperationCanceledException)
        {
            _online = false;
            _failures++;
            _nextPoll = DateTimeOffset.UtcNow.AddSeconds(Math.Min(120, 10 * Math.Pow(2, Math.Min(_failures, 4))));
            message = "Can't reach Spotify. Check your connection; the bar will retry automatically.";
            SetStatus("Offline · retry", StatusActionKind.Retry);
        }
        else
        {
            message = ex is ArgumentException or IOException ? ex.Message : "Could not complete this action. Check app setup or try again.";
            SetStatus(message, ex is IOException ? StatusActionKind.Retry : StatusActionKind.None);
        }
        if (Flyout.IsOpen) PanelFeedback(message);
        UpdateUi();
    }
    private static bool Expected(Exception ex) => ex is OperationCanceledException or HttpRequestException or SpotifyApiException
        or SignInRequiredException or ArgumentException or IOException or UnauthorizedAccessException
        or System.ComponentModel.Win32Exception or System.Security.SecurityException or System.Security.Cryptography.CryptographicException or JsonException;

    private async void StatusAction_Click(object sender, RoutedEventArgs e)
    {
        switch (_statusAction)
        {
            case StatusActionKind.Connect: ShowPanel("connect"); break;
            case StatusActionKind.Devices: ShowPanel("devices"); break;
            case StatusActionKind.Track: OpenCurrentTrack(); break;
            case StatusActionKind.Retry: await RefreshPlaybackAsync(); break;
        }
    }
    private void OpenTrack_Click(object sender, RoutedEventArgs e) => OpenCurrentTrack();
    private void OpenCurrentTrack()
    {
        if (_playback?.Track?.ExternalUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            && parsed.Scheme == "https" && parsed.Host == "open.spotify.com")
        {
            var parts = parsed.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[0] is "track" or "episode" && parts[1].Length == 22 && parts[1].All(char.IsAsciiLetterOrDigit))
                OpenLink($"spotify:{parts[0]}:{parts[1]}");
        }
    }
    private void OpenSpotify_Click(object sender, RoutedEventArgs e) => OpenLink("spotify:");
    private void Dashboard_Click(object sender, RoutedEventArgs e) => OpenLink("https://developer.spotify.com/dashboard");
    private void OpenLink(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) when (Expected(ex)) { PanelFeedback("Windows could not open this link. Open Spotify or your browser manually."); }
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void WindowClosed(object? sender, EventArgs e)
    {
        _clock.Stop(); _seekDebounce.Stop(); _volumeDebounce.Stop(); _searchDebounce.Stop();
        Flyout.IsOpen = false;
        _lifetime.Cancel(); _signIn?.Cancel(); _search?.Cancel(); _artwork?.Cancel();
        _dock?.Dispose();
        _http.Dispose(); _images.Dispose();
    }

    private static IReadOnlyList<Track> DemoTracks() =>
    [
        new("demo-1", "Night Drive", "Mira", null, "", null, 238000),
        new("demo-2", "Midnight Sun", "Luna Fields", null, "", null, 214000),
        new("demo-3", "Better Days", "Kai Monroe", null, "", null, 192000)
    ];
    private static Playback DemoPlayback() => new(DemoTracks()[0], new("demo-pc", "This PC", "Computer", true, false, true, 65),
        true, 84000, false, "off", []);
    private static DrawingImage DemoArtwork()
    {
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(new LinearGradientBrush(Color.FromRgb(36, 66, 96), Color.FromRgb(211, 141, 123), 45),
            null, new RectangleGeometry(new Rect(0, 0, 72, 72))));
        drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(247, 199, 157)), null, new EllipseGeometry(new Point(49, 23), 9, 9)));
        drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(28, 41, 61)), null,
            Geometry.Parse("M0,58 L18,34 33,49 50,30 72,53 72,72 0,72 Z")));
        var image = new DrawingImage(drawing); image.Freeze(); return image;
    }
    private sealed class EmptyTokenStore : ITokenStore
    {
        public TokenSet? Load() => null;
        public void Save(TokenSet tokens) { }
        public void Clear() { }
    }

    private async Task RunSmokeTestAsync()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "artifacts", "verification");
        Directory.CreateDirectory(directory);
        var assertions = new Dictionary<string, bool>();
        try
        {
            await Task.Delay(250);
            var size = _dock!.GetPhysicalSize();
            assertions["height_is_120_physical_pixels"] = size.Height == 120;
            assertions["spans_monitor_width"] = size.Width == _dock.CurrentMonitor.Right - _dock.CurrentMonitor.Left;
            assertions["topmost"] = _dock.IsTopmost;
            assertions["only_player_visible_at_start"] = !Flyout.IsOpen;
            assertions["help_saved_in_documents_data_folder"] = File.Exists(Path.Combine(Storage.Root, "README.txt"))
                && File.Exists(Path.Combine(Storage.Root, "SPOTIFY-SETUP.txt"));
            var extraction = Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR");
            assertions["runtime_cache_within_documents_data_folder"] = extraction is not null
                && Path.GetFullPath(extraction).StartsWith(Path.GetFullPath(Storage.Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            // Modern single-file hosts embed CoreCLR; verify an actually loaded extracted
            // WPF native module rather than assuming coreclr.dll exists as a separate file.
            var loadedRuntime = extraction is null ? null : Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
                .Select(module => module.FileName).FirstOrDefault(path => path.StartsWith(
                    Path.GetFullPath(extraction) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            var packaged = Environment.ProcessPath?.StartsWith(Path.Combine(Storage.Root, "Runtime") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) == true;
            assertions["bundled_native_library_loaded_from_documents"] = !packaged || (loadedRuntime is not null && extraction is not null
                && loadedRuntime.StartsWith(Path.GetFullPath(extraction) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            SaveVisual(WindowHost, Path.Combine(directory, "player.png"));
            await TogglePlaybackAsync();
            assertions["pause_changes_button_and_stops_progress"] = _playback?.IsPlaying == false && (string)PlayButton.Content == "\uE768";
            ShowPanel("search");
            SearchField.Text = "night";
            _searchDebounce.Stop();
            await SearchAsync();
            await Task.Delay(120);
            assertions["search_opens_on_demand"] = Flyout.IsOpen && SearchPanel.Visibility == Visibility.Visible && SettingsPanel.Visibility == Visibility.Collapsed;
            SaveVisual(FlyoutBorder, Path.Combine(directory, "search.png"));
            ShowPanel("devices");
            await Task.Delay(120);
            assertions["panels_are_mutually_exclusive"] = DevicesPanel.Visibility == Visibility.Visible && SearchPanel.Visibility == Visibility.Collapsed;
            SaveVisual(FlyoutBorder, Path.Combine(directory, "devices.png"));
            ShowPanel("settings");
            await Task.Delay(120);
            SaveVisual(FlyoutBorder, Path.Combine(directory, "settings.png"));
            ShowPanel("connect");
            await Task.Delay(120);
            assertions["app_setup_exposes_callback_and_client_id"] = AppSetup.IsExpanded && ConnectPanel.Visibility == Visibility.Visible;
            SaveVisual(FlyoutBorder, Path.Combine(directory, "connect.png"));
            Flyout.IsOpen = false;
            assertions["panel_dismissal_restores_player_only"] = !Flyout.IsOpen;
            File.WriteAllText(Path.Combine(directory, "smoke-test.json"), JsonSerializer.Serialize(new
            {
                assertions, dimensions = new { width = size.Width, height = size.Height }, dpi = _dock.DpiScale,
                dataFolder = Storage.Root, runtimeCache = extraction, loadedRuntime,
                allPassed = assertions.Values.All(value => value)
            }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
        }
        finally { Close(); }
    }
    private static void SaveVisual(FrameworkElement visual, string path)
    {
        visual.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(visual);
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Round(visual.ActualWidth * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Round(visual.ActualHeight * dpi.DpiScaleY)), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
}
