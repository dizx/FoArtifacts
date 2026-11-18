using FoArtifacts.Core;
using Xunit;
using static FoArtifacts.Tests.TestData;

namespace FoArtifacts.Tests;

public class ModelCheckTests
{
    [Fact]
    public async Task Finds_a_custom_model_ignoring_case()
    {
        var (info, error) = await new ModelCheck(new FakeD365fo()).LookUpAsync("contosofleet", Ct);

        Assert.Null(error);
        Assert.True(info!.Found);
        Assert.True(info.IsCustom);
    }

    [Fact]
    public async Task Reports_a_model_that_is_not_custom()
    {
        var fake = new FakeD365fo();
        fake.Models[0] = ("ContosoFleet", false);

        var (info, _) = await new ModelCheck(fake).LookUpAsync("ContosoFleet", Ct);

        Assert.True(info!.Found);
        Assert.False(info.IsCustom);
    }

    [Fact]
    public async Task Reports_a_missing_model_with_the_known_names()
    {
        var (info, error) = await new ModelCheck(new FakeD365fo()).LookUpAsync("Nope", Ct);

        Assert.Null(error);
        Assert.False(info!.Found);
        Assert.Equal(new[] { "ContosoFleet" }, info.Known);
    }

    [Fact]
    public async Task Reports_when_d365fo_is_not_usable()
    {
        var fake = new FakeD365fo { Override = a => a[0] == "models" ? FakeD365fo.Fail("NO_INDEX", "No index", "run index build") : null };

        var (info, error) = await new ModelCheck(fake).LookUpAsync("ContosoFleet", Ct);

        Assert.Null(info);
        Assert.Equal("d365fo is not usable: NO_INDEX: No index Hint: run index build", error);
    }
}
