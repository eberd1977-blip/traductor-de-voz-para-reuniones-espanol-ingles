using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using TraductorVozTeams.App.Providers;
using TraductorVozTeams.App.Services;
using TraductorVozTeams.Core.Providers;
using TraductorVozTeams.Core.Settings;

namespace TraductorVozTeams.Tests;

public sealed class RealtimeTranslateTests
{
    [Fact]
    public void VerificationUsesIncludedAudibleSamplesWithoutExternalFiles()
    {
        var (spanish, english) = RealtimeVerificationSamples.Load();
        var bytesPerSecond = RealtimeTranslateProvider.TranslationPcm.AverageBytesPerSecond;
        foreach (var sample in new[] { spanish, english })
        {
            Assert.InRange(sample.Length, bytesPerSecond / 2, bytesPerSecond * 6);
            Assert.Equal(0, sample.Length % RealtimeTranslateProvider.TranslationPcm.BlockAlign);
            Assert.Contains(sample, b => b != 0);
        }
        Assert.False(spanish.SequenceEqual(english));
    }

    [Fact]
    public async Task BothDirectionsUseDedicatedSessionsAndDeliverOnlyTheirOwnAudio()
    {
        using var a = new FakeSocket();
        using var b = new FakeSocket();
        await using var outgoing = Provider("en", a);
        await using var incoming = Provider("es", b);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var aOutput = new TaskCompletionSource<TranslatedAudioEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bOutput = new TaskCompletionSource<TranslatedAudioEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var caption = new TaskCompletionSource<TranslationTextEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        outgoing.AudioReceived += (_, e) => aOutput.TrySetResult(e);
        incoming.AudioReceived += (_, e) => bOutput.TrySetResult(e);
        incoming.Recognized += (_, e) => caption.TrySetResult(e);

        await Task.WhenAll(outgoing.StartAsync(deadline.Token), incoming.StartAsync(deadline.Token));
        Assert.Contains("\"language\":\"en\"", await a.Sent.Reader.ReadAsync(deadline.Token));
        Assert.Contains("\"language\":\"es\"", await b.Sent.Reader.ReadAsync(deadline.Token));
        outgoing.PushAudio([1, 2], 2);
        incoming.PushAudio([3, 4], 2);
        using var sentA = JsonDocument.Parse(await a.Sent.Reader.ReadAsync(deadline.Token));
        using var sentB = JsonDocument.Parse(await b.Sent.Reader.ReadAsync(deadline.Token));
        Assert.Equal("session.input_audio_buffer.append", sentA.RootElement.GetProperty("type").GetString());
        Assert.Equal(new byte[] { 1, 2 }, sentA.RootElement.GetProperty("audio").GetBytesFromBase64());
        Assert.Equal(new byte[] { 3, 4 }, sentB.RootElement.GetProperty("audio").GetBytesFromBase64());
        a.Emit("""{"type":"session.output_audio.delta","delta":"BQY="}""", fragmented: true);
        Assert.Equal(new byte[] { 5, 6 }, (await aOutput.Task.WaitAsync(deadline.Token)).Chunk.Data);
        Assert.False(bOutput.Task.IsCompleted);
        await outgoing.StopAsync();
        Assert.Contains("session.close", await a.Sent.Reader.ReadAsync(deadline.Token));
        Assert.Equal(WebSocketState.Open, b.State);
        b.Emit("""{"type":"response.text.delta","text":"Hola"}""");
        b.Emit("""{"type":"session.output_audio.delta","delta":"Bwg="}""");
        var audio = await bOutput.Task.WaitAsync(deadline.Token);
        Assert.Equal(new byte[] { 7, 8 }, audio.Chunk.Data);
        Assert.Equal(new TraductorVozTeams.Core.Audio.PcmFormat(24000, 16, 1), audio.Chunk.Format);
        var text = await caption.Task.WaitAsync(deadline.Token);
        Assert.True(text.IsContinuousDelta);
        Assert.Equal("Hola", text.TranslatedText);
        Assert.Empty(text.SourceText);
        await incoming.StopAsync();
    }

    [Theory]
    [InlineData("""{"type":"session.output_audio.delta","delta":"AQ=="}""")]
    [InlineData("""{"type":"error","error":{"message":"sensitive test detail"}}""")]
    [InlineData(null)]
    public async Task MalformedPcmServiceErrorsAndDisconnectAreFatalWithoutLoggingPayload(string? frame)
    {
        using var socket = new FakeSocket();
        await using var provider = Provider("es", socket);
        var error = new TaskCompletionSource<ProviderErrorEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.Error += (_, e) => error.TrySetResult(e);
        await provider.StartAsync(CancellationToken.None);
        if (frame is null)
            socket.Disconnect();
        else
            socket.Emit(frame);
        var result = await error.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.IsFatal);
        Assert.DoesNotContain("sensitive test detail", result.Message);
    }

    [Fact]
    public async Task CanceledHandshakeDoesNotStartStreaming()
    {
        using var socket = new FakeSocket(acknowledgeUpdate: false);
        await using var provider = Provider("es", socket);
        using var cancel = new CancellationTokenSource();
        var start = provider.StartAsync(cancel.Token);
        Assert.Contains("session.update", await socket.Sent.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        cancel.Cancel();
        var error = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => start);
        Assert.Contains("cancelada", error.Message);
    }

    [Fact]
    public async Task CloseWaitsForDelayedConfirmationAndKeepsReceivingAudio()
    {
        using var socket = new FakeSocket { CloseAcknowledgementDelay = TimeSpan.FromSeconds(5.5) };
        await using var provider = Provider("en", socket);
        var audio = new TaskCompletionSource<TranslatedAudioEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
        provider.AudioReceived += (_, e) => audio.TrySetResult(e);
        provider.Error += (_, e) => errors.Enqueue(e.Message);
        await provider.StartAsync(CancellationToken.None);
        await socket.Sent.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var stop = provider.StopAsync();
        Assert.Contains("session.close", await socket.Sent.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        socket.Emit("""{"type":"session.output_audio.delta","delta":"BQY="}""");
        Assert.Equal(new byte[] { 5, 6 }, (await audio.Task.WaitAsync(TimeSpan.FromSeconds(5))).Chunk.Data);
        await stop.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(errors);
        Assert.True(socket.CloseHandshake);
    }

    [Fact]
    public async Task ConnectionTestReportsErrorDuringClose()
    {
        using var socket = new FakeSocket { AcknowledgeClose = false };
        var factory = new RealtimeTranslateFactory((_, _, _) => Task.FromResult<WebSocket>(socket));
        var context = new ProviderSessionContext(Values,
            new Dictionary<string, string> { [RealtimeTranslateDescriptor.ApiKey] = "fake" }, "unused", 0);
        var resultTask = factory.TestConnectionAsync(context, CancellationToken.None);
        await socket.Sent.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("session.close", await socket.Sent.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        socket.Emit("""{"type":"error","error":{"message":"sensitive test detail"}}""");
        var result = await resultTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.Success);
        Assert.DoesNotContain("sensitive test detail", result.Message);
    }

    [Fact]
    public void MigrationPreservesSpeechAndGptVerificationIsInvalidatedWhenConfigurationChanges()
    {
        var root = Path.Combine(Path.GetTempPath(), $"Traductor-stage3-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AppPaths(root);
            var old = """{"SchemaVersion":2,"ActiveProfileId":"speech","InputDeviceId":"mic","Profiles":[{"Id":"speech","Name":"Speech","ProviderType":"azure-speech","Values":{"voiceName":"original"}}]}""";
            File.WriteAllText(paths.SettingsFile, old);
            var services = new AppServices(paths);
            var speech = services.Settings.ActiveProfile!;
            var gpt = new ProviderProfile { Name = "Foundry", ProviderType = RealtimeTranslateDescriptor.Type };
            const string fakeKey = "disposable-test-credential";
            var values = new Dictionary<string, string>(Values) { [RealtimeTranslateDescriptor.ApiKey] = fakeKey };
            Assert.Empty(services.Profiles.Validate(gpt, values));
            services.Profiles.SaveProfile(gpt, values);
            services.Save();
            Assert.Same(speech, services.Settings.ActiveProfile);
            Assert.Equal("mic", services.Settings.InputDeviceId);
            Assert.Equal(old, File.ReadAllText(services.SettingsStore.BackupFileFor(2)));
            Assert.DoesNotContain(fakeKey, File.ReadAllText(paths.SettingsFile));
            Assert.Equal(fakeKey, services.Profiles.LoadSecrets(gpt)[RealtimeTranslateDescriptor.ApiKey]);
            Assert.False(services.Profiles.CanStartMeeting(gpt));
            services.Profiles.ConfirmGptVoice(gpt);
            var reload = new AppServices(paths);
            var saved = reload.Settings.Profiles.Single(p => p.Id == gpt.Id);
            Assert.True(reload.Profiles.CanStartMeeting(saved));
            reload.Settings.ActiveProfileId = saved.Id;
            Assert.Equal("en", reload.CreateContext(reload.Settings.ActiveProfile!).RealtimeTarget);
            Assert.Equal("es", reload.CreateIncomingContext(reload.Settings.ActiveProfile!).RealtimeTarget);
            var changed = new Dictionary<string, string>(Values) { [RealtimeTranslateDescriptor.EndpointKey] = "https://other.services.ai.azure.com" };
            Assert.NotEmpty(reload.Profiles.Validate(saved, changed));
            Assert.Throws<InvalidOperationException>(() => reload.Profiles.SaveProfile(saved, changed));
            reload.Profiles.SaveProfile(saved, new Dictionary<string, string>(Values) { [RealtimeTranslateDescriptor.DeploymentKey] = "replacement" });
            Assert.False(reload.Profiles.CanStartMeeting(saved));
            reload.Profiles.ConfirmGptVoice(saved);
            reload.Profiles.SaveProfile(saved, values);
            Assert.False(reload.Profiles.CanStartMeeting(saved));
            reload.Profiles.ConfirmGptVoice(saved);
            Assert.True(reload.Profiles.DeleteSecret(saved, RealtimeTranslateDescriptor.ApiKey));
            Assert.False(reload.Profiles.CanStartMeeting(saved));
            File.Copy(services.SettingsStore.BackupFileFor(2), paths.SettingsFile, overwrite: true);
            Assert.Equal(speech.Id, new AppServices(paths).Settings.ActiveProfile!.Id);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("http://resource.services.ai.azure.com")]
    [InlineData("https://resource.services.ai.azure.com.example.org")]
    [InlineData("https://resource.services.ai.azure.com?api-key=test")]
    [InlineData("https://resource.services.ai.azure.com:8443")]
    public void RejectsUntrustedOrNonCanonicalOrigins(string endpoint) =>
        Assert.Null(RealtimeTranslateDescriptor.ResourceOrigin(new Dictionary<string, string> { [RealtimeTranslateDescriptor.EndpointKey] = endpoint }));

    private static readonly Dictionary<string, string> Values = new()
    {
        [RealtimeTranslateDescriptor.EndpointKey] = "https://example.services.ai.azure.com",
        [RealtimeTranslateDescriptor.DeploymentKey] = "test-deployment"
    };

    private static RealtimeTranslateProvider Provider(string target, FakeSocket socket) => new(
        new(Values, new Dictionary<string, string> { [RealtimeTranslateDescriptor.ApiKey] = "fake" }, "unused", 0, target),
        (uri, key, _) =>
        {
            Assert.Equal("wss://example.services.ai.azure.com/openai/v1/realtime/translations?model=test-deployment", uri.AbsoluteUri);
            Assert.Equal("fake", key);
            return Task.FromResult<WebSocket>(socket);
        });

    private sealed class FakeSocket(bool acknowledgeUpdate = true) : WebSocket
    {
        private readonly Channel<(string? Text, bool Final)> _frames = Channel.CreateUnbounded<(string?, bool)>();
        private WebSocketState _state = WebSocketState.Open;
        public Channel<string> Sent { get; } = Channel.CreateUnbounded<string>();
        public bool AcknowledgeClose { get; init; } = true;
        public TimeSpan CloseAcknowledgementDelay { get; init; }
        public bool CloseHandshake { get; private set; }
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override WebSocketState State => _state;
        public void Emit(string json, bool fragmented = false)
        {
            if (fragmented)
                _frames.Writer.TryWrite((json[..12], false));
            _frames.Writer.TryWrite((fragmented ? json[12..] : json, true));
        }
        public void Disconnect() => _frames.Writer.TryWrite((null, true));
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token)
        {
            var (text, final) = await _frames.Reader.ReadAsync(token);
            return text is null
                ? new WebSocketReceiveResult(0, WebSocketMessageType.Close, true)
                : new WebSocketReceiveResult(Encoding.UTF8.GetBytes(text.AsSpan(), buffer.AsSpan()), WebSocketMessageType.Text, final);
        }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool endOfMessage, CancellationToken token)
        {
            var message = Encoding.UTF8.GetString(buffer.AsSpan());
            Sent.Writer.TryWrite(message);
            if (acknowledgeUpdate && message.Contains("session.update"))
                Emit("""{"type":"session.updated"}""");
            else if (message.Contains("session.close") && AcknowledgeClose)
            {
                if (CloseAcknowledgementDelay > TimeSpan.Zero)
                    _ = AcknowledgeCloseAfterDelay();
                else
                    Emit("""{"type":"session.closed"}""");
            }
            return Task.CompletedTask;
        }
        private async Task AcknowledgeCloseAfterDelay()
        {
            await Task.Delay(CloseAcknowledgementDelay);
            Emit("""{"type":"session.closed"}""");
        }
        public override void Abort() => _state = WebSocketState.Aborted;
        public override void Dispose() => Abort();
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken token)
        {
            CloseHandshake = true;
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
    }
}
