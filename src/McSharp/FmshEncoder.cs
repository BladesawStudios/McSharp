using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using McSharp.Internal;
using ZstdSharp.Unsafe;
using static ZstdSharp.Unsafe.Methods;

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
public static unsafe class FmshEncoder
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
        Validate(indexStream, vertexStream, indexAlign, vertexAlign);

        int[] frames = PlanFrames(indexStream.Length, vertexStream.Length);
        int total = HeaderSize;

        foreach (int size in frames)
            total += size;

        byte[] section = new byte[total];
        Span<byte> span = section;

        WriteHeader(span, (uint)indexStream.Length, (uint)vertexStream.Length, indexAlign, vertexAlign,
                    frames.Length == 0 ? 0u : (uint)frames[0], CodecType.Null, WorkMemSizeForUncompressed);

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
    /// Bytes a codec type 1 block expands to. The runtime decodes <c>min(remaining, 0x20000)</c>
    /// per block.
    /// </summary>
    public const int BlockPayloadSize = 0x20000;

    /// <summary>
    /// Input bytes a codec type 1 frame carries before the runtime closes it and starts the next.
    /// </summary>
    public const int FrameInputLimit = 0x25800;

    /// <summary>
    /// Builds a mesh section holding <paramref name="indexStream"/> and
    /// <paramref name="vertexStream"/> as zstd blocks (codec type 1). Same geometry as
    /// <see cref="EncodeUncompressed"/> at a fraction of the size, in exchange for a real scratch
    /// buffer where a type 0 section needs almost none.
    /// </summary>
    public static byte[] EncodeZstd(ReadOnlySpan<byte> indexStream, ReadOnlySpan<byte> vertexStream,
                                    byte indexAlign = 8, byte vertexAlign = 8, int level = 8)
    {
        Validate(indexStream, vertexStream, indexAlign, vertexAlign);

        // The runtime skips codec setup entirely when the vertex stream is empty, leaving nothing
        // that can decode the blocks, so a type 1 section has to carry one.
        if (vertexStream.IsEmpty)
            throw new ArgumentException(
                "Codec type 1 cannot represent a mesh section with an empty vertex stream, because " +
                "the runtime leaves the codec uninitialised in that case. Use EncodeUncompressed.",
                nameof(vertexStream));

        // The runtime decodes every block through one context, so matches reach back across block
        // and stream boundaries. Compress from one contiguous buffer to match.
        byte[] source = new byte[indexStream.Length + vertexStream.Length];
        indexStream.CopyTo(source);
        vertexStream.CopyTo(source.AsSpan(indexStream.Length));

        List<Block> blocks = CompressBlocks(source, indexStream.Length, vertexStream.Length, level);
        List<List<Block>> frames = GroupIntoFrames(blocks);
        int[] sizes = SizeFrames(frames);

        int total = HeaderSize;
        foreach (int size in sizes)
            total += size;

        byte[] section = new byte[total];
        WriteHeader(section, (uint)indexStream.Length, (uint)vertexStream.Length, indexAlign, vertexAlign,
                    sizes.Length == 0 ? 0u : (uint)sizes[0], CodecType.ZStandard, WorkMemSizeForZstd);

        int pos = HeaderSize;

        for (int i = 0; i < frames.Count; ++i)
        {
            Span<byte> frame = section.AsSpan(pos, sizes[i]);
            uint next = i + 1 < frames.Count ? (uint)sizes[i + 1] : 0;

            int w = VByte.EncodeReversed(next, frame);
            w += VByte.EncodeReversed(0, frame.Slice(w));

            // One bit per block says whether it was stored rather than compressed. The runtime
            // reads these backwards from the end of the frame, and takes one extra past the last
            // block before it stops.
            BitStreamWriter bits = default;

            foreach (Block block in frames[i])
            {
                bits.Write(frame, block.Stored);

                if (!block.Stored)
                    w += VByte.EncodeForward((uint)block.Payload.Length, frame.Slice(w));

                block.Payload.CopyTo(frame.Slice(w));
                w += block.Payload.Length;
            }

            bits.Write(frame, false);

            if (w + BitStreamWriter.ByteLength(bits.Count) > frame.Length)
                throw new InvalidOperationException("Frame data and bit stream overlap.");

            pos += sizes[i];
        }

        if (pos != total)
            throw new InvalidOperationException($"Mesh section came out at {pos} bytes, planned {total}.");

        return section;
    }

    private sealed class Block
    {
        public byte[] Payload = Array.Empty<byte>();
        public bool Stored;

        /// <summary>Input bytes the runtime charges against the frame limit for this block.</summary>
        public int Cost => Payload.Length + (Stored ? 0 : VByte.ForwardLength((uint)Payload.Length));
    }

    private static List<Block> CompressBlocks(byte[] source, int indexSize, int vertexSize, int level)
    {
        List<int> outSizes = new List<int>();

        for (int off = 0; off < indexSize; off += BlockPayloadSize)
            outSizes.Add(Math.Min(BlockPayloadSize, indexSize - off));

        for (int off = 0; off < vertexSize; off += BlockPayloadSize)
            outSizes.Add(Math.Min(BlockPayloadSize, vertexSize - off));

        List<Block> blocks = new List<Block>(outSizes.Count);

        ZSTD_CCtx_s* cctx = ZSTD_createCCtx();

        if (cctx == null)
            throw new InvalidOperationException("Failed to create a zstd compression context.");

        try
        {
            nuint begin = ZSTD_compressBegin(cctx, level);

            if (ZSTD_isError(begin))
                throw new InvalidOperationException($"zstd compressBegin failed: {ZSTD_getErrorName(begin)}");

            if (ZSTD_getBlockSize(cctx) < BlockPayloadSize)
                throw new InvalidOperationException(
                    $"zstd level {level} gives a {ZSTD_getBlockSize(cctx)} byte block limit, under the " +
                    $"{BlockPayloadSize} the format needs. Use a level with a larger window.");

            byte[] scratch = new byte[ZSTD_compressBound(BlockPayloadSize)];
            int offset = 0;

            fixed (byte* pSource = source)
            fixed (byte* pScratch = scratch)
            {
                foreach (int outSize in outSizes)
                {
                    nuint written = ZSTD_compressBlock(cctx, pScratch, (nuint)scratch.Length,
                                                       pSource + offset, (nuint)outSize);

                    if (ZSTD_isError(written))
                        throw new InvalidOperationException($"zstd compressBlock failed: {ZSTD_getErrorName(written)}");

                    Block block = new Block();

                    // Zero means zstd judged the block incompressible and wrote nothing. That is the
                    // only case we may store raw: discarding a block it did produce would leave the
                    // decoder a block behind on entropy tables.
                    if (written == 0)
                    {
                        block.Stored = true;
                        block.Payload = source.AsSpan(offset, outSize).ToArray();
                    }
                    else
                    {
                        block.Payload = scratch.AsSpan(0, (int)written).ToArray();
                    }

                    blocks.Add(block);
                    offset += outSize;
                }
            }
        }
        finally
        {
            ZSTD_freeCCtx(cctx);
        }

        return blocks;
    }

    /// <summary>
    /// Splits blocks the way the runtime does: it keeps pulling blocks out of one frame until the
    /// input it has read reaches <see cref="FrameInputLimit"/>, and then the frame is done.
    /// </summary>
    private static List<List<Block>> GroupIntoFrames(List<Block> blocks)
    {
        List<List<Block>> frames = new List<List<Block>>();
        List<Block> current = new List<Block>();
        int cost = 0;

        foreach (Block block in blocks)
        {
            current.Add(block);
            cost += block.Cost;

            if (cost >= FrameInputLimit)
            {
                frames.Add(current);
                current = new List<Block>();
                cost = 0;
            }
        }

        if (current.Count > 0)
            frames.Add(current);

        return frames;
    }

    private static int[] SizeFrames(List<List<Block>> frames)
    {
        int[] sizes = new int[frames.Count];
        int next = 0;

        for (int i = frames.Count - 1; i >= 0; --i)
        {
            int data = VByte.ReversedLength((uint)next) + VByte.ReversedLength(0);

            foreach (Block block in frames[i])
                data += block.Cost;

            // The bit stream sits at the end of the frame and the reader primes itself eight bytes
            // below that end, so a frame is never shorter than eight bytes.
            int size = data + BitStreamWriter.ByteLength(frames[i].Count + 1);
            sizes[i] = Math.Max(size, 8);
            next = sizes[i];
        }

        return sizes;
    }

    /// <summary>
    /// Scratch a type 1 section needs: the allocator header, the codec object and a static zstd
    /// decompression context.
    /// </summary>
    public static uint WorkMemSizeForZstd
    {
        get
        {
            uint needed = (uint)(StackAllocator.HeaderSize + 0x40 + Zstd.DCtxWorkspaceSize);
            return Math.Max(0x28000u, (needed + 0xfff) & ~0xfffu);
        }
    }

    private static void Validate(ReadOnlySpan<byte> indexStream, ReadOnlySpan<byte> vertexStream,
                                byte indexAlign, byte vertexAlign)
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
                                    byte vertexAlign, uint firstFrameSize, CodecType codec, uint workMemSize)
    {
        ResMeshCodecHeader header = default;
        header.MagicValue = ResMeshCodecHeader.Magic;
        header.Version = Version;
        header.WorkMemSize = workMemSize;
        header.IndexOutputSize = indexSize;
        header.VertexOutputSize = vertexSize;
        header.IndexAlign = indexAlign;
        header.VertexAlign = vertexAlign;

        // Codec type in the low two bits, no codec parameter above them.
        header.CompHeader.Flags = (ushort)codec;
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
