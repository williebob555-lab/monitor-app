namespace MonitorApp.Planning;

/// <summary>How sure an engine is about what it found (docs/ARCHITECTURE-MODEL.md 3.13).</summary>
public enum FindingCertainty
{
    /// <summary>A problem shown by specified or measured values: the plug does not mate, 18 V into a 9 V-only input.</summary>
    VerifiedProblem = 1,

    /// <summary>A problem shown only by values a person typed or an engine estimated.</summary>
    LikelyProblem = 2,

    /// <summary>A value needed to decide is missing.</summary>
    Unknown = 3,

    /// <summary>Nothing found with the evidence available. Never a safety certificate.</summary>
    OkAsDeclared = 4,
}

/// <summary>
/// The one result type every engine returns (T066): what it is about, how serious and how sure, in plain words.
/// <paramref name="Subjects"/> are model object ids or port references ("objectId#portId"), so views can point at them.
/// </summary>
public sealed record Finding(string Code, FindingCertainty Certainty, string Explanation, IReadOnlyList<string> Subjects, string Engine)
{
    public bool IsProblem => Certainty is FindingCertainty.VerifiedProblem or FindingCertainty.LikelyProblem;
}
