using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TraductorVozTeams.App;
using TraductorVozTeams.App.Services;
using TraductorVozTeams.Core.Audio;
using TraductorVozTeams.Core.Devices;
using TraductorVozTeams.Core.Providers;
using TraductorVozTeams.Core.Settings;

namespace TraductorVozTeams.Tests;

public class SessionLifecycleTests
{
    [Fact]
    public Task RepeatedStopCannotEnableStartOrLoseTheActiveSession() => OnWindowThread(async (window, services) =>
    {
        var provider = new DelayedStopProvider();
        var session = CreateSession(provider, services);
        SessionField.SetValue(window, session);
        var startButton = (Button)window.FindName("StartButton");
        startButton.IsEnabled = false;
        var firstStop = Stop(window);
        Task? secondStop = null;
        try
        {
            await provider.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            secondStop = Stop(window);
            Assert.False(secondStop.IsCompleted);
            Assert.False(startButton.IsEnabled);
            Assert.False(((Button)window.FindName("StopButton")).IsEnabled);
            Assert.Same(session, SessionField.GetValue(window));

            startButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Same(session, SessionField.GetValue(window));
        }
        finally
        {
            provider.AllowStop.TrySetResult();
            await firstStop.WaitAsync(TimeSpan.FromSeconds(5));
            if (secondStop is not null)
            {
                await secondStop.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        Assert.Equal(1, provider.StopCalls);
        Assert.Equal(1, provider.DisposeCalls);
        Assert.Null(SessionField.GetValue(window));
        Assert.True(startButton.IsEnabled);
    });

    [Fact]
    public Task ClosingWaitsForAnInProgressStop() => OnWindowThread(async (window, services) =>
    {
        var provider = new DelayedStopProvider();
        SessionField.SetValue(window, CreateSession(provider, services));
        var stopped = Stop(window);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        try
        {
            await provider.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var closing = new System.ComponentModel.CancelEventArgs();
            Method("OnClosing").Invoke(window, [window, closing]);
            Assert.True(closing.Cancel);
            Assert.False(closed.Task.IsCompleted);
            Assert.Equal(0, provider.DisposeCalls);
        }
        finally
        {
            provider.AllowStop.TrySetResult();
            await stopped.WaitAsync(TimeSpan.FromSeconds(5));
        }

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, provider.StopCalls);
        Assert.Equal(1, provider.DisposeCalls);
    });

    [Fact]
    public Task LateFailureFromAnOldSessionDoesNotStopTheCurrentSession() => OnWindowThread(async (window, services) =>
    {
        var provider = new DelayedStopProvider();
        var current = CreateSession(provider, services);
        var previous = CreateSession(new DelayedStopProvider(), services);
        SessionField.SetValue(window, current);
        try
        {
            Method("OnSessionFailed").Invoke(window, [previous, "Error tardio"]);
            Assert.Same(current, SessionField.GetValue(window));
            Assert.Equal(0, provider.StopCalls);
        }
        finally
        {
            provider.AllowStop.TrySetResult();
            await Stop(window).WaitAsync(TimeSpan.FromSeconds(5));
            await previous.DisposeAsync();
        }
    });

    [Fact]
    public Task StoppingIncomingDoesNotTouchTheOutgoingSession() => OnWindowThread(async (window, services) =>
    {
        var outgoingProvider = new DelayedStopProvider();
        var incomingProvider = new DelayedStopProvider();
        var outgoing = CreateSession(outgoingProvider, services);
        SessionField.SetValue(window, outgoing);
        IncomingField.SetValue(window, CreateIncoming(incomingProvider, services));
        incomingProvider.AllowStop.TrySetResult();
        try
        {
            await ((Task)Method("StopIncomingAsync").Invoke(window, null)!).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(IncomingField.GetValue(window));
            Assert.Equal(1, incomingProvider.StopCalls);
            Assert.Equal(1, incomingProvider.DisposeCalls);
            Assert.Same(outgoing, SessionField.GetValue(window));
            Assert.Equal(0, outgoingProvider.StopCalls);
            Assert.True(((Button)window.FindName("IncomingStartButton")).IsEnabled);
        }
        finally
        {
            outgoingProvider.AllowStop.TrySetResult();
            await Stop(window).WaitAsync(TimeSpan.FromSeconds(5));
        }
    });

    [Fact]
    public Task LateFailureFromAnOldIncomingSessionIsIgnored() => OnWindowThread(async (window, services) =>
    {
        var provider = new DelayedStopProvider();
        var current = CreateIncoming(provider, services);
        var previous = CreateIncoming(new DelayedStopProvider(), services);
        IncomingField.SetValue(window, current);
        provider.AllowStop.TrySetResult();
        try
        {
            Method("OnIncomingFailed").Invoke(window, [previous, "Error tardío"]);
            await Task.Delay(50);
            Assert.Same(current, IncomingField.GetValue(window));
            Assert.Equal(0, provider.StopCalls);
        }
        finally
        {
            await ((Task)Method("StopIncomingAsync").Invoke(window, null)!).WaitAsync(TimeSpan.FromSeconds(5));
            await previous.DisposeAsync();
        }
    });

    [Fact]
    public Task EachSelectorLocksOnlyWithItsOwnDirectionAndTheGptTabUsesTheSameProfileStore() => OnWindowThread((window, services) =>
    {
        var speech = new ProviderProfile { Name = "Speech", ProviderType = AzureSpeechDescriptor.Type };
        var gpt = new ProviderProfile { Name = "Foundry", ProviderType = RealtimeTranslateDescriptor.Type };
        services.Settings.Profiles.AddRange([speech, gpt]);
        services.Settings.ActiveProfileId = speech.Id;
        services.Settings.Incoming.ProfileId = speech.Id;
        Method("RefreshProfiles").Invoke(window, null);
        var outgoingCombo = (ComboBox)window.FindName("OutgoingProfileCombo");
        var incomingCombo = (ComboBox)window.FindName("IncomingProfileCombo");
        var configure = (Button)window.FindName("ConfigureProvidersButton");
        foreach (var (startName, locked, free) in new[] { ("StartButton", outgoingCombo, incomingCombo), ("IncomingStartButton", incomingCombo, outgoingCombo) })
        {
            var start = (Button)window.FindName(startName);
            start.IsEnabled = false;
            Method("UpdateCombinedControls").Invoke(window, null);
            Assert.False(configure.IsEnabled);
            Assert.False(locked.IsEnabled);
            Assert.True(free.IsEnabled);
            locked.SelectedItem = gpt;
            Assert.Same(speech, locked.SelectedItem);
            Assert.Same(speech, services.Settings.ActiveProfile);
            Assert.Same(speech, services.Settings.IncomingProfile);
            start.IsEnabled = true;
        }
        var incomingStart = (Button)window.FindName("IncomingStartButton");
        incomingStart.IsEnabled = false;
        Method("UpdateCombinedControls").Invoke(window, null);
        outgoingCombo.SelectedItem = gpt;
        Assert.Same(gpt, services.Settings.ActiveProfile);
        Assert.Same(speech, services.Settings.IncomingProfile);
        incomingStart.IsEnabled = true;
        Method("UpdateCombinedControls").Invoke(window, null);
        Assert.True(configure.IsEnabled);
        Assert.False(((Button)window.FindName("RefreshVoicesButton")).IsEnabled);
        var dialog = new ProvidersWindow(services);
        try
        {
            Assert.Equal(1, ((TabControl)dialog.FindName("ProviderTabs")).SelectedIndex);
            Assert.Same(gpt, ((ListBox)dialog.FindName("ProfileList")).SelectedItem);
            Assert.False(((Button)dialog.FindName("VerifyGptButton")).IsEnabled);
        }
        finally
        {
            dialog.Close();
        }
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task EachDirectionUsesItsOwnProviderInAllFourCombinations(bool outgoingGpt, bool incomingGpt) => OnWindowThread((window, services) =>
    {
        var speech = new ProviderProfile { Name = "Speech", ProviderType = AzureSpeechDescriptor.Type,
            Values = new() { [AzureSpeechDescriptor.ConnectionModeKey] = "region", [AzureSpeechDescriptor.RegionKey] = "eastus",
                             [AzureSpeechDescriptor.VoiceNameKey] = "en-US-AvaMultilingualNeural" } };
        var gpt = new ProviderProfile { Name = "Foundry", ProviderType = RealtimeTranslateDescriptor.Type,
            Values = new() { [RealtimeTranslateDescriptor.EndpointKey] = "https://example.services.ai.azure.com",
                             [RealtimeTranslateDescriptor.DeploymentKey] = "gpt-realtime-translate" } };
        services.Settings.Profiles.AddRange([speech, gpt]);
        // Start from the opposite provider so each selection is a real change that is saved.
        services.Settings.ActiveProfileId = outgoingGpt ? speech.Id : gpt.Id;
        services.Settings.Incoming.ProfileId = incomingGpt ? speech.Id : gpt.Id;
        services.Settings.Incoming.VoiceName = "es-AR-ElenaNeural";
        // Cached voices keep the Speech selection offline.
        var cache = (Dictionary<string, IReadOnlyList<TranslationVoice>>)typeof(MainWindow)
            .GetField("_voiceCache", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        cache[speech.Id] = [];
        cache[speech.Id + "|" + services.Settings.Incoming.TargetLanguage] = [];
        Method("RefreshProfiles").Invoke(window, null);
        var outgoing = outgoingGpt ? gpt : speech;
        var incoming = incomingGpt ? gpt : speech;
        ((ComboBox)window.FindName("OutgoingProfileCombo")).SelectedItem = outgoing;
        ((ComboBox)window.FindName("IncomingProfileCombo")).SelectedItem = incoming;
        Method("UpdateCombinedControls").Invoke(window, null);

        Assert.Same(outgoing, services.Settings.ActiveProfile);
        Assert.Same(incoming, services.Settings.IncomingProfile);
        Assert.Equal(!outgoingGpt, ((Button)window.FindName("RefreshVoicesButton")).IsEnabled);
        Assert.Equal(!incomingGpt, ((Button)window.FindName("IncomingRefreshVoicesButton")).IsEnabled);

        var reload = new AppServices(services.Paths);
        Assert.Equal(outgoing.Id, reload.Settings.ActiveProfile!.Id);
        Assert.Equal(incoming.Id, reload.Settings.IncomingProfile!.Id);
        Assert.True(reload.Factories.ContainsKey(reload.Settings.ActiveProfile.ProviderType));
        Assert.True(reload.Factories.ContainsKey(reload.Settings.IncomingProfile.ProviderType));

        var outgoingContext = reload.CreateContext(reload.Settings.ActiveProfile);
        var incomingContext = reload.CreateIncomingContext(reload.Settings.IncomingProfile);
        Assert.Equal("es-AR", outgoingContext.SourceLanguage);
        Assert.Equal("en-US", incomingContext.SourceLanguage);
        if (outgoingGpt)
            Assert.Equal("en", outgoingContext.RealtimeTarget);
        else
            Assert.Equal("en-US-AvaMultilingualNeural", outgoingContext.Values[AzureSpeechDescriptor.VoiceNameKey]);
        if (incomingGpt)
            Assert.Equal("es", incomingContext.RealtimeTarget);
        else
            Assert.Equal("es-AR-ElenaNeural", incomingContext.Values[AzureSpeechDescriptor.VoiceNameKey]);
        return Task.CompletedTask;
    });

    [Fact]
    public Task GptVerificationActionFitsAtMinimumWindowSize() => OnWindowThread((_, services) =>
    {
        var gpt = new ProviderProfile { Name = "Foundry", ProviderType = RealtimeTranslateDescriptor.Type };
        services.Settings.Profiles.Add(gpt);
        services.Settings.ActiveProfileId = gpt.Id;
        var dialog = new ProvidersWindow(services);
        try
        {
            var root = (FrameworkElement)dialog.Content;
            var size = new Size(680, 460);
            root.Measure(size);
            root.Arrange(new Rect(size));
            root.UpdateLayout();
            var button = (Button)dialog.FindName("VerifyGptButton");
            Assert.Equal(Visibility.Visible, ((FrameworkElement)dialog.FindName("GptVerificationPanel")).Visibility);
            Assert.True(button.ActualWidth >= button.DesiredSize.Width - button.Margin.Left - button.Margin.Right - 1);
            Assert.InRange(button.TranslatePoint(new Point(button.ActualWidth, 0), root).X, 1, root.ActualWidth + 1);
        }
        finally
        {
            dialog.Close();
        }
        return Task.CompletedTask;
    });

    [Fact]
    public Task StartingOutgoingChecksTheActiveIncomingRoute() => OnWindowThread((window, _) =>
    {
        var mic = new AudioDeviceInfo("mic", "Micrófono", AudioDeviceFlow.Capture);
        var capture = new AudioDeviceInfo("b-in", "Line B (Virtual Audio Cable)", AudioDeviceFlow.Capture);
        var meeting = new AudioDeviceInfo("b-out", "Line B (Virtual Audio Cable)", AudioDeviceFlow.Render);
        var translated = new AudioDeviceInfo("a-out", "Line A (Virtual Audio Cable)", AudioDeviceFlow.Render);
        var headphones = new AudioDeviceInfo("headphones", "Auriculares", AudioDeviceFlow.Render);
        var route = new DeviceRouting(mic, translated, false, null);
        var active = new IncomingRouting(meeting, capture, headphones,
            new CablePairing(meeting.Id, capture.Id, translated.Id, DateTimeOffset.UtcNow));
        typeof(MainWindow).GetField("_activeIncomingRouting", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(window, active);

        RoutingValidationResult Validate(DeviceRouting outgoing) =>
            (RoutingValidationResult)Method("ValidateOutgoingRouting").Invoke(window, [outgoing])!;

        Assert.True(Validate(route).IsValid);
        Assert.Contains(Validate(new DeviceRouting(mic, meeting, false, null)).Errors,
            error => error.Contains("misma salida", StringComparison.Ordinal));
        Assert.Contains(Validate(new DeviceRouting(mic,
            new AudioDeviceInfo("c-out", "Line C (Virtual Audio Cable)", AudioDeviceFlow.Render), false, null)).Errors,
            error => error.Contains("Comprobar cables", StringComparison.Ordinal));
        typeof(MainWindow).GetField("_activeIncomingRouting", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(window, null);
        Assert.True(Validate(new DeviceRouting(mic, meeting, false, null)).IsValid);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(1610, 850, true, false)]
    [InlineData(990, 455, true, true)]
    [InlineData(610, 415, false, true)]
    [InlineData(504, 352, false, true)]
    public Task LayoutKeepsActionsAndTranscriptUsable(double width, double height, bool sideBySide, bool scrolls) =>
        OnWindowThread(async (window, _) =>
    {
        ((TextBlock)window.FindName("PartialText")).Text = string.Concat(Enumerable.Repeat(
            "[REUNIÓN] Frase reconocida en la reunión → Traducción en curso. ", 24));
        foreach (var name in new[] { "InputCombo", "VoiceCombo", "MeetingOutputCombo", "IncomingVoiceCombo" })
        {
            var combo = (ComboBox)window.FindName(name);
            combo.ItemsSource = new[] { "Dispositivo o voz con un nombre largo: auriculares Plantronics Blackwire 3320 Series · multilingüe" };
            combo.SelectedIndex = 0;
        }
        var root = (FrameworkElement)window.Content;
        var size = new Size(width, height);
        root.Measure(size);
        root.Arrange(new Rect(size));
        root.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        root.Measure(size);
        root.Arrange(new Rect(size));
        root.UpdateLayout();

        T Find<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        var outgoing = Find<GroupBox>("OutgoingPanel");
        var incoming = Find<GroupBox>("IncomingPanel");
        Assert.Equal(sideBySide, incoming.TranslatePoint(new Point(), outgoing).X > 0);
        var config = Find<ScrollViewer>("ConfigurationScroll");
        Assert.Equal(scrolls, config.ScrollableHeight > 1);
        var transcript = Find<TextBox>("TranscriptBox");
        Assert.True(transcript.ActualHeight >= 55,
            $"Transcript: {transcript.ActualHeight}; root: {root.ActualHeight}; config: {config.ActualHeight}/{config.MaxHeight}");

        void CheckButton(string name, FrameworkElement container)
        {
            var button = Find<Button>(name);
            var contentWidth = button.DesiredSize.Width - button.Margin.Left - button.Margin.Right;
            Assert.True(button.ActualWidth >= contentWidth - 1, $"{name}: ancho {button.ActualWidth}/{contentWidth}");
            var right = button.TranslatePoint(new Point(button.ActualWidth, 0), container).X;
            Assert.InRange(right, 1, container.ActualWidth + 1);
        }

        CheckButton("PreviewVoiceButton", outgoing);
        CheckButton("RefreshVoicesButton", outgoing);
        CheckButton("IncomingPreviewButton", incoming);
        CheckButton("IncomingRefreshVoicesButton", incoming);
        CheckButton("RefreshDevicesButton", root);
    });

    [Theory]
    [InlineData(0.2f, true, 0.3f, true, 0.06f)]
    [InlineData(0.2f, true, 0.3f, false, 0.2f)]
    [InlineData(0.2f, false, 0.3f, true, 0.2f)]
    [InlineData(0f, true, 0.3f, true, 0f)]
    public void OriginalGainDucksOnlyWhileTranslationPlays(float volume, bool duck, float level, bool playing, float expected) =>
        Assert.Equal(expected, IncomingTranslationSession.OriginalGain(volume, duck, level, playing), 4);

    private static readonly FieldInfo IncomingField = typeof(MainWindow).GetField("_incoming", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static IncomingTranslationSession CreateIncoming(ITranslationProvider provider, AppServices services) =>
        new(provider, new IncomingDevices("unused", "unused", 1, 0.2f, true, 0.3f), services.Logger);

    private static readonly FieldInfo SessionField = typeof(MainWindow).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static MethodInfo Method(string name) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!;

    private static Task Stop(MainWindow window) => (Task)Method("StopSessionAsync").Invoke(window, null)!;

    private static TranslationSession CreateSession(ITranslationProvider provider, AppServices services) =>
        new(provider, new SessionDevices("unused", "unused", null, 0, 0), services.Logger);

    private static Task OnWindowThread(Func<MainWindow, AppServices, Task> test)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                var root = Path.Combine(Path.GetTempPath(), "TraductorVozTeams-lifecycle-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var services = new AppServices(new AppPaths(root));
                    var window = new MainWindow(services);
                    await test(window, services);
                    completion.TrySetResult();
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
                finally
                {
                    if (Directory.Exists(root))
                    {
                        Directory.Delete(root, true);
                    }

                    dispatcher.InvokeShutdown();
                }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private sealed class DelayedStopProvider : ITranslationProvider
    {
        public TaskCompletionSource StopEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowStop { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int StopCalls;
        public int DisposeCalls;
        public PcmFormat InputFormat => PcmFormat.SpeechInput;
        public event EventHandler<TranslationTextEventArgs>? Recognizing { add { } remove { } }
        public event EventHandler<TranslationTextEventArgs>? Recognized { add { } remove { } }
        public event EventHandler<TranslatedAudioEventArgs>? AudioReceived { add { } remove { } }
        public event EventHandler<ProviderErrorEventArgs>? Error { add { } remove { } }
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void PushAudio(byte[] buffer, int count) { }

        public async Task StopAsync()
        {
            Interlocked.Increment(ref StopCalls);
            StopEntered.TrySetResult();
            await AllowStop.Task;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref DisposeCalls);
            return ValueTask.CompletedTask;
        }
    }
}
