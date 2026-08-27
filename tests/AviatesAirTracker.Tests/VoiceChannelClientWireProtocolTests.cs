using System.Text;
using AviatesAirTracker.Services.Voice;
using Xunit;

namespace AviatesAirTracker.Tests;

/// <summary>
/// Pure-function verification for VoiceChannelClient's /voice wire protocol:
/// the binary envelope ([idLen][senderId][payload]) and the JSON voice_start/
/// voice_end markers. These are the only parts of Task 4 testable without a
/// live backend socket — connection-lifecycle behavior (reconnect loop, real
/// socket I/O) is exercised only by a real desktop session against a
/// configured DispatchBackendUrl, not by these tests.
/// </summary>
public class VoiceChannelClientWireProtocolTests
{
    [Fact]
    public void EncodeEnvelope_ThenDecodeEnvelope_RoundTripsSenderIdAndPayload()
    {
        var payload = Encoding.UTF8.GetBytes("pretend this is a webm/opus chunk");

        var frame = VoiceChannelClient.EncodeEnvelope("pilot-123", payload);
        var (senderId, decodedPayload) = VoiceChannelClient.DecodeEnvelope(frame);

        Assert.Equal("pilot-123", senderId);
        Assert.Equal(payload, decodedPayload);
    }

    [Fact]
    public void EncodeEnvelope_LayoutMatchesVoiceJsByteForByte()
    {
        // Mirrors voice.js's ondataavailable handler exactly:
        //   const idBytes = new TextEncoder().encode(myPilotId);
        //   const header = new Uint8Array(1 + idBytes.length);
        //   header[0] = idBytes.length;
        //   header.set(idBytes, 1);
        //   ws.send(new Blob([header, event.data]));
        var payload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var frame = VoiceChannelClient.EncodeEnvelope("ab", payload);

        Assert.Equal(1 + 2 + payload.Length, frame.Length);
        Assert.Equal(2, frame[0]); // idLen
        Assert.Equal((byte)'a', frame[1]);
        Assert.Equal((byte)'b', frame[2]);
        Assert.Equal(payload, frame[3..]);
    }

    [Fact]
    public void DecodeEnvelope_MultiByteUtf8SenderId_RoundTrips()
    {
        // senderId isn't guaranteed ASCII (pilotId is a hex GUID-like string today,
        // but the wire format itself is UTF-8-general per voice.js's TextEncoder/
        // TextDecoder use) -- confirm a non-ASCII id survives the round trip too.
        var payload = new byte[] { 1, 2, 3 };
        var frame = VoiceChannelClient.EncodeEnvelope("pilöt-✈", payload);

        var (senderId, decodedPayload) = VoiceChannelClient.DecodeEnvelope(frame);

        Assert.Equal("pilöt-✈", senderId);
        Assert.Equal(payload, decodedPayload);
    }

    [Fact]
    public void DecodeEnvelope_EmptyPayload_RoundTrips()
    {
        var frame = VoiceChannelClient.EncodeEnvelope("solo", Array.Empty<byte>());
        var (senderId, payload) = VoiceChannelClient.DecodeEnvelope(frame);

        Assert.Equal("solo", senderId);
        Assert.Empty(payload);
    }

    [Fact]
    public void EncodeEnvelope_SenderIdOver255Utf8Bytes_Throws()
    {
        var tooLong = new string('x', 256);
        Assert.Throws<ArgumentException>(() => VoiceChannelClient.EncodeEnvelope(tooLong, Array.Empty<byte>()));
    }

    [Fact]
    public void DecodeEnvelope_TooShortForIdLenByte_Throws()
    {
        Assert.Throws<ArgumentException>(() => VoiceChannelClient.DecodeEnvelope(Array.Empty<byte>()));
    }

    [Fact]
    public void DecodeEnvelope_DeclaredIdLenLongerThanFrame_Throws()
    {
        // idLen says 10 but only 2 bytes follow -- a truncated/corrupt frame.
        var malformed = new byte[] { 10, 1, 2 };
        Assert.Throws<ArgumentException>(() => VoiceChannelClient.DecodeEnvelope(malformed));
    }

    [Fact]
    public void BuildMarkerJson_VoiceStart_MatchesVoiceJsFieldNamesAndCasing()
    {
        var json = VoiceChannelClient.BuildMarkerJson("voice_start", "pilot-123");
        Assert.Equal("{\"type\":\"voice_start\",\"from\":\"pilot-123\"}", json);
    }

    [Fact]
    public void BuildMarkerJson_VoiceEnd_MatchesVoiceJsFieldNamesAndCasing()
    {
        var json = VoiceChannelClient.BuildMarkerJson("voice_end", "pilot-123");
        Assert.Equal("{\"type\":\"voice_end\",\"from\":\"pilot-123\"}", json);
    }

    [Fact]
    public void TryParseMarker_VoiceEndJson_ParsesTypeAndFrom()
    {
        var ok = VoiceChannelClient.TryParseMarker(
            "{\"type\":\"voice_end\",\"from\":\"pilot-456\"}", out var type, out var from);

        Assert.True(ok);
        Assert.Equal("voice_end", type);
        Assert.Equal("pilot-456", from);
    }

    [Fact]
    public void TryParseMarker_RoundTripsWhatBuildMarkerJsonProduced()
    {
        var json = VoiceChannelClient.BuildMarkerJson("voice_start", "pilot-789");
        var ok = VoiceChannelClient.TryParseMarker(json, out var type, out var from);

        Assert.True(ok);
        Assert.Equal("voice_start", type);
        Assert.Equal("pilot-789", from);
    }

    [Fact]
    public void TryParseMarker_MalformedJson_ReturnsFalseRatherThanThrowing()
    {
        var ok = VoiceChannelClient.TryParseMarker("{not valid json", out var type, out var from);

        Assert.False(ok);
        Assert.Null(type);
        Assert.Null(from);
    }

    [Fact]
    public void TryParseMarker_JsonWithoutTypeField_ReturnsFalse()
    {
        var ok = VoiceChannelClient.TryParseMarker("{\"from\":\"pilot-1\"}", out _, out _);
        Assert.False(ok);
    }

    [Fact]
    public void AudioReceived_FiresOnVoiceEnd_WithConcatenatedChunksInOrder()
    {
        using var client = new VoiceChannelClient();
        string? capturedSenderId = null;
        byte[]? capturedBlob = null;
        var signal = new ManualResetEventSlim(false);

        client.AudioReceived += (senderId, blob) =>
        {
            capturedSenderId = senderId;
            capturedBlob = blob;
            signal.Set();
        };

        InvokeHandleIncomingBinary(client, VoiceChannelClient.EncodeEnvelope("pilotA", new byte[] { 1, 2 }));
        InvokeHandleIncomingBinary(client, VoiceChannelClient.EncodeEnvelope("pilotA", new byte[] { 3, 4, 5 }));
        InvokeHandleIncomingMarker(client, VoiceChannelClient.BuildMarkerJson("voice_end", "pilotA"));

        Assert.True(signal.Wait(TimeSpan.FromSeconds(2)), "AudioReceived did not fire on voice_end");
        Assert.Equal("pilotA", capturedSenderId);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, capturedBlob);
    }

    [Fact]
    public void AudioReceived_FiresAfterLostMarkerTimeout_WhenVoiceEndNeverArrives()
    {
        using var client = new VoiceChannelClient();
        string? capturedSenderId = null;
        var signal = new ManualResetEventSlim(false);

        client.AudioReceived += (senderId, _) =>
        {
            capturedSenderId = senderId;
            signal.Set();
        };

        InvokeHandleIncomingBinary(client, VoiceChannelClient.EncodeEnvelope("pilotB", new byte[] { 9 }));

        // No voice_end sent -- must fall back to the SESSION_TIMEOUT_MS (2000ms) timeout.
        // Allow generous slack above 2000ms for scheduler jitter on a shared test runner,
        // while still failing well before a test framework's own default timeout.
        Assert.True(signal.Wait(TimeSpan.FromSeconds(5)), "AudioReceived did not fire after the lost-marker timeout");
        Assert.Equal("pilotB", capturedSenderId);
    }

    [Fact]
    public void AudioReceived_KeepsSeparateSendersInSeparateBuffers()
    {
        using var client = new VoiceChannelClient();
        var received = new Dictionary<string, byte[]>();
        var barrier = new CountdownEvent(2);

        client.AudioReceived += (senderId, blob) =>
        {
            lock (received) received[senderId] = blob;
            barrier.Signal();
        };

        InvokeHandleIncomingBinary(client, VoiceChannelClient.EncodeEnvelope("alice", new byte[] { 1 }));
        InvokeHandleIncomingBinary(client, VoiceChannelClient.EncodeEnvelope("bob", new byte[] { 2 }));
        InvokeHandleIncomingMarker(client, VoiceChannelClient.BuildMarkerJson("voice_end", "alice"));
        InvokeHandleIncomingMarker(client, VoiceChannelClient.BuildMarkerJson("voice_end", "bob"));

        Assert.True(barrier.Wait(TimeSpan.FromSeconds(2)));
        Assert.Equal(new byte[] { 1 }, received["alice"]);
        Assert.Equal(new byte[] { 2 }, received["bob"]);
    }

    // VoiceChannelClient's per-sender buffering (HandleIncomingBinary/HandleIncomingMarker)
    // is private -- it's normally driven by the WebSocket receive loop, which needs a live
    // socket. Reflection invokes those private methods directly so the buffering/timeout
    // logic itself can be verified without one, matching this task's brief to test what's
    // "genuinely testable without a live backend connection".
    private static void InvokeHandleIncomingBinary(VoiceChannelClient client, byte[] frame)
    {
        var method = typeof(VoiceChannelClient).GetMethod("HandleIncomingBinary",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(client, new object[] { frame });
    }

    private static void InvokeHandleIncomingMarker(VoiceChannelClient client, string json)
    {
        var method = typeof(VoiceChannelClient).GetMethod("HandleIncomingMarker",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(client, new object[] { json });
    }
}
