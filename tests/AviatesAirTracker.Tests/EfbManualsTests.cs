using AviatesAirTracker.Core.Efb;

namespace AviatesAirTracker.Tests;

public class EfbManualLibraryTests
{
    /// <summary>The markdown manuals, read from the app project's /Manuals folder (the same files the app embeds).</summary>
    private static List<EfbDocument> Library()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "AviatesAirTracker", "Manuals")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var files = Directory.GetFiles(Path.Combine(dir!.FullName, "AviatesAirTracker", "Manuals"), "*.md");
        Assert.NotEmpty(files);
        var docs = files.Select(f => EfbManualLibrary.Parse(File.ReadAllText(f))).ToList();
        Assert.All(docs, d => Assert.NotNull(d));
        return docs.Select(d => d!).Concat(EfbManualLibrary.ChecklistDocuments("2026-09-24")).ToList();
    }

    [Fact]
    public void EveryDocument_HasMetadata_AndAKnownManual()
    {
        var docs = Library();
        var codes = EfbManualLibrary.Manuals.Select(m => m.Code).ToHashSet();
        Assert.Equal(docs.Count, docs.Select(d => d.Id).Distinct().Count());
        Assert.All(docs, d =>
        {
            Assert.Contains(d.Manual, codes);
            Assert.False(string.IsNullOrWhiteSpace(d.Title), d.Id);
            Assert.False(string.IsNullOrWhiteSpace(d.Summary), d.Id);
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", d.Effective);
            Assert.NotEmpty(d.Sections);
        });
        // every manual has content
        Assert.All(codes, c => Assert.Contains(docs, d => d.Manual == c));
    }

    [Fact]
    public void EveryDocLink_ResolvesToADocumentAndSection()
    {
        var docs = Library().ToDictionary(d => d.Id);
        foreach (var d in docs.Values)
            foreach (var target in EfbManualLibrary.LinkTargets(d.BodyMarkdown))
            {
                var parts = target.Split('#');
                Assert.True(docs.ContainsKey(parts[0]), $"{d.Id} links to missing document '{parts[0]}'");
                if (parts.Length > 1)
                    Assert.True(docs[parts[0]].Sections.Any(s => s.Anchor == parts[1]),
                        $"{d.Id} links to missing section '{target}'");
            }
    }

    [Fact]
    public void CompanyNotams_RequireAcknowledgement_AndCoverTheOwnersNotices()
    {
        var cn = Library().Where(d => d.Manual == "CN").ToList();
        Assert.Equal(["cn-001", "cn-002", "cn-003"], cn.Select(d => d.Id).OrderBy(x => x));
        Assert.All(cn, d => Assert.True(d.RequiresAck));
        Assert.Contains("FLAPS 5", cn.Single(d => d.Id == "cn-002").BodyMarkdown);
        var ldg = cn.Single(d => d.Id == "cn-003").BodyMarkdown;
        Assert.Contains("FLAPS 25", ldg);
        Assert.Contains("CONF 3", ldg);
        Assert.Contains("never", ldg, StringComparison.OrdinalIgnoreCase);   // no PIREP action for the minimum
    }

    [Fact]
    public void QrhChecklists_AreGeneratedForEveryFleetSet()
    {
        var qrh = EfbManualLibrary.ChecklistDocuments("2026-09-24");
        var sets = EfbChecklistLibrary.Sets.Where(s => s.Id != EfbChecklistLibrary.GenericSetId).ToList();
        Assert.Equal(sets.Count, qrh.Count);
        var a320 = qrh.Single(d => d.Id == "qrh-nc-a320");
        Assert.Contains(a320.Sections, s => s.Title == "BEFORE START");
        Assert.Contains("| ─── | ─── |", a320.BodyMarkdown);                      // the Airbus line
        Assert.Contains("**COMPLETED** *(BOTH)*", a320.BodyMarkdown);
    }

    [Theory]
    [InlineData("B738", "om-b-737")]
    [InlineData("B38M", "om-b-737")]
    [InlineData("A20N", "om-b-a320")]
    [InlineData("AT46", "om-b-atr")]
    [InlineData("B77F", "om-b-777f")]
    public void TypeSpecificDocuments_ApplyToTheirAircraft(string type, string docId)
    {
        var doc = Library().Single(d => d.Id == docId);
        Assert.True(EfbManualLibrary.AppliesTo(doc, type));
        Assert.False(EfbManualLibrary.AppliesTo(Library().Single(d => d.Id == "om-a-02"), type));
    }

    [Fact]
    public void Search_FindsSections_WithSnippets()
    {
        var hits = EfbManualLibrary.Search(Library(), "final reserve");
        var fuel = hits.First(h => h.DocId == "om-a-02" && h.Anchor != "");
        Assert.Equal("Planned fuel", fuel.Section);
        Assert.Contains("final reserve", fuel.Snippet, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(EfbManualLibrary.Search(Library(), "x"));
    }

    [Fact]
    public void Parse_ReadsFrontMatter()
    {
        var d = EfbManualLibrary.Parse("---\nid: t-1\nmanual: om-a\nchapter: 9\ntitle: T: with colon\nsummary: S\ntags: a, b\naircraft: A320, b737ng\nack: true\nrevision: 2\neffective: 2026-01-01\n---\n## One\ntext\n### Two B\n");
        Assert.NotNull(d);
        Assert.Equal("OM-A", d!.Manual);
        Assert.Equal("T: with colon", d.Title);
        Assert.Equal(["a320", "b737ng"], d.Aircraft);
        Assert.True(d.RequiresAck);
        Assert.Equal(2, d.Revision);
        Assert.Equal(["one", "two-b"], d.Sections.Select(s => s.Anchor));
        Assert.Null(EfbManualLibrary.Parse("# no front matter"));
    }
}

public class EfbCompanyPolicyTests
{
    [Fact]
    public void Boeing737_TakeoffExcludesFlaps1_AndOptNeverPicksIt()
    {
        var p = AircraftPerformanceProfiles.Find("B738")!;
        Assert.DoesNotContain(EfbCompanyPolicy.TakeoffFlaps(p), f => f.Label == "Flaps 1");
        Assert.Contains(p.TakeoffFlaps, f => f.Label == "Flaps 1");                   // the profile itself is unchanged

        var req = new TakeoffPerfRequest
        {
            AircraftCode = "B738", WeightLbs = 120_000, FlapSetting = "OPT", RunwayLengthM = 3500,
            RunwayHeadingDeg = 270, RunwayElevationFt = 100, OatC = 10, QnhMb = 1013,
        };
        Assert.NotEqual("Flaps 1", EfbPerformanceCalculator.CalculateTakeoff(req).FlapSetting);

        req.FlapSetting = "Flaps 1";
        var forced = EfbPerformanceCalculator.CalculateTakeoff(req);
        Assert.StartsWith("CN-002", forced.Warnings[0]);
        Assert.NotEqual("OK", forced.Status);
    }

    [Fact]
    public void LandingFlaps_HonourTheCompanyMinimum_AndStandard()
    {
        var b738 = AircraftPerformanceProfiles.Find("B738")!;
        Assert.Equal(["Flaps 25", "Flaps 30", "Flaps 40"], EfbCompanyPolicy.LandingFlaps(b738).Select(f => f.Label));
        Assert.Equal("Flaps 30", EfbCompanyPolicy.StandardLandingFlap(b738));

        var a20n = AircraftPerformanceProfiles.Find("A20N")!;
        Assert.Equal(["Config 3", "Config Full"], EfbCompanyPolicy.LandingFlaps(a20n).Select(f => f.Label));
        Assert.Equal("Config Full", EfbCompanyPolicy.StandardLandingFlap(a20n));

        var atr = AircraftPerformanceProfiles.Find("AT76")!;
        Assert.Equal(atr.LandingFlaps.Count, EfbCompanyPolicy.LandingFlaps(atr).Count);  // no policy → unchanged
        Assert.Empty(EfbCompanyPolicy.Notices("AT76"));
    }

    [Theory]
    [InlineData("B738", 60)]
    [InlineData("B38M", 60)]
    [InlineData("B739", 60)]
    [InlineData("A20N", 55)]
    [InlineData("A319", 55)]
    [InlineData("A359", 70)]
    [InlineData("AT76", 70)]
    [InlineData(null, 70)]
    public void ReviewFlapThreshold_IsTheCompanyMinimumForTheType(string? type, double pct) =>
        Assert.Equal(pct, EfbCompanyPolicy.MinLandingFlapPercent(type));
}
