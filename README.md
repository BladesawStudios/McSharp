# McSharp

A C# library for decoding **and encoding** Nintendo's MeshCodec - the compression format used by
`.bfres.mc` model files and `.chunk` terrain files in *The Legend of Zelda: Tears of the Kingdom*.

Re-encoding a decoded retail file reproduces the original **byte for byte**.
## Install

```
dotnet add package MeshCodecSharp
```

The package id is `MeshCodecSharp`; the assembly and namespace are `McSharp`.

## Usage

### Decode

```csharp
using McSharp;

byte[] mc = File.ReadAllBytes("Animal_Bass.Bass.bfres.mc");
byte[]? bfres = MeshCodec.DecompressMc(mc);
```

`DecompressMc(ReadOnlySpan<byte>)` allocates its own output and scratch buffers, sizing the scratch
buffer from the file itself. In a loop, reuse one buffer across calls instead:

```csharp
byte[] work = new byte[MeshCodec.GetRequiredWorkBufferSize(mc)];

MeshCodec.TryReadPackageHeader(mc, out var header);
byte[] dst = new byte[header.GetDecompressedSize()];
bool ok = MeshCodec.DecompressMc(dst, mc, work);
```

`GetRequiredWorkBufferSize` reads the figure out of the file, so size the buffer from the largest
file you will process rather than reallocating per file. `DefaultWorkBufferSize` (256 MB) is the
ceiling no retail file exceeds, not a figure you need to allocate up front.

Decoding is thread-safe apart from `FlushDenormalHalves`, which is per-thread: set it on whichever
thread does the decoding.

`DecompressChunk` and `DecompressQuad` handle `.chunk` terrain and quad-tree payloads, and
`DecompressFmsh` decodes a bare mesh section.

### Encode

If you decoded a package, edited the BFRES body, and want to write it back, use `Repack`:

```csharp
byte[] mc = McEncoder.Repack(originalPackage, editedBody);
```

The two lower-level entry points are there when you are building a package from scratch:

```csharp
// A BFRES with no mesh section.
byte[] mc = McEncoder.CompressMc(bfres);

// A BFRES whose mesh section must be preserved.
MeshCodec.TryReadPackageHeader(original, out var header);
int fmsh = McEncoder.FindFmshOffset(original);
byte[] mc = McEncoder.CompressMcWithFmsh(bfres, original.AsSpan(fmsh), header.GetDecompressedSize());
```

`CompressMc` throws if handed a BFRES that declares a mesh section, since writing it as a plain
package would silently drop the geometry. Check with `McEncoder.DeclaresMeshSection` or
`MeshCodec.HasFmshSection`, or just call `Repack`.

Encoder settings live on `EncoderOptions`:

```csharp
byte[] mc = McEncoder.CompressMc(bfres, new EncoderOptions { ZstdLevel = 8 });
```

`ZstdLevel` defaults to `8`, which is what retail TOTK files were built with — **changing it breaks
byte-identical round-tripping**. Raise it only if you want smaller files and don't care about
matching vanilla. `CompressPayload` lets you substitute your own zstd implementation entirely.

### Encoding the mesh section

There are two ways to write a mesh section.

**Copy the existing one through.** `Repack` and `CompressMcWithFmsh` do this, and it is what you
want when you are editing the BFRES body and leaving the geometry alone. It is also the only way to
produce a file that is byte-identical to vanilla.

**Author new geometry with `FmshEncoder`.** McSharp cannot write Nintendo's codec type 2 entropy
coding, but the container carries the codec type per section and the retail loader dispatches on it
without restriction, so McSharp writes codec type 0 instead: the streams are stored verbatim and the
runtime copies them straight out.

```csharp
// Read the existing geometry out of a decoded package.
byte[] decoded = MeshCodec.DecompressMc(mc)!;
int fmsh = McEncoder.FindFmshOffset(mc);
MeshCodec.TryGetMeshLayout(decoded, mc.AsSpan(fmsh), out MeshLayout layout);

ReadOnlySpan<byte> index = decoded.AsSpan((int)layout.IndexOffset, (int)layout.IndexSize);
ReadOnlySpan<byte> vertex = decoded.AsSpan((int)layout.VertexOffset, (int)layout.VertexSize);

// Write your own streams back out.
byte[] section = FmshEncoder.EncodeUncompressed(newIndex, newVertex,
                                                layout.IndexAlign, layout.VertexAlign);
byte[] body = decoded.AsSpan(0, (int)BitConverter.ToUInt32(decoded, 0x1c)).ToArray();
byte[] package = McEncoder.CompressMcWithFmsh(body, section,
                                              McEncoder.GetTotalDecompressedSize(body, section));
```

The trade is size. Nothing in a type 0 section is compressed, so the mesh is as large as the raw GPU
buffers - re-encoding every retail model this way takes the model set from 762 MB to 2.7 GB, about
3.5x. The surrounding package is still zstd compressed as usual.

Every retail model round-trips through this path with byte-identical vertex and index streams. That
verifies the encoder against McSharp's decoder, which is a port of the game's; it is not a substitute
for testing a file in the game.

## Malformed input

The decode entry points treat everything inside a file as untrusted. Sizes, offsets and alignments
are bounds-checked before use, and a malformed, truncated or hostile file makes them return
`false`/`null` rather than throw, crash or read outside the buffers you passed in.

The `byte[]`-returning overloads allocate from sizes declared in the file, so they additionally
refuse anything over `MeshCodec.MaxDecompressedSize` (1 GB) or `DefaultWorkBufferSize`. The
`Span<byte>` overloads do not allocate and leave that budget to you.

Every decode entry point has an overload reporting why it failed, which is what you want in a batch
tool: a file you should skip is a different problem from a buffer you sized wrongly.

```csharp
if (!MeshCodec.DecompressMc(dst, mc, work, out McStatus status))
{
    switch (status)
    {
        case McStatus.NotAPackage:          // not a .mc - skip it
        case McStatus.TruncatedStream:      // damaged file
        case McStatus.CorruptStream:
        case McStatus.InvalidMeshSection:
            break;
        case McStatus.WorkBufferTooSmall:   // your buffer, not the file
            work = new byte[MeshCodec.GetRequiredWorkBufferSize(mc)];
            break;
    }
}
```

## Notes

### zstd

McSharp depends on [`BladesawStudios.ZstdSharp`](https://www.nuget.org/packages/BladesawStudios.ZstdSharp),
a fork of ZstdSharp that reverts two upstream changes postdating the zstd build Nintendo shipped.

## Credit

[MeshCodec](https://github.com/dt-12345/MeshCodec) — original C++ implementation\
[ZstdSharp.Port](https://github.com/oleg-st/ZstdSharp) — zstd library

## License

MIT - see [LICENSE](https://github.com/BladesawStudios/McSharp/blob/main/LICENSE).
