# State ownership map

Scope: first source audit of the active WinUI/Python app. This is an inventory and investigation backlog, not a proof that every transition is correct. Legacy web UI and Rust implementation are not exhaustively audited.

## Decision

Keep independent state machines local to their owners. Preserve current wire status strings and saved data. Make transition boundaries explicit where races are demonstrated, with regression tests before expanding the refactor. Avoid a single global application enum.

## Inventory

| Machine | Owner | States / dimensions | Invariant to verify |
|---|---|---|---|
| App lifetime | App.xaml.cs, NativeHost.cs | visible, tray-hidden, quitting; primary/secondary instance | Only primary instance owns engine and hotkey |
| Engine lifetime | EngineHost.cs, AppRuntime.cs, filemind.py | unresolved, starting, ready, failed, stopping, stopped; owned/reused process | Never kill an unowned server; readiness is not process creation |
| Setup | SetupPage.xaml.cs, fetch.py, runtime.py | missing, download, verify, unpack, ready, error; CPU/GPU choice | Retry cannot overlap the previous attempt |
| Indexing | indexer.py | idle, scanning, loading, embedding, error; queued roots and stop requested | One worker owns shared progress and root queue |
| Indexed file | db.py, indexer.py | pending, done, error, skipped; changed/deleted | Only fully processed files are searchable |
| Search cache | indexer.py, embedder.py | dirty/clean, missing/loaded, cached/uncached query | Invalidation cannot be lost during refresh |
| Navigation | MainWindow.xaml.cs, LibraryPage.xaml.cs | current page, back/forward, cached/loaded/unloaded, pending request | Returning to a cached page restores valid cancellation ownership |
| Library requests | LibraryPage.xaml.cs | loading/idle, more/exhausted, query generation, offset, empty/error | Superseded requests cannot modify current results OR pagination |
| Selection | LibraryPage.xaml.cs, TileVm.cs | focused, hovered, checked, multiselect, range anchor | Selection and actionable files remain consistent across reload |
| Quick search | QuickSearchWindow.cs | hidden/shown, searching/results/empty/error, query and kind | Cache identity includes filters; stale failures cannot replace current status |
| Speech | SpeechInput.cs, QuickSearchWindow.cs, LibraryPage.xaml.cs | idle, recording, stopping, recognizing, failed; Windows/fallback | One recording owner; late results cannot target a departed page |
| Playback/viewer | ViewerPage.xaml.cs, AudioCard.cs | loading, playing, paused, seeking, ended, failed; fullscreen; clip bounds | Previous media callbacks cannot change the next item |
| Moments | moments.py, ViewerPage.xaml.cs | new, running, ready, error; per-file validity | Persisted running state recovers after process interruption |
| Transcription | transcribe.py | idle, downloading, transcribing, done, error; cached output | One job per file and serialized model access |
| Dictation model | transcribe.py | absent, downloading, ready, error | Concurrent warmup requests start only one download |
| Discover/spaces | DiscoverPage.xaml.cs, SpacesPage.xaml.cs, NewSpaceDialog.cs | loading, ready, empty, error; preview request generation | Detached page or superseded preview cannot publish results |
| Duplicates | DuplicatesPage.xaml.cs, duplicates.py | scanning, results, selected, confirming, deleting, failed | Destructive actions operate on confirmed selection |
| Export | export.py, ExportDialog.cs, MainWindow.xaml.cs | queued, running, cancelling, cancelled, done, error | Cancellation and output publication have one atomic winner |
| File changes | commit.py, main.py | proposed, previewed, applied, failed, undoable, undone | Filesystem changes and journal reconcile on failure |
| Preferences | SettingsPage.xaml.cs, Settings.cs | initializing, editing, persisted; shortcut recording | Initialization does not accidentally persist changes |
| Layout | TileLayout.cs, ThemeEngine.cs | dirty/measured, mode, theme | Layout invalidation follows size and item changes |

## Confirmed fix

Export previously released its lock after checking cancellation and before renaming the partial file. A cancellation could be acknowledged in that gap, then overwritten by done while still publishing the file. The cancellation check, final-file publication, and done transition now share the same lock. Existing API statuses remain unchanged. Two regression tests cover cancellation before publication and exclusion during publication.

## Priority investigations (not yet fixed)

1. Library LoadMoreAsync writes pagination fields after awaits before checking cancellation. Capture results locally, then check request identity before publishing the entire result snapshot.
2. Quick search remembers text before success and suppresses retries when old results remain. Include kind and successful request identity in deduplication; guard stale error handlers.
3. Indexer start reads and changes shared running/queue state without a start lock. Reproduce concurrent requests before changing queue ownership.
4. Dictation warmup checks and changes running without a lock. Exercise simultaneous callers.
5. Background launcher lifecycle test timed out in the baseline. Diagnose shutdown versus startup warmup separately; do not mask with a longer timeout.
6. Audit unload/reload, engine restart, and interrupted persistent jobs using deterministic controlled completions.

## Rules for future changes

- Name the state owner, event, guard, side effect, and terminal outcome.
- Treat cancellation as a request until the owner acknowledges it.
- Check request identity after every asynchronous boundary before mutating shared UI state.
- Keep orthogonal dimensions separate (visibility is not readiness; selection is not loading).
- Preserve public status values unless every consumer is migrated together.
- Test out-of-order completion, duplicate starts, retry after failure, and shutdown during work.
- Logging should identify the operation and transition without recording user file content.
