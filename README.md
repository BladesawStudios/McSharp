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
byte[] work = new byte[MeshCodec.DefaultWorkBufferSize];

MeshCodec.TryReadPackageHeader(mc, out var header);
byte[] dst = new byte[header.GetDecompressedSize()];
bool ok = MeshCodec.DecompressMc(dst, mc, work);
```

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

McSharp decodes the FMSH vertex/index streams but does not re-encode them; `CompressMcWithFmsh`
copies the existing mesh section through verbatim. That is enough to round-trip retail files and to
edit anything in the BFRES body, but not to author new geometry.

## Notes

### zstd

McSharp depends on [`BladesawStudios.ZstdSharp`](https://www.nuget.org/packages/BladesawStudios.ZstdSharp),
a fork of ZstdSharp that reverts two upstream changes postdating the zstd build Nintendo shipped.

## Credit

[MeshCodec](https://github.com/dt-12345/MeshCodec) — original C++ implementation\
[ZstdSharp.Port](https://github.com/oleg-st/ZstdSharp) — zstd library

## License

MIT - see [LICENSE](https://github.com/BladesawStudios/McSharp/blob/main/LICENSE).
