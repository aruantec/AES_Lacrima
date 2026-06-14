using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using log4net;

using AES_Core.Logging;

namespace AES_Lacrima.Services;

internal static partial class GitHubAtomReleaseFeedReader
{
    private static readonly ILog Log = LogHelper.For(typeof(GitHubAtomReleaseFeedReader));

    internal sealed record AtomReleaseEntry(
        string Tag,
        string Title,
        DateTimeOffset? PublishedAt,
        string HtmlUrl);

    public static async Task<IReadOnlyList<AtomReleaseEntry>> FetchReleasesAsync(
        HttpClient client,
        string atomFeedUrl,
        int maxEntries,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await client.GetAsync(atomFeedUrl, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var xml = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseReleases(xml, maxEntries);
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to read GitHub atom feed '{atomFeedUrl}'.", ex);
            return Array.Empty<AtomReleaseEntry>();
        }
    }

    internal static IReadOnlyList<AtomReleaseEntry> ParseReleases(string xml, int maxEntries)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return Array.Empty<AtomReleaseEntry>();

        try
        {
            var document = XDocument.Parse(xml);
            XNamespace atom = "http://www.w3.org/2005/Atom";

            var results = new List<AtomReleaseEntry>();
            foreach (var entry in document.Descendants(atom + "entry"))
            {
                var id = entry.Element(atom + "id")?.Value?.Trim();
                var title = entry.Element(atom + "title")?.Value?.Trim();
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
                    continue;

                var tag = ExtractTagFromEntryId(id);
                if (string.IsNullOrWhiteSpace(tag))
                    continue;

                DateTimeOffset? publishedAt = null;
                var updated = entry.Element(atom + "updated")?.Value?.Trim();
                if (DateTimeOffset.TryParse(updated, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedUpdated))
                    publishedAt = parsedUpdated;

                var htmlUrl = entry.Elements(atom + "link")
                    .Select(link => link.Attribute("href")?.Value?.Trim())
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

                results.Add(new AtomReleaseEntry(tag, title, publishedAt, htmlUrl ?? string.Empty));
            }

            return results
                .OrderByDescending(static entry => entry.PublishedAt ?? DateTimeOffset.MinValue)
                .ThenByDescending(static entry => entry.Tag, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(1, maxEntries))
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warn("Failed to parse GitHub atom feed XML.", ex);
            return Array.Empty<AtomReleaseEntry>();
        }
    }

    internal static string? ExtractTagFromEntryId(string entryId)
    {
        if (string.IsNullOrWhiteSpace(entryId))
            return null;

        var match = EntryIdTagRegex().Match(entryId);
        return match.Success ? match.Groups["tag"].Value.Trim() : null;
    }

    [GeneratedRegex(@"/(?<tag>[^/]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex EntryIdTagRegex();
}
