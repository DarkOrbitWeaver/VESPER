using System.Runtime.ExceptionServices;
using Vesper.MediaResolver.Models;
using VESPER;

namespace Vesper.Windows.Tests;

[Collection("Main window UI")]
public sealed class PodcastFollowFormTests
{
    [Fact]
    public void EpisodeRowsStartUncheckedAndPreviouslyAddedRowsCannotBeSelected()
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var episodes = new CheckedListBox();
                const string feedUrl = "https://feeds.example/show.xml";
                var fresh = new CollectionEntry
                {
                    MediaId = "episode-new",
                    Url = new Uri("https://cdn.example/new.mp3"),
                    Title = "New episode"
                };
                var queued = new CollectionEntry
                {
                    MediaId = "episode-queued",
                    Url = new Uri("https://cdn.example/queued.mp3"),
                    Title = "Previously added episode"
                };
                string queuedIdentity = PodcastFeedStore.EpisodeIdentity(feedUrl, queued);

                int newCount = PodcastFollowForm.PopulateEpisodeList(episodes, feedUrl,
                    [fresh, queued], identity => identity == queuedIdentity);

                Assert.Equal(1, newCount);
                Assert.Equal(2, episodes.Items.Count);
                Assert.False(episodes.GetItemChecked(0));
                Assert.False(episodes.GetItemChecked(1));
                Assert.True(PodcastFollowForm.CanSelectEpisode(episodes.Items[0]));
                Assert.False(PodcastFollowForm.CanSelectEpisode(episodes.Items[1]));
                Assert.Contains("Already added", episodes.Items[1]!.ToString() ?? "");
            }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        failure?.Throw();
    }
}
