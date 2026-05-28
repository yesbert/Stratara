namespace Stratara.Projections;

/// <summary>
/// Empty marker interface used by reflection-based scanners (typically
/// <c>AddProjectionsFromAssemblyContaining&lt;IStrataraProjectionsMarker&gt;()</c>) to identify the
/// <c>Stratara.Projections</c> assembly without taking a hard type dependency.
/// </summary>
public interface IStrataraProjectionsMarker;
