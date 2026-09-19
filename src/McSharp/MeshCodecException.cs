using System;

namespace McSharp;

/// <summary>
/// Thrown when the decoder runs out of scratch space. The public decode entry points on
/// <see cref="MeshCodec"/> catch it and report failure through their return value instead, so it
/// only surfaces from the internal codec layer.
/// </summary>
public sealed class MeshCodecException : Exception
{
    public MeshCodecException(string message) : base(message) { }
}
