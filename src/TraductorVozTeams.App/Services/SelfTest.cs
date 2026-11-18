using System.Text;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.CognitiveServices.Speech.Translation;
using TraductorVozTeams.App.Audio;
using TraductorVozTeams.Core.Audio;
using TraductorVozTeams.Core.Devices;
using TraductorVozTeams.Core.Providers;
using TraductorVozTeams.Core.Secrets;
using TraductorVozTeams.Core.Settings;

namespace TraductorVozTeams.App.Services;

/// <summary>
/// Autodiagnóstico sin red ni credenciales: <c>TraductorVozTeams.exe --selftest [archivo]</c>.
/// Verifica que el ejecutable carga el Speech SDK nativo, enumera audio y cifra con DPAPI.
/// </summary>
public static class SelfTest
{
    public static int Run(string? reportPath)
    {
        var report = new StringBuilder();
        var ok = true;

        void Check(string name, Func<string> action)
        {
            try
            {
                var result = action();
                report.AppendLine($"[OK]    {name}: {result}");
            }
            catch (Exception ex)
            {
                ok = false;
                report.AppendLine($"[FALLO] {name}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        report.AppendLine($"Ejecutable: {Environment.ProcessPath}");
        report.AppendLine($"Proceso 64 bits: {Environment.Is64BitProcess}; SO: {Environment.OSVersion.VersionString}");

        Check("Speech SDK (bibliotecas nativas)", () =>
        {
            var config = SpeechTranslationConfig.FromSubscription("selftest-sin-clave", "eastus");
            config.SpeechRecognitionLanguage = "es-AR";
            config.AddTargetLanguage("en");
            config.VoiceName = "en-US-JennyNeural";
            using var stream = AudioInputStream.CreatePushStream(AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1));
            using var audio = AudioConfig.FromStreamInput(stream);
            using var recognizer = new TranslationRecognizer(config, audio);
            return "TranslationRecognizer creado";
        });

        Check("Visual C++ Redistributable x64", () => SystemHelpers.IsVcRedistInstalled()
            ? "instalado"
            : throw new InvalidOperationException("no detectado; instala " + SystemHelpers.VcRedistUrl));

        Check("Dispositivos de audio", () =>
        {
            var inputs = AudioDeviceService.List(AudioDeviceFlow.Capture);
            var outputs = AudioDeviceService.List(AudioDeviceFlow.Render);
            var patterns = DeviceRoutingValidator.DefaultVirtualCablePatterns;
            var vac = outputs.Count(d => DeviceRoutingValidator.IsVirtualCable(d, patterns));
            return $"{inputs.Count} entradas, {outputs.Count} salidas, {vac} salida(s) de cable virtual detectada(s)";
        });

        Check("Salida WASAPI (silencio en dispositivo predeterminado)", () =>
        {
            using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            var device = enumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
            var name = device.FriendlyName;
            using var output = new AudioOutput(device, Core.Audio.PcmFormat.SpeechInput, 0f);
            output.Enqueue(new Core.Audio.AudioChunk(new byte[Core.Audio.PcmFormat.SpeechInput.AverageBytesPerSecond / 5], Core.Audio.PcmFormat.SpeechInput));
            Thread.Sleep(300);
            return name;
        });

        Check("Captura WASAPI y conversión a 16 kHz mono", () =>
        {
            using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            var device = enumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.Role.Communications);
            var name = device.FriendlyName;
            using var capture = new MicrophoneCapture(device, Core.Audio.PcmFormat.SpeechInput);
            var bytes = 0;
            capture.DataReady += (_, count) => Interlocked.Add(ref bytes, count);
            capture.Start();
            Thread.Sleep(600);
            capture.Stop();
            return bytes > 0
                ? $"{name}: {capture.SourceFormatDescription} → {bytes} bytes PCM 16 kHz"
                : throw new InvalidOperationException($"{name}: no se recibió audio");
        });

        Check("Inicio y cierre de sesion desde STA (como la ventana WPF)", () =>
        {
            Exception? failure = null;
            var directory = Path.Combine(Path.GetTempPath(), "TraductorVozTeams-audio-" + Guid.NewGuid().ToString("N"));
            var thread = new Thread(() =>
            {
                try
                {
                    using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                    using var input = enumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.Role.Communications);
                    using var output = enumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
                    for (var attempt = 0; attempt < 2; attempt++)
                    {
                        var provider = new LocalTestProvider();
                        var devices = new SessionDevices(input.ID, output.ID, output.ID, 0f, 0f);
                        var session = new TranslationSession(provider, devices, new FileLogger(directory));
                        try
                        {
                            session.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
                            Task.Run(() => provider.SendSilence(new PcmFormat(24000, 16, 1))).GetAwaiter().GetResult();
                            Thread.Sleep(400);
                            session.SetVolumes(0f, 0f);
                            if (provider.BytesReceived == 0)
                            {
                                throw new InvalidOperationException("No se recibio audio del microfono en la sesion local.");
                            }
                        }
                        finally
                        {
                            try
                            {
                                session.StopAsync().GetAwaiter().GetResult();
                            }
                            finally
                            {
                                session.DisposeAsync().AsTask().GetAwaiter().GetResult();
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            try
            {
                if (failure is not null)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                }

                return "2 ciclos con microfono, salida y monitor silenciados y cambio de formato; sin red ni credenciales";
            }
            finally
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }
            }
        });

        Check("Dirección entrante: inicio y cierre desde STA con original y traducción", () =>
        {
            Exception? failure = null;
            var directory = Path.Combine(Path.GetTempPath(), "TraductorVozTeams-incoming-" + Guid.NewGuid().ToString("N"));
            var thread = new Thread(() =>
            {
                try
                {
                    using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                    using var input = enumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.Role.Communications);
                    using var output = enumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
                    for (var attempt = 0; attempt < 2; attempt++)
                    {
                        var provider = new LocalTestProvider();
                        var session = new IncomingTranslationSession(provider,
                            new IncomingDevices(input.ID, output.ID, 0f, 0.001f, true, 0.3f), new FileLogger(directory));
                        try
                        {
                            session.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
                            // El original se encola a un volumen inaudible (0,1 %) para ejercitar la mezcla sin sonar por el altavoz.
                            Task.Run(() => provider.SendSilence(new PcmFormat(24000, 16, 1))).GetAwaiter().GetResult();
                            Thread.Sleep(400);
                            session.SetVolumes(0f, 0f, true, 0.3f);
                            if (provider.BytesReceived == 0)
                            {
                                throw new InvalidOperationException("No se recibió audio de captura en la sesión entrante.");
                            }
                        }
                        finally
                        {
                            try
                            {
                                var usage = session.StopAsync().GetAwaiter().GetResult();
                                if (usage.AudioSent <= TimeSpan.Zero)
                                {
                                    throw new InvalidOperationException("El medidor de consumo no registró audio.");
                                }
                            }
                            finally
                            {
                                session.DisposeAsync().AsTask().GetAwaiter().GetResult();
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            try
            {
                if (failure is not null)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                }

                return "2 ciclos (captura, auriculares, original y traducción en silencio, consumo medido); sin red ni credenciales";
            }
            finally
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }
            }
        });

        Check("Dirección entrante: configuración guardada", () =>
        {
            var services = new AppServices(AppPaths.Default());
            var incoming = services.Settings.Incoming;
            var outputs = AudioDeviceService.List(AudioDeviceFlow.Render);
            var inputs = AudioDeviceService.List(AudioDeviceFlow.Capture);
            var routing = new IncomingRouting(
                outputs.FirstOrDefault(d => d.Id == incoming.MeetingOutputDeviceId),
                inputs.FirstOrDefault(d => d.Id == incoming.CaptureDeviceId),
                outputs.FirstOrDefault(d => d.Id == incoming.ListenDeviceId),
                incoming.VerifiedPairing);
            var outgoing = new DeviceRouting(
                inputs.FirstOrDefault(d => d.Id == services.Settings.InputDeviceId),
                outputs.FirstOrDefault(d => d.Id == services.Settings.TeamsOutputDeviceId),
                services.Settings.MonitorEnabled,
                outputs.FirstOrDefault(d => d.Id == services.Settings.MonitorDeviceId));
            var validation = IncomingRoutingValidator.Validate(routing, outgoing, services.Settings.VirtualCablePatterns);
            // Informativo: no hace fallar el autodiagnóstico si la dirección entrante aún no se configuró.
            return validation.IsValid
                ? $"lista (original {incoming.EffectiveOriginalVolume:P0}, voz {incoming.VoiceName})"
                : "pendiente: " + string.Join(" | ", validation.Errors);
        });

        Check("Cifrado DPAPI de credenciales", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "TraductorVozTeams-selftest-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new DpapiSecretStore(dir);
                store.Set("selftest.apiKey", "valor-de-prueba");
                var raw = File.ReadAllText(Path.Combine(dir, "selftest.apiKey.bin"));
                if (raw.Contains("valor-de-prueba", StringComparison.Ordinal) || store.Get("selftest.apiKey") != "valor-de-prueba")
                {
                    throw new InvalidOperationException("el valor no se cifró o no se recuperó");
                }

                store.Delete("selftest.apiKey");
                return "cifrar/descifrar/eliminar correcto";
            }
            finally
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
        });

        Check("Datos fuera de la carpeta del ejecutable", () =>
        {
            var exeDir = Path.GetFullPath(AppContext.BaseDirectory);
            var dataDir = Path.GetFullPath(AppPaths.Default().Root);
            return dataDir.StartsWith(exeDir, StringComparison.OrdinalIgnoreCase)
                ? throw new InvalidOperationException($"{dataDir} está dentro de {exeDir}")
                : dataDir;
        });

        report.AppendLine(ok ? "RESULTADO: OK" : "RESULTADO: FALLO");
        var path = reportPath ?? Path.Combine(Path.GetTempPath(), "TraductorVozTeams-selftest.txt");
        File.WriteAllText(path, report.ToString(), Encoding.UTF8);
        return ok ? 0 : 1;
    }

    private sealed class LocalTestProvider : ITranslationProvider
    {
        private int _bytesReceived;

        public PcmFormat InputFormat => PcmFormat.SpeechInput;

        public int BytesReceived => Volatile.Read(ref _bytesReceived);

        public event EventHandler<TranslationTextEventArgs>? Recognizing { add { } remove { } }
        public event EventHandler<TranslationTextEventArgs>? Recognized { add { } remove { } }
        public event EventHandler<ProviderErrorEventArgs>? Error { add { } remove { } }
        public event EventHandler<TranslatedAudioEventArgs>? AudioReceived;

        public Task StartAsync(CancellationToken cancellationToken) => Task.Delay(20, cancellationToken);

        public void PushAudio(byte[] buffer, int count) => Interlocked.Add(ref _bytesReceived, count);

        public void SendSilence(PcmFormat format) =>
            AudioReceived?.Invoke(this, new TranslatedAudioEventArgs(new AudioChunk(new byte[format.AverageBytesPerSecond / 5], format)));

        public Task StopAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
