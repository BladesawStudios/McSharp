using System;
using System.Runtime.InteropServices;
using ZstdSharp.Unsafe;
using static ZstdSharp.Unsafe.Methods;

namespace McSharp;

public sealed class EncoderOptions
{
    public int ZstdLevel { get; set; } = 8;
}

public static unsafe class McEncoder
{
    public const int MeshPackageAlignmentShift = 12;

    public const int PlainPackageAlignmentShift = 3;

    public const uint MeshSectionFlag = 0x10;

    public static uint CalculatePackageFlags(uint decompressedSize, int alignmentShift)
    {
        if ((uint)alignmentShift > 0xf)
            throw new ArgumentOutOfRangeException(nameof(alignmentShift));

        uint align = 1u << alignmentShift;
        uint aligned = (decompressedSize + align - 1) & ~(align - 1);
        uint mantissa = aligned >> alignmentShift;

        if (mantissa > 0x07FFFFFF)
            throw new ArgumentOutOfRangeException(nameof(decompressedSize),
                $"{decompressedSize} bytes does not fit a package header with an alignment shift of {alignmentShift}.");

        return (mantissa << 5) | (uint)alignmentShift;
    }

    public static bool DeclaresMeshSection(ReadOnlySpan<byte> bfres) =>
        bfres.Length > 0xee
        && bfres[0] == (byte)'F' && bfres[1] == (byte)'R' && bfres[2] == (byte)'E' && bfres[3] == (byte)'S'
        && ((bfres[0xee] >> 3) & 1) != 0;

    private static byte[] CompressPackage(ReadOnlySpan<byte> src, EncoderOptions? options)
    {
        options ??= new EncoderOptions();

        ResMeshCodecPackageHeader pkgHeader = new ResMeshCodecPackageHeader
        {
            MagicValue = ResMeshCodecPackageHeader.Magic,
            VersionMicro = 1,
            VersionMinor = 1,
            VersionMajor = 0,
            Flags = CalculatePackageFlags((uint)src.Length, PlainPackageAlignmentShift),
        };

        byte[] payload = CompressPayload(src, options.ZstdLevel);

        byte[] result = new byte[0xc + payload.Length];
        MemoryMarshal.Write(result, in pkgHeader);
        payload.CopyTo(result.AsSpan(0xc));

        return result;
    }

    public static byte[] CompressMc(ReadOnlySpan<byte> src, EncoderOptions? options = null)
    {
        if (src.Length < 0xc)
            throw new ArgumentException("Input is too small to be a BFRES file.", nameof(src));

        if (DeclaresMeshSection(src))
            throw new ArgumentException(
                "This BFRES declares an FMSH mesh section, whose vertex and index buffers live outside the " +
                "zstd payload. Packing it with CompressMc would drop them and produce a file the game cannot " +
                "load. Use CompressMcWithFmsh and pass the original package's mesh section through.",
                nameof(src));

        return CompressPackage(src, options);
    }

    public static byte[] CompressMcWithFmsh(ReadOnlySpan<byte> src, ReadOnlySpan<byte> fmshSection,
                                            uint totalDecompressedSize, EncoderOptions? options = null)
    {
        byte[] package = CompressPackage(src, options);

        ResMeshCodecPackageHeader header = MemoryMarshal.Read<ResMeshCodecPackageHeader>(package);
        header.Flags = CalculatePackageFlags(totalDecompressedSize, MeshPackageAlignmentShift) | MeshSectionFlag;
        MemoryMarshal.Write(package, in header);

        if (fmshSection.IsEmpty)
            return package;

        int aligned = (package.Length + 3) & ~3;
        byte[] result = new byte[aligned + fmshSection.Length];
        package.CopyTo(result.AsSpan());
        fmshSection.CopyTo(result.AsSpan(aligned));
        return result;
    }

    public static int FindFmshOffset(ReadOnlySpan<byte> package)
    {
        int limit = package.Length - Marshal.SizeOf<ResMeshCodecHeader>();
        for (int i = 12; i <= limit; ++i)
        {
            if (MemoryMarshal.Read<uint>(package.Slice(i, 4)) == ResMeshCodecHeader.Magic)
                return i;
        }
        return -1;
    }

    private static byte[] CompressPayload(ReadOnlySpan<byte> src, int level)
    {
        nuint bound = ZSTD_compressBound((nuint)src.Length);
        byte[] scratch = new byte[bound];

        ZSTD_CCtx_s* cctx = ZSTD_createCCtx();
        if (cctx == null)
            throw new InvalidOperationException("Failed to create a zstd compression context.");

        try
        {
            ZSTD_CCtx_setParameter(cctx, ZSTD_cParameter.ZSTD_c_experimentalParam2, (int)ZSTD_format_e.ZSTD_f_zstd1_magicless);
            ZSTD_CCtx_setParameter(cctx, ZSTD_cParameter.ZSTD_c_compressionLevel, level);
            ZSTD_CCtx_setParameter(cctx, ZSTD_cParameter.ZSTD_c_contentSizeFlag, 0);
            ZSTD_CCtx_setPledgedSrcSize(cctx, ulong.MaxValue);

            fixed (byte* pSrc = src)
            fixed (byte* pDst = scratch)
            {
                ZSTD_inBuffer_s inBuf = new ZSTD_inBuffer_s { src = pSrc, size = (nuint)src.Length, pos = 0 };
                ZSTD_outBuffer_s outBuf = new ZSTD_outBuffer_s { dst = pDst, size = bound, pos = 0 };

                nuint result = ZSTD_compressStream2(cctx, &outBuf, &inBuf, ZSTD_EndDirective.ZSTD_e_end);
                if (ZSTD_isError(result))
                    throw new InvalidOperationException($"zstd compression failed: {ZSTD_getErrorName(result)}");

                return scratch.AsSpan(0, (int)outBuf.pos).ToArray();
            }
        }
        finally
        {
            ZSTD_freeCCtx(cctx);
        }
    }
}
