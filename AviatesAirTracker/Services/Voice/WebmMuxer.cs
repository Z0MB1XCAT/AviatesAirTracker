using NEbml.Core;

namespace AviatesAirTracker.Services.Voice;

// ============================================================
// WEBM/OPUS ELEMENT IDS
//
// Standard Matroska/EBML element identifiers, shared between WebmMuxer and
// WebmDemuxer so the two can never drift against each other.
//
// CRITICAL NEbml API TRAP: every one of these MUST be constructed with
// VInt.FromEncoded(ulong) — never VInt.MakeId(uint). MakeId() treats its
// argument as a bare number and re-encodes its own minimal VINT around it,
// silently producing the WRONG bytes for an already-marked conventional ID
// (e.g. MakeId(0xAE) emits a wrong 2-byte sequence instead of the correct
// 1-byte 0xAE). FromEncoded() wraps an already-marked ID byte-for-byte with
// no re-encoding, which is what every one of these constants needs. This has
// no compiler error if gotten wrong — it silently produces a corrupt,
// non-playable WebM file. Confirmed by a hands-on spike before this file was
// written; see docs/superpowers/specs/2026-08-27-native-voice-pipeline-design.md.
// ============================================================
internal static class WebmElementIds
{
    // EBML header
    public static readonly VInt Ebml = VInt.FromEncoded(0x1A45DFA3);
    public static readonly VInt EbmlVersion = VInt.FromEncoded(0x4286);
    public static readonly VInt EbmlReadVersion = VInt.FromEncoded(0x42F7);
    public static readonly VInt EbmlMaxIdLength = VInt.FromEncoded(0x42F2);
    public static readonly VInt EbmlMaxSizeLength = VInt.FromEncoded(0x42F3);
    public static readonly VInt DocType = VInt.FromEncoded(0x4282);
    public static readonly VInt DocTypeVersion = VInt.FromEncoded(0x4287);
    public static readonly VInt DocTypeReadVersion = VInt.FromEncoded(0x4285);

    // Segment and children
    public static readonly VInt Segment = VInt.FromEncoded(0x18538067);
    public static readonly VInt Info = VInt.FromEncoded(0x1549A966);
    public static readonly VInt TimecodeScale = VInt.FromEncoded(0x2AD7B1);
    public static readonly VInt MuxingApp = VInt.FromEncoded(0x4D80);
    public static readonly VInt WritingApp = VInt.FromEncoded(0x5741);

    // Tracks
    public static readonly VInt Tracks = VInt.FromEncoded(0x1654AE6B);
    public static readonly VInt TrackEntry = VInt.FromEncoded(0xAE);
    public static readonly VInt TrackNumber = VInt.FromEncoded(0xD7);
    public static readonly VInt TrackUid = VInt.FromEncoded(0x73C5);
    public static readonly VInt TrackType = VInt.FromEncoded(0x83);
    public static readonly VInt CodecId = VInt.FromEncoded(0x86);
    public static readonly VInt CodecPrivate = VInt.FromEncoded(0x63A2);
    public static readonly VInt Audio = VInt.FromEncoded(0xE1);
    public static readonly VInt SamplingFrequency = VInt.FromEncoded(0xB5);
    public static readonly VInt ChannelsElement = VInt.FromEncoded(0x9F);

    // Cluster
    public static readonly VInt Cluster = VInt.FromEncoded(0x1F43B675);
    public static readonly VInt Timecode = VInt.FromEncoded(0xE7);
    public static readonly VInt SimpleBlock = VInt.FromEncoded(0xA3);
}

// ============================================================
// WEBM MUXER
//
// Builds a WebM/Opus container structurally matching what Chromium's
// MediaRecorder(audio/webm;codecs=opus) produces: EBML header -> Segment ->
// Info/Tracks(A_OPUS track, CodecPrivate = 19-byte OpusHead per RFC 7845) ->
// one or more Clusters, each holding one SimpleBlock per 20ms Opus frame.
// No Cues/SeekHead and no video track — this is a live one-way PTT stream,
// not a seekable file (matches the design spec's scope).
//
// API SHAPE, AND WHY (implementer's-call note from the plan): Mux() takes the
// FULL list of Opus frames for one transmission and returns one complete,
// self-contained blob, internally grouping frames into ~100ms Clusters to
// mirror MediaRecorder's own ondataavailable cadence structurally. This is a
// batch build, not a true incremental/streaming writer that emits separately-
// decodable growing chunks. That was tried first (EBML/Matroska supports a
// Segment written with an "unknown size" streaming marker for exactly this
// use case) but NEbml.Core.EbmlReader 1.1.0.5 explicitly rejects it: reading
// a stream back throws `EbmlDataFormatException: "unknown-size elements are
// not allowed at root level"` (confirmed empirically, not assumed). Since
// WebmDemuxer must use this same reader, an unknown-size Segment isn't a
// viable design here. This is a fine fit for the wire protocol regardless:
// voice.js's own receiver never decodes chunk-by-chunk either — it buffers
// every chunk for a sender-id and decodes the full concatenation as ONE clip
// on voice_end (or the 2000ms lost-marker timeout). A later task (orchestration
// layer, out of this task's scope) can call Mux() once per transmission using
// all frames captured during that PTT press, or re-call it with the frames
// accumulated so far if a growing/periodic resend is ever wanted — both are
// correct against this wire protocol.
// ============================================================
public static class WebmMuxer
{
    /// <summary>The only track in every muxed blob — this is a single-track (audio-only) stream.</summary>
    public const int TrackNumber = 1;

    /// <summary>
    /// Frames grouped per Cluster. 5 * 20ms = 100ms, matching MediaRecorder's own
    /// timeslice cadence in voice.js structurally (this does not change the wire
    /// protocol — it only affects how Clusters subdivide one muxed blob).
    /// </summary>
    public const int FramesPerCluster = 5;

    /// <summary>EBML TimecodeScale: 1 tick = 1,000,000ns = 1ms (the conventional Matroska default).</summary>
    private const ulong TimecodeScaleNs = 1_000_000;

    private const string MuxingAppName = "AviatesAirTracker";

    /// <summary>
    /// Builds one complete WebM/Opus blob from an ordered list of 20ms Opus
    /// frames. Frames are grouped into consecutive Clusters of
    /// <see cref="FramesPerCluster"/> (the final Cluster may hold fewer).
    /// </summary>
    public static byte[] Mux(IReadOnlyList<byte[]> opusFrames)
    {
        ArgumentNullException.ThrowIfNull(opusFrames);

        using var stream = new MemoryStream();
        var root = new EbmlWriter(stream);

        WriteEbmlHeader(root);

        using (var segment = root.StartMasterElement(WebmElementIds.Segment))
        {
            WriteInfo(segment);
            WriteTracks(segment);
            WriteClusters(segment, opusFrames);
        }

        return stream.ToArray();
    }

    private static void WriteEbmlHeader(EbmlWriter root)
    {
        using var ebmlHeader = root.StartMasterElement(WebmElementIds.Ebml);
        ebmlHeader.Write(WebmElementIds.EbmlVersion, 1UL);
        ebmlHeader.Write(WebmElementIds.EbmlReadVersion, 1UL);
        ebmlHeader.Write(WebmElementIds.EbmlMaxIdLength, 4UL);
        ebmlHeader.Write(WebmElementIds.EbmlMaxSizeLength, 8UL);
        ebmlHeader.WriteAscii(WebmElementIds.DocType, "webm");
        ebmlHeader.Write(WebmElementIds.DocTypeVersion, 2UL);
        ebmlHeader.Write(WebmElementIds.DocTypeReadVersion, 2UL);
    }

    private static void WriteInfo(EbmlWriter segment)
    {
        using var info = segment.StartMasterElement(WebmElementIds.Info);
        info.Write(WebmElementIds.TimecodeScale, TimecodeScaleNs);
        info.WriteUtf(WebmElementIds.MuxingApp, MuxingAppName);
        info.WriteUtf(WebmElementIds.WritingApp, MuxingAppName);
    }

    private static void WriteTracks(EbmlWriter segment)
    {
        using var tracks = segment.StartMasterElement(WebmElementIds.Tracks);
        using var trackEntry = tracks.StartMasterElement(WebmElementIds.TrackEntry);
        trackEntry.Write(WebmElementIds.TrackNumber, (ulong)TrackNumber);
        trackEntry.Write(WebmElementIds.TrackUid, (ulong)TrackNumber);
        trackEntry.Write(WebmElementIds.TrackType, 2UL); // 2 = audio
        trackEntry.WriteAscii(WebmElementIds.CodecId, "A_OPUS");
        trackEntry.Write(WebmElementIds.CodecPrivate, BuildOpusHead());

        using var audio = trackEntry.StartMasterElement(WebmElementIds.Audio);
        audio.Write(WebmElementIds.SamplingFrequency, (double)OpusCodec.SampleRateHz);
        audio.Write(WebmElementIds.ChannelsElement, (ulong)OpusCodec.Channels);
    }

    private static void WriteClusters(EbmlWriter segment, IReadOnlyList<byte[]> opusFrames)
    {
        long clusterTimecodeMs = 0;
        for (int i = 0; i < opusFrames.Count; i += FramesPerCluster)
        {
            int count = Math.Min(FramesPerCluster, opusFrames.Count - i);

            using (var cluster = segment.StartMasterElement(WebmElementIds.Cluster))
            {
                cluster.Write(WebmElementIds.Timecode, (ulong)clusterTimecodeMs);

                short relativeTimecodeMs = 0;
                for (int j = 0; j < count; j++)
                {
                    cluster.Write(WebmElementIds.SimpleBlock, BuildSimpleBlock(opusFrames[i + j], relativeTimecodeMs));
                    relativeTimecodeMs += OpusCodec.FrameDurationMs;
                }
            }

            clusterTimecodeMs += count * OpusCodec.FrameDurationMs;
        }
    }

    /// <summary>
    /// Builds the 19-byte OpusHead structure required by CodecPrivate, per RFC 7845 §5.1:
    /// "OpusHead"(8) + Version(1) + ChannelCount(1) + PreSkip(2, LE) + InputSampleRate(4, LE)
    /// + OutputGain(2, LE) + ChannelMappingFamily(1).
    /// </summary>
    private static byte[] BuildOpusHead()
    {
        var head = new byte[19];
        System.Text.Encoding.ASCII.GetBytes("OpusHead").CopyTo(head, 0);
        head[8] = 1; // Version
        head[9] = OpusCodec.Channels; // Channel count

        // Pre-skip (samples to discard from decoder output) — 0 is safe/conservative;
        // this muxer doesn't apply algorithmic-delay priming, so there's nothing to skip.
        WriteUInt16LittleEndian(head, 10, 0);

        WriteUInt32LittleEndian(head, 12, OpusCodec.SampleRateHz); // Input sample rate (informational)
        WriteInt16LittleEndian(head, 16, 0); // Output gain (Q7.8 dB) — no adjustment
        head[18] = 0; // Channel mapping family 0 — mono/stereo, no mapping table
        return head;
    }

    /// <summary>
    /// Builds one SimpleBlock payload: [track number VINT][2-byte signed relative
    /// timecode, big-endian][1-byte flags][raw Opus frame]. Flags = 0x80 marks the
    /// block as a keyframe, which every Opus frame is (no inter-frame prediction
    /// across SimpleBlocks at the container level).
    /// </summary>
    private static byte[] BuildSimpleBlock(byte[] opusFrame, short relativeTimecodeMs)
    {
        var trackVInt = VInt.EncodeSize((ulong)TrackNumber, 1);
        using var block = new MemoryStream(trackVInt.Length + 2 + 1 + opusFrame.Length);
        trackVInt.Write(block);

        Span<byte> timecodeBytes = stackalloc byte[2];
        System.Buffers.Binary.BinaryPrimitives.WriteInt16BigEndian(timecodeBytes, relativeTimecodeMs);
        block.Write(timecodeBytes);

        block.WriteByte(0x80); // flags: keyframe
        block.Write(opusFrame, 0, opusFrame.Length);
        return block.ToArray();
    }

    private static void WriteUInt16LittleEndian(byte[] buffer, int offset, ushort value)
        => System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset, 2), value);

    private static void WriteUInt32LittleEndian(byte[] buffer, int offset, uint value)
        => System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, 4), value);

    private static void WriteInt16LittleEndian(byte[] buffer, int offset, short value)
        => System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(offset, 2), value);
}
