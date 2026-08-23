namespace Stratara.Abstractions.Erasure;

/// <summary>
/// Raised when one plane's sweep fails during a composed erasure. The erasure stops at that plane
/// rather than continuing, so a later plane never shreds the key material an earlier, failed plane
/// still needs to read.
/// </summary>
public sealed class ErasureIncompleteException : Exception
{
    /// <summary>Creates the exception for a plane that failed.</summary>
    /// <param name="plane">The plane whose sweep failed.</param>
    /// <param name="completed">The planes that had already been swept when the failure occurred.</param>
    /// <param name="innerException">The failure the plane's sweep raised.</param>
    public ErasureIncompleteException(ErasurePlane plane, ErasureReport completed, Exception innerException)
        : base(
            $"Erasure stopped at the {plane} plane. {completed.Planes.Count} plane(s) were swept before it. " +
            "Resume from the failed plane rather than restarting: the planes already swept are gone, and " +
            "key material is deliberately shredded last so nothing swept earlier has become unreadable.",
            innerException)
    {
        Plane = plane;
        Completed = completed;
    }

    /// <summary>The plane whose sweep failed.</summary>
    public ErasurePlane Plane { get; }

    /// <summary>The planes swept before the failure. Resume from <see cref="Plane"/>.</summary>
    public ErasureReport Completed { get; }
}
