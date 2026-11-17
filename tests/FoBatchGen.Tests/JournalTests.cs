using System.Text.Json;
using FoBatchGen.Core;
using Xunit;

namespace FoBatchGen.Tests;

public class JournalTests
{
    private static JournalEntry E(string id, string name, string model = "ContosoFleet", string kind = "Table") =>
        new(id, kind, name, model, $"/pkg/{model}/{name}.xml");

    private static RollbackDecision Decide(string? anchor, string[] owned, params JournalEntry[] newestFirst) =>
        RollbackPlanner.Decide(anchor, newestFirst, 2000, "ContosoFleet", owned);

    [Fact]
    public void Parses_d365fo_journal_output()
    {
        using var doc = JsonDocument.Parse("""
            {"journalDirectory":"/x","count":1,"totalCount":1,"entries":[{"id":"6f50","timestampUtc":"2026-11-16T10:58:34+00:00",
             "command":"scaffold-write","targetType":"AotObject","kind":"SecurityRole","name":"FmTruckManager","model":"TestModel",
             "operation":"Create","writePath":"Disk","target":"/p/AxSecurityRole/FmTruckManager.xml","hasPreImage":false,"isTombstone":true}]}
            """);
        var entry = Assert.Single(Journal.Parse(doc.RootElement));
        Assert.Equal(new JournalEntry("6f50", "SecurityRole", "FmTruckManager", "TestModel", "/p/AxSecurityRole/FmTruckManager.xml"), entry);
    }

    [Fact]
    public void Parse_tolerates_missing_data() => Assert.Empty(Journal.Parse(default));

    [Fact]
    public void Builds_list_and_undo_arguments()
    {
        Assert.Equal(new[] { "journal", "list", "-n", "2000", "--output", "json" }, Journal.ListArgs(2000));
        Assert.Equal(new[] { "undo", "--steps", "3", "--output", "json" }, Journal.UndoArgs(3));
    }

    [Fact]
    public void Entries_the_batch_wrote_above_the_anchor_can_be_undone()
    {
        var d = Decide("a", ["c", "b"], E("c", "CFltVehicle"), E("b", "Vehicle_NumberSeqHandler"), E("a", "Older"), E("0", "Oldest"));
        Assert.True(d.CanUndo);
        Assert.Equal(2, d.Steps);
    }

    [Fact]
    public void An_empty_journal_before_the_batch_means_every_entry_can_be_ours()
    {
        var d = Decide(null, ["b", "a"], E("b", "CFltVehicle"), E("a", "CFltVehicleId"));
        Assert.True(d.CanUndo);
        Assert.Equal(2, d.Steps);
    }

    [Fact]
    public void Nothing_written_means_nothing_to_undo()
    {
        var d = Decide("a", [], E("a", "Older"));
        Assert.True(d.CanUndo);
        Assert.Equal(0, d.Steps);
    }

    [Fact]
    public void A_missing_anchor_blocks_the_undo()
    {
        var d = Decide("gone", ["b"], E("b", "CFltVehicle"));
        Assert.False(d.CanUndo);
        Assert.Contains("not among the newest", d.Reason);
    }

    [Fact]
    public void A_full_journal_without_an_anchor_blocks_the_undo()
    {
        var d = RollbackPlanner.Decide(null, [E("b", "CFltVehicle"), E("a", "CFltVehicleId")], 2, "ContosoFleet", ["b", "a"]);
        Assert.False(d.CanUndo);
    }

    [Fact]
    public void An_entry_the_batch_did_not_write_blocks_the_undo_whatever_its_name()
    {
        // Same model, and a name that contains a batch name: still another session's write.
        var d = Decide("a", ["c"], E("c", "CFltVehicle"), E("x", "CFltVehicleStatus"), E("a", "Older"));
        Assert.False(d.CanUndo);
        Assert.Contains("not from this batch", d.Reason);
        Assert.Contains("CFltVehicleStatus (ContosoFleet)", d.Reason);
        Assert.Equal(2, d.Entries.Count);
    }

    [Fact]
    public void An_entry_in_another_model_blocks_the_undo_even_if_it_appeared_during_a_batch_command()
    {
        var d = Decide("a", ["c", "x"], E("c", "CFltVehicle"), E("x", "SalesTable", model: "OtherModel"), E("a", "Older"));
        Assert.False(d.CanUndo);
        Assert.Contains("SalesTable (OtherModel)", d.Reason);
    }

    [Fact]
    public void A_batch_write_missing_from_the_journal_blocks_the_undo()
    {
        // d365fo prunes its journal (D365FO_JOURNAL_MAX_ENTRIES); undoing what is left would report success over leftovers.
        var d = Decide(null, ["c", "b", "a"], E("c", "E3"), E("b", "E2"));
        Assert.False(d.CanUndo);
        Assert.Contains("no longer in the d365fo journal", d.Reason);
    }
}
