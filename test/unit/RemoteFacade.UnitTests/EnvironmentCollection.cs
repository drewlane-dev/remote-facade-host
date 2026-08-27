namespace RemoteFacade.UnitTests;

/// <summary>
/// Everything that writes to the REAL process environment shares this
/// collection, so xUnit never runs two of them at once.
///
/// Environment variables are process-global. Two classes setting
/// SampleOptions__* concurrently would see each other's values, and the
/// failures would be intermittent -- the worst kind, because a green run
/// proves nothing and a red one looks like a real defect.
/// </summary>
[CollectionDefinition("environment", DisableParallelization = true)]
public sealed class EnvironmentCollection;
