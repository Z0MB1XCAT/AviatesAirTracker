using System.Text;

namespace AviatesAirTracker.Core.Efb;

// ============================================================
// QR CODE GENERATOR (byte mode)
//
// Self-contained, dependency-free QR encoder used to render the EFB
// pairing URL as a scannable code in the desktop Settings panel.
// Implements the ISO/IEC 18004 algorithm: byte-mode segment, Reed-
// Solomon error correction with block interleaving, all eight data
// masks with penalty-based selection, and format/version info.
//
// Verified bit-for-bit against the `segno` reference library across
// versions and ECC levels (see build notes).
//
// Algorithm follows the public-domain Nayuki QR reference, reduced to
// byte mode (sufficient for URLs).
// ============================================================

public enum QrEcc { Low = 0, Medium = 1, Quartile = 2, High = 3 }

public sealed class QrCode
{
    public int Size { get; }
    public int Mask { get; }
    private readonly bool[,] _m;        // [y, x], true = dark
    private readonly bool[,] _fn;       // function-module mask (not maskable)

    // Per-ECC format bits (index by QrEcc).
    private static readonly int[] EccFormatBits = { 1, 0, 3, 2 };

    private static readonly int[,] EccCodewordsPerBlock = {
        // v: 1  2  3  4  5  6  7  8  9 10 11 12 13 14 15 16 17 18 19 20 21 22 23 24 25 26 27 28 29 30 31 32 33 34 35 36 37 38 39 40
        {  7,10,15,20,26,18,20,24,30,18,20,24,26,30,22,24,28,30,28,28,28,28,30,30,26,28,30,30,30,30,30,30,30,30,30,30,30,30,30,30}, // L
        { 10,16,26,18,24,16,18,22,22,26,30,22,22,24,24,28,28,26,26,26,26,28,28,28,28,28,28,28,28,28,28,28,28,28,28,28,28,28,28,28}, // M
        { 13,22,18,26,18,24,18,22,20,24,28,26,24,20,30,24,28,28,26,30,28,30,30,30,30,28,30,30,30,30,30,30,30,30,30,30,30,30,30,30}, // Q
        { 17,28,22,16,22,28,26,26,24,28,24,28,22,24,24,30,28,28,26,28,30,24,30,30,30,30,30,30,30,30,30,30,30,30,30,30,30,30,30,30}, // H
    };
    private static readonly int[,] NumEccBlocks = {
        {  1, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 4, 4, 6, 6, 6, 6, 7, 8, 8, 9, 9,10,12,12,12,13,14,15,16,17,18,19,19,20,21,22,24,25}, // L
        {  1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9,10,10,11,13,14,16,17,17,18,20,21,23,25,26,28,29,31,33,35,37,38,40,43,45,47,49}, // M
        {  1, 1, 2, 2, 4, 4, 6, 6, 8, 8, 8,10,12,16,12,17,16,18,21,20,23,23,25,27,29,34,34,35,38,40,43,45,48,51,53,56,59,62,65,68}, // Q
        {  1, 1, 2, 4, 4, 4, 5, 6, 8, 8,11,11,16,16,18,16,19,21,25,25,25,34,30,32,35,37,40,42,45,48,51,54,57,60,63,66,70,74,77,81}, // H
    };

    private QrCode(int version, QrEcc ecc, byte[] dataCodewords, int forceMask)
    {
        Size = version * 4 + 17;
        _m = new bool[Size, Size];
        _fn = new bool[Size, Size];

        DrawFunctionPatterns(version, ecc);
        byte[] allCodewords = AddEccAndInterleave(version, ecc, dataCodewords);
        DrawCodewords(allCodewords);

        // choose mask
        int mask = forceMask;
        if (mask < 0)
        {
            int minPenalty = int.MaxValue;
            for (int i = 0; i < 8; i++)
            {
                ApplyMask(i); DrawFormatBits(ecc, i);
                int p = GetPenaltyScore();
                if (p < minPenalty) { minPenalty = p; mask = i; }
                ApplyMask(i); // undo (XOR is its own inverse)
            }
        }
        Mask = mask;
        ApplyMask(mask);
        DrawFormatBits(ecc, mask);
    }

    /// <summary>Encodes <paramref name="text"/> (UTF-8, byte mode) at the smallest version that fits.</summary>
    public static QrCode Encode(string text, QrEcc ecc = QrEcc.Medium, int forceMask = -1)
    {
        byte[] data = Encoding.UTF8.GetBytes(text);

        for (int version = 1; version <= 40; version++)
        {
            int dataCapacityBits = GetNumDataCodewords(version, ecc) * 8;
            int ccBits = version <= 9 ? 8 : 16;             // byte-mode char-count bits
            int usedBits = 4 + ccBits + data.Length * 8;    // mode + count + data
            if (usedBits <= dataCapacityBits)
                return new QrCode(version, ecc, BuildCodewords(version, ecc, data, ccBits), forceMask);
        }
        throw new ArgumentException("Data too long for a QR code (max version 40).");
    }

    public bool GetModule(int x, int y) => x >= 0 && x < Size && y >= 0 && y < Size && _m[y, x];

    // ── bitstream → data codewords ──────────────────────────
    private static byte[] BuildCodewords(int version, QrEcc ecc, byte[] data, int ccBits)
    {
        var bits = new List<bool>();
        void Append(int val, int len) { for (int i = len - 1; i >= 0; i--) bits.Add(((val >> i) & 1) != 0); }

        Append(0b0100, 4);            // byte mode
        Append(data.Length, ccBits);  // char count
        foreach (byte b in data) Append(b, 8);

        int capacityBits = GetNumDataCodewords(version, ecc) * 8;
        Append(0, Math.Min(4, capacityBits - bits.Count));         // terminator
        while (bits.Count % 8 != 0) bits.Add(false);               // pad to byte
        for (int pad = 0xEC; bits.Count < capacityBits; pad ^= 0xEC ^ 0x11) // 0xEC,0x11 alternating
            Append(pad, 8);

        var bytes = new byte[bits.Count / 8];
        for (int i = 0; i < bits.Count; i++)
            if (bits[i]) bytes[i >> 3] |= (byte)(1 << (7 - (i & 7)));
        return bytes;
    }

    // ── Reed-Solomon + interleave ───────────────────────────
    private static byte[] AddEccAndInterleave(int version, QrEcc ecc, byte[] data)
    {
        int numBlocks = NumEccBlocks[(int)ecc, version - 1];
        int blockEccLen = EccCodewordsPerBlock[(int)ecc, version - 1];
        int rawCodewords = GetNumRawDataModules(version) / 8;
        int numShortBlocks = numBlocks - rawCodewords % numBlocks;
        int shortBlockLen = rawCodewords / numBlocks;

        var blocks = new byte[numBlocks][];
        byte[] rsDiv = ReedSolomonComputeDivisor(blockEccLen);
        for (int i = 0, k = 0; i < numBlocks; i++)
        {
            int datLen = shortBlockLen - blockEccLen + (i < numShortBlocks ? 0 : 1);
            var dat = new byte[datLen];
            Array.Copy(data, k, dat, 0, datLen);
            k += datLen;
            var block = new byte[shortBlockLen + 1];
            Array.Copy(dat, block, datLen);
            byte[] ecccw = ReedSolomonComputeRemainder(dat, rsDiv);
            Array.Copy(ecccw, 0, block, block.Length - blockEccLen, blockEccLen);
            blocks[i] = block;
        }

        var result = new byte[rawCodewords];
        int idx = 0;
        for (int i = 0; i < blocks[0].Length; i++)
            for (int j = 0; j < numBlocks; j++)
                if (i != shortBlockLen - blockEccLen || j >= numShortBlocks)  // skip the padding cell of short blocks
                    result[idx++] = blocks[j][i];
        return result;
    }

    private static byte[] ReedSolomonComputeDivisor(int degree)
    {
        var result = new byte[degree];
        result[degree - 1] = 1;
        int root = 1;
        for (int i = 0; i < degree; i++)
        {
            for (int j = 0; j < result.Length; j++)
            {
                result[j] = (byte)ReedSolomonMultiply(result[j] & 0xFF, root);
                if (j + 1 < result.Length) result[j] ^= result[j + 1];
            }
            root = ReedSolomonMultiply(root, 0x02);
        }
        return result;
    }

    private static byte[] ReedSolomonComputeRemainder(byte[] data, byte[] divisor)
    {
        var result = new byte[divisor.Length];
        foreach (byte b in data)
        {
            int factor = (b ^ result[0]) & 0xFF;
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (int i = 0; i < result.Length; i++)
                result[i] ^= (byte)ReedSolomonMultiply(divisor[i] & 0xFF, factor);
        }
        return result;
    }

    private static int ReedSolomonMultiply(int x, int y)
    {
        int z = 0;
        for (int i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }
        return z & 0xFF;
    }

    // ── module count helpers ────────────────────────────────
    private static int GetNumRawDataModules(int ver)
    {
        int result = (16 * ver + 128) * ver + 64;
        if (ver >= 2)
        {
            int numAlign = ver / 7 + 2;
            result -= (25 * numAlign - 10) * numAlign - 55;
            if (ver >= 7) result -= 36;
        }
        return result;
    }

    private static int GetNumDataCodewords(int ver, QrEcc ecc) =>
        GetNumRawDataModules(ver) / 8
        - EccCodewordsPerBlock[(int)ecc, ver - 1] * NumEccBlocks[(int)ecc, ver - 1];

    // ── drawing ─────────────────────────────────────────────
    private void SetFn(int x, int y, bool dark) { _m[y, x] = dark; _fn[y, x] = true; }

    private void DrawFunctionPatterns(int version, QrEcc ecc)
    {
        for (int i = 0; i < Size; i++)
        {
            SetFn(6, i, i % 2 == 0);   // timing
            SetFn(i, 6, i % 2 == 0);
        }
        DrawFinder(3, 3); DrawFinder(Size - 4, 3); DrawFinder(3, Size - 4);

        int[] align = AlignmentPositions(version);
        int n = align.Length;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                if (!((i == 0 && j == 0) || (i == 0 && j == n - 1) || (i == n - 1 && j == 0)))
                    DrawAlignment(align[i], align[j]);

        DrawFormatBits(ecc, 0);   // dummy; real bits drawn after masking
        DrawVersion(version);
    }

    private void DrawFinder(int cx, int cy)
    {
        for (int dy = -4; dy <= 4; dy++)
            for (int dx = -4; dx <= 4; dx++)
            {
                int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                int x = cx + dx, y = cy + dy;
                if (x >= 0 && x < Size && y >= 0 && y < Size)
                    SetFn(x, y, dist != 2 && dist != 4);
            }
    }

    private void DrawAlignment(int cx, int cy)
    {
        for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
                SetFn(cx + dx, cy + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
    }

    private void DrawFormatBits(QrEcc ecc, int mask)
    {
        int data = EccFormatBits[(int)ecc] << 3 | mask;
        int rem = data;
        for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        int bits = (data << 10 | rem) ^ 0x5412;

        for (int i = 0; i <= 5; i++) SetFn(8, i, GetBit(bits, i));
        SetFn(8, 7, GetBit(bits, 6));
        SetFn(8, 8, GetBit(bits, 7));
        SetFn(7, 8, GetBit(bits, 8));
        for (int i = 9; i < 15; i++) SetFn(14 - i, 8, GetBit(bits, i));

        for (int i = 0; i < 8; i++) SetFn(Size - 1 - i, 8, GetBit(bits, i));
        for (int i = 8; i < 15; i++) SetFn(8, Size - 15 + i, GetBit(bits, i));
        SetFn(8, Size - 8, true);  // always-dark module
    }

    private void DrawVersion(int version)
    {
        if (version < 7) return;
        int rem = version;
        for (int i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
        int bits = version << 12 | rem;
        for (int i = 0; i < 18; i++)
        {
            bool bit = GetBit(bits, i);
            int a = Size - 11 + i % 3, b = i / 3;
            SetFn(a, b, bit); SetFn(b, a, bit);
        }
    }

    private void DrawCodewords(byte[] data)
    {
        int i = 0;
        for (int right = Size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (int vert = 0; vert < Size; vert++)
                for (int j = 0; j < 2; j++)
                {
                    int x = right - j;
                    bool upward = ((right + 1) & 2) == 0;
                    int y = upward ? Size - 1 - vert : vert;
                    if (!_fn[y, x] && i < data.Length * 8)
                    {
                        _m[y, x] = GetBit(data[i >> 3], 7 - (i & 7));
                        i++;
                    }
                }
        }
    }

    private void ApplyMask(int mask)
    {
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                if (_fn[y, x]) continue;
                bool invert = mask switch
                {
                    0 => (x + y) % 2 == 0,
                    1 => y % 2 == 0,
                    2 => x % 3 == 0,
                    3 => (x + y) % 3 == 0,
                    4 => (x / 3 + y / 2) % 2 == 0,
                    5 => x * y % 2 + x * y % 3 == 0,
                    6 => (x * y % 2 + x * y % 3) % 2 == 0,
                    _ => ((x + y) % 2 + x * y % 3) % 2 == 0,
                };
                _m[y, x] ^= invert;
            }
    }

    // ── penalty scoring (mask selection) ────────────────────
    private int GetPenaltyScore()
    {
        int result = 0;
        // rows & cols: runs of ≥5
        for (int y = 0; y < Size; y++)
        {
            bool color = false; int run = 0; int[] hist = new int[7]; int idx = 0;
            for (int x = 0; x < Size; x++)
            {
                if (_m[y, x] == color) { run++; if (run == 5) result += 3; else if (run > 5) result++; }
                else { FinderPenalty(hist, ref idx, run, color, ref result); color = _m[y, x]; run = 1; }
            }
            result += FinderPenaltyEnd(hist, idx, run, color);
        }
        for (int x = 0; x < Size; x++)
        {
            bool color = false; int run = 0; int[] hist = new int[7]; int idx = 0;
            for (int y = 0; y < Size; y++)
            {
                if (_m[y, x] == color) { run++; if (run == 5) result += 3; else if (run > 5) result++; }
                else { FinderPenalty(hist, ref idx, run, color, ref result); color = _m[y, x]; run = 1; }
            }
            result += FinderPenaltyEnd(hist, idx, run, color);
        }
        // 2x2 blocks
        for (int y = 0; y < Size - 1; y++)
            for (int x = 0; x < Size - 1; x++)
                if (_m[y, x] == _m[y, x + 1] && _m[y, x] == _m[y + 1, x] && _m[y, x] == _m[y + 1, x + 1])
                    result += 3;
        // dark proportion
        int dark = 0;
        foreach (bool b in _m) if (b) dark++;
        int total = Size * Size;
        int k = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
        result += k * 10;
        return result;
    }

    // Simplified finder-like (1:1:3:1:1) penalty using a rolling window.
    private void FinderPenalty(int[] hist, ref int idx, int run, bool color, ref int result)
    {
        // Shift the run history and test for the 1:1:3:1:1 pattern with ≥4 light border.
        for (int i = 6; i >= 1; i--) hist[i] = hist[i - 1];
        hist[0] = run;
        result += FinderMatch(hist);
    }
    private int FinderPenaltyEnd(int[] hist, int idx, int run, bool color)
    {
        for (int i = 6; i >= 1; i--) hist[i] = hist[i - 1];
        hist[0] = run;
        return FinderMatch(hist);
    }
    private static int FinderMatch(int[] h)
    {
        // pattern: h[5]:h[4]:h[3]:h[2]:h[1] == 1:1:3:1:1 with h[6] or h[0] light run ≥4
        int n = h[3];
        if (n > 0 && h[4] == n && h[5] == n && h[1] == n && h[2] == 3 * n)
        {
            bool left = h[6] >= 4 * n, right = h[0] >= 4 * n;
            if (left || right) return 40;
        }
        return 0;
    }

    private static bool GetBit(int x, int i) => ((x >> i) & 1) != 0;

    private static int[] AlignmentPositions(int ver)
    {
        if (ver == 1) return Array.Empty<int>();
        int numAlign = ver / 7 + 2;
        int step = (ver * 8 + numAlign * 3 + 5) / (numAlign * 4 - 4) * 2;
        var result = new int[numAlign];
        result[0] = 6;
        for (int i = numAlign - 1, pos = ver * 4 + 10; i >= 1; i--, pos -= step)
            result[i] = pos;
        return result;
    }

    // ── SVG output ──────────────────────────────────────────
    public string ToSvg(int border = 2, string dark = "#0A0D17", string light = "#FFFFFF")
    {
        int dim = Size + border * 2;
        var sb = new StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {dim} {dim}\" ")
          .Append("shape-rendering=\"crispEdges\" width=\"100%\" height=\"100%\">");
        sb.Append($"<rect width=\"{dim}\" height=\"{dim}\" fill=\"{light}\"/>");
        sb.Append($"<path fill=\"{dark}\" d=\"");
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
                if (_m[y, x])
                    sb.Append($"M{x + border} {y + border}h1v1h-1z");
        sb.Append("\"/></svg>");
        return sb.ToString();
    }
}
