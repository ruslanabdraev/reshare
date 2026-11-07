using System.Buffers.Binary;

namespace Reshare.Core.Video;

public static class Avc
{
    /// <summary>WebCodecs codec string ("avc1.PPCCLL") from an avcC record.</summary>
    public static string CodecString(ReadOnlySpan<byte> avcC)
    {
        if (avcC.Length < 4 || avcC[0] != 1) throw new InvalidDataException("Invalid avcC");
        return $"avc1.{avcC[1]:x2}{avcC[2]:x2}{avcC[3]:x2}";
    }

    /// <summary>Enumerate NAL units of an AVCC (4-byte length prefixed) buffer.</summary>
    public static IEnumerable<byte[]> SplitAvcc(byte[] data)
    {
        int pos = 0;
        while (pos + 4 <= data.Length)
        {
            int len = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
            pos += 4;
            if (len <= 0 || pos + len > data.Length) yield break;
            yield return data.AsSpan(pos, len).ToArray();
            pos += len;
        }
    }

    /// <summary>
    /// Build an avcC record from the SPS/PPS carried in-band in a key frame.
    /// Used when the encoder doesn't emit global headers. Returns null if no SPS/PPS present.
    /// </summary>
    public static byte[]? BuildAvcCFromKeyframe(byte[] avccFrame)
    {
        byte[]? sps = null, pps = null;
        foreach (var nal in SplitAvcc(avccFrame))
        {
            int type = nal[0] & 0x1F;
            if (type == 7 && sps == null) sps = nal;
            else if (type == 8 && pps == null) pps = nal;
        }
        if (sps == null || pps == null || sps.Length < 4) return null;
        return BuildAvcC(sps, pps);
    }

    public static byte[] BuildAvcC(byte[] sps, byte[] pps)
    {
        var r = new byte[6 + 2 + sps.Length + 1 + 2 + pps.Length];
        int p = 0;
        r[p++] = 1;          // configurationVersion
        r[p++] = sps[1];     // profile_idc
        r[p++] = sps[2];     // constraint flags
        r[p++] = sps[3];     // level_idc
        r[p++] = 0xFF;       // 6 bits reserved + lengthSizeMinusOne = 3
        r[p++] = 0xE1;       // 3 bits reserved + numOfSPS = 1
        BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(p), (ushort)sps.Length); p += 2;
        sps.CopyTo(r, p); p += sps.Length;
        r[p++] = 1;          // numOfPPS
        BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(p), (ushort)pps.Length); p += 2;
        pps.CopyTo(r, p);
        return r;
    }
}
