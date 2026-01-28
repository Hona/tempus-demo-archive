using FluentAssertions;

namespace TempusDemoArchive.Jobs.Tests;

public class TempusTimeTests
{
    [Theory]
    [InlineData("00:43.60", 4360, "00:43.60")]
    [InlineData("01:23.95", 8395, "01:23.95")]
    [InlineData("1:02:03.42", 372342, "1:02:03.42")]
    public void ParseAndFormat_RoundTrips(string input, int expectedCentiseconds, string expectedFormatted)
    {
        TempusTime.TryParseTimeCentiseconds(input, out var centiseconds).Should().BeTrue();
        centiseconds.Should().Be(expectedCentiseconds);
        TempusTime.FormatTimeFromCentiseconds(centiseconds).Should().Be(expectedFormatted);
    }

    [Theory]
    [InlineData("+00:40.06", "+00:40.06")]
    [InlineData("-00:01.23", "-00:01.23")]
    [InlineData("00:40.06", "00:40.06")]
    public void NormalizeSignedTime_PreservesExpectedShape(string input, string expected)
    {
        TempusTime.NormalizeSignedTime(input).Should().Be(expected);
    }
}

public class WrLookupParsingTests
{
    [Fact]
    public void CompactBonusRecord_IsMarkedLookupEvenWhenSourceIsBonus()
    {
        var candidate = new WrHistoryChat.ChatCandidate(
            DemoId: 1,
            Map: "jump_beef",
            Text: "Tempus | (Demo WR) jump_beef/Bonus 1 :: 00:04.88 :: alle -tt",
            ChatIndex: 0,
            Tick: null,
            FromUserId: null);

        var demoDates = new Dictionary<ulong, DateTime?>();
        var demoUsers = new Dictionary<ulong, WrHistoryChat.DemoUsers>();

        var entry = WrHistoryChat.TryParseTempusRecord(candidate, "jump_beef", demoDates, demoUsers);
        entry.Should().NotBeNull();

        entry!.IsLookup.Should().BeTrue();
        entry.Source.Should().Be($"{WrHistoryConstants.SegmentPrefix.Bonus} 1");
        entry.RecordType.Should().Be(WrHistoryConstants.RecordType.Wr);
    }

    [Fact]
    public void RankedRecord_IsMarkedLookupAndInferred()
    {
        var candidate = new WrHistoryChat.ChatCandidate(
            DemoId: 1,
            Map: "jump_airshift_a4",
            Text: "Tempus | (Demo) newjuls is ranked 1/1134 on jump_airshift_a4 with time: 00:39.81",
            ChatIndex: 0,
            Tick: null,
            FromUserId: null);

        var demoDates = new Dictionary<ulong, DateTime?>();
        var demoUsers = new Dictionary<ulong, WrHistoryChat.DemoUsers>();

        var entry = WrHistoryChat.TryParseTempusRecord(candidate, "jump_airshift_a4", demoDates,
            demoUsers);
        entry.Should().NotBeNull();

        entry!.IsLookup.Should().BeTrue();
        entry.Inferred.Should().BeTrue();
        entry.Source.Should().Be(WrHistoryConstants.Source.Ranked);
        entry.RecordType.Should().Be(WrHistoryConstants.RecordType.Wr);
    }
}
