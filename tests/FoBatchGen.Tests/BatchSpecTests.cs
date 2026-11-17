using FoBatchGen.Core;
using Xunit;
using static FoBatchGen.Tests.TestData;

namespace FoBatchGen.Tests;

public class BatchSpecTests
{
    private static (ValidBatch? Batch, IReadOnlyList<string> Errors) Check(string model, string prefix, params TaskInput[] tasks) =>
        BatchSpec.Validate(new BatchRequest(model, prefix, tasks), Map);

    [Fact]
    public void Normalises_options_and_keeps_order()
    {
        var batch = Valid("CFlt", T("table", "Vehicle",
            """{"field":["A:B","C:D"],"size":20,"--label":"X","non-final":true,"disabled":false}"""));

        var task = Assert.Single(batch.Tasks);
        Assert.Equal(0, task.Index);
        Assert.Equal(
            new KeyValuePair<string, string>[] { new("field", "A:B"), new("field", "C:D"), new("size", "20"), new("label", "X") },
            task.Options);
        Assert.Equal(new[] { "non-final" }, task.Flags);
        Assert.Equal(new[] { "A:B", "C:D" }, task.Values("field"));
        Assert.Equal("tasks[0] (table Vehicle)", task.Label);
    }

    [Fact]
    public void Type_is_matched_without_regard_to_case()
    {
        var batch = Valid("", T("TABLE", "Vehicle"));
        Assert.Equal("table", Assert.Single(batch.Tasks).Type);
    }

    [Fact]
    public void Unknown_type_lists_the_supported_types()
    {
        var (batch, errors) = Check("M", "", T("tabel", "Vehicle"));
        Assert.Null(batch);
        var error = Assert.Single(errors);
        Assert.Contains("unknown type 'tabel'", error);
        Assert.Contains("table-relation", error);
    }

    [Fact]
    public void Reports_every_problem_at_once()
    {
        var (_, errors) = Check("M", "", T("tabel", "A"), T("table", "B", """{"out":"x"}"""), T("edt", "C", """{"size":{}}"""));
        Assert.Equal(3, errors.Count);
    }

    [Theory]
    [InlineData("out")]
    [InlineData("install-to")]
    [InlineData("apply-to")]
    [InlineData("output")]
    [InlineData("overwrite")]
    [InlineData("no-refresh")]
    [InlineData("add-to")]
    [InlineData("into-role")]
    [InlineData("out-contract")]
    [InlineData("out-staging")]
    public void Reserved_options_are_rejected(string option)
    {
        var (_, errors) = Check("M", "", T("table", "Vehicle", $$"""{"{{option}}":"x"}"""));
        Assert.Contains(errors, e => e.Contains($"option '{option}' is set by fo-batchgen"));
    }

    [Fact]
    public void Empty_and_oversized_task_lists_are_rejected()
    {
        Assert.Contains(Check("M", "").Errors, e => e.Contains("at least one task"));
        var many = Enumerable.Range(0, BatchSpec.MaxTasks + 1).Select(i => T("enum", $"E{i}")).ToArray();
        Assert.Contains(Check("M", "", many).Errors, e => e.Contains("maximum is 500"));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("My Model", "")]
    [InlineData("M", "1x")]
    [InlineData("M", "C-F")]
    public void Bad_model_or_prefix_is_rejected(string model, string prefix)
    {
        var (batch, errors) = Check(model, prefix, T("enum", "E"));
        Assert.Null(batch);
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Empty_prefix_is_allowed() => Assert.Empty(Check("M", "", T("enum", "E")).Errors);

    [Fact]
    public void Object_names_must_be_aot_names_but_literals_are_free()
    {
        Assert.Contains(Check("M", "", T("table", "My Table")).Errors, e => e.Contains("not a valid AOT name"));
        Assert.Empty(Check("M", "", T("report-extension", "custom-design")).Errors);
    }

    [Fact]
    public void Extension_needs_exactly_one_kind()
    {
        Assert.Contains(Check("M", "", T("extension", "CustTable")).Errors, e => e.Contains("option 'kind' is required exactly once"));
        Assert.Empty(Check("M", "", T("extension", "CustTable", """{"kind":"Table"}""")).Errors);
    }

    [Fact]
    public void Nested_objects_and_nulls_are_rejected()
    {
        var (_, errors) = Check("M", "", T("edt", "E", """{"size":{"a":1},"label":null,"x":[true]}"""));
        Assert.Equal(3, errors.Count);
    }
}
