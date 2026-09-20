using FakeItEasy;
using Vfps.Components;
using Vfps.PseudonymGenerators;

namespace Vfps.Tests.ComponentsTests;

/// <summary>
/// What the namespace-creation form offers. Offering a method this deployment cannot generate
/// with puts a choice in the form whose only possible outcome is an error.
/// </summary>
public class PseudonymGenerationMethodDisplayTests
{
    [Fact]
    public void SelectableFor_WithoutAVoprfServer_ShouldNotOfferVoprf()
    {
        var methods = PseudonymGenerationMethodDisplay.SelectableFor(
            new PseudonymizationMethodsLookup()
        );

        methods.Should().NotContain(PseudonymGenerationMethod.Voprf);
        methods
            .Should()
            .Contain(PseudonymGenerationMethod.Uuid4, "the random methods are always available");
    }

    [Fact]
    public void SelectableFor_WithAVoprfServer_ShouldOfferVoprf()
    {
        var lookup = new PseudonymizationMethodsLookup(A.Fake<IValueDependentPseudonymGenerator>());

        PseudonymGenerationMethodDisplay
            .SelectableFor(lookup)
            .Should()
            .Contain(PseudonymGenerationMethod.Voprf);
    }

    // Unspecified is a protobuf default, not a choice a user makes.
    [Fact]
    public void SelectableFor_ShouldNeverOfferUnspecified()
    {
        PseudonymGenerationMethodDisplay
            .SelectableFor(new PseudonymizationMethodsLookup())
            .Should()
            .NotContain(PseudonymGenerationMethod.Unspecified);
    }

    // Every offered method must have a friendly name, so a newly added one cannot show up in the
    // dropdown as its raw enum name.
    [Fact]
    public void FriendlyName_ShouldBeSetForEverySelectableMethod()
    {
        var lookup = new PseudonymizationMethodsLookup(A.Fake<IValueDependentPseudonymGenerator>());

        foreach (var method in PseudonymGenerationMethodDisplay.SelectableFor(lookup))
        {
            PseudonymGenerationMethodDisplay
                .FriendlyName(method)
                .Should()
                .NotBe(method.ToString(), $"'{method}' should have a friendly name");
        }
    }
}
