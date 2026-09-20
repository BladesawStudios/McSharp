using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using McSharp.Internal;
using ZstdSharp.Unsafe;
using static ZstdSharp.Unsafe.Methods;

namespace McSharp;

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

    public uint End => VertexOffset + VertexSize;
}

public static unsafe class FmshEncoder
{
    public const int FramePayloadSize = 0x40000;

    private const uint Version = 1;

    private static readonly int HeaderSize = Marshal.SizeOf<ResMeshCodecHeader>();

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
            uint next = i + 1 < frames.Length ? (uint)frames[i + 1] : 0;
            pos += VByte.EncodeReversed(next, span.Slice(pos));
            pos += VByte.EncodeReversed(0, span.Slice(pos));

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

    public const int BlockPayloadSize = 0x20000;

    public const int FrameInputLimit = 0x25800;

    public static byte[] EncodeZstd(ReadOnlySpan<byte> indexStream, ReadOnlySpan<byte> vertexStream,
                                    byte indexAlign = 8, byte vertexAlign = 8, int level = 8)
    {
        Validate(indexStream, vertexStream, indexAlign, vertexAlign);

        if (vertexStream.IsEmpty)
            throw new ArgumentException(
                "Codec type 1 cannot represent a mesh section with an empty vertex stream, because " +
                "the runtime leaves the codec uninitialised in that case. Use EncodeUncompressed.",
                nameof(vertexStream));

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

            int size = data + BitStreamWriter.ByteLength(frames[i].Count + 1);
            sizes[i] = Math.Max(size, 8);
            next = sizes[i];
        }

        return sizes;
    }

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

        header.CompHeader.Flags = (ushort)codec;
        header.CompHeader.SizeInfo.StreamOffset.Set(firstFrameSize);
        header.CompHeader.SizeInfo.EndOffset.Set(0);

        MemoryMarshal.Write(dst, in header);
    }

    public const uint WorkMemSizeForUncompressed = 0x1000;
}
