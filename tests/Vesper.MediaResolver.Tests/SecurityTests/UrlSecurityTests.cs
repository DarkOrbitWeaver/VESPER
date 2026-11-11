using System.Net;
using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Security;
using Xunit;

namespace Vesper.MediaResolver.Tests.SecurityTests;

public class UrlSecurityTests
{
    private static bool Allowed(string text) =>
        UrlValidator.TryParse(text, out var uri, out _) && UrlValidator.IsAllowed(uri, new ResolverOptions(), out _);

    [Theory]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("ftp://example.com/a.mp4")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,hello")]
    [InlineData("http://user:pass@example.com/")]
    [InlineData("http://localhost/a")]
    [InlineData("http://sub.localhost/a")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://2130706433/")]                  // decimal form of 127.0.0.1
    [InlineData("http://0x7f.0.0.1/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://[::ffff:127.0.0.1]/")]
    [InlineData("http://10.0.0.5/x")]
    [InlineData("http://192.168.1.1/")]
    [InlineData("http://172.20.0.1/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://printer.local/")]
    [InlineData("http://intranet/")]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("https://example.com/\r\nHost: evil")]
    public void DangerousOrMalformedUrls_AreRejected(string text) => Assert.False(Allowed(text));

    [Theory]
    [InlineData("https://example.com/a?b=1")]
    [InlineData("http://93.184.216.34/x.mp4")]
    [InlineData("https://sub.domain.example.org:8443/path")]
    public void OrdinaryPublicUrls_AreAccepted(string text) => Assert.True(Allowed(text));

    [Fact]
    public void PrivateNetworkGuard_CanBeDisabledOnlyExplicitly()
    {
        Assert.True(UrlValidator.TryParse("http://127.0.0.1:8080/x", out var u, out _));
        Assert.False(UrlValidator.IsAllowed(u, new ResolverOptions(), out _));
        Assert.True(UrlValidator.IsAllowed(u, new ResolverOptions { AllowPrivateNetworks = true }, out _));
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("1.1.1.1", true)]
    [InlineData("172.15.0.1", true)]
    [InlineData("172.32.0.1", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.31.255.255", false)]
    [InlineData("192.168.0.1", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("::1", false)]
    [InlineData("::", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("fd12:3456::1", false)]
    [InlineData("ff02::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("64:ff9b::7f00:1", false)]     // NAT64 embedding 127.0.0.1
    [InlineData("2002:7f00:1::", false)]       // 6to4 embedding 127.0.0.1
    public void IpClassifier(string ip, bool isPublic) => Assert.Equal(isPublic, IpAddressClassifier.IsPublic(IPAddress.Parse(ip)));

    [Fact]
    public void Redactor_RemovesQueryValuesAndShortensOpaqueSegments()
    {
        string s = UrlRedactor.Redact(new Uri("https://cdn.example.com/v/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/file.mp4?token=SECRET123&sig=abcdef"));
        Assert.DoesNotContain("SECRET123", s);
        Assert.DoesNotContain("abcdef", s);
        Assert.Contains("token=", s);
        Assert.DoesNotContain("aaaaaaaaaaaaaaaaaaaaaaaaaaaa", s);
        Assert.StartsWith("https://cdn.example.com/v/", s);
    }

    [Fact]
    public void SecretBearingModels_NeverPrintSecrets()
    {
        var ctx = new MediaRequestContext
        {
            Referer = "https://example.com/page?session=ABC",
            CookieHeader = "sid=TOPSECRET",
            SensitiveHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer TOPSECRET" },
            CredentialHosts = ["example.com"]
        };
        Assert.DoesNotContain("TOPSECRET", ctx.ToString());
        Assert.DoesNotContain("ABC", ctx.ToString());
        Assert.Contains("[redacted]", ctx.ToString());

        var auth = new BrowserAuthorization { Origin = "https://example.com", ConsentGranted = true, CookieHeader = "sid=TOPSECRET" };
        Assert.DoesNotContain("TOPSECRET", auth.ToString());

        var rep = new MediaRepresentation { Id = "x", Url = new Uri("https://cdn.example.com/a.mp4?sig=TOPSECRET"), Context = ctx };
        Assert.DoesNotContain("TOPSECRET", rep.ToString());
    }

    [Fact]
    public void CredentialScoping_ExactAndSubdomainOnly()
    {
        var ctx = new MediaRequestContext { CookieHeader = "a=b", CredentialHosts = ["example.com"] };
        Assert.True(ctx.MayForwardCredentialsTo("example.com"));
        Assert.True(ctx.MayForwardCredentialsTo("media.example.com"));
        Assert.False(ctx.MayForwardCredentialsTo("notexample.com"));
        Assert.False(ctx.MayForwardCredentialsTo("example.com.evil.net"));
        Assert.DoesNotContain(ctx.HeadersFor(new Uri("https://evil.net/x")), h => h.Key == "Cookie");
        Assert.Contains(ctx.HeadersFor(new Uri("https://media.example.com/x")), h => h.Key == "Cookie");
    }

    [Theory]
    [InlineData("../../Windows/system32/config", "/")]
    [InlineData("C:\\Windows\\win.ini", ":")]
    [InlineData("a<b>c:d|e?f*g\"h", "<")]
    public void FileNames_NeverContainSeparatorsOrInvalidCharacters(string input, string forbidden)
    {
        string s = FileNameSanitizer.Sanitize(input);
        Assert.DoesNotContain("/", s);
        Assert.DoesNotContain("\\", s);
        Assert.DoesNotContain(forbidden, s);
        Assert.NotEmpty(s);
    }

    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("LPT1", "_LPT1")]
    [InlineData("..", "media")]
    [InlineData("   ", "media")]
    [InlineData("name...", "name")]
    public void FileNames_ReservedAndDegenerateNames(string input, string expected) =>
        Assert.Equal(expected, FileNameSanitizer.Sanitize(input));

    [Fact]
    public void FileNames_StripBidiTricksControlCharsAndLimitLength()
    {
        Assert.DoesNotContain('\u202E', FileNameSanitizer.Sanitize("\u202Egpj.exe"));
        Assert.DoesNotContain('\n', FileNameSanitizer.Sanitize("a\nb\0c"));

        string longName = new('x', 500);
        Assert.True(FileNameSanitizer.Sanitize(longName).Length <= 120);

        string emoji = string.Concat(Enumerable.Repeat("😀", 200));
        string cut = FileNameSanitizer.Sanitize(emoji);
        Assert.True(cut.Length <= 120);
        Assert.False(char.IsHighSurrogate(cut[^1]));
    }

    [Fact]
    public void BuildFileName_SanitisesExtension()
    {
        Assert.Equal("My video.mp4", FileNameSanitizer.BuildFileName("My video", "id", "mp4"));
        Assert.Equal("clip.mp4", FileNameSanitizer.BuildFileName("clip", null, "m p4!"));
        Assert.Equal("id1", FileNameSanitizer.BuildFileName("", "id1", null));
    }
}
