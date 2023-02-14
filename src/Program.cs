﻿using System.Collections.Concurrent;
using System.Text.Json;
using GetMoarFediverse;
using TurnerSoftware.RobotsExclusionTools;

var configPath = Environment.GetEnvironmentVariable("CONFIG_PATH");
if (args.Length == 1){
    configPath = args[0];
}

if (configPath.IsNullOrEmpty())
{
    throw new Exception("Missing config path");
}

Config.Init(configPath);

if (Config.Instance == null)
{
    throw new Exception("Error initializing config object");
}

var client = new HttpClient();
client.DefaultRequestHeaders.Add("User-Agent", "GetMoarFediverse");

var authClient = new HttpClient
{
    BaseAddress = new Uri(Config.Instance.FakeRelayUrl)
};
authClient.DefaultRequestHeaders.Add("Authorization", "Bearer " + Config.Instance.FakeRelayApiKey);

var importedPath = Config.Instance.ImportedPath;
if (!File.Exists(importedPath))
{
    File.WriteAllText(importedPath, "");
}

var robotsFileParser = new RobotsFileParser();
var sitesRobotFile = new ConcurrentDictionary<string, RobotsFile>();
await Parallel.ForEachAsync(Config.Instance.Sites,
    new ParallelOptions { MaxDegreeOfParallelism = Config.Instance.Sites.Length },
    async (site, _) =>
    {
        try
        {
            sitesRobotFile[site.Host] = await robotsFileParser.FromUriAsync(new Uri($"http://{site.Host}/robots.txt"));
        }
        catch
        {
            Console.WriteLine($"Ignoring {site.Host} because had issues fetching its robots data (is the site down?)");
        }
    }
);

List<(string host, string tag)> sitesTags;
int numberOfTags;
if (Config.Instance.MastodonPostgresConnectionString.HasValue())
{
    var tags = await MastodonConnectionHelper.GetFollowedTagsAsync();
    if (Config.Instance.PinnedTags)
    {
        tags = tags.Concat(await MastodonConnectionHelper.GetPinnedTagsAsync()).Distinct().ToList();
    }
    numberOfTags = tags.Count;
    sitesTags = Config.Instance.Sites
        .SelectMany(s => tags.Select(t => (s.Host, t)))
        .OrderBy(e => e.t)
        .ToList();
}
else
{
    numberOfTags = Config.Instance.Tags.Length;
    sitesTags = Config.Instance.Sites
        .SelectMany(s => Config.Instance.Tags.Select(tag => (s.Host, tag)))
        .Concat(Config.Instance.Sites.SelectMany(s => s.SiteSpecificTags.Select(tag => (s.Host, tag))))
        .OrderBy(t => t.tag)
        .ToList();
}

var importedList = File.ReadAllLines(importedPath).ToList();
var imported = importedList.ToHashSet();
var statusesToLoadBag = new ConcurrentBag<string>();
await Parallel.ForEachAsync(sitesTags, new ParallelOptions{MaxDegreeOfParallelism = numberOfTags * 2}, async (st, _) =>
{
    var (site, tag) = st;
    Console.WriteLine($"Fetching tag #{tag} from {site}");

    var url = $"https://{site}/api/v1/timelines/tag/{tag}?limit=40";
    if (sitesRobotFile.TryGetValue(site, out var robotsFile))
    {
        var allowed = robotsFile.IsAllowedAccess(new Uri(url), "GetMoarFediverse");
        if (!allowed)
        {
            Console.WriteLine($"Scraping {url} is not allowed based on their robots.txt file");
            return;
        }
    }
    else
    {
        Console.WriteLine($"Not scraping {url} because I couldn't fetch robots data.");
        return;
    }
    
    HttpResponseMessage? response = null;
    try
    {
        response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
    }
    catch (Exception e)
    {
        Console.WriteLine($"Error fetching tag {tag} from {site}, status code: {response?.StatusCode}. Error: {e.Message}");
        return;
    }

    var json = await response.Content.ReadAsStringAsync();

    var data = JsonSerializer.Deserialize(json, CamelCaseJsonContext.Default.StatusArray);
    if (data == null)
    {
        Console.WriteLine($"Error deserializing the response when pulling #{tag} posts from {site}");
        return;
    }

    var count = 0;
    foreach (var statusLink in data.Where(i => !imported.Contains(i.Uri)))
    {
        statusesToLoadBag.Add(statusLink.Uri);
        count++;
    }

    Console.WriteLine($"Retrieved {count} new statuses from {site} with hashtag #{tag}");
});

var statusesToLoad = statusesToLoadBag.ToHashSet();
Console.WriteLine($"Originally retrieved {statusesToLoadBag.Count} statuses. After removing duplicates, I got {statusesToLoad.Count} really unique ones");
foreach (var statusLink in statusesToLoad)
{
    Console.WriteLine($"Bringing in {statusLink}");
    try
    {
        var content = new List<KeyValuePair<string, string>>
        {
            new("statusUrl", statusLink)
        };

        var res = await authClient.PostAsync("index", new FormUrlEncodedContent(content));
        res.EnsureSuccessStatusCode();
        importedList.Add(statusLink);
    }
    catch (Exception e)
    {
        Console.WriteLine($"{e.Message}");
    }
}

var maxFileLines = sitesTags.Count * 40;
if (importedList.Count > maxFileLines)
{
    Console.WriteLine($"Keeping the last {maxFileLines} on the status file");
    importedList = importedList
        .Skip(importedList.Count - maxFileLines)
        .ToList();
}

File.WriteAllLines(importedPath, importedList);

public class Status
{
    public string Uri { get; }

    public Status(string uri)
    {
        Uri = uri;
    }
}
