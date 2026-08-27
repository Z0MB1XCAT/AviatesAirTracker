using NEbml.Core;

namespace AviatesAirTracker.Services.Voice;

// ============================================================
// WEBM DEMUXER
//
// Reads a complete WebM/Opus blob (as produced by WebmMuxer, or received over
// the wire from voice.js's own MediaRecorder output) back into an ordered list
// of raw Opus packets — the SimpleBlock payloads, in the order they appear
// across every Cluster in the Segment.
//
// Uses VInt.FromEncoded-based IDs shared with WebmMuxer via WebmElementIds —
// never VInt.MakeId (see the trap documented at the top of WebmMuxer.cs).
// ============================================================
public static class WebmDemuxer
{
    /// <summary>
    /// Walks a WebM blob's Segment/Cluster/SimpleBlock structure and returns the
    /// raw Opus frame payload of every SimpleBlock, in stream order. Non-audio
    /// elements (Info, Tracks, etc.) are skipped, not validated — this is a
    /// deliberately narrow reader matching what this app ever produces or
    /// receives (single-track Opus audio, no video, no Cues).
    /// </summary>
    public static IReadOnlyList<byte[]> Demux(byte[] webmBlob)
    {
        ArgumentNullException.ThrowIfNull(webmBlob);

        var packets = new List<byte[]>();
        using var reader = new EbmlReader(new MemoryStream(webmBlob));

        while (reader.ReadNext())
        {
            if (reader.ElementId.EncodedValue != WebmElementIds.Segment.EncodedValue)
            {
                continue;
            }

            reader.EnterContainer();
            while (reader.ReadNext())
            {
                if (reader.ElementId.EncodedValue == WebmElementIds.Cluster.EncodedValue)
                {
                    ReadCluster(reader, packets);
                }
            }
            reader.LeaveContainer();
        }

        return packets;
    }

    private static void ReadCluster(EbmlReader reader, List<byte[]> packets)
    {
        reader.EnterContainer();
        while (reader.ReadNext())
        {
            if (reader.ElementId.EncodedValue != WebmElementIds.SimpleBlock.EncodedValue)
            {
                continue;
            }

            var raw = new byte[reader.ElementSize];
            int read = reader.ReadBinary(raw, 0, raw.Length);
            if (read != raw.Length)
            {
                throw new EbmlDataFormatException(
                    $"SimpleBlock declared {raw.Length} bytes but only {read} were readable.");
            }

            packets.Add(ExtractFramePayload(raw));
        }
        reader.LeaveContainer();
    }

    /// <summary>
    /// A SimpleBlock payload is [track number VINT][2-byte signed relative
    /// timecode, big-endian][1-byte flags][raw frame data]. The track-number
    /// VINT's length is parsed properly (not assumed to be 1 byte) so this stays
    /// correct even if a future track uses a multi-byte track number.
    /// </summary>
    private static byte[] ExtractFramePayload(byte[] simpleBlock)
    {
        using var ms = new MemoryStream(simpleBlock);
        var trackVIntBuffer = new byte[8];
        VInt.Read(ms, 8, trackVIntBuffer);

        // Skip past the 2-byte relative timecode + 1-byte flags.
        ms.Seek(3, SeekOrigin.Current);

        var frame = new byte[simpleBlock.Length - ms.Position];
        int read = ms.Read(frame, 0, frame.Length);
        if (read != frame.Length)
        {
            throw new EbmlDataFormatException("SimpleBlock frame payload was shorter than expected.");
        }
        return frame;
    }
}
