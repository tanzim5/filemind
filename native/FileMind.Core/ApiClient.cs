using System.Net.Http.Json;
using System.Text.Json;

namespace FileMind.Core;

/// <summary>The engine's own message when a request fails (FastAPI puts it in "detail").</summary>
public sealed class EngineException(string message, int status) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>Typed client for the engine's local HTTP API.</summary>
public sealed class ApiClient(Uri baseUri, string token = "") : IDisposable
{
    // The token proves to the engine that a request comes from this app and not from a web page.
    private readonly HttpClient _http = Make(baseUri, token);

    private static HttpClient Make(Uri baseUri, string token)
    {
        var http = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromMinutes(2) };
        if (token.Length > 0) http.DefaultRequestHeaders.Add("X-FileMind-Token", token);
        return http;
    }

    public Uri BaseUri => baseUri;
    /// <summary>Where live dictation connects (a WebSocket; the token proves it is this app).</summary>
    public Uri VoiceStreamUri => new UriBuilder(baseUri) { Scheme = "ws", Path = "/api/voice/stream", Query = "fm_token=" + Uri.EscapeDataString(token) }.Uri;
    public Task<VoiceModelState> GetVoiceModelAsync(CancellationToken ct = default) => GetAsync<VoiceModelState>("/api/voice/model", ct);
    public Task<VoiceModelState> PrepareVoiceModelAsync(CancellationToken ct = default) => PostAsync<VoiceModelState>("/api/voice/model", null, ct);
    // Pictures and media are fetched by Windows itself (no custom headers), so their addresses carry the token.
    private string TokenQuery(char first) => token.Length > 0 ? $"{first}fm_token={Uri.EscapeDataString(token)}" : "";
    public Uri ThumbUri(int id) => new(baseUri, $"/api/thumb/{id}{TokenQuery('?')}");
    public Uri FileUri(int id) => new(baseUri, $"/api/file/{id}{TokenQuery('?')}");

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
        (await SearchWithChipsAsync(texts, query, limit, offset, ct)).Files;

    /// <summary>The same, and what the engine understood in the words (dates, colours, sizes...).</summary>
    public Task<SearchResult> SearchWithChipsAsync(IEnumerable<string> texts, FileQuery query, int limit, int offset, CancellationToken ct = default) =>
        PostAsync<SearchResult>("/api/search", query.ToSearchBody(texts, limit, offset), ct);

    /// <summary>Reads a typed or spoken question into filters and the words left to search for.</summary>
    public Task<Understood> UnderstandAsync(string text, CancellationToken ct = default) =>
        PostAsync<Understood>("/api/understand", FileQuery.AskContext(text), ct);

    /// <summary>Files like this one. With <paramref name="words"/> the engine mixes the file's vector with the words
    /// ("like this, but at night"); <paramref name="textWeight"/> (0 to 1, engine default 0.5) says how much the words count.</summary>
    public async Task<List<FileItem>> SimilarAsync(int id, FileQuery query, int limit = 100, string? words = null, double? textWeight = null, CancellationToken ct = default)
    {
        var body = query.ToSearchBody([], limit, 0);
        body.Remove("qs");
        body["similar_to"] = id;
        if (!string.IsNullOrWhiteSpace(words)) { body["text"] = words; if (textWeight is { } w) body["text_weight"] = w; }
        return (await PostAsync<SearchResult>("/api/search", body, ct)).Files;
    }

    public async Task<List<int>> GetFileIdsAsync(FileQuery query, CancellationToken ct = default) =>
        (await GetAsync<IdsResponse>("/api/files?ids_only=true&" + query.ToQueryString(100000, 0), ct)).Ids;

    public async Task<List<NameGroup>> GetNameGroupsAsync(CancellationToken ct = default) =>
        (await GetAsync<NameGroupsResponse>("/api/name_groups", ct)).Groups;

    public Task<TranscriptState> GetTranscriptAsync(int id, CancellationToken ct = default) => GetAsync<TranscriptState>($"/api/transcript/{id}", ct);
    public Task<TranscriptState> StartTranscriptAsync(int id, CancellationToken ct = default) => PostAsync<TranscriptState>($"/api/transcript/{id}", null, ct);

    public async Task DeleteTagAsync(string name, CancellationToken ct = default)
    {
        using var response = await _http.DeleteAsync($"/api/tags/{Uri.EscapeDataString(name)}", ct);
        response.EnsureSuccessStatusCode();
    }
    /// <summary>Makes the preview video of a sequence (the first time only) so it can be played.</summary>
    public Task PrepareSequenceAsync(int id, CancellationToken ct = default) => PostAsync<JsonElement>($"/api/sequence/{id}/proxy", null, ct);
    public Task SetSequenceFpsAsync(int id, double fps, CancellationToken ct = default) => PostAsync<JsonElement>($"/api/sequence/{id}/fps", new { fps }, ct);
    public Task<LabelList> GetLabelsAsync(CancellationToken ct = default) => GetAsync<LabelList>("/api/labels", ct);
    /// <summary>Gives files a colour label, or clears it with null.</summary>
    public Task<LabelList> SetLabelAsync(IEnumerable<int> ids, string? label, CancellationToken ct = default) =>
        PostAsync<LabelList>("/api/labels", new { ids = ids.ToArray(), label }, ct);
    /// <summary>Hides pictures and clips below this size (0 = show all). Nothing is deleted.</summary>
    public async Task SetMinSideAsync(int pixels, string mode)
    {
        try { using var r = await _http.PostAsJsonAsync("/api/prefs", new { min_side = pixels, min_side_mode = mode }); } catch (Exception) { }
    }
    public Task<TagList> GetTagsAsync(CancellationToken ct = default) => GetAsync<TagList>("/api/tags", ct);
    public Task<TagList> TagFilesAsync(IEnumerable<int> ids, IEnumerable<string>? add, IEnumerable<string>? remove, CancellationToken ct = default) =>
        PostAsync<TagList>("/api/tags", new { ids = ids.ToList(), add = add?.ToList() ?? [], remove = remove?.ToList() ?? [] }, ct);
    public Task<TagList> DefineTagAsync(string name, string? color, string? rename = null, CancellationToken ct = default) =>
        PostAsync<TagList>("/api/tags/define", new { name, color, rename }, ct);

    /// <summary>Counts for the Refine options: over a browsed scope (kind, root, source, ...) or the given result ids.</summary>
    public Task<FacetResult> GetFacetsAsync(Dictionary<string, object?> scope, IEnumerable<int>? ids, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>(scope);
        if (ids is not null) body["ids"] = ids.ToList();
        return PostAsync<FacetResult>("/api/facets", body, ct);
    }


    /// <summary>Tells the engine how hard to work while indexing: quiet, balanced or fast.</summary>
    public async Task SetIndexSpeedAsync(string level)
    {
        try { using var r = await _http.PostAsJsonAsync("/api/index/speed", new { level }); } catch (Exception) { }
    }

    /// <summary>Find files that look or sound like a picture on disk.</summary>
    public async Task<List<FileItem>> SearchByImageAsync(string path, FileQuery query, string? words = null, double? textWeight = null, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        var bytes = new ByteArrayContent(await File.ReadAllBytesAsync(path, ct));
        form.Add(bytes, "file", Path.GetFileName(path));
        using var response = await _http.PostAsync("/api/search/image?limit=200" + (query.Root is { } r ? $"&root={r}" : "")
            + (string.IsNullOrWhiteSpace(words) ? "" : "&text=" + Uri.EscapeDataString(words) + (textWeight is { } w ? "&text_weight=" + w.ToString(System.Globalization.CultureInfo.InvariantCulture) : "")), form, ct);
        return (await ReadAsync<SearchResult>(response, ct)).Files;
    }


    public Task<List<ColorSwatch>> GetColorsAsync(CancellationToken ct = default) => GetAsync<List<ColorSwatch>>("/api/colors", ct);
    public async Task<List<SourceEntry>> GetSourcesAsync(CancellationToken ct = default) => (await GetAsync<SourcesResponse>("/api/sources", ct)).Sources;
    /// <summary>Source counts over what is on screen only, so the list follows the view.</summary>
    public async Task<List<SourceEntry>> GetSourcesAsync(Dictionary<string, object?> scope, CancellationToken ct = default) =>
        (await PostAsync<SourcesResponse>("/api/sources", scope, ct)).Sources;
    public Task<List<MoodEntry>> GetMoodsAsync(CancellationToken ct = default) => GetAsync<List<MoodEntry>>("/api/moods", ct);
    public Task<List<ProjectEntry>> GetProjectsAsync(CancellationToken ct = default) => GetAsync<List<ProjectEntry>>("/api/projects", ct);
    public Task<FileDetails> GetInfoAsync(int id, CancellationToken ct = default) => GetAsync<FileDetails>($"/api/info/{id}", ct);
    public async Task<List<IdeaEntry>> GetIdeasAsync(int n = 36, CancellationToken ct = default) => (await GetAsync<IdeasResponse>($"/api/ideas?n={n}", ct)).Ideas;
    public Task<WaveData> GetWaveAsync(int id, CancellationToken ct = default) => GetAsync<WaveData>($"/api/wave/{id}", ct);
    public async Task<List<(int Id, string Path)>> GetOcrPendingAsync(string mode, int limit, CancellationToken ct = default) =>
        (await GetAsync<OcrPending>($"/api/ocr/pending?limit={limit}&mode={Uri.EscapeDataString(mode)}", ct)).Files.Select(f => (f.Id, f.Path)).ToList();
    public Task StoreOcrAsync(int id, string text, CancellationToken ct = default) => PostAsync<JsonElement>("/api/ocr", new { id, text }, ct);
    /// <summary>Moments inside videos and sounds that match these words (only those already analysed).</summary>
    public async Task<List<MomentHit>> SearchMomentsAsync(string text, int limit = 12, CancellationToken ct = default) =>
        (await PostAsync<MomentHits>("/api/moments/search", new { q = text, limit }, ct)).Moments;
    public Task<MomentQueue> QueueMomentsAsync(bool all, IEnumerable<int>? ids = null, CancellationToken ct = default) =>
        PostAsync<MomentQueue>("/api/moments/queue", new { all, ids = ids?.ToArray() ?? [] }, ct);
    public Task<MomentQueue> GetMomentQueueAsync(CancellationToken ct = default) => GetAsync<MomentQueue>("/api/moments/queue", ct);
    public Uri MomentFrameUri(int id, double seconds) => new(baseUri, $"/api/moments/{id}/frame?t={seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}{TokenQuery('&')}");
    public Task<MomentState> GetMomentsAsync(int id, CancellationToken ct = default) => GetAsync<MomentState>($"/api/moments/{id}", ct);
    public Task<MomentState> StartMomentsAsync(int id, CancellationToken ct = default) => PostAsync<MomentState>($"/api/moments/{id}", null, ct);
    public async Task SetProposedNameAsync(int id, string name, CancellationToken ct = default)
    {
        using var response = await _http.PutAsJsonAsync("/api/names", new { file_ids = new[] { id }, new_name = name }, ct);
        if (!response.IsSuccessStatusCode) throw new EngineException(await ReadErrorAsync(response), (int)response.StatusCode);
    }
    public Task<DiscoverResponse> GetDiscoverAsync(CancellationToken ct = default) => GetAsync<DiscoverResponse>("/api/discover", ct);
    /// <summary>The last duplicate scan (instant). <paramref name="rescan"/> compares the library again.</summary>
    public Task<DuplicatesResponse> GetDuplicatesAsync(bool rescan = false, bool cachedOnly = false, CancellationToken ct = default) =>
        GetAsync<DuplicatesResponse>(rescan ? "/api/duplicates?rescan=true" : cachedOnly ? "/api/duplicates?cached_only=true" : "/api/duplicates", ct);

    public Task CreateProjectAsync(string name, IEnumerable<int> fileIds, CancellationToken ct = default) =>
        PostAsync<JsonElement>("/api/projects", new { name, file_ids = fileIds.ToArray() }, ct);
    /// <summary>The engine's proposals for new spaces ("Auto-group"). Nothing is created until the caller keeps one.</summary>
    public Task<ClusterResponse> ProposeSpacesAsync(bool unassignedOnly = true, CancellationToken ct = default) =>
        PostAsync<ClusterResponse>("/api/cluster", new { unassigned = unassignedOnly }, ct);
    /// <summary>Which files fit these short descriptions ("cats", "dogs at the beach"). Read only.</summary>
    public Task<SpaceMatchResult> MatchSpacesAsync(IEnumerable<string> descriptions, CancellationToken ct = default) =>
        PostAsync<SpaceMatchResult>("/api/spaces/match", new { descriptions = descriptions.ToArray() }, ct);
    public Task AddToProjectAsync(int project, IEnumerable<int> fileIds, CancellationToken ct = default) =>
        PostAsync<JsonElement>($"/api/projects/{project}/add", new { file_ids = fileIds.ToArray() }, ct);
    /// <summary>Files that probably belong in a space (nearest to what it holds, not members, not dismissed). Nothing is added.</summary>
    public async Task<List<FileItem>> GetSpaceSuggestionsAsync(int project, int limit = 12, CancellationToken ct = default) =>
        (await GetAsync<SearchResult>($"/api/projects/{project}/suggestions?limit={limit}", ct)).Files;
    /// <summary>"Not this one": the files are not offered for this space again.</summary>
    public Task DismissSuggestionsAsync(int project, IEnumerable<int> fileIds, CancellationToken ct = default) =>
        PostAsync<JsonElement>($"/api/projects/{project}/suggestions/dismiss", new { file_ids = fileIds.ToArray() }, ct);
    public Task RemoveFromProjectAsync(int project, IEnumerable<int> fileIds, CancellationToken ct = default) =>
        PostAsync<JsonElement>($"/api/projects/{project}/remove", new { file_ids = fileIds.ToArray() }, ct);

    /// <summary>"copy", "move" or "delete" (to the Recycle Bin) on indexed originals. The engine never overwrites.</summary>
    public Task FileActionAsync(string action, IEnumerable<int> fileIds, string? destination = null, CancellationToken ct = default) =>
        PostAsync<JsonElement>("/api/file_actions", new { action, file_ids = fileIds.ToArray(), destination }, ct);

    /// <summary>Queues one export per file. The engine writes new files and never touches the originals.</summary>
    public async Task<List<ExportJob>> StartExportAsync(IEnumerable<int> fileIds, ExportSpec spec, string? destination, CancellationToken ct = default) =>
        (await PostAsync<ExportJobs>("/api/export", spec.ToBody(fileIds, destination), ct)).Jobs;
    public async Task<List<ExportJob>> GetExportJobsAsync(CancellationToken ct = default) => (await GetAsync<ExportJobs>("/api/export/jobs", ct)).Jobs;
    public Task CancelExportAsync(int id, CancellationToken ct = default) => PostAsync<JsonElement>($"/api/export/jobs/{id}/cancel", null, ct);
    /// <summary>Forgets the finished, failed and cancelled exports.</summary>
    public Task ClearExportsAsync(CancellationToken ct = default) => PostAsync<JsonElement>("/api/export/clear", null, ct);

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
