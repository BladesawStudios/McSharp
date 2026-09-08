using System;
using System.Runtime.InteropServices;
using ZstdSharp.Unsafe;
using static ZstdSharp.Unsafe.Methods;

namespace McSharp;

public sealed class EncoderOptions
{
    public int ZstdLevel { get; set; } = 3;
}

public static unsafe class McEncoder
{
    public static uint CalculatePackageFlags(uint decompressedSize)
    {
        uint shift = 0;
        uint value = decompressedSize;

        while ((value & 1) == 0 && value > 0x07FFFFFF)
        {
            value >>= 1;
            shift++;
        }

        return (value << 5) | (shift & 0xf);
    }

    public static byte[] CompressMc(ReadOnlySpan<byte> src, EncoderOptions? options = null)
    {
        if (src.Length < 0xc)
            throw new ArgumentException("Input is too small to be a BFRES file.", nameof(src));

        options ??= new EncoderOptions();

        ResMeshCodecPackageHeader pkgHeader = new ResMeshCodecPackageHeader
        {
            MagicValue = ResMeshCodecPackageHeader.Magic,
            VersionMicro = 0,
            VersionMinor = 1,
            VersionMajor = 0,
            Flags = CalculatePackageFlags((uint)src.Length),
        };

        byte[] payload = CompressPayload(src, options.ZstdLevel);

        byte[] result = new byte[0xc + payload.Length];
        MemoryMarshal.Write(result, in pkgHeader);
        payload.CopyTo(result.AsSpan(0xc));

        return result;
    }

    public static byte[] CompressMcWithFmsh(ReadOnlySpan<byte> src, ReadOnlySpan<byte> fmshSection,
                                            uint totalDecompressedSize, EncoderOptions? options = null)
    {
        byte[] package = CompressMc(src, options);

        ResMeshCodecPackageHeader header = MemoryMarshal.Read<ResMeshCodecPackageHeader>(package);
        header.Flags = CalculatePackageFlags(totalDecompressedSize);
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
