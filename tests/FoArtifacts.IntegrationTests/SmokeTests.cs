using FoArtifacts.Core;
using Xunit;
using static FoArtifacts.IntegrationTests.D365foSandbox;

namespace FoArtifacts.IntegrationTests;

/// <summary>One small batch per subcommand, run through the real d365fo against its sample model.</summary>
public class SmokeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Subcommands that change forms only through the metadata bridge (Windows dev VM): checked by hand.</summary>
    public static readonly string[] BridgeOnly = ["datasource-method", "control-method"];

    private static readonly TaskInput Edt = T("edt", "Edt", """{"base-type":"String","size":10}""");
    private static readonly TaskInput Item = T("menu-item", "Item", """{"object":"FmVehicleList"}""");
    private static readonly TaskInput Priv = T("privilege", "Priv", """{"entry-point":"Item","entry-kind":"MenuItemDisplay","access":"Read"}""");
    private static readonly TaskInput Duty = T("duty", "Duty", """{"privilege":"Priv"}""");
    private static readonly TaskInput Entity = T("entity", "Entity", """{"table":"FmVehicle","public-entity":"FxEntity","public-collection":"FxEntities","all-fields":true}""");
    private static readonly TaskInput Category = T("workflow-category", "Category", """{"module":"Basic"}""");

    public static readonly Dictionary<string, TaskInput[]> Cases = new()
    {
        ["aggregate-entity"] = [Edt, T("aggregate-entity", "Agg", """{"measurement":"Measure","measure":"Cnt:Grp:Cnt:Edt"}""")],
        ["business-event"] = [T("business-event", "Event", """{"payload":"Vin:VIN"}""")],
        ["class"] = [T("class", "Class")],
        ["coc"] = [T("coc", "FmVehicleService", """{"method":"run"}""")],
        ["composite-entity"] = [Entity, T("composite-entity", "Composite", """{"root":"Entity"}""")],
        ["configuration-key"] = [T("configuration-key", "Key")],
        ["custom-service"] = [T("custom-service", "Service")],
        ["duty"] = [Item, Priv, Duty],
        ["edt"] = [Edt],
        ["entity"] = [Entity],
        ["enum"] = [T("enum", "Enum", """{"value":["A:0","B:1"]}""")],
        ["event-handler"] = [T("event-handler", "Handler", """{"source-kind":"Table","source-object":"FmVehicle","event":"OnInserted"}""")],
        ["extension"] = [T("extension", "FmVehicle", """{"kind":"Table","field":"Mileage:FmMileage"}""")],
        ["find-methods"] = [T("find-methods", "FmVehicle")],
        ["form"] = [T("form", "Form", """{"pattern":"SimpleList","table":"FmVehicle","field":"VIN"}""")],
        ["form-clone"] = [T("form-clone", "Clone", """{"from":"FmVehicleList"}""")],
        ["form-part"] = [T("form-part", "Part", """{"form":"FmVehicleList","caption":"Part"}""")],
        ["label-file"] = [T("label-file", "FxLabels", """{"entry":"Hello=Hello"}""")],
        ["map"] = [T("map", "Map", """{"field":"Vin:VIN","map-to":"FmVehicle"}""")],
        ["menu"] = [Item, T("menu", "Menu", """{"item":"Item"}""")],
        ["menu-item"] = [Item],
        ["migration-script"] = [T("migration-script", "Mig", """{"source-table":"FmVehicle"}""")],
        ["number-sequence"] = [T("number-sequence", "Num", """{"table":"FmVehicle"}""")],
        ["privilege"] = [Item, Priv],
        ["query"] = [T("query", "Query", """{"ds":"FmVehicle"}""")],
        ["report"] = [T("report", "Report", """{"field":"VIN"}""")],
        ["report-extension"] = [T("report-extension", "dataset", """{"dp":"FxReportDP","tmp-table":"FxReportTmp","dataset-accessor":"getFxReportTmp"}""")],
        ["resource"] = [T("resource", "Res", """{"file-name":"Logo.png"}""")],
        ["role"] = [Item, Priv, Duty, T("role", "Role", """{"duty":"Duty"}""")],
        ["runbase"] = [T("runbase", "Run", """{"dialog-param":"Vin:VIN"}""")],
        ["security-policy"] = [T("security-policy", "Policy", """{"constrained-table":"FmVehicle","policy-query":"FmVehicleQuery"}""")],
        ["sysoperation"] = [T("sysoperation", "Op", """{"param":"Vin:VIN"}""")],
        ["systest"] = [T("systest", "Test", """{"class":"FmVehicleService","method":"run"}""")],
        ["table"] = [Edt, T("table", "Table", """{"pattern":"main","field":["Code:Edt","Vin:VIN"]}""")],
        ["table-relation"] = [T("table", "Rel", """{"field":"Vin:VIN"}"""), T("table-relation", "Rel")],
        ["tile"] = [Item, T("tile", "Tile", """{"menu-item":"Item"}""")],
        ["view"] = [T("view", "View", """{"query":"FmVehicleQuery","field":"VIN:FmVehicle"}""")],
        ["workflow"] = [Category, T("workflow", "Wf", """{"table":"FmVehicle","category":"Category"}""")],
        ["workflow-category"] = [Category],
    };

    public static TheoryData<string> CaseNames => new(Cases.Keys.Order(StringComparer.Ordinal));

    [Fact]
    public void Every_subcommand_has_a_smoke_case_or_is_bridge_only() =>
        Assert.Equal(
            ReferenceMap.LoadEmbedded().Types,
            Cases.Keys.Concat(BridgeOnly).Order(StringComparer.Ordinal).ToList());

    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task Subcommand_runs_through_a_batch(string type)
    {
        Assert.SkipWhen(SkipReason is not null, SkipReason ?? "");
        await using var box = await CreateAsync(Ct);

        var result = await box.Executor.RunAsync(new BatchRequest(Model, "Fx", Cases[type]), Ct);

        Assert.True(result.Ok, Summary.ToJson(result));
        Assert.Contains(result.Completed, o => o.Task.Type == type);
    }
}
