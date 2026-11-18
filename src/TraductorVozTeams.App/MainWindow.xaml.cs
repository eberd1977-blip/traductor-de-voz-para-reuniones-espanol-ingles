using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using TraductorVozTeams.App.Audio;
using TraductorVozTeams.App.Services;
using TraductorVozTeams.Core.Audio;
using TraductorVozTeams.Core.Devices;
using TraductorVozTeams.Core.Metrics;
using TraductorVozTeams.Core.Providers;
using TraductorVozTeams.Core.Settings;

namespace TraductorVozTeams.App;

public partial class MainWindow : Window
{
    private static readonly string[] SourceLanguages = ["es-AR", "es-ES", "es-MX", "es-US", "es-CO", "es-CL", "es-PE", "es-UY"];

    private readonly AppServices _services;
    private readonly Dictionary<string, IReadOnlyList<TranslationVoice>> _voiceCache = [];
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private TranslationSession? _session;
    private string? _metricsFile;
    private bool _loading;
    private int _voiceRequest;
    private bool _closing;
    private bool _allowClose;

    public MainWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();
        SourceLanguageCombo.ItemsSource = SourceLanguages;
        DataPathText.Text = $"Ejecutable: {Environment.ProcessPath}   ·   Configuración, credenciales cifradas, registros y métricas: {services.Paths.Root}";
        LoadSettingsIntoUi();
        InitializeIncoming();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void FlushRealtimeTranscript(bool incoming)
    {
        var index = incoming ? 1 : 0;
        if (string.IsNullOrWhiteSpace(_realtimePartial[index]))
            return;
        TranscriptBox.AppendText($"{(incoming ? "[REUNIÓN] ES" : "[YO] EN")}: {_realtimePartial[index].Trim()}{Environment.NewLine}");
        TranscriptBox.ScrollToEnd();
        _realtimePartial[index] = "";
        PartialText.Text = _realtimePartial[1 - index];
    }

    private AppSettings Settings => _services.Settings;

    private void OnLayoutSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Reserve room for two rows of actions, the partial utterance and the transcript itself.
        ConfigurationScroll.MaxHeight = Math.Min(e.NewSize.Height * 0.63, Math.Max(0, e.NewSize.Height - 230));
        // The extra width at the breakpoint leaves room for the vertical scrollbar.
        var sideBySide = e.NewSize.Width >= 940;
        DirectionGap.Width = new GridLength(sideBySide ? 8 : 0);
        DirectionSecondColumn.Width = sideBySide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetRow(IncomingPanel, sideBySide ? 0 : 1);
        Grid.SetColumn(IncomingPanel, sideBySide ? 2 : 0);
        IncomingPanel.Margin = sideBySide ? new Thickness(0) : new Thickness(0, 6, 0, 0);
    }

    private bool NeedsProviderSetup(ProviderProfile? profile) =>
        profile is null || (_services.Registry.Find(profile.ProviderType) is { IsAvailable: true } descriptor
                            && descriptor.Fields.Any(f => f.IsSecret && !_services.Profiles.HasSecret(profile, f.Key)));

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (NeedsProviderSetup(Settings.ActiveProfile) || NeedsProviderSetup(Settings.IncomingProfile))
        {
            MessageBox.Show(this,
                "Primero configura un perfil de proveedor con los datos del recurso y su clave. " +
                "La clave se guarda cifrada para tu usuario de Windows, fuera del ejecutable.",
                "Configuración inicial", MessageBoxButton.OK, MessageBoxImage.Information);
            OpenProvidersWindow();
        }
        else
        {
            _ = LoadVoicesAsync(forceRefresh: false);
            _ = LoadIncomingVoicesAsync(forceRefresh: false);
        }
    }

    private IVoiceCatalog? VoiceCatalogFor(ProviderProfile? profile) =>
        profile is not null && _services.Factories.TryGetValue(profile.ProviderType, out var factory) ? factory as IVoiceCatalog : null;

    private static string CurrentVoice(ProviderProfile profile) => ProviderValues.Get(profile.Values, AzureSpeechDescriptor.VoiceNameKey);

    private async Task LoadVoicesAsync(bool forceRefresh)
    {
        var request = ++_voiceRequest;
        var profile = Settings.ActiveProfile;
        var catalog = VoiceCatalogFor(profile);
        if (profile is null || catalog is null)
        {
            ShowVoices([], null, "Este proveedor no ofrece selección de voz.");
            UpdateCombinedControls();
            return;
        }

        var current = CurrentVoice(profile);
        if (!forceRefresh && _voiceCache.TryGetValue(profile.Id, out var cached))
        {
            ShowVoices(cached, current, $"{cached.Count} voces disponibles.");
            return;
        }

        ShowVoices([], current, "Consultando voces disponibles en Azure…");
        RefreshVoicesButton.IsEnabled = false;
        try
        {
            var context = _services.CreateContext(profile);
            var voices = await Task.Run(() => catalog.ListVoicesAsync(context, CancellationToken.None));
            _voiceCache[profile.Id] = voices;
            if (request == _voiceRequest)
            {
                ShowVoices(voices, CurrentVoice(profile), $"{voices.Count} voces disponibles. Las multilingües y las en-US suelen sonar más naturales.");
            }
        }
        catch (Exception ex)
        {
            _services.Logger.Error("No se pudo obtener la lista de voces", ex);
            if (request == _voiceRequest)
            {
                ShowVoices([], current, "No se pudo obtener la lista de voces: " + ex.Message);
            }
        }
        finally
        {
            if (request == _voiceRequest)
                UpdateCombinedControls();
        }
    }

    private void ShowVoices(IReadOnlyList<TranslationVoice> voices, string? current, string status)
    {
        var items = voices.ToList();
        if (!string.IsNullOrEmpty(current) && !items.Any(v => v.Name.Equals(current, StringComparison.OrdinalIgnoreCase)))
        {
            items.Insert(0, new TranslationVoice(current, current, "configurada", "—", false));
        }

        var wasLoading = _loading;
        _loading = true;
        VoiceCombo.ItemsSource = items;
        VoiceCombo.SelectedItem = items.FirstOrDefault(v => v.Name.Equals(current, StringComparison.OrdinalIgnoreCase));
        _loading = wasLoading;
        VoiceStatusText.Text = status;
    }

    private void OnVoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || VoiceCombo.SelectedItem is not TranslationVoice voice || Settings.ActiveProfile is not { } profile)
        {
            return;
        }

        profile.Values[AzureSpeechDescriptor.VoiceNameKey] = voice.Name;
        SaveSettings();
        _services.Logger.Info($"Voz seleccionada: {voice.Name}");
    }

    private async void OnRefreshVoices(object sender, RoutedEventArgs e) => await LoadVoicesAsync(forceRefresh: true);

    private async void OnPreviewVoice(object sender, RoutedEventArgs e)
    {
        var profile = Settings.ActiveProfile;
        var catalog = VoiceCatalogFor(profile);
        if (profile is null || catalog is null || VoiceCombo.SelectedItem is not TranslationVoice voice)
        {
            MessageBox.Show(this, "Selecciona una voz.", "Probar voz", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var device = MonitorCombo.SelectedItem as AudioDeviceInfo;
        var teams = TeamsOutputCombo.SelectedItem as AudioDeviceInfo;
        if (device is null || device.Id == teams?.Id || DeviceRoutingValidator.IsVirtualCable(device, Settings.VirtualCablePatterns))
        {
            MessageBox.Show(this,
                "Para escuchar la muestra elige tus auriculares o altavoces en \"Escuchar traducción en\". " +
                "La muestra nunca se envía a la salida hacia Teams ni a un cable virtual.",
                "Probar voz", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        PreviewVoiceButton.IsEnabled = false;
        VoiceStatusText.Text = $"Reproduciendo muestra de {voice.DisplayName} en {device.Name}…";
        try
        {
            var context = _services.CreateContext(profile);
            var result = await Task.Run(() => catalog.PreviewVoiceAsync(context, voice.Name, device.Id, CancellationToken.None));
            VoiceStatusText.Text = result.Message;
            if (!result.Success)
            {
                _services.Logger.Error("Muestra de voz: " + result.Message);
            }
        }
        finally
        {
            UpdateCombinedControls();
        }
    }

    private void LoadSettingsIntoUi()
    {
        _loading = true;
        try
        {
            RefreshProfiles();
            RefreshDevices();
            SourceLanguageCombo.Text = Settings.SourceLanguage;
            MonitorCheck.IsChecked = Settings.MonitorEnabled;
            TeamsVolumeSlider.Value = Settings.TeamsOutputVolume;
            MonitorVolumeSlider.Value = Settings.MonitorVolume;
        }
        finally
        {
            _loading = false;
        }
    }

    private void RefreshProfiles()
    {
        var wasLoading = _loading;
        _loading = true;
        OutgoingProfileCombo.ItemsSource = null;
        OutgoingProfileCombo.ItemsSource = Settings.Profiles.ToList();
        OutgoingProfileCombo.SelectedItem = Settings.ActiveProfile;
        IncomingProfileCombo.ItemsSource = null;
        IncomingProfileCombo.ItemsSource = Settings.Profiles.ToList();
        IncomingProfileCombo.SelectedItem = Settings.IncomingProfile;
        _loading = wasLoading;
    }

    private void RefreshDevices()
    {
        try
        {
            var inputs = AudioDeviceService.List(AudioDeviceFlow.Capture);
            var outputs = AudioDeviceService.List(AudioDeviceFlow.Render);
            Select(InputCombo, inputs, InputCombo.SelectedItem is AudioDeviceInfo i ? i.Id : Settings.InputDeviceId);
            Select(TeamsOutputCombo, outputs, TeamsOutputCombo.SelectedItem is AudioDeviceInfo t ? t.Id : Settings.TeamsOutputDeviceId);
            Select(MonitorCombo, outputs, MonitorCombo.SelectedItem is AudioDeviceInfo m ? m.Id : Settings.MonitorDeviceId);
            RefreshIncomingDevices(inputs, outputs);
            RoutingText.Text = string.Empty;
        }
        catch (Exception ex)
        {
            RoutingText.Text = "No se pudieron listar los dispositivos de audio: " + ex.Message;
        }
    }

    private static void Select(ComboBox combo, IReadOnlyList<AudioDeviceInfo> devices, string? id)
    {
        combo.ItemsSource = devices;
        combo.SelectedItem = devices.FirstOrDefault(d => d.Id == id);
    }

    private void CaptureUiIntoSettings()
    {
        Settings.InputDeviceId = (InputCombo.SelectedItem as AudioDeviceInfo)?.Id;
        Settings.TeamsOutputDeviceId = (TeamsOutputCombo.SelectedItem as AudioDeviceInfo)?.Id;
        Settings.MonitorDeviceId = (MonitorCombo.SelectedItem as AudioDeviceInfo)?.Id;
        Settings.MonitorEnabled = MonitorCheck.IsChecked == true;
        Settings.TeamsOutputVolume = (float)TeamsVolumeSlider.Value;
        Settings.MonitorVolume = (float)MonitorVolumeSlider.Value;
        var language = SourceLanguageCombo.Text.Trim();
        Settings.SourceLanguage = language.Length == 0 ? "es-AR" : language;
        CaptureIncomingUiIntoSettings();
    }

    private void SaveSettings()
    {
        CaptureUiIntoSettings();
        try
        {
            _services.Save();
        }
        catch (Exception ex)
        {
            _services.Logger.Error("No se pudo guardar la configuración", ex);
        }
    }

    private bool OutgoingRunning => _closing || _session is not null || !StartButton.IsEnabled;

    private bool IncomingRunning => _closing || _incoming is not null || !IncomingStartButton.IsEnabled;

    private void OnOutgoingProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
            return;
        if (OutgoingRunning)
        {
            RefreshProfiles();
            return;
        }
        if (OutgoingProfileCombo.SelectedItem is ProviderProfile profile)
        {
            Settings.ActiveProfileId = profile.Id;
            SaveSettings();
            _ = LoadVoicesAsync(forceRefresh: false);
        }
    }

    private void OnIncomingProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
            return;
        if (IncomingRunning)
        {
            RefreshProfiles();
            return;
        }
        if (IncomingProfileCombo.SelectedItem is ProviderProfile profile)
        {
            Settings.Incoming.ProfileId = profile.Id;
            SaveSettings();
            _ = LoadIncomingVoicesAsync(forceRefresh: false);
        }
    }

    private void OnConfigureProviders(object sender, RoutedEventArgs e) => OpenProvidersWindow();

    private void OpenProvidersWindow()
    {
        if (ProviderChangeLocked)
            return;
        CaptureUiIntoSettings();
        var window = new ProvidersWindow(_services) { Owner = this };
        window.ShowDialog();
        RefreshProfiles();
        SaveSettings();
        _voiceCache.Clear();
        _ = LoadVoicesAsync(forceRefresh: false);
        _ = LoadIncomingVoicesAsync(forceRefresh: false);
    }

    private void OnRefreshDevices(object sender, RoutedEventArgs e) => RefreshDevices();

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        _session?.SetVolumes((float)TeamsVolumeSlider.Value, (float)MonitorVolumeSlider.Value);

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (_session is not null || _closing || !_sessionGate.Wait(0))
        {
            return;
        }

        try
        {
            await StartSessionAsync();
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private async Task StartSessionAsync()
    {
        CaptureUiIntoSettings();
        var profile = Settings.ActiveProfile;
        if (profile is null)
        {
            MessageBox.Show(this, "Crea y selecciona un perfil de proveedor.", "Falta proveedor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var descriptor = _services.Registry.Find(profile.ProviderType);
        if (descriptor is null || !descriptor.IsAvailable || !_services.Factories.TryGetValue(profile.ProviderType, out var factory))
        {
            MessageBox.Show(this, $"El proveedor \"{descriptor?.DisplayName ?? profile.ProviderType}\" no está disponible en esta versión.",
                "Proveedor no disponible", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var profileErrors = _services.Profiles.Validate(profile, profile.Values);
        if (profileErrors.Count > 0)
        {
            MessageBox.Show(this, string.Join(Environment.NewLine, profileErrors), "Revisa el perfil", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!CheckMeetingProvider(profile))
            return;

        var routing = new DeviceRouting(
            InputCombo.SelectedItem as AudioDeviceInfo,
            TeamsOutputCombo.SelectedItem as AudioDeviceInfo,
            Settings.MonitorEnabled,
            MonitorCombo.SelectedItem as AudioDeviceInfo);
        var validation = ValidateOutgoingRouting(routing);
        RoutingText.Text = string.Join(Environment.NewLine, validation.Errors.Select(x => "✖ " + x).Concat(validation.Warnings.Select(x => "⚠ " + x)));
        RoutingText.Foreground = validation.IsValid ? System.Windows.Media.Brushes.DarkOrange : System.Windows.Media.Brushes.Firebrick;
        if (!validation.IsValid)
        {
            return;
        }

        SaveSettings();
        SetRunning(true, "Conectando…");
        if (_incoming is null)
        {
            TranscriptBox.Clear();
        }

        PartialText.Text = string.Empty;
        LatencyText.Text = profile.ProviderType == RealtimeTranslateDescriptor.Type
            ? "Traducción continua: la métrica por frase no se aplica."
            : "Sin mediciones todavía.";
        OutgoingUsageText.Text = string.Empty;

        var devices = new SessionDevices(
            routing.Input!.Id,
            routing.TeamsOutput!.Id,
            Settings.MonitorEnabled ? routing.Monitor?.Id : null,
            Settings.TeamsOutputVolume,
            Settings.MonitorVolume);

        try
        {
            var provider = factory.Create(_services.CreateContext(profile));
            var session = new TranslationSession(provider, devices, _services.Logger);
            _session = session;
            void Dispatch(Action action) => Dispatcher.BeginInvoke(() =>
            {
                if (ReferenceEquals(_session, session) && !_closing)
                {
                    action();
                }
            });
            session.Recognizing += (_, args) => Dispatch(() => PartialText.Text = $"[YO] {args.SourceText}  →  {args.TranslatedText}");
            session.Recognized += (_, args) =>
            {
                if (!args.IsContinuousDelta)
                    _outgoingUsage.AddUtterance(args.SourceText, args.TranslatedText);
                Dispatch(() => AppendTranscript(args));
            };
            session.UtteranceCompleted += (_, u) => Dispatch(() => OnUtteranceCompleted(u));
            session.LevelChanged += (_, level) => Dispatch(() => LevelBar.Value = level);
            session.Failed += (_, message) => Dispatch(() => OnSessionFailed(session, message));

            Directory.CreateDirectory(_services.Paths.MetricsDirectory);
            _metricsFile = Path.Combine(_services.Paths.MetricsDirectory, $"latencia-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
            File.WriteAllText(_metricsFile, LatencyTracker.CsvHeader + Environment.NewLine, Encoding.UTF8);

            await session.StartAsync(CancellationToken.None);
            _outgoingUsage.Start(provider.InputFormat.AverageBytesPerSecond);
            _services.Logger.Info($"Sesión iniciada. Perfil: {profile.Name}; idioma: {Settings.SourceLanguage}");
            StatusText.Text = "● Traduciendo";
            StatusText.Foreground = System.Windows.Media.Brushes.ForestGreen;
        }
        catch (Exception ex)
        {
            _services.Logger.Error("No se pudo iniciar la sesión", ex);
            await StopSessionCoreAsync();
            MessageBox.Show(this, "No se pudo iniciar: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private RoutingValidationResult ValidateOutgoingRouting(DeviceRouting routing)
    {
        var outgoing = DeviceRoutingValidator.Validate(routing, Settings.VirtualCablePatterns);
        if (!outgoing.IsValid || _activeIncomingRouting is not { } incoming)
        {
            return outgoing;
        }

        var crossCheck = IncomingRoutingValidator.Validate(incoming, routing, Settings.VirtualCablePatterns);
        return new RoutingValidationResult(
            crossCheck.IsValid ? [] :
                ["Detén la dirección entrante y comprueba los cables antes de cambiar esta ruta.", .. crossCheck.Errors],
            outgoing.Warnings.Concat(crossCheck.Warnings).Distinct().ToArray());
    }

    private async void OnStop(object sender, RoutedEventArgs e) => await StopSessionAsync();

    private async Task StopSessionAsync()
    {
        await _sessionGate.WaitAsync();
        try
        {
            await StopSessionCoreAsync();
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private async Task StopSessionCoreAsync()
    {
        var session = _session;
        if (session is not null)
        {
            SetRunning(true, "Deteniendo…");
            StopButton.IsEnabled = false;
            try
            {
                await session.StopAsync();
                RecordOutgoingUsage(session);
            }
            finally
            {
                await session.DisposeAsync();
                _session = null;
            }

            _services.Logger.Info("Sesión detenida.");
        }

        LevelBar.Value = 0;
        FlushRealtimeTranscript(incoming: false);
        SetRunning(false, "Detenido");
    }

    private void SetRunning(bool running, string status)
    {
        StartButton.IsEnabled = !running;
        StopButton.IsEnabled = running;
        OutgoingProfileCombo.IsEnabled = !running;
        InputCombo.IsEnabled = !running;
        TeamsOutputCombo.IsEnabled = !running;
        MonitorCombo.IsEnabled = !running;
        MonitorCheck.IsEnabled = !running;
        SourceLanguageCombo.IsEnabled = !running;
        VoiceCombo.IsEnabled = !running;
        PreviewVoiceButton.IsEnabled = !running;
        RefreshVoicesButton.IsEnabled = !running;
        StatusText.Text = status;
        StatusText.Foreground = System.Windows.Media.Brushes.Black;
        UpdateCombinedControls();
    }

    private void AppendTranscript(TranslationTextEventArgs args)
    {
        if (args.IsContinuousDelta)
        {
            AppendRealtimeDelta(args.TranslatedText, incoming: false);
            return;
        }
        PartialText.Text = string.Empty;
        TranscriptBox.AppendText($"[{DateTime.Now:HH:mm:ss}] [YO] ES: {args.SourceText}{Environment.NewLine}                  EN: {args.TranslatedText}{Environment.NewLine}");
        TranscriptBox.ScrollToEnd();
    }

    private readonly string[] _realtimePartial = ["", ""];

    private void AppendRealtimeDelta(string delta, bool incoming)
    {
        var index = incoming ? 1 : 0;
        _realtimePartial[index] += delta;
        var tag = incoming ? "[REUNIÓN] ES" : "[YO] EN";
        var trimmed = _realtimePartial[index].TrimEnd();
        if (trimmed.Length >= 160 || (trimmed.Length != 0 && ".!?".Contains(trimmed[^1])))
        {
            TranscriptBox.AppendText($"{tag}: {_realtimePartial[index].Trim()}{Environment.NewLine}");
            TranscriptBox.ScrollToEnd();
            _realtimePartial[index] = "";
        }
        PartialText.Text = $"[YO] EN: {_realtimePartial[0]}{Environment.NewLine}[REUNIÓN] ES: {_realtimePartial[1]}";
    }

    private bool ProviderChangeLocked => OutgoingRunning || IncomingRunning;

    private bool CheckMeetingProvider(ProviderProfile profile)
    {
        try
        {
            if (_services.Profiles.CanStartMeeting(profile))
                return true;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            MessageBox.Show(this, "No se pudo leer la verificación cifrada. Vuelve a verificar la voz.", "Verificación GPT");
            return false;
        }
        MessageBox.Show(this, "Antes de usar GPT en reuniones, verifica y escucha la voz en ambos sentidos en la pestaña GPT Realtime Translate.", "Verificación GPT");
        return false;
    }

    private void OnUtteranceCompleted(UtteranceLatency utterance)
    {
        if (_session is null)
        {
            return;
        }

        var summary = _session.Latency.GetSummary();
        LatencyText.Text = $"Última: {Ms(summary.Last)}   ·   Mediana: {Ms(summary.Median)}   ·   p95: {Ms(summary.P95)}   ·   Frases medidas: {summary.Count}" +
                           $"{Environment.NewLine}Medición local aproximada (no incluye el retardo de la plataforma de reunión). CSV: {_metricsFile}";
        if (_metricsFile is not null)
        {
            try
            {
                File.AppendAllText(_metricsFile, LatencyTracker.ToCsvLine(utterance) + Environment.NewLine, Encoding.UTF8);
            }
            catch (IOException ex)
            {
                _services.Logger.Error("No se pudo escribir la métrica", ex);
            }
        }
    }

    private static string Ms(TimeSpan? value) => value is { } v ? $"{v.TotalMilliseconds:0} ms" : "—";

    private async void OnSessionFailed(TranslationSession session, string message)
    {
        await _sessionGate.WaitAsync();
        try
        {
            if (!ReferenceEquals(_session, session) || _closing)
            {
                return;
            }

            _services.Logger.Error(message);
            await StopSessionCoreAsync();
            MessageBox.Show(this, message, "La traducción se detuvo", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private void OnOpenDataFolder(object sender, RoutedEventArgs e) => SystemHelpers.OpenFolder(_services.Paths.Root);

    private void OnCreateShortcuts(object sender, RoutedEventArgs e)
    {
        try
        {
            var created = SystemHelpers.CreateShortcuts();
            MessageBox.Show(this, "Accesos directos creados:" + Environment.NewLine + string.Join(Environment.NewLine, created),
                "Accesos directos", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "No se pudieron crear los accesos directos: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        if (_closing)
        {
            return;
        }

        _closing = true;
        SaveSettings();
        await StopSessionAsync();
        await StopIncomingAsync();
        if (_incomingUsed)
        {
            MessageBox.Show(this, MeetingAudioReminder, "Antes de seguir en la reunión", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        _allowClose = true;
        _ = Dispatcher.BeginInvoke(Close);
    }
}
