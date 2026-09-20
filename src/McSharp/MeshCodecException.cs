using System;

namespace McSharp;

public sealed class MeshCodecException : Exception
{
    public MeshCodecException(string message) : base(message) { }
}
