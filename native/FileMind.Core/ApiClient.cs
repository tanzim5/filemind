using System.Net.Http.Json;
using System.Text.Json;

namespace FileMind.Core;

/// <summary>The engine's own message when a request fails (FastAPI puts it in "detail").</summary>
public sealed class EngineException(string message, int status) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>Typed client for the engine's local HTTP API.</summary>
public sealed class ApiClient(Uri baseUri) : IDisposable
{
    private readonly HttpClient _http = new() { BaseAddress = baseUri, Timeout = TimeSpan.FromMinutes(2) };

    public Uri BaseUri => baseUri;
    public Uri ThumbUri(int id) => new(baseUri, $"/api/thumb/{id}");
    public Uri FileUri(int id) => new(baseUri, $"/api/file/{id}");

    public Task<ModelStatus> GetModelAsync(CancellationToken ct = default) => GetAsync<ModelStatus>("/api/model", ct);
    public Task<ModelStatus> StartModelDownloadAsync(CancellationToken ct = default) => PostAsync<ModelStatus>("/api/model/download", null, ct);
    public Task<List<RootFolder>> GetRootsAsync(CancellationToken ct = default) => GetAsync<List<RootFolder>>("/api/roots", ct);
    public Task<IndexState> GetStatusAsync(CancellationToken ct = default) => GetAsync<IndexState>("/api/status", ct);
    public Task<LibraryStats> GetStatsAsync(CancellationToken ct = default) => GetAsync<LibraryStats>("/api/stats", ct);

    public Task AddRootAsync(string path, CancellationToken ct = default) => PostAsync<JsonElement>("/api/roots", new { path }, ct);
    public async Task RemoveRootAsync(int id, CancellationToken ct = default)
    {
        using var response = await _http.DeleteAsync($"/api/roots/{id}", ct);
        if (!response.IsSuccessStatusCode) throw new EngineException(await ReadErrorAsync(response), (int)response.StatusCode);
    }
    public Task ReindexAsync(int id, CancellationToken ct = default) => PostAsync<JsonElement>($"/api/roots/{id}/index", null, ct);
    public Task StopIndexingAsync(CancellationToken ct = default) => PostAsync<JsonElement>("/api/index/stop", null, ct);
    public Task RevealAsync(int id, CancellationToken ct = default) => PostAsync<JsonElement>($"/api/reveal/{id}", null, ct);

    public Task<FilesPage> GetFilesAsync(FileQuery query, int limit, int offset, CancellationToken ct = default) =>
        GetAsync<FilesPage>("/api/files?" + query.ToQueryString(limit, offset), ct);

    /// <summary>Semantic search, kept inside the same filters the library view uses.</summary>
    public async Task<List<FileItem>> SearchAsync(IEnumerable<string> texts, FileQuery query, int limit, int offset, CancellationToken ct = default) =>
        (await PostAsync<SearchResult>("/api/search", query.ToSearchBody(texts, limit, offset), ct)).Files;

    public async Task<List<FileItem>> SimilarAsync(int id, FileQuery query, int limit = 100, CancellationToken ct = default)
    {
        var body = query.ToSearchBody([], limit, 0);
        body.Remove("qs");
        body["similar_to"] = id;
        return (await PostAsync<SearchResult>("/api/search", body, ct)).Files;
    }

    public async Task<List<int>> GetFileIdsAsync(FileQuery query, CancellationToken ct = default) =>
        (await GetAsync<IdsResponse>("/api/files?ids_only=true&" + query.ToQueryString(100000, 0), ct)).Ids;

    public async Task<List<NameGroup>> GetNameGroupsAsync(CancellationToken ct = default) =>
        (await GetAsync<NameGroupsResponse>("/api/name_groups", ct)).Groups;

    public Task<TranscriptState> GetTranscriptAsync(int id, CancellationToken ct = default) => GetAsync<TranscriptState>($"/api/transcript/{id}", ct);
    public Task<TranscriptState> StartTranscriptAsync(int id, CancellationToken ct = default) => PostAsync<TranscriptState>($"/api/transcript/{id}", null, ct);

    /// <summary>Counts for the Refine options: over a browsed scope (kind, root, source, ...) or the given result ids.</summary>
    public Task<FacetResult> GetFacetsAsync(Dictionary<string, object?> scope, IEnumerable<int>? ids, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>(scope);
        if (ids is not null) body["ids"] = ids.ToList();
        return PostAsync<FacetResult>("/api/facets", body, ct);
    }

    public Task<VoiceModelState> GetVoiceModelAsync(CancellationToken ct = default) => GetAsync<VoiceModelState>("/api/voice/model", ct);
    public Task<VoiceModelState> PrepareVoiceModelAsync(CancellationToken ct = default) => PostAsync<VoiceModelState>("/api/voice/model", null, ct);

    /// <summary>Speech to text on the engine (local Whisper): used when Windows speech recognition is unavailable.</summary>
    public async Task<string> VoiceToTextAsync(string audioPath, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(await File.ReadAllBytesAsync(audioPath, ct)), "file", Path.GetFileName(audioPath));
        using var response = await _http.PostAsync("/api/voice/text", form, ct);
        return (await ReadAsync<JsonElement>(response, ct)).GetProperty("text").GetString() ?? "";
    }

    /// <summary>Find files that look or sound like a picture on disk.</summary>
    public async Task<List<FileItem>> SearchByImageAsync(string path, FileQuery query, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        var bytes = new ByteArrayContent(await File.ReadAllBytesAsync(path, ct));
        form.Add(bytes, "file", Path.GetFileName(path));
        using var response = await _http.PostAsync("/api/search/image?limit=200" + (query.Root is { } r ? $"&root={r}" : ""), form, ct);
        return (await ReadAsync<SearchResult>(response, ct)).Files;
    }

    /// <summary>Sends a recording (any format ffmpeg reads); the engine embeds the sound itself as the search query.</summary>
    public async Task<VoiceResult> VoiceAsync(string path, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(await File.ReadAllBytesAsync(path, ct)), "file", Path.GetFileName(path));
        using var response = await _http.PostAsync("/api/voice", form, ct);
        return await ReadAsync<VoiceResult>(response, ct);
    }

    public Task<List<ColorSwatch>> GetColorsAsync(CancellationToken ct = default) => GetAsync<List<ColorSwatch>>("/api/colors", ct);
    public async Task<List<SourceEntry>> GetSourcesAsync(CancellationToken ct = default) => (await GetAsync<SourcesResponse>("/api/sources", ct)).Sources;
    public Task<List<MoodEntry>> GetMoodsAsync(CancellationToken ct = default) => GetAsync<List<MoodEntry>>("/api/moods", ct);
    public Task<List<ProjectEntry>> GetProjectsAsync(CancellationToken ct = default) => GetAsync<List<ProjectEntry>>("/api/projects", ct);
    public Task<FileDetails> GetInfoAsync(int id, CancellationToken ct = default) => GetAsync<FileDetails>($"/api/info/{id}", ct);
    public async Task<List<IdeaEntry>> GetIdeasAsync(int n = 36, CancellationToken ct = default) => (await GetAsync<IdeasResponse>($"/api/ideas?n={n}", ct)).Ideas;
    public Task<WaveData> GetWaveAsync(int id, CancellationToken ct = default) => GetAsync<WaveData>($"/api/wave/{id}", ct);
    public Task<MomentState> GetMomentsAsync(int id, CancellationToken ct = default) => GetAsync<MomentState>($"/api/moments/{id}", ct);
    public Task<MomentState> StartMomentsAsync(int id, CancellationToken ct = default) => PostAsync<MomentState>($"/api/moments/{id}", null, ct);
    public async Task SetProposedNameAsync(int id, string name, CancellationToken ct = default)
    {
        using var response = await _http.PutAsJsonAsync("/api/names", new { file_ids = new[] { id }, new_name = name }, ct);
        if (!response.IsSuccessStatusCode) throw new EngineException(await ReadErrorAsync(response), (int)response.StatusCode);
    }
    public Task<DiscoverResponse> GetDiscoverAsync(CancellationToken ct = default) => GetAsync<DiscoverResponse>("/api/discover", ct);
    public Task<DuplicatesResponse> GetDuplicatesAsync(CancellationToken ct = default) => GetAsync<DuplicatesResponse>("/api/duplicates", ct);

    public Task CreateProjectAsync(string name, IEnumerable<int> fileIds, CancellationToken ct = default) =>
        PostAsync<JsonElement>("/api/projects", new { name, file_ids = fileIds.ToArray() }, ct);
    public Task AddToProjectAsync(int project, IEnumerable<int> fileIds, CancellationToken ct = default) =>
        PostAsync<JsonElement>($"/api/projects/{project}/add", new { file_ids = fileIds.ToArray() }, ct);
    public Task RemoveFromProjectAsync(int project, IEnumerable<int> fileIds, CancellationToken ct = default) =>
        PostAsync<JsonElement>($"/api/projects/{project}/remove", new { file_ids = fileIds.ToArray() }, ct);
    public async Task DeleteProjectAsync(int project, CancellationToken ct = default)
    {
        using var response = await _http.DeleteAsync($"/api/projects/{project}", ct);
        if (!response.IsSuccessStatusCode) throw new EngineException(await ReadErrorAsync(response), (int)response.StatusCode);
    }
    public async Task RenameProjectAsync(int project, string name, CancellationToken ct = default)
    {
        using var response = await _http.PatchAsJsonAsync($"/api/projects/{project}", new { name }, ct);
        if (!response.IsSuccessStatusCode) throw new EngineException(await ReadErrorAsync(response), (int)response.StatusCode);
    }

    /// <summary>"copy", "move" or "delete" (to the Recycle Bin) on indexed originals. The engine never overwrites.</summary>
    public Task FileActionAsync(string action, IEnumerable<int> fileIds, string? destination = null, CancellationToken ct = default) =>
        PostAsync<JsonElement>("/api/file_actions", new { action, file_ids = fileIds.ToArray(), destination }, ct);

    private async Task<T> GetAsync<T>(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct);
        return await ReadAsync<T>(response, ct);
    }

    private async Task<T> PostAsync<T>(string url, object? body, CancellationToken ct)
    {
        using HttpContent content = body is null ? new StringContent("") : JsonContent.Create(body);
        using var response = await _http.PostAsync(url, content, ct);
        return await ReadAsync<T>(response, ct);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode) throw new EngineException(await ReadErrorAsync(response), (int)response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct))!;
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response)
    {
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String) return d.GetString()!;
        }
        catch { }
        return $"Request failed ({(int)response.StatusCode})";
    }

    public void Dispose() => _http.Dispose();
}
