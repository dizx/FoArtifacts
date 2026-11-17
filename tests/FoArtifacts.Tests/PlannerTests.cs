using FoArtifacts.Core;
using Xunit;
using static FoArtifacts.Tests.TestData;

namespace FoArtifacts.Tests;

public class PlannerTests
{
    private static IReadOnlyList<Stage> PlanOk(params TaskInput[] tasks)
    {
        var (stages, errors) = Planner.Plan(Expanded("CFlt", tasks), Map);
        Assert.Empty(errors);
        return stages!;
    }

    private static string[][] Types(IReadOnlyList<Stage> stages) =>
        stages.Select(s => s.Tasks.Select(t => t.Type).ToArray()).ToArray();

    [Fact]
    public void Independent_tasks_share_one_stage_ordered_by_rank()
    {
        var stages = PlanOk(T("role", "R"), T("enum", "E"), T("table", "Tb"));
        var stage = Assert.Single(stages);
        Assert.Equal(1, stage.Number);
        Assert.Equal(new[] { "enum", "table", "role" }, stage.Tasks.Select(t => t.Type));
    }

    [Fact]
    public void A_feature_runs_in_dependency_order()
    {
        var stages = PlanOk(
            T("enum", "VehicleStatus"),
            T("edt", "VehicleId"),
            T("table", "Vehicle", """{"field":["VehicleId:VehicleId:mandatory","Status:VehicleStatus"]}"""),
            T("form", "Vehicle", """{"table":"Vehicle"}"""),
            T("menu-item", "Vehicle", """{"object":"Vehicle"}"""),
            T("privilege", "VehicleMaintain", """{"entry-point":"Vehicle"}"""),
            T("duty", "VehicleMaintain", """{"privilege":"VehicleMaintain"}"""),
            T("role", "VehicleManager", """{"duty":"VehicleMaintain"}"""));

        Assert.Equal(
            new[]
            {
                new[] { "enum", "edt" }, new[] { "table" }, new[] { "form" }, new[] { "menu-item" },
                new[] { "privilege" }, new[] { "duty" }, new[] { "role" },
            },
            Types(stages));
        Assert.Equal(Enumerable.Range(1, 7), stages.Select(s => s.Number));
    }

    [Fact]
    public void A_task_waits_for_its_deepest_dependency()
    {
        var stages = PlanOk(
            T("privilege", "P", """{"entry-point":"SomeMenuItem"}"""),
            T("duty", "D", """{"privilege":"P"}"""),
            T("role", "R", """{"privilege":"P","duty":"D"}"""));
        Assert.Equal(new[] { new[] { "privilege" }, new[] { "duty" }, new[] { "role" } }, Types(stages));
    }

    [Fact]
    public void A_cycle_is_reported_with_the_tasks_involved()
    {
        var (stages, errors) = Planner.Plan(Expanded("CFlt",
            T("edt", "A", """{"extends":"B"}"""),
            T("edt", "B", """{"extends":"A"}"""),
            T("enum", "C")), Map);

        Assert.Null(stages);
        var error = Assert.Single(errors);
        Assert.Contains("cycle", error);
        Assert.Contains("tasks[0] (edt CFltA)", error);
        Assert.Contains("tasks[1] (edt CFltB)", error);
        Assert.DoesNotContain("enum", error);
    }
}
