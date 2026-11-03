using System.Text.RegularExpressions;

namespace Daventry.Core.Tests;

/// <summary>
/// The README the packages carry (NUGET.md): nuget.org renders it as Markdown alone, without the
/// HTML GitHub's README uses, shows images only from hosts it trusts, and has no repository for a
/// relative link to point into.
/// </summary>
public partial class NugetReadmeTests
{
    private const string Raw = "https://raw.githubusercontent.com/jeffnyman/daventry/main/";

    [Fact]
    public void The_packages_readme_is_markdown_that_nuget_shows()
    {
        string root = RepositoryRoot();

        // what is not code: the fenced blocks out, then the spans in backquotes, which may run across lines
        string prose = InlineCode().Replace(Fenced().Replace(File.ReadAllText(Path.Combine(root, "NUGET.md")), ""), "");

        Assert.DoesNotContain(prose.Split('\n'), line => HtmlTag().IsMatch(line));

        var images = Image().Matches(prose).Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(images);
        Assert.All(images, url => Assert.Matches("^https://(raw\\.githubusercontent\\.com|img\\.shields\\.io)/", url));

        // an image of the repository's is one it holds
        foreach (string url in images.Where(u => u.StartsWith(Raw, StringComparison.Ordinal)))
        {
            Assert.True(File.Exists(Path.Combine(root, url[Raw.Length..])), url);
        }

        Assert.All(Link().Matches(prose).Select(m => m.Groups[1].Value), url => Assert.StartsWith("https://", url));
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "NUGET.md")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("NUGET.md is not in any folder above the tests.");
    }

    [GeneratedRegex("^```.*?^```", RegexOptions.Singleline | RegexOptions.Multiline)]
    private static partial Regex Fenced();

    [GeneratedRegex("`[^`]*`")]
    private static partial Regex InlineCode();

    [GeneratedRegex("</?[A-Za-z][^>]*>")]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"!\[[^\]]*\]\(([^)\s]+)\)")]
    private static partial Regex Image();

    [GeneratedRegex(@"\]\(([^)\s]+)\)")]
    private static partial Regex Link();
}
