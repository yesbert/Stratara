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
            "Run the erasure again once the cause is fixed: every sweep is safe to repeat, the planes already " +
            "swept find nothing left, and the memberships that name the other planes' scopes are removed last.",
            innerException)
    {
        Plane = plane;
        _completed = completed;
    }

    /// <summary>The plane whose sweep failed.</summary>
    /// <remarks>
    /// On an exception that crossed a process boundary this reads as the first plane; the message names the plane
    /// that failed.
    /// </remarks>
    public ErasurePlane Plane { get; }

    /// <summary>The planes swept before the failure. Running the erasure again repeats them harmlessly; empty on an exception that crossed a process boundary, where only its type, message and inner exception are carried.</summary>
    public ErasureReport Completed => _completed ?? new ErasureReport([]);

    private readonly ErasureReport? _completed;
}
