# File Mind: road to 1.0 (implementation plan)

Written 2026-11-28 against `feature/all-glass-0.17` (HEAD 9ca7b8f). Audience: the model that executes it.

## How to use this plan
- Follow AGENTS.md: `git pull`, branch from `main` (`feature/...`, `fix/...`), build + run both test suites before each commit, push, `gh pr create`, wait for "merge". Never commit to main. Short replies, modest corner radii, no unrequested features.
- One work item = one branch = one PR, in the **Execution order** at the end. Each item lists files, the change, and an **Accept** check. P0 = needed for 1.0, P1 = should ship in 1.0, P2 = after 1.0 is fine.
- Line numbers refer to HEAD 9ca7b8f. Re-grep before editing; earlier items shift lines.
- Any "delete" must be preceded by `git grep -n <symbol>` showing only the definition (the audit below did this once).

## 1. Voice rewrite and natural-language questions (highest user pain)

### 1.1 Why it feels bad today
- `SpeechInput.ListenWhisperAsync` (Services/SpeechInput.cs:71-114) records an m4a with `VoiceRecorder`, waits for 0.9 s of quiet measured by a second audio graph (`LevelMeter`), uploads the whole file to `/api/voice/text` (main.py:1005-1019), which shells out to ffmpeg and runs Whisper *small* once. Nothing appears while speaking except "Listening…"; the text arrives 1-3 s after you stop.
- The result is then parsed by `SmartFilter.Parse` (FileQuery.cs:109-160): no dates, no colours, no sources, strips words like "with/of/the" blindly. The engine has a second, different parser `_interpret` (main.py:904-932). The two disagree, so spoken questions degrade into a short tag.
- The "windows" engine (SpeechRecognizer with the WebSearch topic, SpeechInput.cs:48-69) gives live hypotheses but the dictation/web-search topic uses Microsoft's online service (requires "Online speech recognition"); offline it only supports grammars. It breaks the "nothing leaves this PC" promise.

### 1.2 Options considered (offline, accurate, snappy)
| Option | Offline | Live words | Accuracy | Verdict |
|---|---|---|---|---|
| Windows.Media.SpeechRecognition dictation | No (online service) | Yes | Good | Reject as default; delete |
| Windows 11 on-device speech (Voice Access / Copilot+ APIs) | Yes | Yes | Good | Not a public, dependable app API on all Win10/11 PCs. Reject |
| faster-whisper on a rolling buffer + VAD, LocalAgreement for stable prefix, bigger model for the final pass | Yes | Yes (~0.4-0.8 s lag) | Best | **Choose** |

**Decision:** stream 16 kHz PCM from the app to the engine over a WebSocket. The engine re-decodes the growing buffer with faster-whisper **base** (already used for transcripts) every ~400 ms, emits a stable prefix (words that two consecutive hypotheses agree on) plus a tentative tail, detects end of speech with the Silero VAD that faster-whisper already ships (onnxruntime is already bundled), then runs **small** once on the trimmed utterance for the final text. The final text goes through one engine-side question parser (`app/understand.py`) that both Library and Quick Search use.

### 1.3 Work items

**V1 (P0) Question parser `app/understand.py` + tests.** New pure module, no new dependency. `understand(text: str, now: float, utc_offset_min: int, day_first: bool) -> dict` returning `{"text": leftover, "without": str|None, "filters": {...}, "kinds": [...], "color": key|None, "source": key|None, "favourite": bool, "chips": [{"key","label","value"}]}`. Filters use the existing engine names (`after`, `before`, `min_size`, `max_size`, `min_width`, `min_height`, `min_pixels`, `min_duration`, `max_duration`, `min_fps`, `aspect`, `codec`). Rules, applied in this order, each removing what it consumed:
1. Normalise: lowercase, strip fillers ("um", "uh", "please", "can you", "could you", "i'm looking for", "show me", "find me", "get me", "search for", "look for", "give me", "i want"), spoken numbers ("ten" -> 10, "fifth" -> 5th, "a minute" -> 1 minute, "half a minute" -> 30 s), "colour/color" spelling.
2. Dates (local day boundaries from `now` + `utc_offset_min`): "today", "yesterday", "this/last week|month|year", "past/last N days|weeks|months|years" (= [now-N, now]), "N units ago" (window centred on the point, ±25 %, at least ±1 day), "on 5th August", "5 aug", "august 5", "5/8" (`day_first` from the client's region), optional year; a date without a year = the most recent past occurrence; "in March", "March 2024", "in 2023", "since X", "after X", "before X", "between X and Y", "from X to Y", "last summer/winter" (northern-hemisphere months). Whole-day ranges: `after` = start of day, `before` = start of next day (matches `before` being exclusive in `files()` main.py:275).
3. Kinds: reuse `_KIND_WORDS` (main.py:905-906) + "screenshots" -> source screenshot, "docs/pdfs/documents" -> doc.
4. Resolution/shape/motion: 8K, 4K/UHD, 1080p/full HD/HD, 720p, vertical/portrait/tall, horizontal/landscape, square, widescreen/wide/cinematic, 60 fps/high frame rate/slow motion (min_fps 50), codecs (h264, hevc/h265, av1, prores, vp9).
5. Durations: "over/longer than/more than/at least N s|sec|seconds|min|minutes|hours", "under/shorter than/less than", "N to M seconds", "N second clips" (= around N: N*0.75..N*1.25).
6. Sizes: "over/bigger than/larger than N MB|GB|KB", "under/smaller than", "huge" (>500 MB), "small files" (<1 MB).
7. Colours: every key and label in `colors.SWATCHES` plus synonyms (reddish, bluish, gray, golden -> amber, beige -> tan, violet -> purple, turquoise -> teal), with or without "colour"/"coloured"/"tones". If two colours are named, keep the first as the filter and leave the second in the text. "black and white" -> leave in text (the mood query handles it better than pixels).
8. Sources: reuse the `names.LABEL` loop from `_interpret` (main.py:920-927, keep its exclusion list) and add "from my phone/camera" -> camera, "screenshots" -> screenshot, "from whatsapp", "downloads".
9. "favourites/starred/favorite" -> favourite. "without/except/excluding/no X" -> `without` (same rule as main.py:928-930).
10. Leftover: collapse spaces, drop dangling glue words at the ends only ("of", "with", "from", "a", "the", "that", "files", "file"). Empty leftover with a kind -> the kind's generic query as today (main.py:931).

`chips` gives the display line, e.g. `[{"key":"date","label":"Date","value":"5 Aug 2026"}, {"key":"color",...}, {"key":"text","label":"Text","value":"beach"}]`.

Then delete `_interpret` and `_KIND_WORDS`/`_LEAD` from main.py and make `/api/search` call `understand`.

Tests: new `tests/test_understand.py` with a fixed `now` (2026-11-28 12:00 local, offset 0) covering at least: "find me a file from 5th August, red colour" (after=2026-08-05 00:00, before=08-06, color=red, text=""); "files from 1 month ago" (window 2026-10-21..11-05); "vertical 4K videos of a beach over 10 seconds" (aspect portrait, min_pixels 8294400, kind video, min_duration 10, text "a beach"); "photos from last week"; "screenshots from yesterday"; "midjourney images of cats without text"; "pdfs about invoices from 2024"; "songs under 3 minutes"; "clips between 5 and 10 seconds"; "files bigger than 2 gb"; "blue and orange sunset" (color blue, text "orange sunset"); "5/8" with day_first True and False; "August" in November vs in February; "um can you show me ten second slow motion clips"; empty string; text with no filters stays unchanged.

**Accept:** `.venv\Scripts\python.exe -m unittest tests.test_understand` passes; `git grep _interpret` empty.

**V2 (P0) Engine endpoints and a real "taken" date.**
- `POST /api/understand` `{text, now, utc_offset_min, day_first}` -> `understand(...)` (pure, <5 ms).
- `/api/search` accepts `understand: true` + the same three context fields: runs `understand` on `q`, merges the filters under any explicit ones from the body (explicit wins), applies kind/colour/source/favourite exactly like the existing body fields (`_search_filter`, main.py:786-843), and returns `"understood": result["chips"]`.
- Dates: "files from 5 August" means when the photo was taken, not when it was copied. Add column `taken REAL` to `files` (db.py migration, see C1). Fill it in `media_meta.probe` (media_meta.py:12-36): images -> EXIF DateTimeOriginal (Pillow `getexif().get_ifd(0x8769).get(36867)`), video/audio -> ffprobe `format_tags=creation_time` (add to the existing `-show_entries`). Date filters in `files()` (main.py:273-276) and `_search_filter` use `COALESCE(f.taken, f.mtime)`. Facet "Date" chips (main.py:555-558) use the same expression.
- Bump the media-meta stamp (C1) so `backfill()` re-probes once to fill `taken`.

**Accept:** a JPEG with EXIF 2026-08-05 copied today is found by "photos from 5 August"; new tests in `tests/test_understand.py::SearchIntegration` call `main.search({...,"understand":True})` on a temp DB.

**V3 (P0) Client: one parser, visible understanding.**
- Delete `SmartFilter` + `SmartFilterResult` (FileQuery.cs:105-160). Add `ApiClient.UnderstandAsync(text)` (sends `now`, `TimeZoneInfo.Local.GetUtcOffset(DateTime.Now)`, `day_first` from `CultureInfo.CurrentCulture.DateTimeFormat .ShortDatePattern` starting with "d") and a `FileQuery.Apply(Understood u)` that copies filters into the query and returns the leftover text.
- Library `ApplySmartAsync` (LibraryPage.xaml.cs:1560-1579): call `UnderstandAsync`, apply via `GoAsync`, so every understood part becomes an active chip that `RebuildActiveChips` already renders and Back/Forward already records. Add the colour and date chips there (DetailLabel already formats after/before).
- Quick Search (QuickSearchWindow.cs:246-273): send `understand: true` in `SearchAsync`; add a one-line `TextBlock _understood` under the query row: `Date: 5 Aug 2026 · Colour: red · Videos · Text: beach` (secondary brush, 12.5 px, trimmed). While typing, refresh it from `/api/understand` debounced 200 ms.

**Accept:** typing or saying the three example questions shows the right chips in both places; `git grep SmartFilter` empty; C# test `FileQueryTests.ApplyUnderstood` added.

**V4 (P0) Streaming dictation in the engine: `app/voice_stream.py` + `WS /api/voice/stream`.**
- WebSocket route in main.py. The HTTP middleware does **not** run for WebSocket scopes: check `token` (query parameter `t` or header `x-filemind-token`, `hmac.compare_digest`) and refuse any request carrying an `Origin` header, then `accept()`; otherwise `close(1008)`.
- Protocol. Client -> server: binary frames = little-endian int16 mono 16 kHz PCM (any size, normally 100 ms); text frames `{"type":"start","lang":null,"now":..,"utc_offset_min":..,"day_first":..}`, `{"type":"stop"}` (finish now), `{"type":"cancel"}`. Server -> client: `{"type":"partial","stable":"...","tentative":"..."}`, `{"type":"speech","on":true|false}`, `{"type":"final","text":"...","understood":{...}}`, `{"type":"error","code":"model_missing|busy|internal","message":"..."}`.
- Session: numpy float32 buffer, capped at 20 s. A decode task wakes every 400 ms; if ≥300 ms of new audio and no decode is running, run the **partial model** (faster-whisper base; reuse `transcribe._model` loading code, make a shared `_load(name)` helper) with `beam_size=1, without_timestamps=True, condition_on_previous_text=False, temperature=0, vad_filter=False, language=<first detected, then fixed for the session>, hotwords=<source names: Midjourney, ChatGPT, Veo, Kling, Runway, Sora, ComfyUI, 4K, 1080p>`, and the existing `initial_prompt` (transcribe.py:186). LocalAgreement-2: `stable` = longest common word prefix of the last two hypotheses (never shrinks), `tentative` = rest of the newest hypothesis. Run decoding in `asyncio.to_thread`.
- End of speech: every 200 ms run `faster_whisper.vad.get_speech_timestamps` on the last 1.5 s (threshold 0.5). Speech seen and ≥700 ms of trailing silence -> finalise. No speech in the first 6 s -> final with empty text. Hard cap 15 s.
- Final: VAD-trim, run **small** (`transcribe.DICTATION_*`) with `beam_size=5`; if small is not downloaded, use the base hypothesis. Then `understand()` and send `final`. One active session at a time (`busy` error otherwise).
- Models: extend `/api/voice/model` (main.py:978-985, currently unused by the app) to report and download **both** base and small (~145 MB + ~480 MB) with progress; unload both after 5 min without a session (frees ~1 GB).
- Delete afterwards: `/api/voice/text` (main.py:1005-1019), `/api/voice/warm` (997-1002), `transcribe.dictate`, `warm_dictation`.
- Tests `tests/test_voice_stream.py`: LocalAgreement prefix function; session state machine with a fake model (callable returning scripted hypotheses) and synthetic PCM (silence/tone); token and Origin refusal using `fastapi.testclient.TestClient.websocket_connect`.

**Accept:** tests pass; manual: partial words appear in <1 s on CPU, final in <1.5 s after you stop on a 6-core CPU (log both timings at INFO in the engine log).

**V5 (P0) Client microphone stream: `Services/MicStream.cs`, rewrite `SpeechInput`.**
- Reuse the AudioGraph code in `LevelMeter.cs` (20-56): frame output node, read float samples per quantum, downmix to mono, resample to 16 kHz (linear interpolation from `graph.EncodingProperties.SampleRate`), convert to int16, buffer and send every 100 ms on a `ClientWebSocket` (set header `X-FileMind-Token`). Raise `Level` (RMS) for the UI meter from the same samples.
- `SpeechInput` becomes: `StartAsync()`, `StopAsync()` (send stop), `Cancel()`, events `Partial(stable, tentative)`, `Speech(bool)`, `Final(text, understood)`, `Failed(code, message)`. Map AudioGraph `AccessDenied` / `DeviceNotAvailable` / `FormatNotSupported` to `mic_denied` / `no_mic`.
- Delete `VoiceRecorder.cs`, `LevelMeter.cs`, `ApiClient.VoiceToTextAsync`, `ApiClient.WarmVoiceAsync`, `AppSettings.VoiceEngine`, the "Voice search uses" radio buttons (SettingsPage.xaml:95, SettingsPage.xaml.cs:30,100).

**Accept:** no temp m4a files are created (`%TEMP%\filemind_voice_*` stays empty); app builds; voice works in Library and Quick Search.

**V6 (P0) The listening flow (Quick Search, Library, global hotkey).**
- Settings: `VoiceHotkey` (default `Ctrl+Alt+Space`, fallbacks `Ctrl+Alt+Shift+V`, `Alt+Shift+V`), editable next to the Quick Search shortcut. `NativeHost` supports one hotkey id (NativeHost.cs:12); add a second id and `RegisterHotkey(int id, string combo)`; `App.StartLauncher` registers both and shows both in the tray tooltip.
- Global voice hotkey -> `Launcher.ListenFromHotkey()`: show the launcher if hidden, enter Listening. Inside the launcher Alt+V or the mic; inside the Library Alt+V (add an accelerator) or the mic button. Pressing the voice hotkey again while listening = Stop (finalise now).
- Listening state UI: mic button filled with the accent plus a ring whose scale follows `Level` (composition, 60 fps, no layout); the query line shows stable words in the primary brush and the tentative tail in the tertiary brush (a `TextBlock` with two `Run`s laid over the `TextBox` while listening; the `TextBox` gets the final text); placeholder "Listening… ask for a file"; the understood line updates from partials (debounced `/api/understand`).
- On `final`: put the text in the box, show the understood line, run the search immediately (`understand: true`), select the first result. Empty final -> "Didn't catch that. Try again (Alt+V)".
- Esc while listening = cancel and restore the previous query; Esc otherwise = hide (today's behaviour, QS cs:345-348). Clicking away while listening does not hide (already guarded by `_listening`, QS cs:179).
- Errors in the status line with an action: `mic_denied` -> "Microphone is off for desktop apps" + button that opens `ms-settings:privacy-microphone`; `no_mic` -> "No microphone found"; `model_missing` -> "Voice needs a one-time 620 MB download" + Download button (calls `/api/voice/model`, shows progress, then starts listening); engine not ready -> "File Mind is still starting".

**Accept:** QA steps 9-14 below pass; Narrator announces "Listening" and the final text (set `AutomationProperties.LiveSetting=Polite` on the status and understood lines).

## 2. Full code audit: delete list

Every entry below was grepped across `app/`, `native/`, `tests/`, `packaging/`; the only hit was the definition (or the listed callers, which are deleted together). Do all of A1 in one PR, rerun both test suites, smoke-run the app.

**A1 (P0) Engine (`app/`)**
| Where | Symbol | Why dead |
|---|---|---|
| main.py:935-975 | `_voice`, `POST /api/voice`, and the `voice:` branch in `search` (881-885) | App never calls `ApiClient.VoiceAsync` |
| main.py:97-131 | `_PICK_PS`, `POST /api/pick_folder` | App uses FolderPicker; no client reference |
| main.py:1390-1401 | macOS/Linux branches of `reveal`; also `sys.platform == "darwin"` branch in pick_folder | Windows-only app |
| main.py:489-517 | `POST /api/tags/define`, `DELETE /api/tags/{name}` | Tags sidebar removed; only Favourites remain |
| main.py:593-597 | `GET /api/sources` (keep POST) | Only POST is called |
| main.py:520-521 | `_DETAIL_KEYS` | Unreferenced |
| main.py:672-674, suggest.py:107+ | `POST /api/ideas/related`, `suggest.related` | No client call |
| main.py:1125-1133 | `GET /api/moment-clips` | No client call |
| main.py:1553-1594, 1728-1748 | `PUT /api/names`, `/api/names/clear`, `/api/names/pattern`, `/api/commit/plan`, `/api/commit/run`, `/api/commits`, `/api/commits/{cid}/undo`; `commit.build_plan`, `commit.undo` (keep `commit.run`, used by `file_actions`) | Half feature: names can be "proposed" but never applied. Also remove the proposed-name box in ViewerPage.xaml.cs:~1300-1310 and `proposed` from `files()` (330, 334), `info()` (1196-1199), `stats()` "renames" |
| main.py:1598-1613 | `_STOP`, `_suggest_name` | Unreferenced |
| main.py:1648-1724 | `/api/projects/regroup`, `/restore`, `/latest` | No client call (delete `test_regroup_can_restore_previous_spaces` in tests/test_library_tools.py:104-120 with it) |
| main.py:85 | `"snippet"`, `"mode"` keys in `_file_dict` | `snippet` is never written; `mode` unused by the app |
| main.py:596, 610 | local `from collections import Counter` re-imports | Already imported at top |
| embedder.py:120-126, 239-240 | `embed_image_file`, `embed_document` | Unreferenced |
| extract.py:20-22, 40, 68, 112, 133; indexer.py:176; embedder.py:184 | the `code` kind | Never indexed (`MEDIA_KINDS`, indexer.py:22) and purged at startup (main.py:43) |
| db.py:34 | `cluster` column in the CREATE (leave existing column in old DBs; do not ALTER) | Never read or written |
| transcribe.py:172-188 | `warm_dictation`, `dictate` | After V4 |

**Keep, do not delete:** `/api/known_folders` (wire into first run, U5), `PATCH/DELETE /api/projects/{pid}` (wire into Spaces, U1), `/api/voice/model` (V4), `tags.for_vector` (used by `/api/info`), `commit.run`.

**A2 (P0) Native**
| Where | Symbol | Why dead |
|---|---|---|
| ApiClient.cs:72-76, 92-93, 136-143, 146 | `DeleteTagAsync`, `DefineTagAsync`, `VoiceAsync`, parameterless `GetSourcesAsync` | No callers |
| ApiClient.cs:167-171 | `SetProposedNameAsync` | With the proposed-name UI |
| Models.cs:27, 133-140; Models2.cs:233+ | `FileItem.Snippet`, `VoiceModelState`, `VoiceResult` | Unreferenced (replace VoiceModelState with the V4 status record) |
| Pages/WrapLayout.cs | whole file | Never instantiated |
| LibraryPage.xaml.cs:388 | `foreach (var t in _tiles) t.PropertyChanged -= null!;` | No-op |
| LibraryPage.xaml.cs:263, 556 | `"voice:"` filtering | The voice: query form is gone |
| LibraryPage.xaml.cs:1084 | `("code","Code","f-text")` type tile | Kind never exists |
| ViewerPage.xaml.cs:573 | `or "code"` | Same |
| FileMind.WinUI.csproj | `ARM64` in `Platforms`/`RuntimeIdentifiers` | Engine is x64-only; no ARM64 build ships |
| FileQuery.cs:105-160 | `SmartFilter` | After V3 |
| Services/VoiceRecorder.cs, LevelMeter.cs | whole files | After V5 |

**Accept:** `git grep -n "VoiceAsync\|WrapLayout\|Snippet\|SmartFilter\|pick_folder\|regroup\|names/pattern"` returns nothing outside docs; both test suites pass; app builds and the Library, Viewer, Spaces, Discover, Duplicates pages open.

**A3 (P1) Dependencies / bloat** (verify each with `packaging/smoke_test.py`, which embeds a photo, video and sound):
- `librosa` (+ `numba`, `llvmlite`, `pooch`, `soxr`, `lazy-loader`, `msgpack`, `decorator`, `joblib`) and `scikit-learn` (+ `threadpoolctl`): not imported by `app/`. Remove from `requirements.txt`, `packaging/requirements-windows.txt` and `COLLECT_ALL` (build_windows.py:24-25) unless the smoke test's audio embed fails without them (sentence-transformers' audio path may want librosa: test, do not guess).
- `scipy`: only kept if transformers' audio feature extractor needs it in the smoke test.
- `psutil`, `rich`, `typer`, `shellingham`, `watchfiles` (keep watchfiles if W5 ships), `Pygments`, `markdown-it-py`: transitive of other tools; drop the pins that nothing imports.
- `packaging/nvidia-imports.txt` (2520 lines) + `scan_imports.py`: still needed by the stdlib stub for the downloaded PyTorch; regenerate after A3 rather than delete.
- Report the before/after size of `dist/engine/FileMind` in the PR.

## 3. Security review

Threat model: a local single-user app. Attackers: (a) web pages in the user's browser reaching `127.0.0.1:<port>`, (b) other low-privilege software on the same PC, (c) supply chain (CI, downloads, update channel).

**Verified safe (no change needed)**
- SQL: every f-string fragment is an internally built clause, an `int()`-cast id list, or a whitelisted value (ORDER BY dict main.py:297-299, aspect dict 289-294, labels filtered against `LABELS`, `_min_side_sql` casts int). User strings are always bound parameters.
- DNS rebinding: the Host guard (main.py:1787-1796) only allows loopback names. No CORS headers are sent, so cross-site JS cannot read JSON responses. Engine binds 127.0.0.1 only (filemind.py:57). Token is 128-bit random per run and passed by environment, not command line (EngineHost.cs:53, 81).
- Zip-slip: .NET `ZipFile.ExtractToDirectory` throws on entries outside the target (UpdateService.cs:157); Python `zipfile.extractall` strips `..`/absolute names, and the PyTorch wheels are SHA-256 pinned (runtime-windows.json, fetch.py:94).
- ffmpeg/ffprobe: argument lists (no shell), inputs are absolute paths (cannot start with `-`), timeouts on every call except exports (cancellable by design).
- Updater refuses when no checksum exists (UpdateService.cs:118) and deletes a mismatching download (137).
- `explorer /select,"<path>"` (main.py:1388): Windows paths cannot contain `"`.

| ID | Sev | Finding | Fix |
|---|---|---|---|
| S1 | **High** | GET routes need no token. Any web page can embed `http://127.0.0.1:P/api/file/{id}` / `/api/thumb/{id}` (images and video render cross-origin; `naturalWidth` and load/error timing leak which ids exist and their sizes), trigger `/api/duplicates?rescan=true` (CPU DoS) and `GET /api/tags` (writes). The ephemeral port is scannable from JS. | Require the token on every route except `/api/health`: header `X-FileMind-Token` **or** query `t=` (needed for `MediaSource`/`BitmapImage` URIs; add `t` in `ApiClient.FileUri`, `MomentFrameUri`). Compare with `hmac.compare_digest`. Reject any request with an `Origin` header or `Sec-Fetch-Site` other than `none`. Thumbnails stop going over HTTP at all (C3). Tests: `tests/test_security.py` (no token -> 403 on GET/POST/WS; Origin -> 403; health open). |
| S2 | Medium | New WebSocket bypasses the HTTP middleware. | Authenticate inside the route (V4). |
| S3 | **High** | Release supply chain: ffmpeg is downloaded from BtbN `latest` with no checksum (release.yml:36-42); third-party actions pinned by tag only (`softprops/action-gh-release@v2`, `vedantmgoyal9/winget-releaser@v2`); `permissions: contents: write` for the whole workflow. | Pin a dated BtbN release asset URL and verify a SHA-256 stored in the repo; pin every action to a full commit SHA; set `permissions: contents: read` at the top and `contents: write` only on the `release` job. |
| S4 | Medium | Model downloads (EmbeddingGemma via fetch.py:17-18, Whisper via transcribe.py:64,152) use mutable `resolve/main` and are not verified. A compromised repo or a wrong file is loaded as weights (safetensors is not code, but config/tokenizer files steer loading). | Pin a revision commit hash per repo (constant next to `REPO`), use `.../resolve/<sha>/`, and verify each LFS file against `siblings[].lfs.sha256` from the API response (`?blobs=true` already returns it) and each small file against its size. Store `model_revision` in meta (C1). |
| S5 | Medium | Updater: MSI/zip integrity only, authenticity = "whoever controls the GitHub release"; the verified MSI sits in `%TEMP%\filemind-update\FileMind-x64.msi` and the PowerShell scripts in fixed `%TEMP%` names, run up to 120 s later (TOCTOU for same-user malware). | Use `Directory.CreateTempSubdirectory("filemind-update-")`, write script and MSI there; pass the expected SHA-256 to the script and re-check with `Get-FileHash` immediately before `msiexec`/rename; once signing exists (K2) verify Authenticode (`WinVerifyTrust`) and pin the publisher subject. |
| S6 | Medium | Delete on a network/UNC path: Windows has no Recycle Bin there, so `send2trash` deletes permanently while the app says "Moved to the Recycle Bin" (LibraryPage.xaml.cs:2070-2077). | Engine `file_actions` returns which files are on non-local drives (`GetDriveTypeW` != DRIVE_FIXED/REMOVABLE or UNC); the client asks "These N files are on a network drive and will be deleted permanently" before calling. |
| S7 | Low | Uploads read fully into memory with no limit (main.py:958, 1011, 1026); temp names are millisecond stamps; suffix comes from the client filename. | Cap: 300 MB for `/api/search/image`; stream to `tempfile.NamedTemporaryFile(dir=TMP_DIR, delete=False, suffix=<whitelisted ext>)`; keep the `finally` unlink. |
| S8 | Low | OCR `LIKE` (main.py:779-780) does not escape `%`/`_`. | Escape and add `ESCAPE '\'`. |
| S9 | Low | `"testserver"` is an allowed Host in production (main.py:1784). | Only when `FILEMIND_TESTING=1` (set it in tests). |
| S10 | Medium | Reparse points: `os.walk` follows file symlinks out of the chosen folder and may loop through junctions; OneDrive "online-only" placeholders are fully downloaded by thumbnailing, embedding and OCR (privacy + bandwidth). | In `indexer.scan` read `os.stat(p, follow_symlinks=False).st_file_attributes`: skip `FILE_ATTRIBUTE_REPARSE_POINT` dirs and symlinked files; skip files with `RECALL_ON_DATA_ACCESS (0x400000)`, `RECALL_ON_OPEN (0x40000)` or `OFFLINE (0x1000)` and count them in `state["cloud_skipped"]`; show "N files are online-only in OneDrive and were skipped" in the index strip. OCR reads only paths the engine returns, so it inherits this. |
| S11 | Low | Logs: `app.log` never rotates (App.xaml.cs:119-128) and records search text and paths (QuickSearchWindow.cs:271, LibraryPage.xaml.cs:1864). | Rotate at 5 MB keeping one `.1`; never log query text; keep paths only for errors. Mention local logs in docs/PRIVACY.md. |
| S12 | Low | `settings.json` written non-atomically (Settings.cs:65-66); a torn write makes `Load` silently return defaults (Settings.cs:61-62). | Write `settings.json.tmp`, `File.Replace` with `.bak`; `Load` falls back to `.bak` and logs. C# test. |

## 4. Performance and cache robustness

### 4.1 Root causes of what the user sees
| Symptom | Cause (file:line) |
|---|---|
| Tiles visibly "populate" on every scroll | `TileRepeater.ElementPrepared += Anim.In(...)` (LibraryPage.xaml.cs:181) fades every realised/recycled element |
| Blank flash on every filter click | `ReloadAsync` clears `_tiles` before the request (LibraryPage.xaml.cs:384-400) |
| Sidebar rebuilds on every click | `ReloadAsync` -> `RebuildPanels` (1067-1168) recreates every chip, a `ColorPicker` (1176) and the spaces combo; then `FillRefineAsync` -> `/api/facets` + `/api/sources` |
| Slow pages on big libraries | `/api/files` selects and sorts the whole matching set, then slices (main.py:301-336), loads all `proposed_names` (330), and computes `source_of_row` regexes per row for source/pattern filters; `/api/sources` and `/api/colors` scan every row and parse JSON each call |
| Files re-thumbnailed / re-embedded | Thumbnail key ignores size/mtime so changed files keep stale thumbs while moved/renamed/re-added files are redone (extract.py:76-84); a vanished root/subfolder deletes rows, labels and spaces (indexer.py:143-148) and re-embeds when it returns; FAT/exFAT drives shift mtime by 3600 s at DST -> whole drive re-embedded; `media_meta.backfill` re-probes files whose probe failed on **every** start (media_meta.py:43-55) |
| Slow startup | Engine startup event runs orphan cleanup + thumbnail sweep (main.py:38-61) before `/api/health` answers; `_warm_dims` opens every thumbnail with PIL each launch (650-652); `_warm_ideas` loads the full model immediately; the app shows nothing of the library until the engine is healthy and the model checked |
| Viewer jumps | `BuildImage` loads the full-resolution original with no placeholder (ViewerPage.xaml.cs:729-731); nothing is prefetched; every video adds another `MediaHost.SizeChanged` handler that is never removed (824) |
| Search pages slow | `/api/search` re-ranks everything for every page (`limit + offset`, main.py:900) |

### 4.2 Work items

**C1 (P0) Cache-integrity foundation (engine).**
- Schema versioning with `PRAGMA user_version`. Turn the ad-hoc `ALTER`s in `db.init` (db.py:88-164) into an ordered list `MIGRATIONS = [(1, fn), (2, fn), ...]`; version 1 = today's schema. Before applying any migration: `VACUUM INTO 'index.pre-v{n}.db'` (keep the last 2). New columns in this plan: `taken REAL`, `tw INTEGER`, `th INTEGER`, `src TEXT`, `qhash TEXT`, `meta_key TEXT`, `missing_since REAL`.
- Stamps in `meta`: `embed_model` (+ revision, S4), `thumb_version`, `palette_version` (move from the `data/palette_version` file, indexer.py:327-331), `source_rules_version`, `meta_probe_version`. A stamp mismatch marks the derived data stale and refreshes it in the background; it never deletes anything before the replacement exists. A changed `embed_model` sets `status='pending'` but keeps `vec` until the new vector is written (search keeps working on old vectors meanwhile; mixing dims is guarded in `_rank`).
- Pragmas in `db.conn` (db.py:77-85): `synchronous=NORMAL`, `temp_store=MEMORY`, `cache_size=-65536`, `mmap_size=268435456`, `busy_timeout=30000`; drop `foreign_keys=ON` (no FKs exist). `PRAGMA optimize` on shutdown. Partial indexes: `files(kind, mtime) WHERE status='done'`, `files(src) WHERE status='done'`, `files(size, qhash)`.
- Corruption: a maintenance thread (C5) runs `PRAGMA quick_check` once per launch after the first request. On failure: copy `index.db` to `index.corrupt-<ts>.db`, restore the newest `index.bak.db`, set a flag the app shows ("The library index was damaged and was restored from <date>"). Nightly-idle backup: `VACUUM INTO index.bak.db` when idle ≥10 min and the last backup is >24 h old.
- User data is never only in the index: on every change of labels, favourites or space membership, write `data/user-data.json` (`[{path,size,label,fav,spaces:[name]}]`, atomic replace). On a rebuilt/restored DB, re-apply it by path (then size).
- Tests `tests/test_cache_integrity.py`: migrate a v0 fixture DB; stamps; `quick_check` failure path (write garbage into a copy); user-data.json round trip.

**Accept:** tests pass; upgrading an existing 0.17 `index.db` keeps every vector, label, favourite and space.

**C2 (P0) Indexer change detection that never throws work away.** (`app/indexer.py`)
- `qhash` = SHA-1 of size + first 64 KB + last 64 KB (cheap even for 4 GB videos). Compute for new rows and when size/mtime changed. If size equal and qhash equal -> update `mtime` only, keep status/vec/thumb.
- DST: same size and `abs(mtime_delta) in (3600, 7200)` -> treat as unchanged without hashing.
- Moves/renames: before inserting a new path, look for a row of the same root set with equal `(size, qhash)` whose path was not seen in this scan (or is `missing`); move that row to the new path (keeps id, vec, thumb, labels, spaces).
- Never delete on absence. Rows not seen get `missing_since=now` (status unchanged) and are hidden from views (`missing_since IS NULL` in `files()`, `_rank`, facets, counts). Purge after 30 days, on "Forget folder", or from Settings > Storage "Clean up missing files". If the root itself is unreachable, skip the scan entirely (today only the startup path checks `isdir`, main.py:60). `os.walk(onerror=...)`: record failed subtrees and do not mark their files missing.
- Prep/embed threads run at background priority (W3).
- Tests (add to `tests/test_index_speed.py` or new `tests/test_scan_safety.py`): rename keeps id/vec/label; DST shift keeps vec; unplugged folder keeps labels; changed content gets re-embedded.

**Accept:** renaming a folder of 1 000 photos and re-reading it does no embedding (engine log shows 0 embedded).

**C3 (P0) Thumbnails: correct keys, stored sizes, read straight from disk.**
- Key: `sha1(path | size | int(mtime) | THUMB_VERSION)`; when a file changes, unlink the old thumb after the new one is written. Store `tw`/`th` when the thumb is written (indexer `_run`, sequences) and backfill once; delete `_dims`, `_thumb_dims`, `_warm_dims` (main.py:65-78, 650-652, 56).
- `_file_dict` adds `"thumb": r["thumb"]`. Client: `FileItem.Thumb`; `TileVm.Thumb` and every `ThumbUri` use `new Uri(Path.Combine(paths.Data, "thumbs", thumb))` (data folder from `AppRuntime.StorageFolders()`), so thumbnails load with no HTTP, no token (S1) and before the engine is up (C7). Keep `/api/thumb` (token-protected) as fallback.
- Self-heal: `/api/thumb` and a weekly maintenance pass regenerate a missing thumb file instead of returning 500.
- `TileVm.Thumb` decode size: `DecodePixelWidth = bucket(tileWidth * RasterizationScale)` with buckets 240/360/480, `DecodePixelType = Logical`; recreate only when the bucket changes (TileLayout knows the width).

**Accept:** editing a photo then re-reading shows the new thumb; launch with the engine killed still shows thumbnails from the snapshot (C7).

**C4 (P0) Cheap library queries.**
- `src` column computed in `scan`/`_register_sequence` with `names.source_of`; recomputed in the maintenance thread when `source_rules_version` changes (bump it whenever names.RULES changes, add a test that fails if RULES changes without the bump: hash of the rule keys and patterns). `files()` source filter -> `f.src=?`; `_source_counts` -> `GROUP BY src`; `source_of_row` in `_file_dict` reads the column.
- `files()`: when no computed filter is active (discover, smart space, color, near, pattern) use `SELECT COUNT(*)` + `ORDER BY ... LIMIT ? OFFSET ?`. Otherwise cache the ordered id list in an LRU(16) keyed by `(frozen params without limit/offset, index_gen, labels_gen)`.
- `GET /api/panels` returns `{stats, roots, colors, moods, projects, labels, favourites}` in one response, memoised by `(index_gen, labels_gen, projects_gen)`; `RefreshFacetsAsync` (LibraryPage.xaml.cs:366-379) makes one call. Add `labels_gen`/`projects_gen` counters in `meta`, bumped by the label, favourite and space endpoints.
- `/api/info` (main.py:1172-1206): stop quantising the thumbnail on every call (store the 8-colour list at palette time or LRU it by thumb name) and stop recomputing every smart space per call (memoise `_smart_space_ids` by `(pid, index_gen)`).
- `/api/search`: keep the last 8 rankings keyed by `(body without limit/offset, index_gen)`; pages 2+ slice it.
- `indexer.matrix()` (indexer.py:58-70): during indexing, append newly committed vectors instead of reloading every BLOB; full rebuild only after deletes/forget.

**Accept:** on a 50 000-file library, `/api/files` page 2 < 30 ms and `/api/panels` (warm) < 20 ms (add timing to a test with a synthetic DB in `tests/test_index_speed.py`).

**C5 (P0) Engine startup and maintenance.**
- `_startup` keeps only `db.init()` + migrations + starting a `maintenance` thread. Move orphan cleanup, `_drop_files` of forgotten roots, `_sweep_thumbs`, colour/meta backfills, GC (below), `quick_check`, backups into it, running after the first request or 3 s, whichever is first.
- `_warm_ideas` model load: start 10 s after startup only if the model is downloaded; skip when on battery (`GetSystemPowerStatus`).
- `media_meta.backfill`: write `meta_key=size:mtime` even when the probe fails; select only rows whose `meta_key` differs (fixes re-probing every start).
- GC: sweep `TMP_DIR` on start; remove `waves/`, `transcripts/`, `momentframes/`, `proxies/` entries whose file id is gone or whose size/mtime in the name no longer match.

**Accept:** engine log shows `/api/health` answering <2 s after process start on a 50 000-file library.

**C6 (P0) Library client: steady, no flashing.** (`Pages/LibraryPage.xaml.cs`)
- Remove `ElementPrepared += Anim.In` (181). Fade the whole repeater once on the first page of a new view only.
- `ReloadAsync`: request the first page first, then replace `_tiles` in one step; if the request takes >150 ms, dim the old tiles to 0.6 instead of clearing. Keep the scroll position when Back restores a view (store `VerticalOffset` in `NavState`).
- Split `RebuildPanels` into `BuildPanels()` (when panel data changed: after `/api/panels`) and `SyncPanelState()` (navigation: set `IsChecked` on existing chips kept in `Dictionary<string, ToggleButton>`; rebuild only the active chips row). Create the custom colour `ColorPicker` on first tap.
- `PollAsync` (650-671): reload when indexing finishes only if `index_gen` changed (return it in `/api/status`).

**Accept:** clicking Photos/Videos/All with a 10 000-file library shows no blank frame (record with Snipping Tool); sidebar chips do not flicker.

**C7 (P1) Warm start.**
- On successful first page of the default All view and on exit, write `data-native/snapshot.json` under `AppPaths.Root`: `{version, index_gen, stats, panels, files:[first 150 FileItems incl. thumb, tw, th]}` (atomic write).
- `MainWindow.BootAsync` (MainWindow.xaml.cs:290-313): if the model is known to be present (setting `ModelReady=true` saved after a successful setup) and a snapshot exists, show the Shell immediately with the Library rendering the snapshot; run `SetupPage.RunAsync` logic in the background with a small "Starting…" pill in the top bar; disable search/actions until `Api` is ready; then reload silently (no flash, C6). First run and model download still use the full SetupPage.

**Accept:** cold start to first thumbnails < 1 s on an SSD with the engine still booting.

**C8 (P0) Viewer stepping.** (`Pages/ViewerPage.xaml.cs`)
- `BuildImage`: show the thumbnail immediately (from disk, C3) in the same `Image`, then load the original with `DecodePixelWidth = ceil(stage width * RasterizationScale)` (re-decode only when zooming past 1.5x), swap on `ImageOpened` with a 120 ms cross-fade.
- Prefetch the previous and next image originals at the same decode width (keep a 3-entry cache keyed by id).
- `BuildVideo`: poster = thumbnail until `MediaOpened`. Store the `SizeChanged` handler in a field and remove it in `Teardown` (824).
- Debounce `LoadSimilarAsync` / `LoadInfoAsync` by 250 ms so holding an arrow key does not fire a search per file.

**Accept:** holding Right through 50 photos never shows an empty stage; memory stays flat (Task Manager).

## 5. Glass and UX

### 5.1 Why it looks muddy now
1. Everything is glass, including content. Wallpaper -> `DesktopAcrylicBackdrop` (its own luminosity + tint layer) -> `PanelCard` tinted `#0C0D14` at ~63 % (ThemeEngine.cs:61 with the default 0.6 transparency) -> `AuraBrush` accent glow -> the in-app `ToolbarGlassBrush` acrylic (Glass.xaml:88, tint 0.62 + luminosity 0.72) blurring what is already blurred -> `GlassFillBrush` white 10-21 % veils on cards and chips (ThemeEngine.cs:76, 99) -> strokes. Each semi-opaque layer averages toward mid-grey and lowers contrast; nothing restores saturation. Apple's glass works because it is one thin layer on top of vivid content with a saturation boost and a crisp specular edge.
2. White veils on dark surfaces read as grey haze (`#1A`-`#36` white).
3. Blur of blur: the toolbar acrylic samples a page that already sits on window acrylic.
4. Edges are soft: the frosted stroke is a diagonal 0x40->0x10 gradient (Glass.xaml:33-37); the bright top edge only exists in "liquid" mode.
5. Light theme (the default, Settings.cs:21 `paper`): solid white fills and `#C4C2BC` borders over acrylic look flat.
6. Two surface styles, an AMOLED special case and a transparency slider multiply paths that are each half-tuned.

### 5.2 Target
Glass only for the **control layer** (rail, top bar, floating pills, flyouts); **content** (tiles, viewer stage, Settings cards, inspector) sits on a calm, nearly opaque surface so thumbnails stay vivid. One look, no Surface option. Radii: 12 for floating pills and panels, 8 for inner controls (concentric: inner = outer - padding 4), 6 for chips. This stays within "modest corner radii".

**G1 (P1) Backdrop and surfaces.**
- New `Services/GlassBackdrop.cs : SystemBackdrop` (WinUI 3 custom backdrop pattern: override `OnTargetConnected`, create `DesktopAcrylicController`, `SetSystemBackdropConfiguration` with `IsInputActive` and `Theme`). Dark: `Kind=Thin`, `TintColor #0B0C10`, `TintOpacity 0.10`, `LuminosityOpacity 0.55`, `FallbackColor #FF101116`. Light: `Kind=Thin`, `TintColor #F5F5F7`, `TintOpacity 0.08`, `LuminosityOpacity 0.72`, `FallbackColor #FFF3F3F5`. Use it in `MainWindow` (replace lines 17 and 189) and `QuickSearchWindow` (65, 207).
- Rail: transparent (no `RailCard` fill, MainWindow.xaml.cs:166, 192). Page: `PanelCard` = solid content surface `#F2111216` dark / `#F7FAFAFB` light (a hint of the backdrop at the edges, not a veil). Delete `AuraBrush`.
- Remove `Settings.Surface`, `Settings.Transparency` slider, `LiquidSheenBrush`, `LiquidEdgeBrush`, `Ui.Surface`'s liquid branch, `ThemeEngine.RailOpacity/PanelOpacity/IsLiquid/Liquid`, and the AMOLED theme (black pages fight glass; keep Midnight, Graphite, Light, System). Map old settings on load.
- Content cards (`GlassCard` style): solid `#FF18191D` dark / `#FFFFFFFF` light, 1 px stroke `#14FFFFFF` / `#14000000`, radius 10, no blur.

**Accept:** side-by-side screenshots (FILEMIND_DEV_PAGE library/settings, dark and light) attached to the PR; `git grep Liquid` empty.

**G2 (P1) Real glass for the control layer.**
- Add `CommunityToolkit.WinUI.Media` (8.x) for a Win2D-free effect pipeline, or hand-write `IGraphicsEffect` wrappers for GaussianBlur/Saturation/Blend if the package adds >2 MB. Brush recipe `GlassBrush` (in-app backdrop): `Backdrop -> GaussianBlur(28, Hard) -> Saturation(1.8) -> Blend(Normal) with tint`, tint dark `#1C1D22 @ 0.38`, light `#FFFFFF @ 0.55`, optional 2 % noise tile (`Assets/noise.png`, 128 px) at 0.03 opacity. Apply to: top bar search pill (`SearchPill`), selection `ActionBar`, `Toast`, viewer `ZoomPill`/`TransportPill`/ `ToolsPill`, export tray. The top bar strip itself becomes transparent (the pill floats).
- Specular edge: 1 px gradient stroke, vertical: dark `#8CFFFFFF -> #14FFFFFF` (0 -> 0.6) `-> #0AFFFFFF`, light `#FFFFFFFF -> #1A000000 -> #26000000`; plus a 1 px inner top highlight inset 1 px at `#33FFFFFF` (dark only).
- Depth: keep `ThemeShadow` (Translation Z 24) and add a composition `DropShadow` under pills: blur 28, offset (0, 8), colour black, opacity 0.22 dark / 0.10 light.
- Flyouts and menus: `FlyoutBase.SystemBackdrop = new DesktopAcrylicBackdrop()` (WinAppSDK 1.3+) for the Filters flyout, MenuFlyouts and the colour picker.
- Motion: one helper `Anim.Spring(element, scale|offset)` using `Compositor.CreateSpringVector3Animation` (DampingRatio 0.72, Period 45 ms) for press (0.97), hover lift, the search pill widening on focus (morph from 420 to 560 px), the listening mic ring, and selection-bar entrance. Respect `UISettings.AnimationsEnabled` (already in `Anim.On`). Keep `Glow` (pointer light) but cap its alpha at 30.

**Accept:** the toolbar pill over colourful tiles shows the tiles blurred and saturated, not grey; GPU use idle < 2 %.

### 5.3 UX review: top 12 fixes (ranked)
| # | Pri | Fix |
|---|---|---|
| U1 | P0 | **Spaces cannot be renamed or deleted** (engine has `PATCH/DELETE /api/projects/{pid}`, main.py:1456-1472, no UI). Add a right-click menu and a "…" button on each space card (SpacesPage.xaml.cs `Card`) and on sidebar space rows: Rename (PromptAsync) / Delete (confirm "Files are not touched"). ApiClient `RenameProjectAsync`, `DeleteProjectAsync`. |
| U2 | P0 | Understood-question chips (V3/V6) everywhere search happens. |
| U3 | P0 | Steady Library and viewer (C6, C8). |
| U4 | P1 | Accessibility: add `<Setter Property="UseSystemFocusVisuals" Value="True"/>` to `GlassButton`, `GlassChip`, `BarButton`, `RailItem`, `QuickRow` (Glass.xaml); tiles get `AutomationProperties.Name="{name}, {kind}, {size}"` (LibraryPage.xaml tile template, line ~9); chips get "Label, count"; arrow keys move focus in the grid (TileRepeater + `XYFocusKeyboardNavigation=Enabled`), Space toggles the check, Enter opens. Contrast: `GlassTextTertiaryBrush` dark `#A3FFFFFF` is fine, Glass.xaml default `#80FFFFFF` (4.0:1) -> `#A3FFFFFF`; verify all text ≥4.5:1 with the Accessibility Insights tool. |
| U5 | P1 | First run: before downloading, show what and how big ("AI model 1.5 GB, PyTorch 0.1 or 2.8 GB, once; nothing is uploaded"); let the person add folders while it downloads, with one-click known folders from `/api/known_folders` (main.py:134-148, currently unused). |
| U6 | P1 | High contrast: when `AccessibilitySettings.HighContrast` is true, no backdrops, `ThemeDictionaries["HighContrast"]` mapping every brush key to `SystemColor*` resources; listen to `HighContrastChanged`. |
| U7 | P1 | Settings: turn the 9-card slide-over (MainWindow.xaml:88-101, SettingsPage.xaml) into 5 sections with a left section list: **General** (start with Windows W7, keep running in tray, updates), **Appearance** (theme, accent, pointer light), **Search & voice** (quick-search shortcut, voice shortcut, voice model status, OCR), **Library** (folders, smallest size shown, sidebar order, indexing speed), **Engine & storage** (CPU/GPU, storage with "Remove AI downloads", "Clean up missing files", "Open logs folder"), plus About. Remove the Classic/Slim sidebar and Similar-placement options (keep Slim, keep "below"). |
| U8 | P1 | Empty states with one action: no results -> "Clear filters" button; engine unreachable -> "Restart engine" (calls `RestartEngineAsync`); indexing -> progress + "Pause". |
| U9 | P1 | Keyboard sheet: `F1` / `Ctrl+/` opens a flyout listing every shortcut (Ctrl+1-4, Ctrl+K, Ctrl+F, /, Alt+V, F, 0-7, Del, Ctrl+C, Ctrl+Shift+C, Esc, Alt+arrows, viewer keys). |
| U10 | P1 | Network-drive delete warning (S6) and clearer delete copy. |
| U11 | P2 | Undo for Move (the commit journal is written by `commit.run`; add one toast "Moved 12 files · Undo" calling a kept `/api/commits/{cid}/undo`). If not done, it stays deleted per A1. |
| U12 | P2 | Viewer siblings stop at the loaded tiles (LibraryPage passes `_tiles` only): pass a loader callback so Next fetches the next page. |

## 6. Packaging, updates, winget, Microsoft Store

### 6.1 Decision
- **MSI (per-user WiX, already built) is the default** for GitHub and winget; the zip stays as the portable option (its updater path exists and is tested). **MSIX for the Store is post-1.0 (P2)**, produced from the same publish folder in the same CI run.
- Data lives in `%LOCALAPPDATA%\File Mind` (AppPaths.cs:16-17) and the MSI installs to `%LOCALAPPDATA%\Programs\File Mind` (FileMind.wxs:14-16), so MSI upgrades never touch data. Multi-GB downloads live in `File Mind\runtime` (PyTorch, up to ~2.8 GB compressed) and `File Mind\models` (~1.5 GB + Whisper ~0.6 GB).

**K1 (P1) MSI, winget and CI install tests.**
- Rename the engine executable to `FileMindEngine.exe` (build_windows.py `--name`, EngineLocator.FindBundled EngineHost.cs:32, validate_build.py, smoke_test.py, release.yml). Two processes called FileMind.exe confuse Restart Manager, Task Manager, `FocusRunningInstance` (App.xaml.cs:103) and AGENTS.md's "close FileMind.exe".
- Uninstall: remove only re-downloadable data. WiX util extension `RemoveFolderEx` on `[LocalAppDataFolder]File Mind\runtime` and `...\models`, condition `REMOVE="ALL" AND NOT UPGRADINGPRODUCTCODE`. Keep `data` (index, labels, spaces) and say so in Settings > Storage, which also gets "Remove everything File Mind stored" (asks, then deletes `File Mind\` after stopping the engine).
- winget manifest (fix packaging/winget/README.md): `InstallerType: wix`, `Scope: user`, `InstallerSwitches: {Silent: /quiet, SilentWithProgress: /passive}`, `UpgradeBehavior: install`, `AppsAndFeaturesEntries: [{UpgradeCode: {7C1B0E52-3F7A-4D5E-9A62-1E5B2D4C8F10}}]`, `ProductCode` from the built MSI (changes every version; the releaser fills it). **Remove** `NestedInstallerType`, `RelativeFilePath`, `PortableCommandAlias` (zip-only fields). An app installed by winget lands in the same folder with the MSI marker, so the in-app MSI updater handles it; winget sees the new ARP version.
- Engine writes `engine.json` (`{port, pid}`, no token) next to `engine.log` on start (EngineHost) so tests can find it.
- CI job `install-test` (windows-latest, after `windows`): `msiexec /i FileMind-x64.msi /quiet /l*v install.log`; assert `%LOCALAPPDATA%\Programs\File Mind\FileMind.exe` exists and one ARP entry; start it with `FILEMIND_NATIVE_HOME=<temp>` and `--hidden`; poll `engine.json` then `GET /api/health` (60 s); kill; upgrade test: install the previous release's MSI first (`gh release download --pattern FileMind-x64.msi`, skip if none), then the new one; assert a single ARP entry, the new version in ARP and a file created in `FILEMIND_NATIVE_HOME\data` before the upgrade still exists; `msiexec /x` and assert the program folder is gone and `data\` remains.

**Accept:** the job is green on a tag build; manual v0.16 -> v0.17 -> v0.18 upgrade keeps labels and spaces.

**K2 (P1) Code signing.**
- Options for an indie developer: **Azure Trusted Signing** (monthly fee, identity validation; check current eligibility for individuals in your country), **SignPath Foundation** (free for qualifying open-source projects), or an OV certificate on a hardware token (slow in CI). The Store signs MSIX itself.
- Without signing: SmartScreen shows "Windows protected your PC / Unknown publisher" for each new release until enough downloads build reputation; signed builds carry reputation across versions.
- Sign `FileMind.exe`, `engine\FileMindEngine.exe`, the MSI (and later MSIX) in `release.yml` before checksums. Updater then verifies Authenticode + publisher subject (S5).

**K3 (P2) Smaller package.** Results of A3, plus ffmpeg `lgpl-shared` build (ffmpeg and ffprobe share DLLs instead of two static copies), `PublishReadyToRun=true` for the app (startup, W8). Report sizes per release.

**K4 (P2) Microsoft Store (MSIX) build.**
- Same `dotnet publish` output + engine, packaged with `MakeAppx` from a `packaging/msix/AppxManifest.xml` (Identity/Publisher from Partner Center, `runFullTrust`, `internetClient`, `microphone`, `TargetDeviceFamily Windows.Desktop 10.0.19041.0`, `desktop:StartupTask` instead of the Run key, `uap5:AppExecutionAlias` optional).
- Build flag `-p:StoreBuild=true` defines `STORE`: compile out `Updater.InstallAsync`, hide update UI and "Remove everything" (Store uninstall removes package data), use `StartupTask`.
- Store policy: first-run download of native code (PyTorch wheels) is a certification risk. Store build bundles the CPU PyTorch inside the package and has no CUDA download; models are data (disclose size and network use in the listing and privacy policy, docs/PRIVACY.md must be hosted publicly with contact details).
- Delete for the Store build: zip/MSI updater code paths, `installed-by-msi.txt` marker, Run-key startup, the `runtime/` download path (`app/runtime.py` spec), "Engine: NVIDIA" option.

## 7. Windows stability and performance (only what pays off)

**W1 (P1) Engine watchdog that also catches hangs.** `WatchEngineAsync` (MainWindow.xaml.cs:316-328) only sees a dead process. Every 10 s `GET /api/health` with a 3 s timeout; 3 failures in a row -> kill + restart, toast "The engine restarted"; after restart rebuild `Api` and tell pages (event `AppRuntime.EngineRestarted`) so Library reloads.

**W2 (P1) Single instance is fine** (mutex + event, App.xaml.cs:22-31). Only change: `FocusRunningInstance` must not match the engine process (fixed by the K1 rename).

**W3 (P1) Indexing that does not make the PC feel slow.** In indexer worker threads and the `_run` thread call `SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_BEGIN)` (lowers CPU and I/O priority); start ffmpeg/ ffprobe from those threads with `BELOW_NORMAL_PRIORITY_CLASS | CREATE_NO_WINDOW`; in "quiet" mode also enable EcoQoS (`SetThreadInformation(ThreadPowerThrottling)`). Search requests stay normal priority.

**W4 (P0, part of S10/C2)** OneDrive placeholders, reparse points, unreachable roots, network drives (no watcher, slower rescans).

**W5 (P1) Watch folders for changes.** `watchfiles` (already installed with uvicorn[standard]) uses ReadDirectoryChangesW: one watcher thread per local root; debounce 3 s; rescan only the changed directories (add `scan(root_id, root, only_dirs=set)`). Network/UNC roots: rescan every 30 min instead. Turn watching off when the app quits.

**W6 (P1) Long paths.** The app manifest is long-path aware; check the engine: add a test that indexes a file at a 300-character path (needs `LongPathsEnabled`; skip if off). If ffmpeg or PIL fail, pass `\\?\`-prefixed paths via a helper `extract.long_path(p)` for paths ≥ 240 chars.

**W7 (P1) Start with Windows.** The hotkeys only work while File Mind runs. Setting "Start with Windows (in the background)" writes `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\FileMind = "<exe>" --hidden` (Store build: StartupTask). Default on after first successful setup, with the setting visible in General.

**W8 (P2) Startup time.** `PublishReadyToRun=true` in the WinUI csproj (measure; keep if ≥15 % faster). No trimming (XAML reflection). Engine import time: lazy-import `send2trash`, `fitz`, `docx` where used.

**W9 (P1) Memory.** Unload the embedding model after 15 min with no search and no indexing (`embedder.unload()`, `torch.cuda.empty_cache()`); reload on demand (first search after that takes the load time; show "Waking up…"). Whisper models unload after 5 min (V4).

**W10 (P1) Logs.** Engine `filemind.log`, app `app.log`, host `engine.log`: rotate at 5 MB keeping one old file (S11).

Not worth it for 1.0: jump lists, Windows Search integration, taskbar progress, ARM64.

## 8. Release readiness

### 8.1 Version policy
- Tags since v0.9.0 were never pushed (0.9.1-0.17.0 exist only as PRs). After R1, ship **0.18.0** as a GitHub *prerelease* (the updater ignores prereleases, UpdateService.cs:78) for testers, then **0.19.0** release candidate, then **1.0.0** only when every go/no-go box is ticked. Patch releases (1.0.x) for fixes only.
- Version lives only in `FileMind.WinUI.csproj <Version>`; the MSI reads it (release.yml:62). Add the engine version to `/api/health` (`{"ok":true,"version":...}`) and have the app warn if they differ.

### 8.2 Go / no-go for 1.0
- [ ] All P0 items merged; both test suites green in CI (`Check app behavior` + `dotnet test`, add the latter to release.yml).
- [ ] S1, S3, S4 fixed; no High finding open.
- [ ] Fresh install -> first search works on: NVIDIA PC, Intel/AMD-only PC, a PC with no microphone.
- [ ] Upgrade 0.17 -> 1.0 keeps vectors, labels, favourites, spaces (K1 CI job + manual).
- [ ] Re-reading an unchanged 10 000-file folder embeds 0 files; renaming a folder embeds 0 files.
- [ ] Voice: three example questions understood correctly by voice on CPU; partial words < 1 s.
- [ ] Unplugging a drive and plugging it back keeps its labels and spaces.
- [ ] No crash in app.log/engine.log across the QA script; app.log contains no search text.
- [ ] Uninstall removes runtime/models, keeps data; documented in README.
- [ ] README, PRIVACY.md (local logs, downloads, voice stays local) and release notes updated.

### 8.3 Tests to add (summary)
Python: `test_understand.py` (≥40 cases, V1/V2), `test_voice_stream.py` (V4), `test_security.py` (S1/S2/S7/S8/S9), `test_cache_integrity.py` (C1), `test_scan_safety.py` (C2), thumbnail key + self-heal (C3), `/api/files` paging and `/api/panels` timing on a synthetic DB (C4), meta-probe stamp (C5), source-rules version guard (C4). C#: `FileQueryTests` (apply understood filters, query strings), `SettingsTests` (atomic save, .bak fallback), `UpdateServiceTests` (random temp dir, script contains the hash re-check, winget/MSI detection), `NativeHostTests` (two hotkeys parse, `Ctrl+Alt+Space`), `SnapshotTests` (C7 round trip, version mismatch ignored).

### 8.4 Manual QA script (run on a clean Windows 11 user account, then on Windows 10)
1. Install the MSI by double-click; SmartScreen text matches the release notes; app starts.
2. First run shows download sizes; add Pictures with the one-click button while downloading.
3. Indexing strip counts up; tiles appear without per-tile fading; Pause and resume work.
4. Close and reopen: thumbnails show in <1 s, before the engine finishes starting (C7).
5. Click Photos, Videos, All, a colour, a source: no blank flash, sidebar does not flicker.
6. Scroll 2 000 tiles fast: no stutter, no re-fading.
7. Type "vertical 4K videos of a beach over 10 seconds": chips Portrait, 4K, Videos, Over 10 s, Text "a beach".
8. Type "files from 1 month ago" and "photos from 5 August": date chips correct; EXIF-dated photo found.
9. Press the voice hotkey with the app in the tray: launcher opens listening, words appear while speaking.
10. Pause: search runs by itself; understood line shown; Enter opens the first result.
11. Start voice, press Esc: listening stops, previous text restored.
12. Turn off microphone access in Windows privacy: clear message with a working "Open settings" button.
13. With the Whisper models deleted: the download prompt appears, progress shows, listening starts after it.
14. Alt+V in the Library: same live behaviour; result becomes chips + a text tag; Back undoes it.
15. Open a photo, hold Right for 30 files: no empty stage, no jump; open a video, step to an image and back.
16. Rename a folder on disk with 200 indexed photos, re-read it: no embedding, labels kept.
17. Edit a photo in Paint, re-read: new thumbnail.
18. Unplug a USB drive that was added, restart: its files hidden, not lost; plug in: back with labels.
19. Spaces: create, rename, delete a space; files untouched.
20. Select 3 files on a network share, Delete: permanent-delete warning shown.
21. Settings: every section opens; theme Light/Dark/System switch live; shortcuts editable and conflict message works.
22. Narrator: tiles, chips, mic and search box are announced; Tab/arrow navigation reaches every control; focus visible.
23. High contrast theme on: app readable, no translucent surfaces.
24. Check for updates against a test release (fork or prerelease toggle): download, verify, install, restart, data kept.
25. Uninstall from Settings > Apps: program folder and runtime/models gone; data folder kept; reinstall finds library.

### 8.5 Merge and tag order for the stacked PRs (#23 -> #31)
The stack is linear: #23 `feature/hardening-0.9.1 -> main`, #24 `labels-0.10 -> hardening-0.9.1`, ... #31 `all-glass-0.17 -> msi-voice-0.16`. Confirm 9ca7b8f is pushed (`git status -sb` shows in sync).
1. Merge **#23** into main with **"Create a merge commit"** (never squash or rebase a stacked PR: later PRs would re-apply the same changes and conflict). Delete its branch: GitHub retargets #24 to `main` automatically.
2. Repeat for #24, #25, ... #31 in order, each time waiting for CI on the retargeted PR.
3. Do not tag 0.10-0.17: they were never released. After all plan items for the first beta land, bump to 0.18.0 and tag `v0.18.0` as a prerelease (edit the release in GitHub to tick "pre-release", or add `prerelease: true` driven by a `-beta`/`-rc` tag suffix in release.yml). Alternative if the owner prefers: retarget #31 to `main`, merge it alone with a merge commit, then close #23-#30 as included. Same result, fewer CI runs, less granular history.

## 9. Execution order (30 items)

| # | Pri | Item | Depends on |
|---|---|---|---|
| 1 | P0 | R1 Merge #23-#31 (8.5) | - |
| 2 | P0 | A1 + A2 dead-code removal (not the V-dependent rows) | 1 |
| 3 | P0 | S1 token on every route, Origin/Sec-Fetch refusal, `compare_digest`, S8, S9 + test_security.py | 2 |
| 4 | P0 | S3 CI supply chain (ffmpeg pin + hash, actions by SHA, job permissions, add `dotnet test`) | 1 |
| 5 | P0 | S4 model revision pinning + sha256 verification | 2 |
| 6 | P0 | C1 migrations, stamps, pragmas, backups, user-data.json; S12 atomic settings | 2 |
| 7 | P0 | C2 indexer qhash / DST / rename reuse / tombstones; S10 reparse + OneDrive skip | 6 |
| 8 | P0 | C3 thumbnail keys, tw/th columns, disk-loaded thumbs, decode buckets | 6 |
| 9 | P0 | C4 src column, SQL paging, `/api/panels`, memoised info/search, incremental matrix | 6 |
| 10 | P0 | C5 lean startup + maintenance thread + meta stamp + GC | 6 |
| 11 | P0 | C6 steady Library client | 8, 9 |
| 12 | P0 | C8 viewer stepping | 8 |
| 13 | P0 | V1 `understand.py` + tests | 2 |
| 14 | P0 | V2 `/api/understand`, search integration, `taken` date | 6, 13 |
| 15 | P0 | V3 client parser swap + understood line | 14 |
| 16 | P0 | V4 streaming dictation engine + tests | 13 |
| 17 | P0 | V5 MicStream + SpeechInput rewrite, remove recorder/meter/windows engine | 16 |
| 18 | P0 | V6 voice hotkey + listening UX + errors + model download | 15, 17 |
| 19 | P0 | U1 rename/delete Spaces | 2 |
| 20 | P1 | S5 + S6 + S7 updater hardening, network-delete warning, upload limits | 3 |
| 21 | P1 | G1 backdrop + surfaces, remove Surface/Transparency/AMOLED | 11 |
| 22 | P1 | G2 control-layer glass, specular edges, shadows, springs | 21 |
| 23 | P1 | U4 + U6 accessibility, focus visuals, contrast, high contrast | 22 |
| 24 | P1 | U5 + U7 + U8 + U9 first run, Settings restructure, empty states, shortcut sheet, W7 start with Windows | 21 |
| 25 | P1 | W1 + W3 + W9 + W10 watchdog, background priority, model unload, log rotation (S11) | 10 |
| 26 | P1 | C7 warm start snapshot | 8, 11 |
| 27 | P1 | K1 engine rename, uninstall cleanup, winget manifest, CI install/upgrade/uninstall job | 4 |
| 28 | P1 | W5 folder watching + W6 long paths | 7 |
| 29 | P1 | K2 code signing + Authenticode check in updater; A3 dependency diet | 27 |
| 30 | P0 | R2 0.18 beta -> 0.19 RC -> 1.0: run 8.2 and 8.4, bump version, tag | all P0 (+ P1 you keep) |

## 10. Cut from scope to ship sooner
- Microsoft Store / MSIX (K4) and the CPU-bundled Store variant: after 1.0.
- Windows SpeechRecognizer voice engine and its setting: deleted, not maintained.
- "Surface" (frosted/liquid) choice, Transparency slider, AMOLED theme, Classic sidebar style, Similar-files placement: one look, one layout.
- Proposed names, rename planner, commit plan/undo endpoints, regroup backups, ideas/related, moment-clips, pick_folder, tag define/delete: deleted (A1). Undo-for-move (U11) only if time remains.
- ARM64 build, jump lists, Windows Search integration, taskbar progress, ReadyToRun if it does not measure faster.
- Package-size work beyond A3 (ffmpeg shared build) and the Store CPU-bundle: P2.
- Any new filter type or new page not listed here (AGENTS.md: no unrequested features).
