using System.Xml.Linq;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Protocols.Dash;
using Xunit;

namespace Vesper.MediaResolver.Tests.ProtocolTests;

public sealed class DashTimelineRegressionTests
{
    private static readonly Uri Url = new("https://cdn.example.com/manifest.mpd");
    [Fact]
    public void NegativeRepeat_StopsAtTheNextExplicitStart_NotAtThePeriodEnd()
    {
        var template = DashTemplate.Merge(XElement.Parse("""
            <SegmentTemplate media="s$Time$.m4s" timescale="1"><SegmentTimeline><S t="0" d="2" r="-1"/><S t="6" d="2" r="1"/></SegmentTimeline></SegmentTemplate>
            """))!;
        var plan = template.Expand("a", 1, Url, 10, 50, true);
        Assert.Equal(new[] { "/s0.m4s", "/s2.m4s", "/s4.m4s", "/s6.m4s", "/s8.m4s" }, plan.Segments.Select(s => s.Url.AbsolutePath));
    }
    [Fact]
    public void BoundedRepeat_DoesNotRequireAKnownPeriodDuration()
    {
        var template = DashTemplate.Merge(XElement.Parse("""
            <SegmentTemplate media="s$Time$.m4s"><SegmentTimeline><S t="0" d="2" r="-1"/><S t="4" d="2"/></SegmentTimeline></SegmentTemplate>
            """))!;
        Assert.Equal(3, template.Expand("a", 1, Url, null, 50, true).Segments.Count);
    }
    [Fact]
    public void InvalidNegativeRepeat_IsNotTreatedAsMinusOne()
    {
        Assert.Throws<MediaResolverException>(() => DashTemplate.Merge(XElement.Parse("""
            <SegmentTemplate media="s.m4s"><SegmentTimeline><S d="2" r="-2"/></SegmentTimeline></SegmentTemplate>
            """)));
    }
    [Fact]
    public void PeriodTemplate_IsInheritedAndRepresentationCanOverrideMedia()
    {
        const string xml = """
            <MPD mediaPresentationDuration="PT4S"><Period><SegmentTemplate timescale="1" duration="2" initialization="init.mp4" media="old-$Number$.m4s"/>
            <AdaptationSet mimeType="audio/mp4"><Representation id="a" codecs="mp4a.40.2"><SegmentTemplate media="new-$Number$.m4s"/></Representation></AdaptationSet></Period></MPD>
            """;
        var plan = Assert.Single(DashParser.Parse(xml, Url).Representations).Segments!;
        Assert.Equal("/init.mp4", plan.InitializationUrl!.AbsolutePath);
        Assert.Equal(new[] { "/new-1.m4s", "/new-2.m4s" }, plan.Segments.Select(s => s.Url.AbsolutePath));
    }
}
