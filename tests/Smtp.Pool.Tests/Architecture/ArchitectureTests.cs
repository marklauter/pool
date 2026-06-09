using ArchUnitNET.Fluent;
using ArchUnitNET.Fluent.Extensions;
using ArchUnitNET.Loader;
using static ArchUnitNET.Fluent.ArchRuleDefinition;
using ArchitectureModel = ArchUnitNET.Domain.Architecture;

namespace Smtp.Pool.Tests.Architecture;

// Encodes the design invariants from the writing-csharp guidance for the sample so drift trips the build, not code review.
public sealed class ArchitectureTests
{
    private static readonly ArchitectureModel SmtpPoolArchitecture = new ArchLoader()
        .LoadAssemblies(typeof(SmtpHostOptions).Assembly)
        .Build();

    [Fact]
    public void AllTypesResideInSmtpPoolNamespaceTree() =>
        Verify(Types()
            .That()
            .DoNotHaveNameContaining("<") // exclude compiler-generated closures / async state machines
            .Should()
            .ResideInNamespaceMatching(@"^Smtp\.Pool(\..*)?$")
            .Because("New top-level namespaces outside the Smtp.Pool tree require explicit design review."));

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
            .Should()
            .NotBePublic()
            .Because("writing-csharp: immutable-by-default; no public mutable instance state."));

    [Fact]
    public void PoolStrategyImplementationsAreInternal() =>
        Verify(Classes()
            .That()
            .HaveNameEndingWith("Factory")
            .Or()
            .HaveNameEndingWith("PreparationStrategy")
            .Should()
            .NotBePublic()
            .Because("writing-csharp: public interfaces, internal implementations — the factory and preparation strategy are wired through DI, never referenced directly."));

    [Fact]
    public void SampleDoesNotDependOnAspNetCore() =>
        Verify(Types()
            .Should()
            .NotDependOnAnyTypesThat()
            .ResideInNamespaceMatching(@"^Microsoft\.AspNetCore.*")
            .Because("The pooling sample is host- and framework-agnostic; an ASP.NET Core dependency would narrow it."));

    private static void Verify(IArchRule rule)
    {
        if (!rule.HasNoViolations(SmtpPoolArchitecture))
        {
            Assert.Fail(rule.Evaluate(SmtpPoolArchitecture).ToErrorMessage());
        }
    }
}
