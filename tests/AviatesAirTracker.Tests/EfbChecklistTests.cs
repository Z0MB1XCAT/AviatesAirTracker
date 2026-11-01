using AviatesAirTracker.Core.Efb;
using AviatesAirTracker.Services.Efb;

namespace AviatesAirTracker.Tests;

public class EfbChecklistLibraryTests
{
    private static readonly HashSet<string> PhaseKeys =
    [
        "preflight", "before-start", "after-start", "before-taxi", "taxi", "before-takeoff", "after-takeoff",
        "cruise", "descent", "approach", "landing", "after-landing", "shutdown", "secure",
    ];

    [Theory]
    // the AviatesAir fleet, as ICAO codes and as the fleet list names them
    [InlineData("A319", "a320")]
    [InlineData("A20N", "a320")]
    [InlineData("A320NEO", "a320")]
    [InlineData("A359", "a350")]
    [InlineData("A350", "a350")]
    [InlineData("A388", "a380")]
    [InlineData("A380", "a380")]
    [InlineData("B736", "b737ng")]
    [InlineData("B738", "b737ng")]
    [InlineData("B739ER", "b737ng")]
    [InlineData("B38M", "b737max")]
    [InlineData("B77F", "b777")]
    [InlineData("B748", "b748")]
    [InlineData("AT76", "atr72")]
    [InlineData("ATR 72", "atr72")]
    [InlineData("AT46", "atr42")]
    [InlineData("ATR 42", "atr42")]
    // free text from the sim title / booking
    [InlineData("Boeing 737-800", "b737ng")]
    [InlineData("Boeing 737 MAX 8", "b737max")]
    [InlineData("Airbus A320neo", "a320")]
    [InlineData("atr 72-600", "atr72")]
    // outside the fleet
    [InlineData("C172", "generic")]
    [InlineData("", "generic")]
    [InlineData(null, "generic")]
    public void Resolve_MapsAircraftTypesToTheirSet(string? type, string expected) =>
        Assert.Equal(expected, EfbChecklistLibrary.Resolve(type).Id);

    [Fact]
    public void EverySet_IsWellFormed()
    {
        Assert.Equal(EfbChecklistLibrary.Sets.Count, EfbChecklistLibrary.Sets.Select(s => s.Id).Distinct().Count());
        foreach (var set in EfbChecklistLibrary.Sets)
        {
            Assert.NotEmpty(set.Checklists);
            Assert.False(string.IsNullOrWhiteSpace(set.Source));
            Assert.Contains(set.Style, new[] { "airbus", "boeing", "atr", "generic" });
            Assert.Equal(set.Checklists.Count, set.Checklists.Select(c => c.Id).Distinct().Count());
            foreach (var cl in set.Checklists)
            {
                Assert.Contains(cl.PhaseKey, PhaseKeys);
                Assert.NotEmpty(cl.Items);
                Assert.Equal(cl.Items.Count, cl.Items.Select(i => i.Id).Distinct().Count());
                Assert.All(cl.Items, i => Assert.False(string.IsNullOrWhiteSpace(i.Response)));
            }
            // every set covers the full flight
            foreach (var key in new[] { "before-start", "before-takeoff", "after-takeoff", "landing", "shutdown" })
                Assert.Contains(set.Checklists, c => c.PhaseKey == key);
        }
    }

    [Fact]
    public void TypeCodes_AreClaimedByOneSetOnly()
    {
        var all = EfbChecklistLibrary.Sets.SelectMany(s => s.Types).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
    }

    private static EfbChecklistDef Cl(string set, string id) =>
        EfbChecklistLibrary.FindSet(set)!.Checklists.Single(c => c.Id == id);

    [Fact]
    public void Airbus_BeforeStart_HasTheLineBeforeWindowsDoors()
    {
        var items = Cl("a320", "before-start").Items;
        var line = items.Single(i => i.LineBefore);
        Assert.Equal("WINDOWS/DOORS", line.Challenge);
        Assert.Equal("BOTH", line.Who);
    }

    [Fact]
    public void TypeDifferences_AreModelled()
    {
        // 737 MAX probe heat has an AUTO position; the NG is switched ON/OFF
        Assert.Equal("ON", Cl("b737ng", "before-taxi").Items.Single(i => i.Challenge == "Probe heat").Response);
        Assert.Equal("AUTO", Cl("b737max", "before-taxi").Items.Single(i => i.Challenge == "Probe heat").Response);
        // 747 beacon BOTH, 777 ON
        Assert.Equal("BOTH", Cl("b748", "before-start").Items.Single(i => i.Challenge == "Beacon").Response);
        Assert.Equal("ON", Cl("b777", "before-start").Items.Single(i => i.Challenge == "Beacon").Response);
        // A350 has automatic pitch trim; the A320 sets it
        Assert.DoesNotContain(Cl("a350", "after-start").Items, i => i.Challenge == "PITCH TRIM");
        Assert.Contains(Cl("a320", "after-start").Items, i => i.Challenge == "PITCH TRIM");
        // ATR 72 needs a tail prop and lands FLAPS 30; the ATR 42 lands FLAPS 35
        Assert.Contains(Cl("atr72", "before-prop-rotation").Items, i => i.Challenge == "TAIL PROP");
        Assert.DoesNotContain(EfbChecklistLibrary.FindSet("atr42")!.Checklists.SelectMany(c => c.Items), i => i.Challenge == "TAIL PROP");
        Assert.Equal("30 SET", Cl("atr72", "before-landing").Items.Single(i => i.Challenge == "FLAPS").Response);
        Assert.Equal("35 SET", Cl("atr42", "before-landing").Items.Single(i => i.Challenge == "FLAPS").Response);
    }
}

public class EfbChecklistServiceTests
{
    [Fact]
    public void Bundle_FollowsTheAircraftType()
    {
        var svc = new EfbChecklistService();
        var b = svc.GetBundle("B38M");
        Assert.Equal("b737max", b.SetId);
        Assert.Equal("b737max", b.AutoSetId);
        Assert.Equal("boeing", b.Style);
        Assert.False(b.Manual);
        Assert.Equal(EfbChecklistLibrary.Sets.Count, b.Sets.Count);
        Assert.Equal("preflight", b.Checklists[0].PhaseKey);
    }

    [Fact]
    public void ManualSelection_OverridesAuto_UntilAutoIsPicked()
    {
        var svc = new EfbChecklistService();
        Assert.True(svc.SelectSet("a380"));
        var b = svc.GetBundle("B738");
        Assert.Equal("a380", b.SetId);
        Assert.Equal("b737ng", b.AutoSetId);
        Assert.True(b.Manual);

        Assert.True(svc.SelectSet("auto"));
        Assert.Equal("b737ng", svc.GetBundle("B738").SetId);
        Assert.False(svc.SelectSet("concorde"));
    }

    [Fact]
    public void CheckState_IsKeptPerSet()
    {
        var svc = new EfbChecklistService();
        Assert.True(svc.SetItem("a320", "before-start", "beacon", true));
        Assert.False(svc.SetItem("a320", "before-start", "no-such-item", true));
        Assert.False(svc.SetItem("nope", "before-start", "beacon", true));

        Assert.True(svc.GetBundle("A20N").Checklists.Single(c => c.Id == "before-start").Items.Single(i => i.Id == "beacon").IsChecked);
        Assert.False(svc.GetBundle("A359").Checklists.Single(c => c.Id == "before-start").Items.Single(i => i.Id == "beacon").IsChecked);
    }

    [Fact]
    public void Checking_Twice_KeepsTheFirstTimestamp()
    {
        var svc = new EfbChecklistService();
        svc.SetItem("b777", "before-taxi", "recall", true);
        var t1 = svc.GetBundle("B77F").Checklists.Single(c => c.Id == "before-taxi").Items.Single(i => i.Id == "recall").CheckedAtUtc;
        Thread.Sleep(15);
        svc.SetItem("b777", "before-taxi", "recall", true);
        var t2 = svc.GetBundle("B77F").Checklists.Single(c => c.Id == "before-taxi").Items.Single(i => i.Id == "recall").CheckedAtUtc;
        Assert.Equal(t1, t2);
    }

    [Fact]
    public void Complete_And_Reset_WorkPerChecklistAndPerSet()
    {
        var svc = new EfbChecklistService();
        Assert.True(svc.CompleteChecklist("atr72", "taxi"));
        svc.SetItem("b748", "preflight", "oxygen", true);

        var taxi = svc.GetBundle("AT76").Checklists.Single(c => c.Id == "taxi");
        Assert.True(taxi.IsComplete);
        Assert.NotNull(taxi.CompletedAtUtc);

        svc.ResetAll("atr72");
        Assert.False(svc.GetBundle("AT76").Checklists.Single(c => c.Id == "taxi").IsComplete);
        Assert.True(svc.GetBundle("B748").Checklists.Single(c => c.Id == "preflight").Items.Single(i => i.Id == "oxygen").IsChecked);

        svc.ResetAll();
        Assert.All(svc.GetBundle("B748").Checklists, c => Assert.Equal(0, c.CheckedCount));
    }

    [Fact]
    public void Changes_RaiseTheChangedEvent()
    {
        var svc = new EfbChecklistService();
        int n = 0;
        svc.Changed += (_, _) => n++;
        svc.SetItem("a320", "landing", "cabin", true);
        svc.SelectSet("a350");
        svc.ResetChecklist("a320", "landing");
        Assert.Equal(3, n);
    }
}
