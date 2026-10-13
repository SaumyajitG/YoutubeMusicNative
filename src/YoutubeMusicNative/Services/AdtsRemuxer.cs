using System.Buffers.Binary;
using System.IO;

namespace YoutubeMusicNative.Services;

/// <summary>
/// Rewraps YouTube's fragmented MP4 (DASH) audio as a plain ADTS AAC stream.
/// Windows' MP4 reader rejects some of YouTube's fragmented files (they "end" instantly),
/// while the same AAC frames play fine as ADTS. No decoding happens: frames are copied as-is.
/// </summary>
internal static class AdtsRemuxer
{
    /// <summary>Returns null if the data isn't fragmented MP4 audio.</summary>
    public static byte[]? Remux(byte[] mp4)
    {
        int objectType = 2, freqIndex = 4, channels = 2; // AAC-LC, 44.1 kHz, stereo unless esds says otherwise
        var esds = IndexOf(mp4, "esds"u8, 0, mp4.Length);
        if (esds > 0 && FindAudioSpecificConfig(mp4, esds + 8) is var asc and >= 0)
        {
            objectType = mp4[asc] >> 3;
            freqIndex = ((mp4[asc] & 7) << 1) | (mp4[asc + 1] >> 7);
            channels = (mp4[asc + 1] >> 3) & 0xF;
        }

        using var output = new MemoryStream(mp4.Length + mp4.Length / 100);
        var pos = 0;
        while (pos + 8 <= mp4.Length)
        {
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(mp4.AsSpan(pos));
            if (size < 8 || pos + size > mp4.Length) break;
            if (mp4.AsSpan(pos + 4, 4).SequenceEqual("moof"u8))
                WriteFragment(mp4, pos, size, output, objectType, freqIndex, channels);
            pos += size;
        }
        return output.Length > 0 ? output.ToArray() : null;
    }

    private static void WriteFragment(byte[] d, int moof, int moofSize, MemoryStream output, int objectType, int freqIndex, int channels)
    {
        var trun = IndexOf(d, "trun"u8, moof, moof + moofSize) - 4;
        if (trun < 0) return;

        uint defaultSize = 0;
        var tfhd = IndexOf(d, "tfhd"u8, moof, moof + moofSize) - 4;
        if (tfhd >= 0)
        {
            var tf = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(tfhd + 8)) & 0xFFFFFF;
            var p = tfhd + 16; // after header, version/flags and track id
            if ((tf & 0x01) != 0) p += 8; // base data offset
            if ((tf & 0x02) != 0) p += 4; // sample description index
            if ((tf & 0x08) != 0) p += 4; // default sample duration
            if ((tf & 0x10) != 0) defaultSize = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p));
        }

        var flags = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(trun + 8)) & 0xFFFFFF;
        var count = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(trun + 12));
        var q = trun + 16;
        var offset = moof + moofSize + 8; // start of the following mdat's payload
        if ((flags & 0x01) != 0) { offset = moof + BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(q)); q += 4; }
        if ((flags & 0x04) != 0) q += 4;

        for (var i = 0; i < count; i++)
        {
            if ((flags & 0x100) != 0) q += 4;
            var sampleSize = (int)defaultSize;
            if ((flags & 0x200) != 0) { sampleSize = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(q)); q += 4; }
            if ((flags & 0x400) != 0) q += 4;
            if ((flags & 0x800) != 0) q += 4;
            if (offset + sampleSize > d.Length) return;

            var frameLength = sampleSize + 7;
            output.Write([
                0xFF, 0xF1,
                (byte)(((objectType - 1) << 6) | (freqIndex << 2) | (channels >> 2)),
                (byte)(((channels & 3) << 6) | (frameLength >> 11)),
                (byte)(frameLength >> 3),
                (byte)(((frameLength & 7) << 5) | 0x1F),
                0xFC,
            ]);
            output.Write(d, offset, sampleSize);
            offset += sampleSize;
        }
    }

    /// <summary>Walks the esds descriptors (ES → DecoderConfig → DecoderSpecificInfo).</summary>
    private static int FindAudioSpecificConfig(byte[] d, int p)
    {
        p += 4; // version + flags
        for (var guard = 0; guard < 8 && p + 2 < d.Length; guard++)
        {
            var tag = d[p++];
            var length = 0;
            for (var k = 0; k < 4; k++)
            {
                var b = d[p++];
                length = (length << 7) | (b & 0x7F);
                if ((b & 0x80) == 0) break;
            }
            switch (tag)
            {
                case 0x03:
                    var esFlags = d[p + 2];
                    p += 3;
                    if ((esFlags & 0x80) != 0) p += 2;
                    if ((esFlags & 0x40) != 0) p += 1 + d[p];
                    if ((esFlags & 0x20) != 0) p += 2;
                    break;
                case 0x04: p += 13; break;
                case 0x05: return p;
                default: p += length; break;
            }
        }
        return -1;
    }

    private static int IndexOf(byte[] d, ReadOnlySpan<byte> pattern, int from, int to)
    {
        var i = d.AsSpan(from, to - from).IndexOf(pattern);
        return i < 0 ? -1 : from + i;
    }
}
