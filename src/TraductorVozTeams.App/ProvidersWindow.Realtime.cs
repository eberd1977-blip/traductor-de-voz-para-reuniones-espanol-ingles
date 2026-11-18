using System.Windows;
using System.Windows.Controls;
using TraductorVozTeams.App.Audio;
using TraductorVozTeams.App.Providers;
using TraductorVozTeams.App.Services;
using TraductorVozTeams.Core.Audio;
using TraductorVozTeams.Core.Devices;
using TraductorVozTeams.Core.Providers;

namespace TraductorVozTeams.App;

public partial class ProvidersWindow
{
    private async void OnVerifyGpt(object sender, RoutedEventArgs e)
    {
        if (_current is not { } profile || _isNew || !TryPrepare(out var values))
            return;
        if (!string.IsNullOrWhiteSpace(ProviderValues.Get(values, RealtimeTranslateDescriptor.ApiKey))
            || RealtimeTranslateDescriptor.ResourceOrigin(values) != RealtimeTranslateDescriptor.ResourceOrigin(profile.Values)
            || ProviderValues.Get(values, RealtimeTranslateDescriptor.DeploymentKey) != ProviderValues.Get(profile.Values, RealtimeTranslateDescriptor.DeploymentKey))
        {
            ShowMessage("Guarda el nuevo endpoint, despliegue o clave antes de verificar.", error: true);
            return;
        }

        SetTesting(true);
        try
        {
            var (spanish, english) = RealtimeVerificationSamples.Load();
            var output = PickVerificationOutput();
            if (output is null)
                return;
            var seconds = (spanish.Length + english.Length) / (double)RealtimeTranslateProvider.TranslationPcm.AverageBytesPerSecond + 4;
            var estimatedSessionSeconds = Math.Ceiling(seconds + 2 * (10 + 15));
            if (MessageBox.Show(this,
                    $"Se abrirán dos sesiones en Foundry y se enviarán muestras sintéticas integradas en español e inglés (aprox. {seconds:0.0} s de audio en total, con silencios). " +
                    $"No se activa el micrófono ni se usa el cable de reunión. Estimación antes de redondeos: tarifa por hora × {estimatedSessionSeconds:0}/3600; " +
                    "consulta la tarifa y el tiempo facturable de tu recurso. " +
                    "No se envía audio de participantes. ¿Autorizas el envío y el posible cargo?",
                    "Verificación de voz", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;

            var context = _services.CreateContext(profile);
            ShowMessage("Verificando español → inglés…", error: false);
            var toEnglish = await TranslateSampleAsync(context with { RealtimeTarget = "en" }, spanish);
            ShowMessage("Verificando inglés → español…", error: false);
            var toSpanish = await TranslateSampleAsync(context with { RealtimeTarget = "es" }, english);
            ShowMessage("Reproduciendo primero inglés y luego español en el dispositivo local…", error: false);
            using (var player = new AudioOutput(AudioDeviceService.Open(output.Id), RealtimeTranslateProvider.TranslationPcm, 0.65f))
            {
                foreach (var clip in new[] { toEnglish, toSpanish })
                {
                    player.Enqueue(clip);
                    await Task.Delay(clip.Duration + TimeSpan.FromMilliseconds(400));
                }
            }
            if (MessageBox.Show(this, "¿Escuchaste primero la traducción en inglés y después la traducción en español?",
                    "Confirmar ambas traducciones", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                ShowMessage("No se confirmó la voz en ambos sentidos.", error: true);
                return;
            }
            _services.Profiles.ConfirmGptVoice(profile);
            GptVerificationText.Text = "Voz verificada para este perfil.";
            ShowMessage("GPT queda habilitado para reuniones con este perfil.", error: false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or FormatException or ArgumentException
                                   or System.Security.Cryptography.CryptographicException or System.Runtime.InteropServices.COMException)
        {
            ShowMessage(ex is RealtimeProtocolException ? ex.Message
                : "No se pudo completar la verificación. Revisa la aplicación, la credencial y el dispositivo local.", error: true);
        }
        finally
        {
            SetTesting(false);
        }
    }

    private AudioDeviceInfo? PickVerificationOutput()
    {
        var settings = _services.Settings;
        var outputs = AudioDeviceService.List(AudioDeviceFlow.Render).Where(device =>
            device.Id != settings.TeamsOutputDeviceId && device.Id != settings.Incoming.MeetingOutputDeviceId
            && !DeviceRoutingValidator.IsVirtualCable(device, settings.VirtualCablePatterns)).ToList();
        if (outputs.Count == 0)
            throw new RealtimeProtocolException("No se encontraron salidas locales para escuchar la prueba.");
        var picker = new Window
        {
            Owner = this, Title = "Salida local de la prueba", WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.Height, Width = 480
        };
        var panel = new StackPanel { Margin = new Thickness(12) };
        var devices = new ComboBox { ItemsSource = outputs };
        var ok = new Button { Content = "Usar estos auriculares", IsDefault = true, IsEnabled = false };
        devices.SelectionChanged += (_, _) => ok.IsEnabled = devices.SelectedItem is AudioDeviceInfo;
        ok.Click += (_, _) => picker.DialogResult = true;
        panel.Children.Add(new TextBlock { Text = "Elige tus auriculares físicos. No selecciones un cable ni una salida compartida con la reunión.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(devices);
        panel.Children.Add(ok);
        panel.Children.Add(new Button { Content = "Cancelar", IsCancel = true });
        picker.Content = panel;
        return picker.ShowDialog() == true ? devices.SelectedItem as AudioDeviceInfo : null;
    }

    private async Task<AudioChunk> TranslateSampleAsync(ProviderSessionContext context, byte[] input)
    {
        var factory = _services.Factories[RealtimeTranslateDescriptor.Type];
        await using var provider = factory.Create(context);
        using var output = new MemoryStream();
        var maxBytes = provider.OutputFormat.AverageBytesPerSecond * 25;
        string? failure = null;
        provider.Error += (_, e) => Volatile.Write(ref failure, e.Message);
        provider.AudioReceived += (_, e) =>
        {
            if (output.Length + e.Chunk.Data.Length > maxBytes)
                Volatile.Write(ref failure, "La prueba devolvió más de 25 segundos de audio.");
            else if (Volatile.Read(ref failure) is null)
                output.Write(e.Chunk.Data);
        };
        await provider.StartAsync(CancellationToken.None);
        // Stream at capture speed, including silence, instead of uploading the recording in one burst.
        var block = new byte[provider.InputFormat.AverageBytesPerSecond / 10];
        var streamLength = input.Length + 2 * provider.InputFormat.AverageBytesPerSecond;
        for (var offset = 0; offset < streamLength && Volatile.Read(ref failure) is null; offset += block.Length)
        {
            Array.Clear(block);
            if (offset < input.Length)
                input.AsSpan(offset, Math.Min(input.Length - offset, block.Length)).CopyTo(block);
            provider.PushAudio(block, Math.Min(block.Length, streamLength - offset));
            await Task.Delay(100);
        }
        await provider.StopAsync();
        if (failure is not null)
            throw new RealtimeProtocolException(failure);
        var pcm = output.ToArray();
        if (pcm.Length < provider.OutputFormat.AverageBytesPerSecond / 5 || !RealtimeVerificationSamples.HasSignal(pcm))
            throw new RealtimeProtocolException($"Foundry no devolvió voz traducida audible para el destino «{context.RealtimeTarget}».");
        return new(pcm, provider.OutputFormat);
    }
}
