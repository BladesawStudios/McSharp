namespace McSharp;

/// <summary>
/// Why a decode failed. Every decode entry point on <see cref="MeshCodec"/> has an overload taking
/// an <c>out McStatus</c>, so a caller can tell a file that is not a package from one that is
/// damaged, and either of those from a buffer it sized wrongly itself.
/// </summary>
public enum McStatus
{
    /// <summary>The file decoded.</summary>
    Ok = 0,

    /// <summary>
    /// The input is not a file of this kind at all: too short to hold the header, or the magic does
    /// not match. Skip it.
    /// </summary>
    NotAPackage,

    /// <summary>The container version is one this build does not know how to read.</summary>
    UnsupportedVersion,

    /// <summary>
    /// The destination is smaller than the size the file declares. Caller error: size it from
    /// <c>GetDecompressedSize()</c> or <c>DecompressedSize</c>.
    /// </summary>
    DestinationTooSmall,

    /// <summary>
    /// The scratch buffer is smaller than the file asks for. Caller error: size it from
    /// <see cref="MeshCodec.GetRequiredWorkBufferSize"/>.
    /// </summary>
    WorkBufferTooSmall,

    /// <summary>The file ends part way through a stream it declared.</summary>
    TruncatedStream,

    /// <summary>A compressed stream did not decode. The file is damaged.</summary>
    CorruptStream,

    /// <summary>
    /// The trailing FMSH mesh section is missing, or its sizes, offsets or alignments disagree with
    /// the rest of the file.
    /// </summary>
    InvalidMeshSection,

    /// <summary>
    /// A size declared in the file exceeds what the allocating overloads will allocate for. The
    /// <see cref="System.Span{T}"/> overloads do not apply this limit.
    /// </summary>
    SizeLimitExceeded,
}
