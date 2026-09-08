using System.Runtime.CompilerServices;

namespace McSharp.Internal;

internal enum BitStreamDirection
{
    Backwards = -1,
    Forwards = 1,
}

internal unsafe struct BitStreamReader
{
    private ulong* _stream;
    private ulong _remainder;
    private uint _offset;
    private BitStreamDirection _direction;

    public BitStreamReader(ulong* stream)
    {
        _stream = stream;
        _remainder = 0;
        _offset = 0;
        _direction = BitStreamDirection.Forwards;
    }

    public BitStreamReader(ulong* stream, BitStreamDirection dir)
    {
        _stream = stream;
        _remainder = 0;
        _offset = 0;
        _direction = dir;
    }

    public void SetDirection(BitStreamDirection dir) => _direction = dir;

    public void SetStream(ulong* stream) => _stream = stream;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong Read(uint nbits)
    {
        ulong value = (*_stream >> (int)(_offset & 0x3fu)) | _remainder;

        _stream = (ulong*)((byte*)_stream - ((_offset >> 3) ^ 7));
        _offset = (_offset | 0x38u) - nbits;
        _remainder = value << (int)nbits;

        return value >> (int)(0x40u - nbits);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong ReadForwards(uint nbits)
    {
        ulong value = (Bits.Swap(*_stream) >> (int)(_offset & 0x3fu)) | _remainder;

        _stream = (ulong*)((byte*)_stream + ((_offset >> 3) ^ 7));
        _offset = (_offset | 0x38u) - nbits;
        _remainder = value << (int)nbits;

        return value >> (int)(0x40u - nbits);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong ReadRaw()
    {
        ulong value = *_stream >> (int)(_offset & 0x3fu);

        _stream = (ulong*)((byte*)_stream - ((_offset >> 3) ^ 7));

        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong ReadRawForwards()
    {
        ulong value = Bits.Swap(*_stream) >> (int)(_offset & 0x3fu);

        _stream = (ulong*)((byte*)_stream + ((_offset >> 3) ^ 7));

        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong ReadZeroes()
    {
        ulong value = (*_stream >> (int)(_offset & 0x3fu)) | _remainder;
        uint numZeroes = Bits.Clz(value);

        _stream = (ulong*)((byte*)_stream - ((_offset >> 3) ^ 7));
        _offset = (_offset | 0x38u) - (numZeroes + 1);
        _remainder = value << (int)(numZeroes + 1);

        return numZeroes;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong DirectionalRead(uint nbits)
    {
        bool reverse = IsReverse;
        ulong value = ((reverse ? *_stream : Bits.Swap(*_stream)) >> (int)(_offset & 0x3fu)) | _remainder;

        int delta = (int)((_offset >> 3) ^ 7);
        _stream = (ulong*)((byte*)_stream + (reverse ? -delta : delta));
        _offset = (_offset | 0x38u) - nbits;
        _remainder = value << (int)nbits;

        return value >> (int)(0x40u - nbits);
    }

    public uint BitOffset
    {
        readonly get => _offset;
        set => _offset = value;
    }

    public ulong Remainder
    {
        readonly get => _remainder;
        set => _remainder = value;
    }

    public readonly ulong* Stream => _stream;

    public readonly bool IsReverse => _direction != BitStreamDirection.Forwards;
}

internal sealed unsafe class DecompContext
{
    public BitStreamReader BitStream0;
    public BitStreamReader BitStream1;
    public BitStreamReader BitStream2;
    public byte* CurrentPos;
}
