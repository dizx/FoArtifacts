using FoArtifacts.Core;
using Xunit;
using static FoArtifacts.Tests.TestData;

namespace FoArtifacts.Tests;

public class ModelLockTests
{
    [Fact]
    public async Task A_second_holder_waits_until_the_first_releases()
    {
        var gate = new ModelLock();
        var first = await gate.AcquireAsync(Ct);

        var second = gate.AcquireAsync(Ct);
        await Task.Delay(50, Ct);
        Assert.False(second.IsCompleted);

        first.Dispose();
        (await second).Dispose();
    }

    [Fact]
    public async Task Releasing_twice_releases_once()
    {
        var gate = new ModelLock();
        var first = await gate.AcquireAsync(Ct);
        first.Dispose();
        first.Dispose();

        var second = await gate.AcquireAsync(Ct);
        var third = gate.AcquireAsync(Ct);
        await Task.Delay(50, Ct);
        Assert.False(third.IsCompleted);

        second.Dispose();
        (await third).Dispose();
    }
}
