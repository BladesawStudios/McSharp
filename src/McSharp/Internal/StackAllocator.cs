using System;

namespace McSharp.Internal;

internal static class McResult
{
    public const uint SizeMismatch = 0x80000002;
    public const uint InvalidCodec = 0x80000007;
    public const uint Error8 = 0x80000008;
    public const uint Error20 = 0x80000020;
}

public sealed class MeshCodecException : Exception
{
    public MeshCodecException(string message) : base(message) { }
}

internal unsafe struct StreamContext
{
    public byte* Stream;
    public nuint Size;
    public ulong Alignment;
}

internal sealed unsafe class StackAllocator
{
    public const nuint HeaderSize = 0x80;

    private readonly byte* _memory;
    private readonly nuint _memorySize;
    private nuint _memoryOffset;
    private nuint _lastAllocationStart;
    private nuint _peakMemoryUsage;

    private ICodec? _codec;
    private uint _streamOffset;
    private uint _frameEndOffset;

    private readonly DecompContext _decompContext = new DecompContext();

    public StackAllocator(byte* mem, nuint memSize)
    {
        _memory = mem;
        _memorySize = memSize;
        _memoryOffset = 0;
        _lastAllocationStart = 0;
        _peakMemoryUsage = 0;
    }

    public nuint PeakMemoryUsage => _peakMemoryUsage;

    public ICodec Codec
    {
        get => _codec!;
        set => _codec = value;
    }

    public void SetStreamSizes(uint stream, uint end)
    {
        _streamOffset = stream;
        _frameEndOffset = end;
    }

    private static ulong GetBlockData(void* ptr) => *(ulong*)((byte*)ptr - 8);

    private static void SetBlockData(void* ptr, ulong data) => *(ulong*)((byte*)ptr - 8) = data;

    private static nuint PrevAllocStartOffset(ulong data) => (nuint)(data & 0x7fffffff);

    private static nuint PrevAllocEndOffset(ulong data) => (nuint)((data >> 0x1f) & 0xffff);

    private static bool NeedsCoalescing(ulong data) => (data >> 0x2f) != 0;

    public void* Alloc(nuint size, long alignment)
    {
        if (size == 0)
            return null;

        nuint start = (nuint)(((ulong)alignment + _memoryOffset + 7) & (ulong)(-alignment));
        nuint end = start + size;
        if (end > _memorySize)
            throw new MeshCodecException($"MeshCodec work buffer exhausted (needed {end} bytes, have {_memorySize}).");

        void* ptr = _memory + start;
        SetBlockData(ptr, (ulong)(start - _lastAllocationStart) | ((ulong)(start - _memoryOffset) << 0x1f));
        _memoryOffset = end;
        _lastAllocationStart = start;
        if (end > _peakMemoryUsage)
            _peakMemoryUsage = end;

        return ptr;
    }

    public void Free(void* ptr)
    {
        if (ptr == null)
            return;

        nuint startOffset = _lastAllocationStart;

        if (_memory + _lastAllocationStart != (byte*)ptr)
        {
            SetBlockData(ptr, GetBlockData(ptr) | (1ul << 0x2f));
            return;
        }

        ulong info = GetBlockData(ptr);
        nuint memOffset = startOffset - PrevAllocEndOffset(info);
        startOffset -= PrevAllocStartOffset(info);

        ulong blockInfo = GetBlockData(_memory + startOffset);
        if (startOffset != 0 && NeedsCoalescing(blockInfo))
        {
            while (NeedsCoalescing(blockInfo))
            {
                memOffset = startOffset;
                startOffset -= PrevAllocStartOffset(blockInfo);
                if (startOffset == 0)
                    break;
                blockInfo = GetBlockData(_memory + startOffset);
            }
            memOffset -= PrevAllocEndOffset(blockInfo);
        }

        _memoryOffset = memOffset;
        _lastAllocationStart = startOffset;
    }

    public int DecompressFrame(byte* data, nuint size)
    {
        uint bitStreamOffset0 = _streamOffset;
        uint bitStreamOffset1 = _frameEndOffset;
        if (bitStreamOffset1 + bitStreamOffset0 != size)
            return unchecked((int)McResult.SizeMismatch);

        byte* ptr = data;

        _streamOffset = VByte.DecodeReversed(ref ptr);
        _frameEndOffset = VByte.DecodeReversed(ref ptr);

        DecompContext ctx = _decompContext;
        ctx.CurrentPos = ptr;
        ctx.BitStream0 = new BitStreamReader((ulong*)(data + bitStreamOffset0 - 8), BitStreamDirection.Backwards);
        ctx.BitStream1 = new BitStreamReader((ulong*)(data + bitStreamOffset0), BitStreamDirection.Forwards);
        ctx.BitStream2 = new BitStreamReader((ulong*)(data + bitStreamOffset0 + bitStreamOffset1 - 8), BitStreamDirection.Backwards);

        _codec!.Decompress(ctx);

        if (_frameEndOffset + _streamOffset != 0)
            return (int)(_frameEndOffset + _streamOffset);

        return 0;
    }

    private static ICodec CreateCodec(CodecType type, StackAllocator allocator)
    {
        switch (type)
        {
            case CodecType.MeshCodec:
                allocator.Alloc(0x420, 8);
                return new MeshCodecCodec();
            case CodecType.ZStandard:
                allocator.Alloc(0x30, 8);
                return new ZStdCodec();
            default:
                allocator.Alloc(0x28, 8);
                return new NullCodec();
        }
    }

    private static bool UnkValueIsValid(uint value) => value < 0x400;

    public static int Initialize(out StackAllocator? outAllocator, CodecType codec, uint codecParam,
                                 in StreamContext indexStream, in StreamContext vertexStream,
                                 byte* workMemory, nuint workMemorySize, ResFrameSize sizes, ulong type)
    {
        outAllocator = null;
        if (type != 6)
            return unchecked((int)McResult.SizeMismatch);

        if (workMemorySize <= HeaderSize)
            return unchecked((int)McResult.SizeMismatch);

        StackAllocator allocator = new StackAllocator(workMemory + HeaderSize, workMemorySize - HeaderSize);
        ICodec c = CreateCodec(codec, allocator);
        c.Initialize(indexStream, vertexStream, codecParam, allocator);
        allocator.Codec = c;

        uint streamOffset = sizes.StreamOffset.Value;
        uint endOffset = sizes.EndOffset.Value;

        allocator.SetStreamSizes(streamOffset, endOffset);

        outAllocator = allocator;

        return (int)(streamOffset + endOffset);
    }

    public static int Create(out StackAllocator? outAllocator, in StreamContext indexStream, in StreamContext vertexStream,
                             byte* workMemory, nuint workMemorySize, in ResCompressionHeader res, ulong a4)
    {
        outAllocator = null;
        if (a4 != 8)
            return unchecked((int)McResult.Error20);

        CodecType codec = res.GetCodecType();
        uint codecParam = (uint)(res.Flags >> 2);

        if (codec == CodecType.Invalid)
            return unchecked((int)McResult.InvalidCodec);

        if (!UnkValueIsValid(codecParam))
            return unchecked((int)McResult.Error8);

        return Initialize(out outAllocator, codec, codecParam, indexStream, vertexStream,
                          workMemory, workMemorySize, res.SizeInfo, 6);
    }

    public static uint ConvertResult(ulong raw)
    {
        if ((raw >> 0x1f) != 0)
        {
            if (raw == McResult.SizeMismatch)
                return 0x1c;
            if (raw > 0x80000003)
                return unchecked((uint)raw + 0x7ffffffcu);
            return 2;
        }

        return 0;
    }
}

internal unsafe interface ICodec
{
    void Initialize(in StreamContext indexStream, in StreamContext vertexStream, uint param, StackAllocator allocator);
    void Decompress(DecompContext ctx);
    void Close();
}
