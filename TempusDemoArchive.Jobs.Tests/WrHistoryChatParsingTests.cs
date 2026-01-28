using FluentAssertions;

namespace TempusDemoArchive.Jobs.Tests;

public class WrHistoryChatParsingTests
{
    [Fact]
    public void MapRecordWithoutLabel_AssumesWorldRecord()
    {
        var entry = ParseTempus(
            map: "jump_example",
            text: "Tempus | (Demoman) Player beat the map record: 00:40.00 (-00:00.10)");

        entry.RecordType.Should().Be(WrHistoryConstants.RecordType.Wr);
        entry.Source.Should().Be(WrHistoryConstants.Source.MapRecord);
        entry.Class.Should().Be("Demo");
        entry.Map.Should().Be("jump_example");
        entry.RecordTime.Should().Be("00:40.00");
        entry.Split.Should().Be("-00:00.10");
        entry.Inferred.Should().BeFalse();
        entry.IsLookup.Should().BeFalse();
    }

    [Fact]
    public void MapRecordNoSplit_Parses()
    {
        var entry = ParseTempus(
            map: "jump_example",
            text: "Tempus | (Soldier) Player beat the map record: 00:40.00!");

        entry.RecordType.Should().Be(WrHistoryConstants.RecordType.Wr);
        entry.Source.Should().Be(WrHistoryConstants.Source.MapRecord);
        entry.Class.Should().Be("Solly");
        entry.RecordTime.Should().Be("00:40.00");
        entry.Split.Should().BeNull();
        entry.Improvement.Should().BeNull();
    }

    [Fact]
    public void MapRecordWithSrLabel_ParsesAsWorldRecord()
    {
        var entry = ParseTempus(
            map: "jump_example",
            text: "Tempus | (Demo) Player beat the map record: 00:40.00 (SR -00:00.10) | 00:00.10 improvement!");

        entry.RecordType.Should().Be(WrHistoryConstants.RecordType.Wr);
        entry.Source.Should().Be(WrHistoryConstants.Source.MapRecord);
        entry.RecordTime.Should().Be("00:40.00");
        entry.Split.Should().Be("-00:00.10");
        entry.Improvement.Should().Be("00:00.10");
        entry.Inferred.Should().BeFalse();
    }

    [Fact]
    public void MapRecordWithPrLabel_ParsesAsCandidateRecord()
    {
        var entry = ParseTempus(
            map: "jump_example",
            text: "Tempus | (Demo) Player beat the map record: 00:40.00 (PR -00:00.10) | 00:00.10 improvement!");

        entry.RecordType.Should().Be(WrHistoryConstants.RecordType.Wr);
        entry.Source.Should().Be(WrHistoryConstants.Source.MapRecord);
        entry.RecordTime.Should().Be("00:40.00");
        entry.Split.Should().Be("-00:00.10");
        entry.Improvement.Should().Be("00:00.10");
    }

    [Fact]
    public void FirstMapRecord_Parses()
    {
        var entry = ParseTempus(
            map: "jump_example",
            text: "Tempus | (Demo) Player set the first map record: 00:40.00!");

        entry.RecordType.Should().Be(WrHistoryConstants.RecordType.Wr);
        entry.Source.Should().Be(WrHistoryConstants.Source.FirstRecord);
        entry.RecordTime.Should().Be("00:40.00");
    }

    [Fact]
    public void BonusRecord_Parses()
    {
        var entry = ParseTempus(
            map: "jump_example",
            text: "Tempus | (Demo) Player broke Bonus 1 00:05.00 (WR -00:00.10) | 00:00.10 improvement!");

        entry.RecordType.Should().Be(WrHistoryConstants.RecordType.Wr);
        entry.Source.Should().Be($"{WrHistoryConstants.SegmentPrefix.Bonus} 1");
        entry.RecordTime.Should().Be("00:05.00");
        entry.Split.Should().Be("-00:00.10");
        entry.Improvement.Should().Be("00:00.10");
        entry.IsLookup.Should().BeFalse();
    }

    [Fact]
    public void SetBonusRecord_ParsesAsFirst()
    {
        var entry = ParseTempus(
            map: "jump_example",
            text: "Tempus | (Demo) Player set Bonus 1 00:05.00!");

        entry.RecordType.Should().Be(WrHistoryConstants.RecordType.Wr);
        entry.Source.Should().Be($"{WrHistoryConstants.SegmentPrefix.Bonus} 1{WrHistoryConstants.SegmentPrefix.FirstSuffix}");
        entry.RecordTime.Should().Be("00:05.00");
        entry.Split.Should().BeNull();
        entry.Improvement.Should().BeNull();
    }

    [Fact]
    public void CourseRecord_Parses()
    {
        var entry = ParseTempus(
            map: "jump_example",
            text: "Tempus | (Demo) Player broke Course 2 00:10.00 (WR -00:00.25)");

        entry.RecordType.Should().Be(WrHistoryConstants.RecordType.Wr);
        entry.Source.Should().Be($"{WrHistoryConstants.SegmentPrefix.Course} 2");
        entry.RecordTime.Should().Be("00:10.00");
        entry.Split.Should().Be("-00:00.25");
    }

    [Fact]
    public void CourseSegmentRecord_Parses()
    {
        var entry = ParseTempus(
            map: "jump_example",
            text: "Tempus | (Demo) Player broke C2 - Jump One 00:03.33 (WR -00:00.10)");

        entry.RecordType.Should().Be(WrHistoryConstants.RecordType.Wr);
        entry.Source.Should().Be($"{WrHistoryConstants.SegmentPrefix.CourseSegment}2 - Jump One");
        entry.RecordTime.Should().Be("00:03.33");
    }

    [Fact]
    public void SetCourseSegmentRecord_ParsesAsFirst()
    {
        var entry = ParseTempus(
            map: "jump_example",
            text: "Tempus | (Demo) Player set C2 - Jump One 00:03.33!");

        entry.RecordType.Should().Be(WrHistoryConstants.RecordType.Wr);
        entry.Source.Should().Be($"{WrHistoryConstants.SegmentPrefix.CourseSegment}2 - Jump One{WrHistoryConstants.SegmentPrefix.FirstSuffix}");
        entry.RecordTime.Should().Be("00:03.33");
    }

    [Fact]
    public void MapRunWithNegativeSplit_IsNotInferred()
    {
        var entry = ParseTempus(
            map: "jump_example",
            text: "Tempus | (Demo) Player map run 00:40.00 (WR -00:00.10)");

        entry.RecordType.Should().Be(WrHistoryConstants.RecordType.Wr);
        entry.Source.Should().Be(WrHistoryConstants.Source.MapRun);
        entry.RecordTime.Should().Be("00:40.00");
        entry.RunTime.Should().Be("00:40.00");
        entry.Split.Should().Be("-00:00.10");
        entry.Inferred.Should().BeFalse();
    }

    [Fact]
    public void MapRunWithPositiveSplit_IsIgnored()
    {
        var entry = TryParseTempus(
            map: "jump_example",
            text: "Tempus | (Demo) Player map run 00:40.00 (WR +00:00.10)");

        entry.Should().BeNull();
    }

    [Fact]
    public void IrcBrokeRecord_Parses()
    {
        var entry = ParseIrc(
            map: null,
            text: ":: (Demo) Player broke jump_example WR: 00:40.00 (WR -00:00.10)!");

        entry.RecordType.Should().Be(WrHistoryConstants.RecordType.Wr);
        entry.Source.Should().Be(WrHistoryConstants.Source.Irc);
        entry.Class.Should().Be("Demo");
        entry.Map.Should().Be("jump_example");
        entry.RecordTime.Should().Be("00:40.00");
        entry.Split.Should().Be("-00:00.10");
        entry.Inferred.Should().BeFalse();
    }

    [Fact]
    public void IrcSetRecord_Parses()
    {
        var entry = ParseIrc(
            map: null,
            text: ":: (Demo) Player set jump_example WR: 00:40.00!");

        entry.RecordType.Should().Be(WrHistoryConstants.RecordType.Wr);
        entry.Source.Should().Be(WrHistoryConstants.Source.IrcSet);
        entry.Map.Should().Be("jump_example");
        entry.RecordTime.Should().Be("00:40.00");
        entry.Split.Should().BeNull();
    }

    [Fact]
    public void RankedBonusRecord_ParsesAsLookupAndInferred()
    {
        var entry = ParseTempus(
            map: "jump_example",
            text: "Tempus | (Demo) Player is ranked 1/12 on jump_example Bonus 1 with time: 00:05.00");

        entry.RecordType.Should().Be(WrHistoryConstants.RecordType.Wr);
        entry.Source.Should().Be($"{WrHistoryConstants.SegmentPrefix.Bonus} 1");
        entry.RecordTime.Should().Be("00:05.00");
        entry.Inferred.Should().BeTrue();
        entry.IsLookup.Should().BeTrue();
    }

    private static WrHistoryEntry? TryParseTempus(string map, string text)
    {
        var candidate = new WrHistoryChat.ChatCandidate(
            DemoId: 1,
            Map: map,
            Text: text,
            ChatIndex: 0,
            Tick: null,
            FromUserId: null);
        var demoDates = new Dictionary<ulong, DateTime?>
        {
            [1] = new DateTime(2024, 1, 1)
        };
        var demoUsers = new Dictionary<ulong, WrHistoryChat.DemoUsers>();

        return WrHistoryChat.TryParseTempusRecord(candidate, map, demoDates, demoUsers);
    }

    private static WrHistoryEntry ParseTempus(string map, string text)
    {
        var entry = TryParseTempus(map, text);
        entry.Should().NotBeNull();
        return entry!;
    }

    private static WrHistoryEntry ParseIrc(string? map, string text)
    {
        var candidate = new WrHistoryChat.ChatCandidate(
            DemoId: 1,
            Map: null,
            Text: text,
            ChatIndex: 0,
            Tick: null,
            FromUserId: null);
        var demoDates = new Dictionary<ulong, DateTime?>
        {
            [1] = new DateTime(2024, 1, 1)
        };
        var demoUsers = new Dictionary<ulong, WrHistoryChat.DemoUsers>();

        var entry = WrHistoryChat.TryParseIrcRecord(candidate, map, demoDates, demoUsers);
        entry.Should().NotBeNull();
        return entry!;
    }
}
