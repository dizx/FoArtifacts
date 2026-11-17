using FoBatchGen.Core;
using Xunit;
using static FoBatchGen.Tests.TestData;

namespace FoBatchGen.Tests;

public class NameExpanderTests
{
    private static ExpandedTask Find(ExpandedBatch batch, string type) => Assert.Single(batch.Tasks, t => t.Type == type);

    private static string[] Values(ExpandedTask task, string option) =>
        task.Options.Where(o => o.Key == option).Select(o => o.Value).ToArray();

    [Fact]
    public void Prefixes_new_object_names()
    {
        var table = Find(Expanded("CFlt", T("table", "Vehicle")), "table");
        Assert.Equal("CFltVehicle", table.Name);
        Assert.Equal(new[] { "CFltVehicle" }, table.DefinedNames);
    }

    [Fact]
    public void Never_adds_the_prefix_twice()
    {
        var batch = Expanded("CFlt", T("table", "CFltVehicle"), T("edt", "cfltCode"));
        Assert.Equal("CFltVehicle", Find(batch, "table").Name);
        Assert.Equal("cfltCode", Find(batch, "edt").Name);
    }

    [Fact]
    public void Empty_prefix_changes_no_names_but_still_links_references()
    {
        var batch = Expanded("", T("edt", "VehicleId"), T("table", "Vehicle", """{"field":["Id:VehicleId"]}"""));
        var table = Find(batch, "table");
        Assert.Equal("Vehicle", table.Name);
        Assert.Equal(new[] { "Id:VehicleId" }, Values(table, "field"));
        Assert.Equal(new[] { 0 }, Assert.Single(table.References).TargetTasks);
    }

    [Fact]
    public void Rewrites_references_to_objects_in_the_batch()
    {
        var batch = Expanded("CFlt",
            T("enum", "VehicleStatus"),
            T("edt", "VehicleId"),
            T("table", "Vehicle", """{"field":["VehicleId:VehicleId:mandatory","Status:VehicleStatus"]}"""),
            T("form", "Vehicle", """{"table":"Vehicle"}"""));

        // The field-name segment "VehicleId" stays; only the EDT segment is an object reference.
        Assert.Equal(new[] { "VehicleId:CFltVehicleId:mandatory", "Status:CFltVehicleStatus" }, Values(Find(batch, "table"), "field"));
        var form = Find(batch, "form");
        Assert.Equal(new[] { "CFltVehicle" }, Values(form, "table"));
        Assert.Equal(new[] { 2 }, Assert.Single(form.References).TargetTasks);
    }

    [Fact]
    public void Leaves_existing_objects_alone()
    {
        var batch = Expanded("CFlt",
            T("table", "Vehicle", """{"field":["Vin:VIN"]}"""),
            T("edt", "Code", """{"extends":"SysGroup"}"""));
        Assert.Equal(new[] { "Vin:VIN" }, Values(Find(batch, "table"), "field"));
        Assert.Equal(new[] { "SysGroup" }, Values(Find(batch, "edt"), "extends"));
        Assert.All(batch.Tasks, t => Assert.Empty(t.References));
    }

    [Fact]
    public void A_reference_only_matches_objects_of_a_fitting_kind()
    {
        // The batch defines a table called Name; the EDT extends the standard EDT Name.
        var batch = Expanded("CFlt", T("table", "Name"), T("edt", "Code", """{"extends":"Name"}"""));
        Assert.Equal(new[] { "Name" }, Values(Find(batch, "edt"), "extends"));
    }

    [Fact]
    public void Matching_ignores_case_and_accepts_already_prefixed_references()
    {
        var batch = Expanded("CFlt",
            T("table", "Vehicle"),
            T("form", "Vehicle", """{"table":"vehicle"}"""),
            T("privilege", "VehicleMaintain", """{"entry-point":"SomeMenuItem"}"""),
            T("duty", "VehicleMaintain", """{"privilege":"CFltVehicleMaintain"}"""));
        Assert.Equal(new[] { "CFltVehicle" }, Values(Find(batch, "form"), "table"));
        var duty = Find(batch, "duty");
        Assert.Equal(new[] { "CFltVehicleMaintain" }, Values(duty, "privilege"));
        Assert.Equal(new[] { 2 }, Assert.Single(duty.References).TargetTasks);
    }

    [Fact]
    public void Batch_names_outside_reference_positions_are_untouched()
    {
        var batch = Expanded("CFlt",
            T("table", "Vehicle", """{"field-group":["Vehicle:Vehicle"]}"""),
            T("form", "Vehicle", """{"table":"Vehicle","caption":"Vehicle","field":["Vehicle"]}"""));
        var form = Find(batch, "form");
        Assert.Equal(new[] { "Vehicle" }, Values(form, "caption"));
        Assert.Equal(new[] { "Vehicle" }, Values(form, "field"));
        Assert.Equal(new[] { "Vehicle:Vehicle" }, Values(Find(batch, "table"), "field-group"));
    }

    [Fact]
    public void Defining_options_are_prefixed_and_can_be_referenced()
    {
        var batch = Expanded("CFlt",
            T("sysoperation", "Close", """{"contract-name":"CloseContract"}"""),
            T("class", "Helper", """{"extends":"CloseContract"}"""));
        var sysop = Find(batch, "sysoperation");
        Assert.Equal(new[] { "CFltCloseContract" }, Values(sysop, "contract-name"));
        Assert.Equal(new[] { "CFltClose", "CFltCloseContract" }, sysop.DefinedNames);
        Assert.Equal(new[] { "CFltCloseContract" }, Values(Find(batch, "class"), "extends"));
    }

    [Fact]
    public void Reference_role_names_are_rewritten_only_for_batch_objects()
    {
        var batch = Expanded("CFlt", T("table", "Vehicle"), T("table-relation", "Vehicle"), T("find-methods", "CustTable"));
        var relation = Find(batch, "table-relation");
        Assert.Equal("CFltVehicle", relation.Name);
        Assert.Equal(InstallModes.ApplyTo, relation.Install);
        Assert.Equal("<name>", Assert.Single(relation.References).Option);
        Assert.Equal("CustTable", Find(batch, "find-methods").Name);
    }

    [Fact]
    public void Extension_kind_becomes_the_first_argument()
    {
        var batch = Expanded("CFlt",
            T("enum", "VehicleStatus"),
            T("extension", "CustTable", """{"kind":"Table","field":["Status:VehicleStatus"]}"""));
        var extension = Find(batch, "extension");
        Assert.Equal(new[] { "Table" }, extension.LeadingArgs);
        Assert.Equal("CustTable", extension.Name);
        var option = Assert.Single(extension.Options);
        Assert.Equal(new KeyValuePair<string, string>("field", "Status:CFltVehicleStatus"), option);
    }

    [Fact]
    public void Menu_items_inside_submenu_paths_are_found()
    {
        var batch = Expanded("CFlt",
            T("menu-item", "Vehicle", """{"object":"SomeForm"}"""),
            T("menu", "Fleet", """{"item":["Setup/Vehicle:Display","Other/CustTable"]}"""));
        Assert.Equal(new[] { "Setup/CFltVehicle:Display", "Other/CustTable" }, Values(Find(batch, "menu"), "item"));
    }

    [Fact]
    public void Two_definitions_of_one_object_are_rejected()
    {
        var (batch, errors) = NameExpander.Expand(Valid("CFlt", T("table", "Vehicle"), T("table", "vehicle")), Map);
        Assert.Null(batch);
        Assert.Contains("is defined twice", Assert.Single(errors));
    }

    [Fact]
    public void One_name_for_objects_of_different_kinds_is_fine()
    {
        var batch = Expanded("CFlt", T("table", "Vehicle"), T("form", "Vehicle"), T("menu-item", "Vehicle"));
        Assert.All(batch.Tasks, t => Assert.Equal("CFltVehicle", t.Name));
    }

    [Fact]
    public void Touch_names_cover_new_and_changed_objects()
    {
        var batch = Expanded("CFlt", T("table", "Vehicle"), T("coc", "CustTable", """{"method":"insert"}"""));
        Assert.Contains("CFltVehicle", batch.TouchNames);
        Assert.Contains("CustTable", batch.TouchNames);
    }
}
