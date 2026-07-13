using ClickUpTodo.ClickUp;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for <see cref="MentionDetector"/> and <see cref="MentionSpec"/> (#113): @-prefixed
/// handle matching over a comment's flattened text, with the boundary/casing/email-local-part rules
/// the feed relies on to flag which entries mention the current user.
/// </summary>
public sealed class MentionDetectorTests
{
    private static MentionSpec Spec(string displayName) => MentionSpec.ForUser(new ClickUpUser(7, displayName));

    [Fact]
    public void Mentions_AtPrefixedFullDisplayName_IsMention()
    {
        Assert.True(MentionDetector.Mentions("hey @Ben Seymour can you check this?", Spec("Ben Seymour")));
    }

    [Fact]
    public void Mentions_NameInProseWithoutAt_IsNotMention()
    {
        // Requiring the leading @ avoids false positives from a name appearing in prose.
        Assert.False(MentionDetector.Mentions("Ben Seymour looks good to me", Spec("Ben Seymour")));
    }

    [Fact]
    public void Mentions_HandleIsPrefixOfLongerWord_IsNotMention()
    {
        // "@Benny" must not match the handle "ben" — the char after the handle is a word char.
        Assert.False(MentionDetector.Mentions("thanks @Benny", Spec("Ben")));
    }

    [Fact]
    public void Mentions_IsCaseInsensitive()
    {
        Assert.True(MentionDetector.Mentions("cc @ben seymour", Spec("Ben Seymour")));
        Assert.True(MentionDetector.Mentions("cc @BEN SEYMOUR", Spec("ben seymour")));
    }

    [Fact]
    public void Mentions_MatchesAtEndOfText()
    {
        Assert.True(MentionDetector.Mentions("assigning to @Ben", Spec("Ben")));
    }

    [Fact]
    public void Mentions_MatchFollowedByPunctuation_IsMention()
    {
        Assert.True(MentionDetector.Mentions("@Ben, please review", Spec("Ben")));
    }

    [Fact]
    public void Mentions_EmailDisplayName_MatchesLocalPartAndFullEmail()
    {
        var spec = Spec("ben@odb.org");
        Assert.True(MentionDetector.Mentions("ping @ben please", spec));       // local part
        Assert.True(MentionDetector.Mentions("ping @ben@odb.org please", spec)); // full email handle
    }

    [Fact]
    public void Mentions_HandleEmbeddedInEmailAddress_IsNotMention()
    {
        // The "@ben" inside an email address must NOT match the auto-derived local-part handle "ben":
        // the char before the "@" is a word char, so it isn't a standalone @-mention.
        var spec = Spec("ben@odb.org");
        Assert.False(MentionDetector.Mentions("email alice@ben.dev for access", spec));
        Assert.False(MentionDetector.Mentions("cc bob@bencorp.com", spec));
    }

    [Fact]
    public void Mentions_DifferentHandle_IsNotMention()
    {
        Assert.False(MentionDetector.Mentions("@Alice Jones take a look", Spec("Ben Seymour")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Mentions_NullOrEmptyText_IsNotMention(string? text)
    {
        Assert.False(MentionDetector.Mentions(text, Spec("Ben")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ForUser_BlankDisplayName_YieldsSpecThatMatchesNothing(string displayName)
    {
        var spec = Spec(displayName);
        Assert.Empty(spec.Handles);
        Assert.False(MentionDetector.Mentions("@anyone @Ben", spec));
    }

    [Fact]
    public void ForUser_DisambiguatesEmailLocalPartFromDisplayName_NoDuplicateHandles()
    {
        // "ben@odb.org" → handles { "ben@odb.org", "ben" }; a plain name adds a single handle.
        Assert.Equal(new[] { "ben@odb.org", "ben" }, MentionSpec.ForUser(new ClickUpUser(1, "ben@odb.org")).Handles);
        Assert.Equal(new[] { "ben seymour" }, MentionSpec.ForUser(new ClickUpUser(1, "Ben Seymour")).Handles);
    }

    [Fact]
    public void Mentions_CommentItemOverload_UsesText()
    {
        var comment = new CommentItem("c1", "Alice", 100, "cc @Ben here", false, "task1");
        Assert.True(MentionDetector.Mentions(comment, Spec("Ben")));
    }

    // ── Structured user-id matching (#167) ─────────────────────────────────────

    private static CommentItem CommentWithIds(string text, params long[] ids)
        => new("c1", "author", 100, text, false, "task1", MentionedUserIds: ids);

    [Fact]
    public void Mentions_ByUserId_WhenTextHasNoHandleMatch_IsMention()
    {
        // The rendered text carries no "@Ben" we'd match, but a structured block references Ben's id (7).
        Assert.True(MentionDetector.Mentions(CommentWithIds("thanks, will take a look", 42, 7), Spec("Ben")));
    }

    [Fact]
    public void Mentions_ByUserId_DifferentIdsOnly_IsNotMention()
    {
        Assert.False(MentionDetector.Mentions(CommentWithIds("thanks, will take a look", 42, 99), Spec("Ben")));
    }

    [Fact]
    public void Mentions_TextHandleStillMatches_WhenNoMentionedIds()
    {
        // With the id signal empty, the @handle text path (#113) still flags the mention.
        Assert.True(MentionDetector.Mentions(CommentWithIds("cc @Ben here"), Spec("Ben")));
    }

    [Fact]
    public void Mentions_NoneSpec_NeverMatchesById()
    {
        // MentionSpec.None has no handles and no UserId, so even a matching-looking id can't flag it.
        Assert.False(MentionDetector.Mentions(CommentWithIds("plain", 7), MentionSpec.None));
    }

    [Fact]
    public void ForUser_CarriesPositiveUserId_AndDropsNonPositiveId()
    {
        Assert.Equal(7, MentionSpec.ForUser(new ClickUpUser(7, "Ben")).UserId);
        Assert.Null(MentionSpec.ForUser(new ClickUpUser(0, "Ben")).UserId);
    }

    [Fact]
    public void ForUser_BlankName_ButValidId_MatchesByIdAlone()
    {
        // The precise case #167 targets: no usable @handle, but the mentioned numeric id still matches.
        var spec = MentionSpec.ForUser(new ClickUpUser(7, ""));
        Assert.Empty(spec.Handles);
        Assert.Equal(7, spec.UserId);
        Assert.True(MentionDetector.Mentions(CommentWithIds("no handle rendered here", 7), spec));
    }
}
