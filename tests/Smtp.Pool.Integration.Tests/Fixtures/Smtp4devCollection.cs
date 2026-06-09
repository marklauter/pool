using System.Diagnostics.CodeAnalysis;

namespace Smtp.Pool.Integration.Tests.Fixtures;

/// <summary>
/// Shares one <see cref="Smtp4devFixture"/> (one container) across every test in the collection.
/// No code — it exists only to host the collection definition and the fixture interface.
/// </summary>
[CollectionDefinition("Smtp4devCollection")]
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "xUnit collection-definition naming convention")]
public sealed class Smtp4devCollection : ICollectionFixture<Smtp4devFixture>;
