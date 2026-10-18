using Scrimshaw.Core.Detection;

namespace Scrimshaw.Core.Tests;

public class TitleWatcherTests
{
    [Theory]
    [InlineData("(86) Cinema Paradiso - YouTube - Google Chrome", "Cinema Paradiso - YouTube - Google Chrome")]
    [InlineData("  (3)   Etude  ", "Etude")]
    [InlineData("Etude (1987)", "Etude (1987)")]  // a number elsewhere is part of the title
    [InlineData("(live) Concert", "(live) Concert")]
    [InlineData(null, "")]
    public void Normalisation_drops_only_a_leading_notification_count(string? title, string expected) =>
        Assert.Equal(expected, TitleWatcher.Normalize(title));

    [Fact]
    public void Notification_count_changes_are_not_a_new_video()
    {
        var w = new TitleWatcher("(86) Etude - YouTube");
        Assert.False(w.Observe("(87) Etude - YouTube"));
        Assert.False(w.Observe("Etude - YouTube"));
        Assert.False(w.Observe("(1) Etude - YouTube"));
    }

    [Fact]
    public void A_new_title_must_be_seen_twice_in_a_row()
    {
        var w = new TitleWatcher("Etude - YouTube");
        Assert.False(w.Observe("Next - YouTube"));
        Assert.True(w.Observe("(2) Next - YouTube"));
        Assert.Equal("(2) Next - YouTube", w.ChangedTo);
        Assert.True(w.Observe("Etude - YouTube")); // stays stopped once confirmed
    }

    [Fact]
    public void A_one_check_flicker_is_ignored()
    {
        var w = new TitleWatcher("Etude");
        Assert.False(w.Observe("Loading…"));
        Assert.False(w.Observe("Etude"));
        Assert.False(w.Observe("Something else"));
        Assert.False(w.Observe("Etude"));
        Assert.Null(w.ChangedTo);
    }

    [Fact]
    public void Two_different_new_titles_in_a_row_need_their_own_confirmation()
    {
        var w = new TitleWatcher("Etude");
        Assert.False(w.Observe("A"));
        Assert.False(w.Observe("B"));
        Assert.True(w.Observe("B"));
    }

    [Fact]
    public void Missing_titles_are_not_evidence_of_a_change()
    {
        var w = new TitleWatcher("Etude");
        Assert.False(w.Observe("Next"));
        Assert.False(w.Observe(null));
        Assert.False(w.Observe(""));
        Assert.False(w.Observe("Next")); // the run was broken by the missing title
        Assert.True(w.Observe("Next"));
    }

    [Fact]
    public void Confirmations_can_be_set()
    {
        var w = new TitleWatcher("Etude", confirmations: 1);
        Assert.True(w.Observe("Next"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TitleWatcher("x", 0));
    }
}
