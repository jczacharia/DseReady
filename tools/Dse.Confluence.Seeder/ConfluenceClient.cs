// Copyright (c) PNC Financial Services. All rights reserved.


using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Dse.Confluence.Seeder;

// REST v1 wrapper. All writes go through Confluence's API so Lucene/caches stay consistent (CQL reads Lucene).
public sealed class ConfluenceClient
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<string, HttpClient> _asUser = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _authorPassword;

    private readonly Uri _baseUri;

    public ConfluenceClient(SeederOptions.ConfluenceConnection conn)
    {
        _baseUri = new Uri(conn.BaseUrl.TrimEnd('/') + "/");
        _authorPassword = conn.AuthorPassword;
        Admin = MakeClient(conn.AdminUsername, conn.AdminPassword);
    }

    public HttpClient Admin { get; }

    private HttpClient MakeClient(string user, string password)
    {
        var client = new HttpClient { BaseAddress = _baseUri, Timeout = TimeSpan.FromMinutes(2) };
        string basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basic);
        client.DefaultRequestHeaders.Add("X-Atlassian-Token", "no-check");
        return client;
    }

    // Cached client authed as the author, or admin if creds fail.
    public async Task<HttpClient> ForAuthorAsync(Author? author)
    {
        if (author is null)
        {
            return Admin;
        }

        if (_asUser.TryGetValue(author.Username, out HttpClient? cached))
        {
            return cached;
        }

        HttpClient client = MakeClient(author.Username, _authorPassword);
        try
        {
            using HttpResponseMessage probe = await client.GetAsync("rest/api/user/current");
            client = probe.IsSuccessStatusCode ? client : Admin;
        }
        catch
        {
            client = Admin;
        }

        _asUser[author.Username] = client;
        return client;
    }

    // ---- Users ----

    public async Task<bool> UserExistsAsync(string username)
    {
        using HttpResponseMessage res = await Admin.GetAsync($"rest/api/user?username={Uri.EscapeDataString(username)}");
        return res.StatusCode == HttpStatusCode.OK;
    }

    public async Task<string?> ResolveUserKeyAsync(string username)
    {
        using HttpResponseMessage res = await Admin.GetAsync($"rest/api/user?username={Uri.EscapeDataString(username)}");
        if (!res.IsSuccessStatusCode)
        {
            return null;
        }

        using JsonDocument doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("userKey", out JsonElement k) ? k.GetString() : null;
    }

    // ---- Spaces ----

    public async Task<bool> SpaceExistsAsync(string key)
    {
        using HttpResponseMessage res = await Admin.GetAsync($"rest/api/space/{Uri.EscapeDataString(key)}");
        return res.IsSuccessStatusCode;
    }

    public async Task EnsureSpaceAsync(SeedSpace space)
    {
        if (await SpaceExistsAsync(space.Key))
        {
            return;
        }

        var payload = new
        {
            key = space.Key,
            name = space.Name,
            description = new { plain = new { value = space.Description, representation = "plain" } },
        };
        using HttpResponseMessage res = await Admin.PostAsJsonAsync("rest/api/space", payload, s_json);
        await EnsureSuccess(res, $"create space {space.Key}");
    }

    // ---- Content ----

    /// <summary>The id of an existing page with this title in the space, or null.</summary>
    public async Task<string?> FindContentIdAsync(string spaceKey, string title, string type = "page")
    {
        string url = $"rest/api/content?spaceKey={Uri.EscapeDataString(spaceKey)}" +
                     $"&title={Uri.EscapeDataString(title)}&type={type}&limit=1";
        using HttpResponseMessage res = await Admin.GetAsync(url);
        if (!res.IsSuccessStatusCode)
        {
            return null;
        }

        using JsonDocument doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        JsonElement results = doc.RootElement.GetProperty("results");
        return results.GetArrayLength() > 0 ? results[0].GetProperty("id").GetString() : null;
    }

    /// <summary>Creates a page/blogpost; returns its new id.</summary>
    public static async Task<string> CreateContentAsync(
        string spaceKey,
        SeedPage page,
        string? parentId,
        HttpClient asAuthor)
    {
        object payload = parentId is null
            ? new
            {
                type = page.Type,
                title = page.Title,
                space = new { key = spaceKey },
                body = new { storage = new { value = page.BodyStorage, representation = "storage" } },
            }
            : new
            {
                type = page.Type,
                title = page.Title,
                space = new { key = spaceKey },
                ancestors = new[] { new { id = parentId } },
                body = new { storage = new { value = page.BodyStorage, representation = "storage" } },
            };

        using HttpResponseMessage res = await asAuthor.PostAsJsonAsync("rest/api/content", payload, s_json);
        await EnsureSuccess(res, $"create {page.Type} '{page.Title}'");
        using JsonDocument doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetString()!;
    }

    /// <summary>Publishes a new version (bumps <c>version.number</c>) authored by <paramref name="asAuthor" />.</summary>
    public async Task UpdateContentAsync(string id, string title, string type, string body, HttpClient asAuthor)
    {
        int current = await GetVersionAsync(id);
        var payload = new
        {
            type,
            title,
            version = new { number = current + 1 },
            body = new { storage = new { value = body, representation = "storage" } },
        };
        using HttpResponseMessage res = await asAuthor.PutAsJsonAsync($"rest/api/content/{id}", payload, s_json);
        await EnsureSuccess(res, $"update '{title}' -> v{current + 1}");
    }

    private async Task<int> GetVersionAsync(string id)
    {
        using HttpResponseMessage res = await Admin.GetAsync($"rest/api/content/{id}?expand=version");
        res.EnsureSuccessStatusCode();
        using JsonDocument doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("version").GetProperty("number").GetInt32();
    }

    public async Task AddLabelsAsync(string id, IReadOnlyCollection<string> labels)
    {
        if (labels.Count == 0)
        {
            return;
        }

        object[] payload = labels.Select(l => (object)new { prefix = "global", name = l }).ToArray();
        using HttpResponseMessage res = await Admin.PostAsJsonAsync($"rest/api/content/{id}/label", payload, s_json);
        await EnsureSuccess(res, $"label {id}");
    }

    public async Task UploadAttachmentAsync(string id, SeedAttachment attachment)
    {
        using var form = new MultipartFormDataContent();
        var bytes = new ByteArrayContent(attachment.Content);
        bytes.Headers.ContentType = new MediaTypeHeaderValue(attachment.ContentType);
        form.Add(bytes, "file", attachment.FileName);
        form.Add(new StringContent(attachment.Comment), "comment");
        form.Add(new StringContent("true"), "minorEdit");

        // POST creates, or adds a new version if the filename already exists.
        using HttpResponseMessage res = await Admin.PostAsync($"rest/api/content/{id}/child/attachment", form);
        await EnsureSuccess(res, $"attach {attachment.FileName} -> {id}");
    }

    private static async Task EnsureSuccess(HttpResponseMessage res, string what)
    {
        if (res.IsSuccessStatusCode)
        {
            return;
        }

        string body = await res.Content.ReadAsStringAsync();
        if (body.Length > 400)
        {
            body = body[..400];
        }

        throw new InvalidOperationException($"Confluence {what} failed: {(int)res.StatusCode} {res.ReasonPhrase} — {body}");
    }
}
