using System.Text.Json;

namespace Siakun.AutoUpdate.Tests;

public sealed class CatalogTests
{
    [Fact]
    public async Task FiltersUninstallableReleasesAndSortsSemanticVersions()
    {
        using var http = new TestHttp();
        http.Responses[TestHttp.ReleasesUrl] = JsonSerializer.Serialize(new[]
        {
            TestHttp.ReleaseJson(TestHttp.Release("1.9.0")),
            TestHttp.ReleaseJson(TestHttp.Release("1.10.0")),
            TestHttp.ReleaseJson(TestHttp.Release("2.0.0"), draft: true),
            TestHttp.ReleaseJson(TestHttp.Release("3.0.0"), complete: false)
        });
        var releases = await http.Catalog().GetVersionsAsync();
        Assert.Equal(new[] { "v1.10.0", "v1.9.0" }, releases.Select(r => r.Tag));
    }

    [Fact]
    public async Task ReadsLaterPagesEvenWhenFirstPageHasNoInstallableReleases()
    {
        using var http = new TestHttp();
        http.Responses[TestHttp.ReleasesUrl] = JsonSerializer.Serialize(Enumerable.Range(0, 100)
            .Select(_ => TestHttp.ReleaseJson(TestHttp.Release("2.0.0"), draft: true)));
        http.Responses[TestHttp.ReleasesUrl.Replace("&page=1", "&page=2")] =
            JsonSerializer.Serialize(new[] { TestHttp.ReleaseJson(TestHttp.Release("1.0.0")) });
        Assert.Single(await http.Catalog().GetVersionsAsync());
        Assert.Equal(2, http.Requests.Count);
    }

    [Fact]
    public async Task RebuildsEmptyReleaseListFromTagsAndIgnoresTagsWithoutPackages()
    {
        using var http = new TestHttp();
        var beta = TestHttp.Release("2.0.0-beta");
        http.Responses[TestHttp.ReleasesUrl] = "[]";
        http.Responses[TestHttp.TagsUrl] = "[{\"name\":\"v2.0.0-beta\"},{\"name\":\"v1.0.0\"},{\"name\":\"docs\"}]";
        http.Feed(beta, TestHttp.Asset("2.0.0-beta"));
        var release = Assert.Single(await http.Catalog().GetVersionsAsync());
        Assert.Equal(beta.Tag, release.Tag);
        Assert.True(release.IsPrerelease);
    }

    [Fact]
    public async Task DoesNotHideHttpFailuresByReturningAnEmptyList()
    {
        using var http = new TestHttp();
        await Assert.ThrowsAsync<HttpRequestException>(() => http.Catalog().GetVersionsAsync());
        Assert.DoesNotContain(TestHttp.TagsUrl, http.Requests);
    }

    [Fact]
    public async Task RejectsAmbiguousPackagesInsteadOfChoosingAnArchitecture()
    {
        using var http = new TestHttp();
        http.Responses[TestHttp.ReleasesUrl] = """
            [{"tag_name":"v1.0.0","assets":[
              {"name":"Application-x64-full.nupkg"},
              {"name":"Application-arm64-full.nupkg"},
              {"name":"releases.win.json"}
            ]}]
            """;
        await Assert.ThrowsAsync<NotSupportedException>(() => http.Catalog().GetVersionsAsync());
    }

    [Theory]
    [InlineData("http://github.com/example/application")]
    [InlineData("https://example.com/example/application")]
    [InlineData("https://github.com/example/application/releases")]
    [InlineData("https://user:password@github.com/example/application")]
    [InlineData("https://github.com/example/application?token=secret")]
    public void RejectsAddressesOutsideRepositoryBoundary(string url) =>
        Assert.Throws<ArgumentException>(() => new GitHubReleaseCatalog(url));
}
