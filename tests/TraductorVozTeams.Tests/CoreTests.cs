using System.Text;
using TraductorVozTeams.Core.Audio;
using TraductorVozTeams.Core.Devices;
using TraductorVozTeams.Core.Metrics;
using TraductorVozTeams.Core.Providers;
using TraductorVozTeams.Core.Secrets;
using TraductorVozTeams.Core.Settings;

namespace TraductorVozTeams.Tests;

public class WavParserTests
{
    private static byte[] Wav(int sampleRate, short bits, short channels, byte[] pcm, short formatTag = 1)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + pcm.Length);
        w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        w.Write(16);
        w.Write(formatTag);
        w.Write(channels);
        w.Write(sampleRate);
        w.Write(sampleRate * channels * bits / 8);
        w.Write((short)(channels * bits / 8));
        w.Write(bits);
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(pcm.Length);
        w.Write(pcm);
        return ms.ToArray();
    }

    [Fact]
    public void ParsesWavHeaderAndStripsIt()
    {
        var wav = Wav(24000, 16, 1, [1, 2, 3, 4]);
        var chunk = WavParser.Parse(wav, PcmFormat.SpeechInput);

        Assert.True(WavParser.IsCompleteWave(wav));
        Assert.Equal(new PcmFormat(24000, 16, 1), chunk.Format);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, chunk.Data);
    }

    [Fact]
    public void RawPcmUsesFallbackFormat()
    {
        var chunk = WavParser.Parse([9, 8, 7, 6], PcmFormat.SpeechInput);

        Assert.Equal(PcmFormat.SpeechInput, chunk.Format);
        Assert.Equal(4, chunk.Data.Length);
    }

    [Fact]
    public void RejectsNonPcmWav() =>
        Assert.Throws<NotSupportedException>(() => WavParser.Parse(Wav(16000, 32, 1, [0, 0, 0, 0], formatTag: 3), PcmFormat.SpeechInput));

    [Theory]
    [InlineData("raw")]
    [InlineData("truncated")]
    [InlineData("streaming-zero")]
    [InlineData("streaming-max")]
    public void DoesNotTreatPartialOrRawAudioAsACompleteUtterance(string kind)
    {
        var wav = Wav(16000, 16, 1, [1, 2, 3, 4]);
        var data = kind switch
        {
            "raw" => new byte[] { 1, 2, 3, 4 },
            "truncated" => wav[..^2],
            _ => wav,
        };
        if (kind.StartsWith("streaming", StringComparison.Ordinal))
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                data.AsSpan(4, 4), kind == "streaming-zero" ? 0u : uint.MaxValue);
        }

        Assert.False(WavParser.IsCompleteWave(data));
    }
}

public class DeviceRoutingValidatorTests
{
    private static readonly IReadOnlyList<string> Patterns = DeviceRoutingValidator.DefaultVirtualCablePatterns;
    private static readonly AudioDeviceInfo Mic = new("mic", "Micrófono (USB)", AudioDeviceFlow.Capture);
    private static readonly AudioDeviceInfo VacIn = new("vac-in", "Line 1 (Virtual Audio Cable)", AudioDeviceFlow.Capture);
    private static readonly AudioDeviceInfo VacOut = new("vac-out", "Line 1 (Virtual Audio Cable)", AudioDeviceFlow.Render);
    private static readonly AudioDeviceInfo Headphones = new("hp", "Auriculares (Realtek)", AudioDeviceFlow.Render);

    [Fact]
    public void ValidRoutingWithHeadphoneMonitor() =>
        Assert.True(DeviceRoutingValidator.Validate(new DeviceRouting(Mic, VacOut, true, Headphones), Patterns).IsValid);

    [Fact]
    public void CapturingFromVirtualCableIsRejected() =>
        Assert.False(DeviceRoutingValidator.Validate(new DeviceRouting(VacIn, VacOut, false, null), Patterns).IsValid);

    [Fact]
    public void MonitorOnSameDeviceAsTeamsOutputIsRejected() =>
        Assert.False(DeviceRoutingValidator.Validate(new DeviceRouting(Mic, VacOut, true, VacOut), Patterns).IsValid);

    [Fact]
    public void NonVirtualTeamsOutputOnlyWarns()
    {
        var result = DeviceRoutingValidator.Validate(new DeviceRouting(Mic, Headphones, false, null), Patterns);

        Assert.True(result.IsValid);
        Assert.NotEmpty(result.Warnings);
    }
}

public class AzureSpeechDescriptorTests
{
    private static readonly HashSet<string> WithKey = [AzureSpeechDescriptor.ApiKeyKey];

    private static Dictionary<string, string> Values(params (string Key, string Value)[] extra)
    {
        var values = new Dictionary<string, string>
        {
            [AzureSpeechDescriptor.ConnectionModeKey] = AzureSpeechDescriptor.ModeRegion,
            [AzureSpeechDescriptor.RegionKey] = "eastus",
            [AzureSpeechDescriptor.VoiceNameKey] = "en-US-JennyNeural",
            [AzureSpeechDescriptor.TargetLanguageKey] = "en",
        };
        foreach (var (key, value) in extra)
        {
            values[key] = value;
        }

        return values;
    }

    [Fact]
    public void RegionModeWithKeyIsValid() => Assert.Empty(new AzureSpeechDescriptor().Validate(Values(), WithKey));

    [Fact]
    public void MissingKeyIsReported() => Assert.NotEmpty(new AzureSpeechDescriptor().Validate(Values(), new HashSet<string>()));

    [Theory]
    [InlineData("http://ejemplo.cognitiveservices.azure.com/", false)]
    [InlineData("no-es-url", false)]
    [InlineData("https://ejemplo.cognitiveservices.azure.com/", true)]
    public void EndpointModeRequiresSecureAbsoluteUrl(string endpoint, bool valid)
    {
        var values = Values((AzureSpeechDescriptor.ConnectionModeKey, AzureSpeechDescriptor.ModeEndpoint), (AzureSpeechDescriptor.EndpointKey, endpoint));

        Assert.Equal(valid, new AzureSpeechDescriptor().Validate(values, WithKey).Count == 0);
    }

    [Fact]
    public void FutureProvidersAreListedButUnavailable()
    {
        var future = ProviderRegistry.CreateDefault().All.Where(d => d.ProviderType is "azure-openai" or "openai").ToList();

        Assert.NotEmpty(future);
        Assert.All(future, d => Assert.False(d.IsAvailable));
    }
}

public sealed class CredentialStorageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tvt-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    [Fact]
    public void DpapiStoreEncryptsRoundTripsAndDeletes()
    {
        var store = new DpapiSecretStore(_dir);
        store.Set("p.apiKey", "clave-super-secreta");

        var file = Directory.GetFiles(_dir).Single();
        Assert.DoesNotContain("clave-super-secreta", File.ReadAllText(file));
        Assert.Equal("clave-super-secreta", store.Get("p.apiKey"));
        Assert.True(store.Delete("p.apiKey"));
        Assert.False(store.Exists("p.apiKey"));
    }

    [Fact]
    public void ProfileSecretsNeverReachSettingsJsonAndAreDeletedWithProfile()
    {
        var paths = new AppPaths(_dir);
        var settings = new AppSettings();
        var service = new ProfileService(settings, new DpapiSecretStore(paths.SecretsDirectory), ProviderRegistry.CreateDefault());
        var profile = new ProviderProfile { ProviderType = AzureSpeechDescriptor.Type, Name = "Prueba" };
        var form = new Dictionary<string, string>
        {
            [AzureSpeechDescriptor.ConnectionModeKey] = AzureSpeechDescriptor.ModeRegion,
            [AzureSpeechDescriptor.RegionKey] = "eastus",
            [AzureSpeechDescriptor.ApiKeyKey] = "clave-super-secreta",
            [AzureSpeechDescriptor.VoiceNameKey] = "en-US-JennyNeural",
            [AzureSpeechDescriptor.TargetLanguageKey] = "en",
        };

        Assert.Empty(service.Validate(profile, form));
        service.SaveProfile(profile, form);
        new SettingsStore(paths.SettingsFile).Save(settings);

        Assert.DoesNotContain("clave-super-secreta", File.ReadAllText(paths.SettingsFile));
        Assert.False(profile.Values.ContainsKey(AzureSpeechDescriptor.ApiKeyKey));
        Assert.Equal("clave-super-secreta", service.LoadSecrets(profile)[AzureSpeechDescriptor.ApiKeyKey]);

        form[AzureSpeechDescriptor.ApiKeyKey] = string.Empty;
        service.SaveProfile(profile, form);
        Assert.True(service.HasSecret(profile, AzureSpeechDescriptor.ApiKeyKey));

        service.DeleteProfile(profile);
        Assert.False(service.HasSecret(profile, AzureSpeechDescriptor.ApiKeyKey));
        Assert.Empty(settings.Profiles);
    }
}

public class LatencyTrackerTests
{
    [Fact]
    public void PercentileInterpolatesLinearly()
    {
        double[] values = [100, 200, 300, 400, 500];

        Assert.Equal(300, LatencyTracker.Percentile(values, 0.5));
        Assert.Equal(480, LatencyTracker.Percentile(values, 0.95), 6);
    }

    [Fact]
    public void MeasuresFromSpeechEndToFirstAudioPerUtterance()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var tracker = new LatencyTracker();
        tracker.MarkStreamStarted(start);

        tracker.OnRecognized(start.AddSeconds(3), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1.5), "hola", "hello");
        var first = tracker.OnAudio(start.AddSeconds(3.2), false);
        Assert.Null(tracker.OnAudio(start.AddSeconds(3.3), false));
        tracker.OnAudio(start.AddSeconds(4), true);

        tracker.OnRecognized(start.AddSeconds(8), TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(1), "adiós", "goodbye");
        var second = tracker.OnAudio(start.AddSeconds(8.5), false);

        Assert.Equal(TimeSpan.FromMilliseconds(700), first!.EndToEnd);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), second!.EndToEnd);
        Assert.Equal(2, tracker.GetSummary().Count);
    }
}

public class MeasurementTests
{
    [Fact]
    public void WordErrorRateIgnoresCaseAccentsAndPunctuation()
    {
        Assert.Equal(0, WordErrorRate.Compute("¿Pueden ver mi pantalla?", "pueden ver mi pantalla"));
        Assert.Equal(0, WordErrorRate.Compute("reunión", "Reunion."));
    }

    [Fact]
    public void WordErrorRateCountsSubstitutionsInsertionsAndDeletions()
    {
        Assert.Equal(0.25, WordErrorRate.Compute("el informe está listo", "el reporte está listo"));
        Assert.Equal(0.5, WordErrorRate.Compute("buenos días", "buenos"));
        Assert.Equal(0.5, WordErrorRate.Compute("buenos días", "muy buenos días"));
    }

    [Fact]
    public void SummarizeReportsPercentilesAndHandlesEmpty()
    {
        var summary = MeasurementStats.Summarize([400, 100, 300, 200]);

        Assert.Equal(4, summary.Count);
        Assert.Equal(100, summary.Min);
        Assert.Equal(250, summary.Median);
        Assert.Equal(385, summary.P95, 3);
        Assert.Equal(400, summary.Max);
        Assert.Equal(0, MeasurementStats.Summarize([]).Count);
    }
}
public class VoiceFilterTests
{
    private static TranslationVoice V(string name, string locale) => new(name, name, locale, "mujer", false);

    [Fact]
    public void KeepsTargetLanguageVoicesWithPrimaryLocaleFirstAndSkipsHdAndOthers()
    {
        var voices = new[]
        {
            V("en-GB-SoniaNeural", "en-GB"),
            V("es-AR-ElenaNeural", "es-AR"),
            V("en-US-JennyNeural", "en-US"),
            V("en-US-Ava:DragonHDLatestNeural", "en-US"),
            V("en-US-JennyNeural", "en-US"),
        };

        var result = VoiceFilter.ForTargetLanguage(voices, "en");

        Assert.Equal(["en-US-JennyNeural", "en-GB-SoniaNeural"], result.Select(v => v.Name));
    }
}
