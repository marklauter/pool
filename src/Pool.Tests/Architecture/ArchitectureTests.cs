using ArchUnitNET.Fluent;
using ArchUnitNET.Fluent.Extensions;
using ArchUnitNET.Loader;
using static ArchUnitNET.Fluent.ArchRuleDefinition;
using ArchitectureModel = ArchUnitNET.Domain.Architecture;

namespace Pool.Tests.Architecture;

// Encodes the design invariants from the writing-csharp guidance so drift trips the build, not code review.
public sealed class ArchitectureTests
{
    private static readonly ArchitectureModel PoolArchitecture = new ArchLoader()
        .LoadAssemblies(typeof(PoolOptions).Assembly)
        .Build();

    [Fact]
    public void AllTypesResideInPoolNamespaceTree() =>
        Verify(Types()
            .That()
            .DoNotHaveNameContaining("<") // exclude compiler-generated closures / async state machines
            .Should()
            .ResideInNamespaceMatching(@"^Pool(\..*)?$")
            .Because("New top-level namespaces outside the Pool tree require explicit design review."));

    [Fact]
    public void ConcreteClassesAreSealed() =>
        Verify(Classes()
            .That()
            .AreNotAbstract() // C# 'static' compiles to 'abstract sealed' — this also excludes static helpers
            .And()
            .DoNotHaveNameContaining("<")
            .Should()
            .BeSealed()
            .Because("writing-csharp: seal records and classes by default (enables devirtualization)."));

    [Fact]
    public void InstanceFieldsAreNotPublic() =>
        Verify(FieldMembers()
            .That()
            .AreNotStatic() // const / static readonly may be public; instance state must not be.
            .And()
            .DoNotHaveNameContaining("<") // exclude compiler-generated backing fields
            .And()
            .DoNotHaveName("value__") // exclude the implicit instance field every C# enum compiles to
            .Should()
            .NotBePublic()
            .Because("writing-csharp: immutable-by-default; no public mutable instance state."));

    [Fact]
    public void PoolDoesNotDependOnAspNetCore() =>
        Verify(Types()
            .Should()
            .NotDependOnAnyTypesThat()
            .ResideInNamespaceMatching(@"^Microsoft\.AspNetCore.*")
            .Because("Pool is a host-free, in-process library; pulling in ASP.NET Core would defeat its purpose."));

    [Fact]
    public void PoolDoesNotDependOnHosting() =>
        Verify(Types()
            .Should()
            .NotDependOnAnyTypesThat()
            .ResideInNamespaceMatching(@"^Microsoft\.Extensions\.Hosting.*")
            .Because("Pool targets host-free .NET; the consumer owns the host, not Pool."));

    [Fact]
    public void PoolDoesNotDependOnConsole() =>
        Verify(Types()
            .Should()
            .NotDependOnAnyTypesThat()
            .HaveFullName("System.Console")
            .Because("Library code routes through ILogger; direct Console writes leak into hosts that suppress stdout."));

    [Fact]
    public void DefaultStrategiesNamespaceContainsOnlyInternalTypes() =>
        Verify(Types()
            .That()
            .ResideInNamespaceMatching(@"^Pool\.DefaultStrategies$")
            .And()
            .DoNotHaveNameContaining("<")
            .Should()
            .NotBePublic()
            .Because("Default strategies are internal implementation details, not part of the public API."));

    private static void Verify(IArchRule rule)
    {
        if (!rule.HasNoViolations(PoolArchitecture))
        {
            Assert.Fail(rule.Evaluate(PoolArchitecture).ToErrorMessage());
        }
    }
}
