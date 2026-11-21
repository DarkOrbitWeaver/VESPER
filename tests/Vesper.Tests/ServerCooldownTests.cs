using System.Net;
using System.Net.Http.Headers;
using Vesper.Engine;

namespace Vesper.Tests;

public sealed class ServerCooldownTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Long_retry_after_is_a_floor_for_all_shared_transports(bool date)
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var wait = TimeSpan.FromHours(12);
        using var reply = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        reply.Headers.RetryAfter = date ? new RetryConditionHeaderValue(now + wait) : new RetryConditionHeaderValue(wait);
        Assert.Equal(wait, HttpFacts.RetryAfter(reply, now));
        Assert.Equal(wait, new RetryPolicy(new RetryOptions()).Delay(1, HttpFacts.RetryAfter(reply, now)));
    }

    [Fact]
    public void Extending_a_known_origin_does_not_evict_another_cooldown()
    {
        var cooldown = new OriginCooldown();
        var first = DateTimeOffset.UtcNow.AddHours(2);
        for (int i = 0; i < 256; i++) cooldown.Remember(new Uri($"https://h{i}.example/"), first.AddMinutes(i));
        cooldown.Remember(new Uri("https://h20.example/"), first.AddDays(2));
        Assert.Equal(first, cooldown.RetryAt(new Uri("https://h0.example/file")));
    }
}