using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Xunit;
using ForgeTekUpdatePackager.Services;

namespace ForgeTekUpdatePackager.Tests;

public class GitHubChangesTests
{
    private static (string Bucket, string Text) SuggestBucket(string line)
    {
        var m = typeof(GitHubService).GetMethod("SuggestBucket",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var r = (System.ValueTuple<string, string>)m.Invoke(null, [line])!;
        return (r.Item1, r.Item2);
    }

    private static List<CommitChange> ExtractChanges(string commitsJson)
    {
        using var doc = JsonDocument.Parse(commitsJson);
        var m = typeof(GitHubService).GetMethod("ExtractChanges",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (List<CommitChange>)m.Invoke(null, [doc.RootElement.Clone()])!;
    }

    [Theory]
    [InlineData("Added Chitubox file management", "Added", "Chitubox file management")]
    [InlineData("Fixed a bug where tags were not updated", "Fixed", "A bug where tags were not updated")]
    [InlineData("Improved scan performance", "Improved", "Scan performance")]
    [InlineData("Removed the legacy importer", "Removed", "The legacy importer")]
    [InlineData("Changed the default theme", "Changed", "The default theme")]
    [InlineData("feat: dark mode", "Added", "Dark mode")]
    [InlineData("fix: crash on startup", "Fixed", "Crash on startup")]
    [InlineData("refactor: some internals", "Changed", "Some internals")]
    public void SuggestBucket_DetectsLeadingWordsAndConventionalPrefixes(string line, string bucket, string text)
    {
        var (b, t) = SuggestBucket(line);
        Assert.Equal(bucket, b);
        Assert.Equal(text, t);
    }

    [Fact]
    public void SuggestBucket_UnknownLine_HasNoBucket()
    {
        var (b, t) = SuggestBucket("Bumped version to 0.1.38");
        Assert.Equal("", b);
        Assert.Equal("Bumped version to 0.1.38", t);
    }

    [Fact]
    public void ExtractChanges_ExpandsMultiLineCommitBody_NotJustTheFirstLine()
    {
        // A single commit whose body is a bulleted changelog — every bullet must surface, not only the subject.
        const string json = """
        [
          {
            "commit": {
              "message": "Build 0.1.38\n- Added Chitubox file management\n- Added Lychee .lys file management\n- Fixed a bug where tags were not updated\n- Fixed a bug where files would not move to a NAS\n\nSigned-off-by: Dev <dev@example.com>"
            }
          }
        ]
        """;

        var changes = ExtractChanges(json);

        // 4 bullets + the subject line = 5 entries; the trailer is dropped.
        Assert.Equal(5, changes.Count);
        Assert.Equal(2, changes.Count(c => c.Suggested == "Added"));
        Assert.Equal(2, changes.Count(c => c.Suggested == "Fixed"));
        Assert.Contains(changes, c => c.Text == "Chitubox file management");
        Assert.Contains(changes, c => c.Text.Contains("tags were not updated"));
        Assert.DoesNotContain(changes, c => c.Text.Contains("Signed-off-by"));
    }

    [Fact]
    public void ExtractChanges_SkipsMergeCommits()
    {
        const string json = """
        [ { "commit": { "message": "Merge branch 'main' into feature" } } ]
        """;
        Assert.Empty(ExtractChanges(json));
    }
}
