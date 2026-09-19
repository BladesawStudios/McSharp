using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using McSharp.Internal;

namespace McSharp;

/// <summary>
/// Where the index and vertex streams sit inside a decoded package, once the mesh section has been
/// expanded into it.
/// </summary>
public readonly struct MeshLayout
{
    internal MeshLayout(uint indexOffset, uint indexSize, uint vertexOffset, uint vertexSize,
                        byte indexAlign, byte vertexAlign)
    {
        IndexOffset = indexOffset;
        IndexSize = indexSize;
        VertexOffset = vertexOffset;
        VertexSize = vertexSize;
        IndexAlign = indexAlign;
        VertexAlign = vertexAlign;
    }

    public uint IndexOffset { get; }
    public uint IndexSize { get; }
    public uint VertexOffset { get; }
    public uint VertexSize { get; }
    public byte IndexAlign { get; }
    public byte VertexAlign { get; }

    /// <summary>First byte past the vertex stream, which is the package's decompressed size.</summary>
    public uint End => VertexOffset + VertexSize;
}

/// <summary>
/// Writes FMSH mesh sections.
/// </summary>
/// <remarks>
/// <para>
/// Nintendo's own encoder uses codec type 2, whose entropy coding McSharp can read but not write.
/// This encoder emits codec type 0 instead: the vertex and index streams are stored verbatim and
/// the runtime copies them straight out. The container carries the codec type per section, and the
/// retail loader dispatches on it without restriction, so a type 0 section is a legal mesh section
/// and this is how you author new geometry rather than copying an existing section through.
/// </para>
/// <para>
/// The trade is size: nothing is compressed, so a type 0 section is as large as the raw GPU
/// buffers. The surrounding package is still zstd compressed as usual.
/// </para>
/// </remarks>
public static class FmshEncoder
{
    /// <summary>
    /// Bytes a codec type 0 frame carries. The runtime copies <c>min(remaining, 0x40000)</c> per
    /// frame, so every frame but the last in a stream is exactly this long.
    /// </summary>
    public const int FramePayloadSize = 0x40000;

    private const uint Version = 1;

    private static readonly int HeaderSize = Marshal.SizeOf<ResMeshCodecHeader>();

    /// <summary>
    /// Builds a mesh section holding <paramref name="indexStream"/> and
    /// <paramref name="vertexStream"/> verbatim. Pass the result to
    /// <see cref="McEncoder.CompressMcWithFmsh"/> to get a package.
    /// </summary>
    public static byte[] EncodeUncompressed(ReadOnlySpan<byte> indexStream, ReadOnlySpan<byte> vertexStream,
                                            byte indexAlign = 8, byte vertexAlign = 8)
    {
        if (!MeshCodec.IsUsableAlignment(indexAlign))
            throw new ArgumentOutOfRangeException(nameof(indexAlign), indexAlign, "Alignment must be a power of two.");

        if (!MeshCodec.IsUsableAlignment(vertexAlign))
            throw new ArgumentOutOfRangeException(nameof(vertexAlign), vertexAlign, "Alignment must be a power of two.");

        if (indexStream.IsEmpty && vertexStream.IsEmpty)
            throw new ArgumentException(
                "A mesh section with neither an index nor a vertex stream carries no geometry. Clear " +
                "the mesh flag at offset 0xee of the BFRES and use CompressMc instead.",
                nameof(indexStream));

        int[] frames = PlanFrames(indexStream.Length, vertexStream.Length);
        int total = HeaderSize;

        foreach (int size in frames)
            total += size;

        byte[] section = new byte[total];
        Span<byte> span = section;

        WriteHeader(span, (uint)indexStream.Length, (uint)vertexStream.Length, indexAlign, vertexAlign,
                    frames.Length == 0 ? 0u : (uint)frames[0]);

        int pos = HeaderSize;
        int indexTaken = 0;
        int vertexTaken = 0;

        for (int i = 0; i < frames.Length; ++i)
        {
            // Every frame opens with the next frame's two stream sizes, least significant group
            // first. A pair of zeroes ends the section.
            uint next = i + 1 < frames.Length ? (uint)frames[i + 1] : 0;
            pos += VByte.EncodeReversed(next, span.Slice(pos));
            pos += VByte.EncodeReversed(0, span.Slice(pos));

            // The index stream drains first, then the vertex stream, matching the runtime codec.
            ReadOnlySpan<byte> source;
            int take;

            if (indexTaken < indexStream.Length)
            {
                take = Math.Min(FramePayloadSize, indexStream.Length - indexTaken);
                source = indexStream.Slice(indexTaken, take);
                indexTaken += take;
            }
            else
            {
                take = Math.Min(FramePayloadSize, vertexStream.Length - vertexTaken);
                source = vertexStream.Slice(vertexTaken, take);
                vertexTaken += take;
            }

            source.CopyTo(span.Slice(pos));
            pos += take;
        }

        if (pos != total)
            throw new InvalidOperationException($"Mesh section came out at {pos} bytes, planned {total}.");

        return section;
    }

    /// <summary>
    /// Frame sizes, in order. A frame is its two leading sizes plus its payload, and each frame has
    /// to declare the next one's length, so the list is costed from the back.
    /// </summary>
    private static int[] PlanFrames(int indexSize, int vertexSize)
    {
        List<int> payloads = new List<int>();

        for (int off = 0; off < indexSize; off += FramePayloadSize)
            payloads.Add(Math.Min(FramePayloadSize, indexSize - off));

        for (int off = 0; off < vertexSize; off += FramePayloadSize)
            payloads.Add(Math.Min(FramePayloadSize, vertexSize - off));

        int[] frames = new int[payloads.Count];
        int next = 0;

        for (int i = payloads.Count - 1; i >= 0; --i)
        {
            frames[i] = VByte.ReversedLength((uint)next) + VByte.ReversedLength(0) + payloads[i];
            next = frames[i];
        }

        return frames;
    }

    private static void WriteHeader(Span<byte> dst, uint indexSize, uint vertexSize, byte indexAlign,
                                    byte vertexAlign, uint firstFrameSize)
    {
        ResMeshCodecHeader header = default;
        header.MagicValue = ResMeshCodecHeader.Magic;
        header.Version = Version;
        header.WorkMemSize = WorkMemSizeForUncompressed;
        header.IndexOutputSize = indexSize;
        header.VertexOutputSize = vertexSize;
        header.IndexAlign = indexAlign;
        header.VertexAlign = vertexAlign;

        // Codec type 0 in the low two bits, no codec parameter above them.
        header.CompHeader.Flags = (ushort)CodecType.Null;
        header.CompHeader.SizeInfo.StreamOffset.Set(firstFrameSize);
        header.CompHeader.SizeInfo.EndOffset.Set(0);

        MemoryMarshal.Write(dst, in header);
    }

    /// <summary>
    /// Scratch the runtime needs for a type 0 section: the allocator header plus the codec object.
    /// Rounded up, and tiny next to what type 2 asks for.
    /// </summary>
    public const uint WorkMemSizeForUncompressed = 0x1000;
}
