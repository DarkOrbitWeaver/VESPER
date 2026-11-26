using System.Xml.Linq;
using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Protocols;
using Vesper.MediaResolver.Protocols.Dash;
using Vesper.MediaResolver.Tests.TestSupport;
using Xunit;

namespace Vesper.MediaResolver.Tests.ProtocolTests;

public class DashParserTests
{
    private static readonly Uri ManifestUrl = new("https://example.com/m/manifest.mpd");

    private const string TimelineMpd = """
<MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT20S">
  <BaseURL>https://cdn.example.com/vod/</BaseURL>
  <Period>
    <AdaptationSet contentType="video" mimeType="video/mp4">
      <SegmentTemplate timescale="1000" initialization="v/$RepresentationID$/init.mp4" media="v/$RepresentationID$/seg-$Number$.m4s" startNumber="1">
        <SegmentTimeline><S t="0" d="5000" r="3"/></SegmentTimeline>
      </SegmentTemplate>
      <Representation id="v1080" bandwidth="4000000" width="1920" height="1080" codecs="avc1.640028" frameRate="30000/1001"/>
      <Representation id="v720" bandwidth="2000000" width="1280" height="720" codecs="avc1.64001f"/>
    </AdaptationSet>
    <AdaptationSet contentType="audio" mimeType="audio/mp4" lang="en">
      <SegmentTemplate timescale="1000" initialization="a/en/init.mp4" media="a/en/seg-$Number$.m4s" startNumber="1">
        <SegmentTimeline><S t="0" d="5000" r="3"/></SegmentTimeline>
      </SegmentTemplate>
      <Representation id="a-en" bandwidth="128000" codecs="mp4a.40.2" audioSamplingRate="48000">
        <AudioChannelConfiguration schemeIdUri="urn:mpeg:dash:23003:3:audio_channel_configuration:2011" value="2"/>
      </Representation>
    </AdaptationSet>
    <AdaptationSet contentType="audio" mimeType="audio/mp4" lang="es">
      <SegmentTemplate timescale="1000" initialization="a/es/init.mp4" media="a/es/seg-$Number$.m4s" startNumber="1">
        <SegmentTimeline><S t="0" d="5000" r="3"/></SegmentTimeline>
      </SegmentTemplate>
      <Representation id="a-es" bandwidth="128000" codecs="mp4a.40.2"/>
    </AdaptationSet>
  </Period>
</MPD>
""";

    private static ManifestResolver Manifests()
    {
        var o = new ResolverOptions();
        return new ManifestResolver(new SecureHttpClient(o, new FakeHttpHandler()), o);
    }

    private static string MpdWithAddressing(string addressing) => $"""
<MPD mediaPresentationDuration="PT10S">
  <Period>
    <AdaptationSet contentType="video" mimeType="video/mp4">
      {addressing}
      <Representation id="v1"/>
    </AdaptationSet>
  </Period>
</MPD>
""";

    [Fact]
    public void Timeline_ExpandsSegmentsInitAndTiming()
    {
        var m = DashParser.Parse(TimelineMpd, ManifestUrl);

        Assert.False(m.IsDynamic);
        Assert.Equal(TimeSpan.FromSeconds(20), m.Duration);
        Assert.Equal(4, m.Representations.Count);   // v1080, v720, a-en, a-es

        var v = m.Representations.Single(r => r.Id == "v1080");
        Assert.Equal(4, v.Segments!.Segments.Count);
        Assert.Equal(new Uri("https://cdn.example.com/vod/v/v1080/seg-1.m4s"), v.Segments.Segments[0].Url);
        Assert.Equal(new Uri("https://cdn.example.com/vod/v/v1080/seg-4.m4s"), v.Segments.Segments[3].Url);
        Assert.Equal(new Uri("https://cdn.example.com/vod/v/v1080/init.mp4"), v.Segments.InitializationUrl);
        Assert.Equal(20, v.Segments.TotalDurationSeconds, 3);
        Assert.Equal([0d, 5d, 10d, 15d], v.Segments.Segments.Select(segment => segment.StartSeconds));
        Assert.False(v.Segments.HasDiscontinuities);
        Assert.Equal(29.97, v.FrameRate!.Value, 2);
    }

    [Fact]
    public void Builder_ProducesVideoOnlyAndAudioOnlyRepresentationsWithLanguages()
    {
        var m = DashParser.Parse(TimelineMpd, ManifestUrl);
        var result = DashRepresentationBuilder.Build(m, ManifestUrl, MediaRequestContext.Empty);

        Assert.Equal(4, result.Representations.Count);
        Assert.All(result.Representations.Where(r => r.HasVideo), r => Assert.Equal(StreamContent.VideoOnly, r.Content));
        var en = result.Representations.Single(r => r.AudioLanguage == "en");
        Assert.Equal(StreamContent.AudioOnly, en.Content);
        Assert.Equal(2, en.AudioChannels);
        Assert.Equal(48000, en.AudioSampleRate);
        Assert.Equal("m4a", en.Container);
        Assert.Equal("aac", en.AudioCodec);
        Assert.True(en.Requirements.HasFlag(ProcessingRequirement.DashSegmentAssembly));
        Assert.Contains(result.AudioTracks, t => t.Language == "es");
        Assert.Equal(TimeSpan.FromSeconds(20), result.Duration);
    }

    [Fact]
    public void DurationTemplate_UsesNumberPaddingAndShortLastSegment()
    {
        const string mpd = """
<MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT10S">
  <Period>
    <AdaptationSet contentType="video" mimeType="video/mp4">
      <SegmentTemplate timescale="1" duration="4" media="s$Number%05d$.m4s" initialization="init.mp4"/>
      <Representation id="r1" bandwidth="1000000" width="640" height="360" codecs="avc1.4d401e"/>
    </AdaptationSet>
  </Period>
</MPD>
""";
        var plan = DashParser.Parse(mpd, ManifestUrl).Representations.Single().Segments!;
        Assert.Equal(3, plan.Segments.Count);
        Assert.Equal(new Uri("https://example.com/m/s00001.m4s"), plan.Segments[0].Url);
        Assert.Equal(new Uri("https://example.com/m/s00003.m4s"), plan.Segments[2].Url);
        Assert.Equal(2.0, plan.Segments[2].DurationSeconds, 3);
        Assert.Equal(10.0, plan.TotalDurationSeconds, 3);
    }

    [Fact]
    public void SegmentList_KeepsByteRangesAndInitRange()
    {
        const string mpd = """
<MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT6S">
  <Period>
    <AdaptationSet contentType="video" mimeType="video/mp4">
      <Representation id="r1" bandwidth="500000" width="640" height="360" codecs="avc1.4d401e">
        <BaseURL>single.mp4</BaseURL>
        <SegmentList timescale="1" duration="3">
          <Initialization range="0-99"/>
          <SegmentURL mediaRange="100-499"/>
          <SegmentURL mediaRange="500-999"/>
        </SegmentList>
      </Representation>
    </AdaptationSet>
  </Period>
</MPD>
""";
        var plan = DashParser.Parse(mpd, ManifestUrl).Representations.Single().Segments!;
        Assert.Equal(new Uri("https://example.com/m/single.mp4"), plan.InitializationUrl);
        Assert.Equal(new ByteRange(0, 100), plan.InitializationRange);
        Assert.Equal(new ByteRange(100, 400), plan.Segments[0].Range);
        Assert.Equal(new ByteRange(500, 500), plan.Segments[1].Range);
        Assert.Equal(6.0, plan.TotalDurationSeconds, 3);
    }

    [Fact]
    public void SegmentList_InheritsTimelineAndOffsetFromPeriod()
    {
        const string mpd = """
<MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT20S">
  <Period start="PT10S" duration="PT5S">
    <SegmentList timescale="1000" presentationTimeOffset="2000">
      <Initialization sourceURL="init.mp4"/>
      <SegmentTimeline><S t="2000" d="2000"/><S t="5000" d="2000"/></SegmentTimeline>
      <SegmentURL media="one.m4s"/>
      <SegmentURL media="two.m4s"/>
    </SegmentList>
    <AdaptationSet contentType="video" mimeType="video/mp4">
      <SegmentList><Initialization sourceURL="adaptation-init.mp4"/></SegmentList>
      <Representation id="v1" codecs="avc1.4d401e"/>
    </AdaptationSet>
  </Period>
</MPD>
""";

        var plan = Assert.Single(DashParser.Parse(mpd, ManifestUrl).Representations).Segments!;

        Assert.Equal(new Uri("https://example.com/m/adaptation-init.mp4"), plan.InitializationUrl);
        Assert.Equal(new[] { "/m/one.m4s", "/m/two.m4s" }, plan.Segments.Select(segment => segment.Url.AbsolutePath));
        Assert.Equal([10d, 13d], plan.Segments.Select(segment => segment.StartSeconds));
        Assert.Equal([2d, 2d], plan.Segments.Select(segment => segment.DurationSeconds));
        Assert.Equal(4d, plan.TotalDurationSeconds, 3);
        Assert.True(plan.HasDiscontinuities);
    }

    [Fact]
    public void SegmentList_RejectsTimelineAndUrlCountMismatch()
    {
        const string mpd = """
<MPD mediaPresentationDuration="PT4S"><Period>
  <AdaptationSet contentType="video" mimeType="video/mp4">
    <SegmentList timescale="1">
      <SegmentTimeline><S t="0" d="2"/><S t="2" d="2"/></SegmentTimeline>
      <SegmentURL media="one.m4s"/>
    </SegmentList>
    <Representation id="v1"/>
  </AdaptationSet>
</Period></MPD>
""";

        var ex = Assert.Throws<MediaResolverException>(() => DashParser.Parse(mpd, ManifestUrl));
        Assert.Equal(MediaErrorCode.UnsupportedManifest, ex.Error.Code);
    }

    [Fact]
    public void SegmentList_RejectsDurationCountThatDoesNotCoverThePeriod()
    {
        const string mpd = """
<MPD mediaPresentationDuration="PT10S"><Period>
  <AdaptationSet contentType="video" mimeType="video/mp4">
    <SegmentList timescale="1" duration="3">
      <SegmentURL media="one.m4s"/><SegmentURL media="two.m4s"/><SegmentURL media="three.m4s"/>
    </SegmentList>
    <Representation id="v1"/>
  </AdaptationSet>
</Period></MPD>
""";

        var ex = Assert.Throws<MediaResolverException>(() => DashParser.Parse(mpd, ManifestUrl));
        Assert.Equal(MediaErrorCode.UnsupportedManifest, ex.Error.Code);
    }

    [Fact]
    public void InheritedSegmentList_ConflictsWithSegmentTemplateAndIsRejected()
    {
        const string mpd = """
<MPD mediaPresentationDuration="PT2S"><Period>
  <SegmentList timescale="1" duration="2"><SegmentURL media="one.m4s"/></SegmentList>
  <AdaptationSet contentType="video" mimeType="video/mp4">
    <SegmentTemplate timescale="1" duration="2" media="s$Number$.m4s"/>
    <Representation id="v1"/>
  </AdaptationSet>
</Period></MPD>
""";

        var ex = Assert.Throws<MediaResolverException>(() => DashParser.Parse(mpd, ManifestUrl));
        Assert.Equal(MediaErrorCode.UnsupportedManifest, ex.Error.Code);
    }

    [Theory]
    [InlineData("timescale", "0")]
    [InlineData("timescale", "4294967296")]
    [InlineData("timescale", "9223372036854775808")]
    [InlineData("duration", "invalid")]
    [InlineData("duration", "0")]
    [InlineData("duration", "4294967296")]
    [InlineData("startNumber", "4294967296")]
    [InlineData("presentationTimeOffset", "-1")]
    public void SegmentList_RejectsInvalidSuppliedNumbers(string name, string value)
    {
        var list = new XElement("SegmentList", new XElement("SegmentURL", new XAttribute("media", "one.m4s")));
        list.SetAttributeValue(name, value);
        var mpd = new XDocument(new XElement("MPD",
            new XAttribute("mediaPresentationDuration", "PT10S"),
            new XElement("Period",
                new XElement("AdaptationSet",
                    new XAttribute("contentType", "video"),
                    new XAttribute("mimeType", "video/mp4"),
                    list,
                    new XElement("Representation", new XAttribute("id", "v1"))))));

        var ex = Assert.Throws<MediaResolverException>(() => DashParser.Parse(mpd.ToString(), ManifestUrl));
        Assert.Equal(MediaErrorCode.UnsupportedManifest, ex.Error.Code);
    }

    [Theory]
    [InlineData("""<SegmentTemplate media="s$Number$.m4s" duration="invalid"/>""")]
    [InlineData("""<SegmentTemplate media="s$Number$.m4s" duration="0"/>""")]
    [InlineData("""<SegmentTemplate media="s$Number$.m4s" duration="4294967296"/>""")]
    [InlineData("""<SegmentTemplate media="s$Number$.m4s" duration="9223372036854775808"/>""")]
    [InlineData("""<SegmentTemplate media="s$Number$.m4s" timescale="0"/>""")]
    [InlineData("""<SegmentTemplate media="s$Number$.m4s" timescale="4294967296"/>""")]
    [InlineData("""<SegmentTemplate media="s$Number$.m4s" startNumber="invalid"/>""")]
    [InlineData("""<SegmentTemplate media="s$Number$.m4s" startNumber="4294967296"/>""")]
    [InlineData("""<SegmentTemplate media="s$Number$.m4s" presentationTimeOffset="-1"/>""")]
    [InlineData("""<SegmentTemplate media="s$Time$.m4s"><SegmentTimeline><S t="invalid" d="2"/></SegmentTimeline></SegmentTemplate>""")]
    [InlineData("""<SegmentTemplate media="s$Time$.m4s"><SegmentTimeline><S t="0" d="9223372036854775808"/></SegmentTimeline></SegmentTemplate>""")]
    [InlineData("""<SegmentTemplate media="s$Time$.m4s"><SegmentTimeline><S t="0" d="2" r="invalid"/></SegmentTimeline></SegmentTemplate>""")]
    [InlineData("""<SegmentTemplate media="s$Number$.m4s"><SegmentTimeline><S t="0" d="2" n="1"/></SegmentTimeline></SegmentTemplate>""")]
    [InlineData("""<SegmentTemplate media="s$Number$.m4s"><SegmentTimeline><S t="0" d="2" k="2"/></SegmentTimeline></SegmentTemplate>""")]
    [InlineData("""<SegmentTemplate media="s$Time$.m4s" duration="2"><SegmentTimeline><S t="0" d="2"/></SegmentTimeline></SegmentTemplate>""")]
    public void SegmentTemplate_RejectsInvalidSuppliedNumbers(string template)
    {
        var ex = Assert.Throws<MediaResolverException>(() => DashParser.Parse(MpdWithAddressing(template), ManifestUrl));
        Assert.Equal(MediaErrorCode.UnsupportedManifest, ex.Error.Code);
    }

    [Fact]
    public void SegmentTemplate_RejectsSequenceNumberOverflow()
    {
        const string mpd = """
<MPD mediaPresentationDuration="PT2S"><Period>
  <AdaptationSet contentType="video" mimeType="video/mp4">
    <SegmentTemplate timescale="1" duration="1" startNumber="9223372036854775807" media="s$Number$.m4s"/>
    <Representation id="v1"/>
  </AdaptationSet>
</Period></MPD>
""";

        var ex = Assert.Throws<MediaResolverException>(() => DashParser.Parse(mpd, ManifestUrl));
        Assert.Equal(MediaErrorCode.UnsupportedManifest, ex.Error.Code);
    }

    [Fact]
    public void SegmentTemplate_RejectsDurationExpansionOverTheSegmentLimit()
    {
        const string mpd = """
<MPD mediaPresentationDuration="PT100S"><Period>
  <AdaptationSet contentType="video" mimeType="video/mp4">
    <SegmentTemplate timescale="1" duration="1" media="s$Number$.m4s"/>
    <Representation id="v1"/>
  </AdaptationSet>
</Period></MPD>
""";

        var ex = Assert.Throws<MediaResolverException>(() => DashParser.Parse(mpd, ManifestUrl, maxSegments: 10));
        Assert.Equal(MediaErrorCode.ResponseTooLarge, ex.Error.Code);
    }

    [Fact]
    public void InvalidExplicitPeriodDuration_IsRejected()
    {
        const string mpd = """
<MPD mediaPresentationDuration="not-a-duration"><Period>
  <AdaptationSet contentType="video" mimeType="video/mp4">
    <Representation id="v1"><BaseURL>video.mp4</BaseURL></Representation>
  </AdaptationSet>
</Period></MPD>
""";

        var ex = Assert.Throws<MediaResolverException>(() => DashParser.Parse(mpd, ManifestUrl));
        Assert.Equal(MediaErrorCode.UnsupportedManifest, ex.Error.Code);
    }

    [Fact]
    public void PlainBaseUrl_IsADirectHttpDownload()
    {
        var m = DashParser.Parse(Fixture.Load("reddit_dash.mpd"), new Uri("https://v.redd.it/abc/DASHPlaylist.mpd"));
        var video = m.Representations.First(r => r.Id == "1");
        Assert.Equal(new Uri("https://v.redd.it/abc/DASH_720.mp4"), video.ProgressiveUrl);
        Assert.Null(video.Segments);

        var built = DashRepresentationBuilder.Build(m, new Uri("https://v.redd.it/abc/DASHPlaylist.mpd"), MediaRequestContext.Empty);
        Assert.All(built.Representations, r => Assert.Equal(StreamProtocol.Http, r.Protocol));
        Assert.All(built.Representations, r => Assert.Equal(ProcessingRequirement.None, r.Requirements));
    }

    [Fact]
    public void MultiPeriod_IsRejectedInsteadOfConcatenated()
    {
        const string mpd = """
<MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT20S">
  <Period id="1" duration="PT10S"><AdaptationSet contentType="video" mimeType="video/mp4"><Representation id="a" bandwidth="1" codecs="avc1.4d401e"><BaseURL>a.mp4</BaseURL></Representation></AdaptationSet></Period>
  <Period id="2" duration="PT10S"><AdaptationSet contentType="video" mimeType="video/mp4"><Representation id="a" bandwidth="1" codecs="avc1.4d401e"><BaseURL>b.mp4</BaseURL></Representation></AdaptationSet></Period>
</MPD>
""";
        var ex = Assert.Throws<MediaResolverException>(() => DashParser.Parse(mpd, ManifestUrl));
        Assert.Equal(MediaErrorCode.UnsupportedManifest, ex.Error.Code);
    }

    [Fact]
    public async Task ContentProtection_IsDrm()
    {
        const string mpd = """
<MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT10S">
  <Period><AdaptationSet contentType="video" mimeType="video/mp4">
    <ContentProtection schemeIdUri="urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed"/>
    <Representation id="a" bandwidth="1" codecs="avc1.4d401e"><BaseURL>a.mp4</BaseURL></Representation>
  </AdaptationSet></Period>
</MPD>
""";
        Assert.True(DashParser.Parse(mpd, ManifestUrl).IsProtected);
        var ex = await Assert.ThrowsAsync<MediaResolverException>(() =>
            Manifests().ResolveTextAsync(mpd, ManifestUrl, MediaRequestContext.Empty, CancellationToken.None));
        Assert.Equal(MediaErrorCode.DrmProtected, ex.Error.Code);
    }

    [Fact]
    public async Task DynamicManifest_IsLive()
    {
        const string mpd = """
<MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="dynamic" availabilityStartTime="2024-01-01T00:00:00Z">
  <Period><AdaptationSet contentType="video" mimeType="video/mp4">
    <SegmentTemplate timescale="1" duration="2" media="s$Number$.m4s" startNumber="1"/>
    <Representation id="a" bandwidth="1" codecs="avc1.4d401e"/>
  </AdaptationSet></Period>
</MPD>
""";
        var ex = await Assert.ThrowsAsync<MediaResolverException>(() =>
            Manifests().ResolveTextAsync(mpd, ManifestUrl, MediaRequestContext.Empty, CancellationToken.None));
        Assert.Equal(MediaErrorCode.LiveStreamNotSupported, ex.Error.Code);
    }

    [Fact]
    public void DoctypeEntities_AreProhibited()
    {
        const string mpd = "<!DOCTYPE MPD [<!ENTITY x SYSTEM \"file:///C:/Windows/win.ini\">]><MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\">&x;</MPD>";
        var ex = Assert.Throws<MediaResolverException>(() => DashParser.Parse(mpd, ManifestUrl));
        Assert.Equal(MediaErrorCode.UnsupportedManifest, ex.Error.Code);
    }

    [Fact]
    public void NonHttpBaseUrl_IsRejected()
    {
        const string mpd = """
<MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT10S">
  <BaseURL>file:///C:/secret/</BaseURL>
  <Period><AdaptationSet contentType="video" mimeType="video/mp4"><Representation id="a" bandwidth="1" codecs="avc1.4d401e"><BaseURL>a.mp4</BaseURL></Representation></AdaptationSet></Period>
</MPD>
""";
        Assert.Throws<MediaResolverException>(() => DashParser.Parse(mpd, ManifestUrl));
    }

    [Fact]
    public void SegmentLimit_IsEnforcedForOpenEndedTimelines()
    {
        const string mpd = """
<MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT100000S">
  <Period><AdaptationSet contentType="video" mimeType="video/mp4">
    <SegmentTemplate timescale="1" media="s$Number$.m4s"><SegmentTimeline><S t="0" d="1" r="-1"/></SegmentTimeline></SegmentTemplate>
    <Representation id="a" bandwidth="1" codecs="avc1.4d401e"/>
  </AdaptationSet></Period>
</MPD>
""";
        var ex = Assert.Throws<MediaResolverException>(() => DashParser.Parse(mpd, ManifestUrl, maxSegments: 500));
        Assert.Equal(MediaErrorCode.ResponseTooLarge, ex.Error.Code);
    }

    [Theory]
    [InlineData("a$$b", "a$b")]
    [InlineData("$RepresentationID$/$Number$", "vid/7")]
    [InlineData("t$Time$-$Bandwidth$", "t900-500")]
    [InlineData("n$Number%03d$", "n007")]
    public void Substitute_ExpandsIdentifiers(string template, string expected) =>
        Assert.Equal(expected, DashTemplate.Substitute(template, "vid", 7, 500, 900));

    [Theory]
    [InlineData("$Unknown$")]
    [InlineData("broken$Number")]
    [InlineData("$Number%x$")]
    public void Substitute_RejectsMalformedTemplates(string template) =>
        Assert.Throws<MediaResolverException>(() => DashTemplate.Substitute(template, "v", 1, 1, 1));
}
