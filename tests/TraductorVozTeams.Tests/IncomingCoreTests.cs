using System.Text.Json;
using TraductorVozTeams.Core.Audio;
using TraductorVozTeams.Core.Devices;
using TraductorVozTeams.Core.Metrics;
using TraductorVozTeams.Core.Providers;
using TraductorVozTeams.Core.Settings;

namespace TraductorVozTeams.Tests;

public class SettingsMigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tvt-mig-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void V1FileKeepsOutgoingFieldsAndIsBackedUpOnFirstSave()
    {
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, "settings.json");
        const string v1 = """
            { "SchemaVersion": 1, "InputDeviceId": "mic", "TeamsOutputDeviceId": "lineA", "SourceLanguage": "es-AR", "ActiveProfileId": "p1" }
            """;
        File.WriteAllText(file, v1);
        var store = new SettingsStore(file);

        var settings = store.Load();
        Assert.Equal(1, settings.SchemaVersion);
        Assert.Equal("mic", settings.InputDeviceId);
        Assert.Equal("lineA", settings.TeamsOutputDeviceId);
        Assert.Equal(IncomingSettings.DefaultOriginalVolume, settings.Incoming.OriginalVolume);

        store.Save(settings);

        Assert.Equal(v1, File.ReadAllText(store.BackupFileFor(1)));
        var reloaded = store.Load();
        Assert.Equal(AppSettings.CurrentSchemaVersion, reloaded.SchemaVersion);
        Assert.Equal("mic", reloaded.InputDeviceId);
        Assert.Equal("lineA", reloaded.TeamsOutputDeviceId);
    }

    [Fact]
    public void V2FileStillReadableByV1Shape()
    {
        var json = JsonSerializer.Serialize(new AppSettings { InputDeviceId = "mic" });
        var v1View = JsonSerializer.Deserialize<V1Shape>(json)!;
        Assert.Equal("mic", v1View.InputDeviceId);
    }

    [Fact]
    public void ExistingSettingsUseTheSharedProfileForBothDirectionsUntilChanged()
    {
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, "settings.json");
        File.WriteAllText(file, """
            { "SchemaVersion": 3, "ActiveProfileId": "speech", "InputDeviceId": "mic",
              "Incoming": { "CaptureDeviceId": "b-in", "VoiceName": "es-AR-TomasNeural" },
              "Profiles": [ { "Id": "speech", "Name": "Speech", "ProviderType": "azure-speech" },
                            { "Id": "gpt", "Name": "GPT", "ProviderType": "foundry-realtime-translate" } ] }
            """);
        var store = new SettingsStore(file);

        var settings = store.Load();
        Assert.Equal("speech", settings.ActiveProfile!.Id);
        Assert.Equal("speech", settings.IncomingProfile!.Id);
        Assert.Equal("b-in", settings.Incoming.CaptureDeviceId);
        Assert.Equal("es-AR-TomasNeural", settings.Incoming.VoiceName);

        settings.Incoming.ProfileId = "gpt";
        store.Save(settings);
        var reloaded = store.Load();
        Assert.Equal(AppSettings.CurrentSchemaVersion, reloaded.SchemaVersion);
        Assert.Equal("speech", reloaded.ActiveProfile!.Id);
        Assert.Equal("gpt", reloaded.IncomingProfile!.Id);
        Assert.Equal("mic", reloaded.InputDeviceId);
    }

    [Fact]
    public void ProfilesAreAssignedAndClearedPerDirection()
    {
        var settings = new AppSettings();
        var profiles = new ProfileService(settings, new TraductorVozTeams.Core.Secrets.DpapiSecretStore(Path.Combine(_dir, "secrets")),
            ProviderRegistry.CreateDefault());
        var first = new ProviderProfile { ProviderType = AzureSpeechDescriptor.Type };
        var second = new ProviderProfile { ProviderType = AzureSpeechDescriptor.Type };
        var values = new Dictionary<string, string>
        {
            [AzureSpeechDescriptor.ConnectionModeKey] = "region",
            [AzureSpeechDescriptor.RegionKey] = "eastus",
            [AzureSpeechDescriptor.ApiKeyKey] = "dummy-test-value",
        };
        profiles.SaveProfile(first, values);
        profiles.SaveProfile(second, values);
        Assert.Same(first, settings.ActiveProfile);
        Assert.Same(first, settings.IncomingProfile);

        settings.Incoming.ProfileId = second.Id;
        profiles.DeleteProfile(second);
        Assert.Same(first, settings.ActiveProfile);
        Assert.Null(settings.IncomingProfile);

        settings.Incoming.ProfileId = first.Id;
        profiles.SaveProfile(second, values);
        settings.ActiveProfileId = second.Id;
        profiles.DeleteProfile(first);
        Assert.Same(second, settings.ActiveProfile);
        Assert.Null(settings.IncomingProfile);
    }

    private sealed class V1Shape
    {
        public string? InputDeviceId { get; set; }
    }
}

public class DirectionContextTests
{
    [Fact]
    public void OverridesLanguagesAndVoiceWithoutTouchingBase()
    {
        var baseValues = new Dictionary<string, string>
        {
            [AzureSpeechDescriptor.RegionKey] = "centralus",
            [AzureSpeechDescriptor.TargetLanguageKey] = "en",
            [AzureSpeechDescriptor.VoiceNameKey] = "en-US-JennyNeural",
        };
        var secrets = new Dictionary<string, string> { ["apiKey"] = "x" };
        var ctx = new ProviderSessionContext(baseValues, secrets, "es-AR", 0);

        var incoming = DirectionContext.WithLanguages(ctx, "en-US", "es", "es-AR-ElenaNeural");

        Assert.Equal("en-US", incoming.SourceLanguage);
        Assert.Equal("es", incoming.Values[AzureSpeechDescriptor.TargetLanguageKey]);
        Assert.Equal("es-AR-ElenaNeural", incoming.Values[AzureSpeechDescriptor.VoiceNameKey]);
        Assert.Equal("centralus", incoming.Values[AzureSpeechDescriptor.RegionKey]);
        Assert.Same(secrets, incoming.Secrets);
        Assert.Equal("en", baseValues[AzureSpeechDescriptor.TargetLanguageKey]);
        Assert.Equal("es-AR", ctx.SourceLanguage);
    }
}

public class IncomingRoutingValidatorTests
{
    private static readonly AudioDeviceInfo Mic = new("mic", "Micrófono (Plantronics)", AudioDeviceFlow.Capture);
    private static readonly AudioDeviceInfo LineA = new("lineA", "Line 1 (Virtual Audio Cable)", AudioDeviceFlow.Render);
    private static readonly AudioDeviceInfo LineB = new("lineB", "Line 2 (Virtual Audio Cable)", AudioDeviceFlow.Render);
    private static readonly AudioDeviceInfo MicB = new("micB", "Mic 2 (Virtual Audio Cable)", AudioDeviceFlow.Capture);
    private static readonly AudioDeviceInfo Headset = new("hs", "Auriculares (Plantronics)", AudioDeviceFlow.Render);
    private static readonly string[] Patterns = [.. DeviceRoutingValidator.DefaultVirtualCablePatterns];
    private static readonly DeviceRouting Outgoing = new(Mic, LineA, false, null);

    private static CablePairing Pairing(string outId = "lineB", string capId = "micB", string? aId = "lineA") =>
        new(outId, capId, aId, DateTimeOffset.UtcNow);

    [Fact]
    public void ValidVerifiedRouteHasNoErrors()
    {
        var result = IncomingRoutingValidator.Validate(new(LineB, MicB, Headset, Pairing()), Outgoing, Patterns);
        Assert.True(result.IsValid, string.Join(" | ", result.Errors));
    }

    [Fact]
    public void RequiresPairingForCurrentDevices()
    {
        Assert.False(IncomingRoutingValidator.Validate(new(LineB, MicB, Headset, null), Outgoing, Patterns).IsValid);
        Assert.False(IncomingRoutingValidator.Validate(new(LineB, MicB, Headset, Pairing(capId: "other")), Outgoing, Patterns).IsValid);
        // Si cambia la salida de la dirección saliente, hay que volver a verificar.
        Assert.False(IncomingRoutingValidator.Validate(new(LineB, MicB, Headset, Pairing(aId: "old")), Outgoing, Patterns).IsValid);
    }

    [Fact]
    public void RejectsSharingTheOutgoingCable()
    {
        var result = IncomingRoutingValidator.Validate(new(LineA, MicB, Headset, Pairing("lineA")), Outgoing, Patterns);
        Assert.Contains(result.Errors, e => e.Contains("altavoz de la reunión", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsPhysicalMicAsCaptureAndCableAsHeadset()
    {
        var result = IncomingRoutingValidator.Validate(new(LineB, Mic, LineB, Pairing(capId: "mic")), Outgoing, Patterns);
        Assert.Contains(result.Errors, e => e.Contains("micrófono físico", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Contains("parecen un cable virtual", StringComparison.Ordinal));
    }

    [Fact]
    public void WarnsWhenListeningOnSpeakers()
    {
        var speakers = new AudioDeviceInfo("spk", "Altavoces (Realtek)", AudioDeviceFlow.Render);
        var result = IncomingRoutingValidator.Validate(new(LineB, MicB, speakers, Pairing()), Outgoing, Patterns);
        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Contains("altavoz", StringComparison.Ordinal));
    }
}

public class ToneSignatureTests
{
    [Fact]
    public void RandomSignatureRespectsRangeAndSpacing()
    {
        for (var seed = 0; seed < 50; seed++)
        {
            var sig = ToneSignature.CreateRandom(new Random(seed));
            Assert.Equal(3, sig.Frequencies.Count);
            Assert.All(sig.Frequencies, f => Assert.InRange(f, 700, 3000));
            foreach (var a in sig.Frequencies)
            {
                Assert.All(sig.Frequencies.Where(b => b != a), b => Assert.True(Math.Abs(a - b) >= 200));
            }
        }
    }

    [Fact]
    public void DetectsOwnSignatureWithDelayAttenuationAndNoise()
    {
        var sig = ToneSignature.CreateRandom(new Random(7));
        var rendered = sig.Render();
        var rng = new Random(1);
        var captured = new byte[rendered.Length + 16000];
        var offset = 4000 * 2 + 2; // demora arbitraria, alineada a muestra
        for (var i = 0; i < captured.Length / 2; i++)
        {
            var src = i - offset / 2;
            var value = src >= 0 && src < rendered.Length / 2 ? (short)(rendered[2 * src] | (rendered[2 * src + 1] << 8)) * 0.3 : 0;
            value += (rng.NextDouble() - 0.5) * 200;
            var s = (short)value;
            captured[2 * i] = (byte)s;
            captured[2 * i + 1] = (byte)(s >> 8);
        }

        var result = sig.Detect(captured);
        Assert.True(result.Detected);
        Assert.Equal(3, result.TonesFound);
    }

    [Fact]
    public void DoesNotDetectDifferentSignatureOrSilence()
    {
        var a = new ToneSignature([800, 1500, 2200]);
        var b = new ToneSignature([1100, 1800, 2600]);

        Assert.False(a.Detect(b.Render()).Detected);
        Assert.False(a.Detect(new byte[32000]).Detected);
    }

    [Fact]
    public void RequiresTonesInOrder()
    {
        var a = new ToneSignature([800, 1500, 2200]);
        var reversed = new ToneSignature([2200, 1500, 800]);
        Assert.False(a.Detect(reversed.Render()).Detected);
    }
}

public class UsageMeterTests
{
    [Fact]
    public void MeasuresAudioSentAndTextPerDirection()
    {
        var now = DateTimeOffset.Parse("2026-11-17T20:00:00Z");
        var meter = new UsageMeter("entrante", () => now);
        meter.Start(PcmFormat.SpeechInput.AverageBytesPerSecond);
        meter.AddAudio(32000 * 90);
        meter.AddUtterance("Hello there", "Hola");
        now = now.AddMinutes(2);

        var s = meter.Stop();

        Assert.Equal(TimeSpan.FromSeconds(90), s.AudioSent);
        Assert.Equal(TimeSpan.FromMinutes(2), s.Active);
        Assert.Equal(1, s.Utterances);
        Assert.Equal(11, s.SourceCharacters);
        Assert.Equal(4, s.TranslatedCharacters);
        Assert.StartsWith("entrante,2026-11-17T20:00:00", UsageMeter.ToCsvLine(s), StringComparison.Ordinal);
    }
}
