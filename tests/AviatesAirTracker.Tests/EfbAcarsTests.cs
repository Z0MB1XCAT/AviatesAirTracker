using AviatesAirTracker.Core.Efb;
using Xunit;

namespace AviatesAirTracker.Tests;

public class EfbAcarsTests
{
    [Fact]
    public void Parse_PeekBody_ReadsIdSenderTypeAndPacket()
    {
        var list = EfbAcars.Parse("ok {1201 EGLL telex {PDC 001 CLRD TO LSZH}} {1202 EDGG cpdlc {/data2/3//WU/CLIMB TO @FL360@}}");
        Assert.Equal(2, list.Count);
        Assert.Equal(new HoppieMessage("1201", "EGLL", "telex", "PDC 001 CLRD TO LSZH"), list[0]);
        Assert.Equal("cpdlc", list[1].Type);
        Assert.Equal("/data2/3//WU/CLIMB TO @FL360@", list[1].Packet);
    }

    [Fact]
    public void Parse_PollAndInforeqBodies_HaveNoId()
    {
        var poll = EfbAcars.Parse("ok {VAOPS telex {LOADSHEET FINAL}}");
        Assert.Single(poll);
        Assert.Null(poll[0].Id);
        Assert.Equal("VAOPS", poll[0].From);

        var info = EfbAcars.Parse("ok {server info {EGLL 131220Z 24012KT 9999 FEW030 15/08 Q1015}}");
        Assert.Equal("info", info[0].Type);
        Assert.StartsWith("EGLL 131220Z", info[0].Packet);
    }

    [Theory]
    [InlineData("ok")]
    [InlineData("ok ")]
    [InlineData("error {illegal logon code}")]
    [InlineData("")]
    public void Parse_EmptyOrError_YieldsNothing(string body) => Assert.Empty(EfbAcars.Parse(body));

    [Fact]
    public void ErrorOf_ExplainsABadLogonCode()
    {
        Assert.Null(EfbAcars.ErrorOf("ok {}"));
        Assert.Contains("logon code", EfbAcars.ErrorOf("error {illegal logon code}"));
        Assert.Equal("Hoppie: callsign in use", EfbAcars.ErrorOf("error {callsign in use}"));
    }

    [Fact]
    public void Cpdlc_ParsesUplinkAndOffersDcduResponses()
    {
        Assert.True(EfbAcars.TryParseCpdlc("/data2/7/4/WU/CLIMB TO @FL360@@_@REPORT LEVEL @FL360@", out var p));
        Assert.Equal(7, p.Min);
        Assert.Equal(4, p.Mrn);
        Assert.Equal("WU", p.Ra);
        Assert.Equal("CLIMB TO FL360\nREPORT LEVEL FL360", p.Display);
        Assert.Equal(["WILCO", "UNABLE", "STANDBY"], p.Responses);

        Assert.True(EfbAcars.TryParseCpdlc("/data2/2//NE/LOGON ACCEPTED", out var q));
        Assert.Null(q.Mrn);
        Assert.Empty(q.Responses);

        Assert.False(EfbAcars.TryParseCpdlc("HELLO", out _));
    }

    [Fact]
    public void Cpdlc_FormatsDownlinksTheWayStationsExpect()
    {
        Assert.Equal("/data2/1//Y/REQUEST LOGON", EfbAcars.FormatCpdlc(1, null, "Y", "REQUEST LOGON"));
        Assert.Equal("/data2/5/7/N/WILCO", EfbAcars.FormatCpdlc(5, 7, "N", "WILCO"));
        Assert.True(EfbAcars.IsClosing("WILCO"));
        Assert.False(EfbAcars.IsClosing("STANDBY"));
    }

    [Fact]
    public void PdcRequest_MatchesTheControllerToolFormat()
    {
        Assert.Equal("REQUEST PREDEP CLEARANCE AVA123 A20N TO LSZH AT EGLL STAND 512 ATIS K",
            EfbAcars.PdcRequest("ava123", "a20n", "lszh", "egll", "512", "k"));
        Assert.Equal("REQUEST PREDEP CLEARANCE AVA123 B738 TO LSZH AT EGLL",
            EfbAcars.PdcRequest("AVA123", "B738", "LSZH", "EGLL", null, ""));
    }

    [Fact]
    public void Clean_UppercasesAndStripsBracesThatWouldBreakTheWireFormat()
    {
        Assert.Equal("HELLO (WORLD) TEST", EfbAcars.Clean("  hello {world}   test "));
        Assert.True(EfbAcars.IsStation("EDGG"));
        Assert.True(EfbAcars.IsStation("EGLL_DEL"));
        Assert.False(EfbAcars.IsStation("E G"));
        Assert.False(EfbAcars.IsStation(""));
    }
}
