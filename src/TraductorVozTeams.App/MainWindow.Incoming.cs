using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TraductorVozTeams.App.Audio;
using TraductorVozTeams.App.Services;
using TraductorVozTeams.Core.Audio;
using TraductorVozTeams.Core.Devices;
using TraductorVozTeams.Core.Metrics;
using TraductorVozTeams.Core.Providers;
using TraductorVozTeams.Core.Settings;

namespace TraductorVozTeams.App;

/// <summary>
/// Dirección entrante (reunión en inglés → español en auriculares). Tiene su propio semáforo,
/// sesión y controles, para que iniciar, detener o fallar no afecte a la dirección saliente.
/// </summary>
public partial class MainWindow
{
    public const string MeetingAudioReminder =
        "Si la reunión sigue abierta, vuelve a elegir tus auriculares como altavoz en Teams, Zoom o Meet " +
        "(Configuración → Dispositivos/Audio → Altavoz). Mientras el altavoz sea el cable virtual no oirás a los demás.";

    private static readonly string[] IncomingSourceLanguages = ["en-US", "en-GB", "en-AU", "en-CA", "en-IN", "en-IE", "en-NZ"];

    private readonly SemaphoreSlim _incomingGate = new(1, 1);
    private readonly UsageMeter _outgoingUsage = new("saliente");
    private IncomingTranslationSession? _incoming;
    private IncomingRouting? _activeIncomingRouting;
    private string? _incomingMetricsFile;
    private int _incomingVoiceRequest;
    private bool _incomingUsed;
    private bool _pairingRunning;

    private IncomingSettings Incoming => Settings.Incoming;

    private void InitializeIncoming()
    {
        var wasLoading = _loading;
        _loading = true;
        try
        {
            IncomingSourceLanguageCombo.ItemsSource = IncomingSourceLanguages;
            IncomingSourceLanguageCombo.Text = Incoming.SourceLanguage;
            TranslationVolumeSlider.Value = Incoming.TranslationVolume;
            OriginalVolumeSlider.Value = Incoming.OriginalVolume;
            OriginalMuteCheck.IsChecked = Incoming.OriginalMuted;
            DuckCheck.IsChecked = Incoming.DuckOriginal;
            UpdateOriginalVolumeText();
            ShowPairingStatus();
        }
        finally
        {
            _loading = wasLoading;
        }

        MeetingOutputCombo.SelectionChanged += (_, _) => ShowPairingStatus();
        IncomingCaptureCombo.SelectionChanged += (_, _) => ShowPairingStatus();
        TeamsOutputCombo.SelectionChanged += (_, _) => ShowPairingStatus();
        UpdateCombinedControls();
    }

    private void RefreshIncomingDevices(IReadOnlyList<AudioDeviceInfo> inputs, IReadOnlyList<AudioDeviceInfo> outputs)
    {
        Select(MeetingOutputCombo, outputs, MeetingOutputCombo.SelectedItem is AudioDeviceInfo o ? o.Id : Incoming.MeetingOutputDeviceId);
        Select(IncomingCaptureCombo, inputs, IncomingCaptureCombo.SelectedItem is AudioDeviceInfo c ? c.Id : Incoming.CaptureDeviceId);
        Select(ListenCombo, outputs, ListenCombo.SelectedItem is AudioDeviceInfo l ? l.Id : Incoming.ListenDeviceId);
        IncomingRoutingText.Text = string.Empty;
    }

    private void CaptureIncomingUiIntoSettings()
    {
        // Durante el constructor (antes de InitializeIncoming) los controles aún no tienen valores cargados.
        if (IncomingSourceLanguageCombo.ItemsSource is null)
        {
            return;
        }

        Incoming.MeetingOutputDeviceId = (MeetingOutputCombo.SelectedItem as AudioDeviceInfo)?.Id;
        Incoming.CaptureDeviceId = (IncomingCaptureCombo.SelectedItem as AudioDeviceInfo)?.Id;
        Incoming.ListenDeviceId = (ListenCombo.SelectedItem as AudioDeviceInfo)?.Id;
        Incoming.TranslationVolume = (float)TranslationVolumeSlider.Value;
        Incoming.OriginalVolume = (float)OriginalVolumeSlider.Value;
        Incoming.OriginalMuted = OriginalMuteCheck.IsChecked == true;
        Incoming.DuckOriginal = DuckCheck.IsChecked == true;
        var language = IncomingSourceLanguageCombo.Text.Trim();
        Incoming.SourceLanguage = language.Length == 0 ? "en-US" : language;
    }

    private IncomingRouting CurrentIncomingRouting() => new(
        MeetingOutputCombo.SelectedItem as AudioDeviceInfo,
        IncomingCaptureCombo.SelectedItem as AudioDeviceInfo,
        ListenCombo.SelectedItem as AudioDeviceInfo,
        Incoming.VerifiedPairing);

    private void UpdateCombinedControls()
    {
        if (StartBothButton is null)
        {
            return;
        }

        var outgoingRunning = _session is not null || !StartButton.IsEnabled;
        var incomingRunning = _incoming is not null || !IncomingStartButton.IsEnabled;
        StartBothButton.IsEnabled = !_closing && (!outgoingRunning || !incomingRunning);
        StopBothButton.IsEnabled = outgoingRunning || incomingRunning;
        OutgoingProfileCombo.IsEnabled = !outgoingRunning;
        IncomingProfileCombo.IsEnabled = !incomingRunning;
        ConfigureProvidersButton.IsEnabled = !ProviderChangeLocked;
        var hasVoiceCatalog = VoiceCatalogFor(Settings.ActiveProfile) is not null;
        var hasIncomingVoiceCatalog = VoiceCatalogFor(Settings.IncomingProfile) is not null;
        VoiceCombo.IsEnabled = !outgoingRunning && hasVoiceCatalog;
        IncomingVoiceCombo.IsEnabled = !incomingRunning && hasIncomingVoiceCatalog;
        RefreshVoicesButton.IsEnabled = !outgoingRunning && hasVoiceCatalog;
        IncomingRefreshVoicesButton.IsEnabled = !incomingRunning && hasIncomingVoiceCatalog;
        PreviewVoiceButton.IsEnabled = !outgoingRunning && hasVoiceCatalog && VoiceCombo.SelectedItem is TranslationVoice;
        IncomingPreviewButton.IsEnabled = !incomingRunning && hasIncomingVoiceCatalog && IncomingVoiceCombo.SelectedItem is TranslationVoice;
        PairingButton.IsEnabled = !outgoingRunning && !incomingRunning && !_pairingRunning;
    }

    // ---------- Comprobación de cables ----------

    private void ShowPairingStatus()
    {
        var output = MeetingOutputCombo.SelectedItem as AudioDeviceInfo;
        var capture = IncomingCaptureCombo.SelectedItem as AudioDeviceInfo;
        var outgoing = TeamsOutputCombo.SelectedItem as AudioDeviceInfo;
        var pairing = Incoming.VerifiedPairing;
        if (output is null || capture is null)
        {
            PairingText.Text = "Elige la salida y la entrada del cable.";
            PairingText.Foreground = Brushes.DimGray;
        }
        else if (pairing is not null && pairing.Matches(output.Id, capture.Id, outgoing?.Id))
        {
            PairingText.Text = $"✔ Verificado por sonido el {pairing.VerifiedAt.LocalDateTime:g}.";
            PairingText.Foreground = Brushes.ForestGreen;
        }
        else
        {
            PairingText.Text = "Sin verificar para estos dispositivos. Pulsa «Comprobar cables» (sin reunión activa).";
            PairingText.Foreground = Brushes.DarkOrange;
        }
    }

    private async void OnCheckCables(object sender, RoutedEventArgs e)
    {
        if (_session is not null || _incoming is not null || _pairingRunning)
        {
            return;
        }

        var output = MeetingOutputCombo.SelectedItem as AudioDeviceInfo;
        var capture = IncomingCaptureCombo.SelectedItem as AudioDeviceInfo;
        if (output is null || capture is null)
        {
            MessageBox.Show(this, "Elige primero la salida (altavoz de la reunión) y la entrada del cable.", "Comprobar cables",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var outgoing = TeamsOutputCombo.SelectedItem as AudioDeviceInfo;
        var mic = InputCombo.SelectedItem as AudioDeviceInfo;
        if (MessageBox.Show(this,
                "Se reproducirán tonos breves por la salida elegida y por la salida de tu traducción al inglés, para comprobar que los cables " +
                "están emparejados y separados. Hazlo sin una reunión activa: los tonos podrían oírse en ella." + Environment.NewLine + Environment.NewLine +
                "¿Continuar?",
                "Comprobar cables", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        _pairingRunning = true;
        UpdateCombinedControls();
        PairingText.Text = "Comprobando con tonos…";
        PairingText.Foreground = Brushes.DimGray;
        try
        {
            var result = await CablePairingTest.RunAsync(output.Id, capture.Id, outgoing?.Id, mic?.Id, _services.Logger);
            Incoming.VerifiedPairing = result.Pairing;
            SaveSettings();
            ShowPairingStatus();
            if (!result.Success)
            {
                PairingText.Text = "✖ " + result.Message;
                PairingText.Foreground = Brushes.Firebrick;
            }
            else
            {
                PairingText.ToolTip = result.Message;
            }
        }
        catch (Exception ex)
        {
            _services.Logger.Error("Comprobación de cables", ex);
            PairingText.Text = "✖ No se pudo hacer la prueba: " + ex.Message;
            PairingText.Foreground = Brushes.Firebrick;
        }
        finally
        {
            _pairingRunning = false;
            UpdateCombinedControls();
        }
    }

    // ---------- Volúmenes ----------

    private void UpdateOriginalVolumeText()
    {
        OriginalVolumeText.Text = OriginalMuteCheck.IsChecked == true ? "mudo" : $"{OriginalVolumeSlider.Value:P0}";
    }

    private void OnIncomingVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => ApplyIncomingVolumes();

    private void OnIncomingOptionChanged(object sender, RoutedEventArgs e) => ApplyIncomingVolumes();

    private void ApplyIncomingVolumes()
    {
        if (OriginalVolumeText is null)
        {
            return;
        }

        UpdateOriginalVolumeText();
        if (_loading)
        {
            return;
        }

        CaptureIncomingUiIntoSettings();
        _incoming?.SetVolumes(Incoming.TranslationVolume, Incoming.EffectiveOriginalVolume, Incoming.DuckOriginal, Incoming.DuckLevel);
    }

    // ---------- Voces en español ----------

    private string IncomingVoiceCacheKey(ProviderProfile profile) => profile.Id + "|" + Incoming.TargetLanguage;

    private async Task LoadIncomingVoicesAsync(bool forceRefresh)
    {
        var request = ++_incomingVoiceRequest;
        var profile = Settings.IncomingProfile;
        var catalog = VoiceCatalogFor(profile);
        if (profile is null || catalog is null)
        {
            ShowIncomingVoices([], null, "Este proveedor no ofrece selección de voz.");
            UpdateCombinedControls();
            return;
        }

        if (!forceRefresh && _voiceCache.TryGetValue(IncomingVoiceCacheKey(profile), out var cached))
        {
            ShowIncomingVoices(cached, Incoming.VoiceName, $"{cached.Count} voces en español disponibles.");
            return;
        }

        ShowIncomingVoices([], Incoming.VoiceName, "Consultando voces en español…");
        IncomingRefreshVoicesButton.IsEnabled = false;
        try
        {
            var context = _services.CreateIncomingContext(profile);
            var voices = await Task.Run(() => catalog.ListVoicesAsync(context, CancellationToken.None));
            _voiceCache[IncomingVoiceCacheKey(profile)] = voices;
            if (request == _incomingVoiceRequest)
            {
                ShowIncomingVoices(voices, Incoming.VoiceName, $"{voices.Count} voces en español disponibles.");
            }
        }
        catch (Exception ex)
        {
            _services.Logger.Error("No se pudo obtener la lista de voces en español", ex);
            if (request == _incomingVoiceRequest)
            {
                ShowIncomingVoices([], Incoming.VoiceName, "No se pudo obtener la lista de voces: " + ex.Message);
            }
        }
        finally
        {
            if (request == _incomingVoiceRequest)
                UpdateCombinedControls();
        }
    }

    private void ShowIncomingVoices(IReadOnlyList<TranslationVoice> voices, string? current, string status)
    {
        var items = voices.ToList();
        if (!string.IsNullOrEmpty(current) && !items.Any(v => v.Name.Equals(current, StringComparison.OrdinalIgnoreCase)))
        {
            items.Insert(0, new TranslationVoice(current, current, voices.Count > 0 ? "no listada en la región" : "configurada", "—", false));
        }

        var wasLoading = _loading;
        _loading = true;
        IncomingVoiceCombo.ItemsSource = items;
        IncomingVoiceCombo.SelectedItem = items.FirstOrDefault(v => v.Name.Equals(current, StringComparison.OrdinalIgnoreCase));
        _loading = wasLoading;
        IncomingVoiceStatusText.Text = status;
    }

    private void OnIncomingVoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || IncomingVoiceCombo.SelectedItem is not TranslationVoice voice)
        {
            return;
        }

        Incoming.VoiceName = voice.Name;
        SaveSettings();
        _services.Logger.Info($"Voz en español seleccionada: {voice.Name}");
    }

    private async void OnIncomingRefreshVoices(object sender, RoutedEventArgs e) => await LoadIncomingVoicesAsync(forceRefresh: true);

    private async void OnIncomingPreviewVoice(object sender, RoutedEventArgs e)
    {
        var profile = Settings.IncomingProfile;
        var catalog = VoiceCatalogFor(profile);
        if (profile is null || catalog is null || IncomingVoiceCombo.SelectedItem is not TranslationVoice voice)
        {
            MessageBox.Show(this, "Selecciona una voz en español.", "Probar voz", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (ListenCombo.SelectedItem is not AudioDeviceInfo device || DeviceRoutingValidator.IsVirtualCable(device, Settings.VirtualCablePatterns))
        {
            MessageBox.Show(this, "Elige tus auriculares en \"Auriculares (español)\". La muestra nunca se envía a un cable virtual.",
                "Probar voz", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        IncomingPreviewButton.IsEnabled = false;
        IncomingVoiceStatusText.Text = $"Reproduciendo muestra de {voice.DisplayName} en {device.Name}…";
        try
        {
            var context = _services.CreateIncomingContext(profile);
            var result = await Task.Run(() => catalog.PreviewVoiceAsync(context, voice.Name, device.Id, CancellationToken.None));
            IncomingVoiceStatusText.Text = result.Message;
            if (!result.Success)
            {
                _services.Logger.Error("Muestra de voz en español: " + result.Message);
            }
        }
        finally
        {
            UpdateCombinedControls();
        }
    }

    // ---------- Iniciar / detener ----------

    private async void OnIncomingStart(object sender, RoutedEventArgs e) => await StartIncomingGuardedAsync();

    private async void OnIncomingStop(object sender, RoutedEventArgs e) => await StopIncomingAsync();

    private async void OnStartBoth(object sender, RoutedEventArgs e)
    {
        if (_session is null && !_closing && _sessionGate.Wait(0))
        {
            try
            {
                await StartSessionAsync();
            }
            finally
            {
                _sessionGate.Release();
            }
        }

        await StartIncomingGuardedAsync();
    }

    private async void OnStopBoth(object sender, RoutedEventArgs e) =>
        await Task.WhenAll(StopSessionAsync(), StopIncomingAsync());

    private async Task StartIncomingGuardedAsync()
    {
        if (_incoming is not null || _closing || !_incomingGate.Wait(0))
        {
            return;
        }

        try
        {
            await StartIncomingCoreAsync();
        }
        finally
        {
            _incomingGate.Release();
        }
    }

    private async Task StartIncomingCoreAsync()
    {
        CaptureUiIntoSettings();
        var profile = Settings.IncomingProfile;
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

        var outgoing = new DeviceRouting(
            InputCombo.SelectedItem as AudioDeviceInfo,
            TeamsOutputCombo.SelectedItem as AudioDeviceInfo,
            Settings.MonitorEnabled,
            MonitorCombo.SelectedItem as AudioDeviceInfo);
        var routing = CurrentIncomingRouting();
        var validation = IncomingRoutingValidator.Validate(routing, outgoing, Settings.VirtualCablePatterns);
        IncomingRoutingText.Text = string.Join(Environment.NewLine,
            validation.Errors.Select(x => "✖ " + x).Concat(validation.Warnings.Select(x => "⚠ " + x)));
        IncomingRoutingText.Foreground = validation.IsValid ? Brushes.DarkOrange : Brushes.Firebrick;
        if (!validation.IsValid)
        {
            return;
        }

        SaveSettings();
        SetIncomingRunning(true, "Conectando…");
        if (_session is null)
        {
            TranscriptBox.Clear();
        }

        IncomingLatencyText.Text = profile.ProviderType == RealtimeTranslateDescriptor.Type
            ? "Traducción continua: la métrica por frase no se aplica."
            : "Sin mediciones todavía.";
        IncomingUsageText.Text = string.Empty;
        var devices = new IncomingDevices(
            routing.Capture!.Id,
            routing.Listen!.Id,
            Incoming.TranslationVolume,
            Incoming.EffectiveOriginalVolume,
            Incoming.DuckOriginal,
            Incoming.DuckLevel);

        try
        {
            var provider = factory.Create(_services.CreateIncomingContext(profile));
            var session = new IncomingTranslationSession(provider, devices, _services.Logger);
            _incoming = session;
            _activeIncomingRouting = routing;
            _incomingUsed = true;
            void Dispatch(Action action) => Dispatcher.BeginInvoke(() =>
            {
                if (ReferenceEquals(_incoming, session) && !_closing)
                {
                    action();
                }
            });
            session.Recognizing += (_, args) => Dispatch(() => PartialText.Text = $"[REUNIÓN] {args.SourceText}  →  {args.TranslatedText}");
            session.Recognized += (_, args) => Dispatch(() => AppendIncomingTranscript(args));
            session.UtteranceCompleted += (_, u) => Dispatch(() => OnIncomingUtteranceCompleted(session, u));
            session.LevelChanged += (_, level) => Dispatch(() => IncomingLevelBar.Value = level);
            session.Failed += (_, message) => Dispatch(() => OnIncomingFailed(session, message));

            Directory.CreateDirectory(_services.Paths.MetricsDirectory);
            _incomingMetricsFile = Path.Combine(_services.Paths.MetricsDirectory, $"latencia-entrante-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
            File.WriteAllText(_incomingMetricsFile, LatencyTracker.CsvHeader.Replace("texto_es,texto_en", "texto_en,texto_es", StringComparison.Ordinal) + Environment.NewLine, Encoding.UTF8);

            await session.StartAsync(CancellationToken.None);
            _services.Logger.Info($"[entrante] Sesión iniciada. Perfil: {profile.Name}; idioma: {Incoming.SourceLanguage} → {Incoming.TargetLanguage}; voz: {Incoming.VoiceName}");
            IncomingStatusText.Text = "● Traduciendo";
            IncomingStatusText.Foreground = Brushes.ForestGreen;
        }
        catch (Exception ex)
        {
            _services.Logger.Error("[entrante] No se pudo iniciar", ex);
            await StopIncomingCoreAsync();
            MessageBox.Show(this, "No se pudo iniciar la traducción de la reunión: " + ex.Message + Environment.NewLine + Environment.NewLine + MeetingAudioReminder,
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task StopIncomingAsync()
    {
        await _incomingGate.WaitAsync();
        try
        {
            await StopIncomingCoreAsync();
        }
        finally
        {
            _incomingGate.Release();
        }
    }

    private async Task StopIncomingCoreAsync()
    {
        var session = _incoming;
        if (session is not null)
        {
            SetIncomingRunning(true, "Deteniendo…");
            IncomingStopButton.IsEnabled = false;
            try
            {
                var usage = await session.StopAsync();
                RecordUsage(usage, IncomingUsageText);
            }
            finally
            {
                await session.DisposeAsync();
                _incoming = null;
                _activeIncomingRouting = null;
            }

            _services.Logger.Info("[entrante] Sesión detenida.");
        }

        IncomingLevelBar.Value = 0;
        FlushRealtimeTranscript(incoming: true);
        SetIncomingRunning(false, "Detenido");
    }

    private void SetIncomingRunning(bool running, string status)
    {
        IncomingStartButton.IsEnabled = !running;
        IncomingStopButton.IsEnabled = running;
        MeetingOutputCombo.IsEnabled = !running;
        IncomingCaptureCombo.IsEnabled = !running;
        ListenCombo.IsEnabled = !running;
        IncomingSourceLanguageCombo.IsEnabled = !running;
        IncomingVoiceCombo.IsEnabled = !running;
        IncomingPreviewButton.IsEnabled = !running;
        IncomingRefreshVoicesButton.IsEnabled = !running;
        IncomingStatusText.Text = status;
        IncomingStatusText.Foreground = Brushes.Black;
        UpdateCombinedControls();
    }

    private void AppendIncomingTranscript(TranslationTextEventArgs args)
    {
        if (args.IsContinuousDelta)
        {
            AppendRealtimeDelta(args.TranslatedText, incoming: true);
            return;
        }
        PartialText.Text = string.Empty;
        TranscriptBox.AppendText($"[{DateTime.Now:HH:mm:ss}] [REUNIÓN] EN: {args.SourceText}{Environment.NewLine}                          ES: {args.TranslatedText}{Environment.NewLine}");
        TranscriptBox.ScrollToEnd();
    }

    private void OnIncomingUtteranceCompleted(IncomingTranslationSession session, UtteranceLatency utterance)
    {
        var summary = session.Latency.GetSummary();
        IncomingLatencyText.Text = $"Última: {Ms(summary.Last)}   ·   Mediana: {Ms(summary.Median)}   ·   p95: {Ms(summary.P95)}   ·   Frases medidas: {summary.Count}" +
                                   $"{Environment.NewLine}Medición local aproximada (no incluye el retardo de la plataforma). CSV: {_incomingMetricsFile}";
        var usage = session.Usage.Snapshot();
        IncomingUsageText.Text = $"Audio enviado en esta sesión: {usage.AudioSent.TotalMinutes:0.0} min";
        if (_incomingMetricsFile is not null)
        {
            try
            {
                File.AppendAllText(_incomingMetricsFile, LatencyTracker.ToCsvLine(utterance) + Environment.NewLine, Encoding.UTF8);
            }
            catch (IOException ex)
            {
                _services.Logger.Error("[entrante] No se pudo escribir la métrica", ex);
            }
        }
    }

    private async void OnIncomingFailed(IncomingTranslationSession session, string message)
    {
        await _incomingGate.WaitAsync();
        try
        {
            if (!ReferenceEquals(_incoming, session) || _closing)
            {
                return;
            }

            _services.Logger.Error(message);
            await StopIncomingCoreAsync();
            MessageBox.Show(this, message + Environment.NewLine + Environment.NewLine +
                                  "Tu traducción al inglés (si estaba activa) sigue funcionando." + Environment.NewLine + MeetingAudioReminder,
                "La traducción de la reunión se detuvo", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _incomingGate.Release();
        }
    }

    // ---------- Consumo ----------

    private void RecordOutgoingUsage(TranslationSession session)
    {
        _outgoingUsage.AddAudio(session.AudioBytes);
        RecordUsage(_outgoingUsage.Stop(), OutgoingUsageText);
    }

    private void RecordUsage(UsageSnapshot usage, TextBlock target)
    {
        try
        {
            var file = UsageMeter.AppendToDailyCsv(_services.Paths.MetricsDirectory, usage);
            target.Text = $"Última sesión: {usage.AudioSent.TotalMinutes:0.0} min de audio enviado, {usage.Utterances} frases, " +
                          $"{usage.TranslatedCharacters} caracteres traducidos. Registro: {file}";
        }
        catch (Exception ex)
        {
            _services.Logger.Error($"[{usage.Direction}] No se pudo registrar el consumo", ex);
        }
    }
}
