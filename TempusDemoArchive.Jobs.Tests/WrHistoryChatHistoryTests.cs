using FluentAssertions;

namespace TempusDemoArchive.Jobs.Tests;

public class WrHistoryChatHistoryTests
{
    [Fact]
    public void BuildWrHistory_SameDateOrdersByDemoId()
    {
        var date = new DateTime(2026, 1, 16);

        var entries = new List<WrHistoryEntry>
        {
            new(
                Player: "jump_gaylord",
                Class: "Solly",
                Map: "jump_example",
                RecordType: WrHistoryConstants.RecordType.Wr,
                Source: WrHistoryConstants.Source.MapRecord,
                RecordTime: "00:44.02",
                RunTime: null,
                Split: null,
                Improvement: null,
                Inferred: false,
                Date: date,
                DemoId: 3718446,
                ChatIndex: 10),
            new(
                Player: "prof",
                Class: "Solly",
                Map: "jump_example",
                RecordType: WrHistoryConstants.RecordType.Wr,
                Source: WrHistoryConstants.Source.MapRecord,
                RecordTime: "00:43.66",
                RunTime: null,
                Split: null,
                Improvement: null,
                Inferred: false,
                Date: date,
                DemoId: 3718723,
                ChatIndex: 10)
        };

        var history = WrHistoryChat.BuildWrHistory(entries, includeAll: false).ToList();
        history.Select(x => x.RecordTime).Should().Equal("00:44.02", "00:43.66");
    }

    [Fact]
    public void BuildWrHistory_ObservedWrIsNotAttributedToRunner_WhenHolderUnknown()
    {
        var entries = new List<WrHistoryEntry>
        {
            new(
                Player: "Holder",
                Class: "Solly",
                Map: "jump_example",
                RecordType: WrHistoryConstants.RecordType.Wr,
                Source: WrHistoryConstants.Source.MapRecord,
                RecordTime: "00:40.00",
                RunTime: null,
                Split: "-00:00.10",
                Improvement: null,
                Inferred: false,
                Date: new DateTime(2024, 1, 1),
                DemoId: 1,
                ChatIndex: 1),
            new(
                Player: "Runner",
                Class: "Solly",
                Map: "jump_example",
                RecordType: WrHistoryConstants.RecordType.Wr,
                Source: WrHistoryConstants.Source.MapRun,
                RecordTime: "00:39.90",
                RunTime: "00:40.00",
                Split: "+00:00.10",
                Improvement: null,
                Inferred: true,
                Date: new DateTime(2024, 1, 2),
                DemoId: 2,
                ChatIndex: 1,
                SteamId64: 123)
        };

        var history = WrHistoryChat.BuildWrHistory(entries, includeAll: false).ToList();
        history.Should().HaveCount(2);

        var observed = history[1];
        observed.Source.Should().Be(WrHistoryConstants.Source.ObservedWr);
        observed.Player.Should().Be(WrHistoryConstants.Unknown);
        observed.DemoId.Should().BeNull();
        observed.SteamId64.Should().BeNull();
        observed.SteamId.Should().BeNull();
        observed.RunTime.Should().BeNull();
        observed.Split.Should().BeNull();
        observed.Inferred.Should().BeTrue();
    }

    [Fact]
    public void BuildWrHistory_ObservedWrUsesHolderCandidate_WhenAvailable()
    {
        var entries = new List<WrHistoryEntry>
        {
            new(
                Player: "Holder0",
                Class: "Solly",
                Map: "jump_example",
                RecordType: WrHistoryConstants.RecordType.Wr,
                Source: WrHistoryConstants.Source.MapRecord,
                RecordTime: "00:40.00",
                RunTime: null,
                Split: null,
                Improvement: null,
                Inferred: false,
                Date: new DateTime(2024, 1, 1),
                DemoId: 1,
                ChatIndex: 1),
            new(
                Player: "Runner",
                Class: "Solly",
                Map: "jump_example",
                RecordType: WrHistoryConstants.RecordType.Wr,
                Source: WrHistoryConstants.Source.MapRun,
                RecordTime: "00:39.90",
                RunTime: "00:40.00",
                Split: "+00:00.10",
                Improvement: null,
                Inferred: true,
                Date: new DateTime(2024, 1, 2),
                DemoId: 2,
                ChatIndex: 1,
                SteamId64: 999),
            new(
                Player: "Holder1",
                Class: "Solly",
                Map: "jump_example",
                RecordType: WrHistoryConstants.RecordType.Wr,
                Source: WrHistoryConstants.Source.Compact,
                RecordTime: "00:39.90",
                RunTime: null,
                Split: null,
                Improvement: null,
                Inferred: false,
                Date: new DateTime(2024, 1, 3),
                DemoId: 3,
                SteamId64: 456,
                IsLookup: true,
                ChatIndex: 1)
        };

        var history = WrHistoryChat.BuildWrHistory(entries, includeAll: false).ToList();
        history.Should().HaveCount(2);

        var observed = history[1];
        observed.Source.Should().Be(WrHistoryConstants.Source.ObservedWr);
        observed.Player.Should().Be("Holder1");
        observed.DemoId.Should().BeNull();
        observed.SteamId64.Should().Be(456);
        observed.RunTime.Should().BeNull();
        observed.Split.Should().BeNull();
    }

    [Fact]
    public void BuildWrHistory_SkipsCommandOutput_WhenRecordExists()
    {
        var entries = new List<WrHistoryEntry>
        {
            new(
                Player: "Holder",
                Class: "Solly",
                Map: "jump_example",
                RecordType: WrHistoryConstants.RecordType.Wr,
                Source: WrHistoryConstants.Source.MapRecord,
                RecordTime: "00:40.00",
                RunTime: null,
                Split: null,
                Improvement: null,
                Inferred: false,
                Date: new DateTime(2024, 1, 1),
                DemoId: 1,
                SteamId64: 76561197960265728,
                ChatIndex: 1),
            new(
                Player: "Holder",
                Class: "Solly",
                Map: "jump_example",
                RecordType: WrHistoryConstants.RecordType.Wr,
                Source: WrHistoryConstants.Source.Compact,
                RecordTime: "00:40.00",
                RunTime: null,
                Split: null,
                Improvement: null,
                Inferred: false,
                Date: new DateTime(2024, 1, 2),
                DemoId: 999,
                IsLookup: true,
                ChatIndex: 1)
        };

        var history = WrHistoryChat.BuildWrHistory(entries, includeAll: false).ToList();
        history.Should().HaveCount(1);
        history[0].Source.Should().Be(WrHistoryConstants.Source.MapRecord);
        history[0].DemoId.Should().Be(1);
    }
}
