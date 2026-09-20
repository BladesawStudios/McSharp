namespace McSharp;

public enum McStatus
{
    Ok = 0,

    NotAPackage,

    UnsupportedVersion,

    DestinationTooSmall,

    WorkBufferTooSmall,

    TruncatedStream,

    CorruptStream,

    InvalidMeshSection,

    SizeLimitExceeded,
}
