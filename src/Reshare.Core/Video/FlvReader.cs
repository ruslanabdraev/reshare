using System.Buffers.Binary;

namespace Reshare.Core.Video;

public enum AvcPacketKind { SequenceHeader, Nalu }

/// <param name="Data">For SequenceHeader: AVCDecoderConfigurationRecord (avcC). For Nalu: 4-byte length prefixed NAL units (AVCC).</param>
public sealed record VideoPacket(AvcPacketKind Kind, bool IsKey, long TimestampMs, byte[] Data);

/// <summary>Minimal streaming FLV demuxer that yields H.264 video packets.</summary>
public sealed class FlvReader(Stream input)
{
    private bool _headerRead;

    public async IAsyncEnumerable<VideoPacket> ReadAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var hdr = new byte[11];
        if (!_headerRead)
        {
            var fileHdr = new byte[9];
            if (!await ReadExactAsync(fileHdr, ct)) yield break;
            if (fileHdr[0] != 'F' || fileHdr[1] != 'L' || fileHdr[2] != 'V')
                throw new InvalidDataException("Not an FLV stream");
            int dataOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(fileHdr.AsSpan(5));
            if (dataOffset > 9 && !await SkipAsync(dataOffset - 9, ct)) yield break;
            _headerRead = true;
        }

        while (!ct.IsCancellationRequested)
        {
            // PreviousTagSize (4) + tag header (11)
            if (!await SkipAsync(4, ct)) yield break;
            if (!await ReadExactAsync(hdr, ct)) yield break;
            int tagType = hdr[0] & 0x1F;
            int size = (hdr[1] << 16) | (hdr[2] << 8) | hdr[3];
            long ts = (hdr[4] << 16) | (hdr[5] << 8) | hdr[6] | (hdr[7] << 24);
            var body = new byte[size];
            if (!await ReadExactAsync(body, ct)) yield break;

            if (tagType != 9 || size < 5) continue;       // not video
            int frameType = body[0] >> 4;
            int codecId = body[0] & 0x0F;
            if (codecId != 7) throw new InvalidDataException($"Unsupported FLV video codec id {codecId} (expected AVC)");
            int avcType = body[1];
            int cts = ((body[2] << 16) | (body[3] << 8) | body[4]);
            if ((cts & 0x800000) != 0) cts |= unchecked((int)0xFF000000);
            var data = body.AsSpan(5).ToArray();

            if (avcType == 0)
                yield return new VideoPacket(AvcPacketKind.SequenceHeader, true, ts, data);
            else if (avcType == 1 && data.Length > 0)
                yield return new VideoPacket(AvcPacketKind.Nalu, frameType == 1, ts + cts, data);
            // avcType 2 = end of sequence
        }
    }

    private async ValueTask<bool> ReadExactAsync(byte[] buf, CancellationToken ct)
    {
        int read = 0;
        while (read < buf.Length)
        {
            int n = await input.ReadAsync(buf.AsMemory(read), ct);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    private async ValueTask<bool> SkipAsync(int count, CancellationToken ct)
    {
        if (count <= 0) return true;
        return await ReadExactAsync(new byte[count], ct);
    }
}
