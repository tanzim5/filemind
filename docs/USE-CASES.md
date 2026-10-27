# File Mind: why install it, and what for

Source for model facts: Google Cloud docs and DeepMind page for Gemini Embedding 2 (checked 2026-10-28).

## What the model gives us

One embedding space for text, images, video, audio and documents (3,072 dimensions, adjustable down to 128 with Matryoshka). A query in any modality finds matches in any other:

| Input | Supported | Limits |
|---|---|---|
| Text, PDF | text/plain, PDF | 8,192 tokens, first 6 PDF pages |
| Image | png, jpeg, webp, bmp, heic, heif, avif | up to 6 per request |
| Video | mp4, mpeg | 80 s with audio, 120 s without |
| Audio | mp3, wav | 180 s |

Also: 100+ languages, task instructions, and it can read the audio track of a video and interleave it with frames. DeepMind's own showcase (Paramount) is exactly this app's idea: a photo or B-roll clip as the search input to retrieve matching video; text queries that find untranscribed expressions.

## The one-line purpose

**Your drive remembers what is in your files, so you stop remembering where you put them.**
Search by meaning, by a picture, by a sound or by your voice, then drag the result straight into the app you are working in.

## Who installs it, and the moment they open it

- **Videographer / editor.** "I need that shot of the street at dusk, handheld." Drop a still from the grade reference onto the window and get matching clips from months of rushes. Drag the clip into Premiere or Resolve.
- **Photographer.** "Find the portraits with warm window light" or drop a client's mood-board image to find your frames that match. Cull duplicates, group a shoot into a space.
- **Designer / motion artist.** Palette search (click a colour, find files that use it), "more like this" to rebuild a lost asset folder.
- **Sound designer / musician.** Hum or say a sound, or drop a reference track, and find samples that sound like it.
- **Student / researcher / lawyer.** PDFs and notes searched by meaning, in any language. (Everlaw's testimonial is this case.)
- **Anyone with a Downloads folder.** "that receipt screenshot from March".

## Built now

- Search by text, voice (mic button in the search bar, the sound is the query), picture, video clip or audio file.
- Drag any file out of a tile, the viewer stage, the title or a "more like this" card into Explorer, Premiere, Photoshop, CapCut, Slack, a browser.
- Drop any file onto the Library to search with it; drop a text file or selected text to search by its words; drop a folder to add it.
- Spaces (collections), Discover themes and questions, colour search, duplicates.
- Everything stays local: nothing leaves the PC.

## Ideas worth building next (ranked by value over effort)

1. **Quick search hotkey (Win+Alt+Space).** A small floating bar anywhere in Windows, voice or typing, results you can drag out directly. This is what makes it part of a workflow instead of an app you open.
2. **Explorer right-click "Find similar in File Mind"** and "Add to space". Fast: a shell verb opening the app with that file as the query.
3. **Find a moment inside video.** Type "she laughs" and land on the second it happens (the engine already has moments; surface it as a timeline with thumbnails and drag out an in/out range).
4. **Rough-cut basket.** Collect clips and stills from search results into a tray, reorder, and export an EDL / FCPXML / folder with the files copied, ready to import into the editor.
5. **Reference board.** Drop 5 images, get a live board that keeps pulling matching files from your library as new files arrive (watch folder).
6. **Reverse "where did I use this?".** Drop an asset, see projects and exports that contain something visually similar.
7. **Auto-organise suggestions.** "These 214 files look like one shoot, make a space?" with one-click move, never deleting.
8. **Clean-up assistant.** Near-duplicates, blurry frames, black frames, screenshots of screenshots, grouped, reviewed, sent to Recycle Bin.
9. **Spoken notes search.** Meeting recordings and voice memos found by what was said, across languages.
10. **Ask your library.** A question in plain language answered with the three best files and the exact page, second or region.
11. **Share a result set.** Export the visible results as a folder or a zip with a contact sheet.
12. **Windows integration polish.** Jump List of recent spaces, Snap/Copilot-friendly sizing, Windows Share target, taskbar progress while indexing.

## Honest limits to design around

- Video is embedded as short clips (80–120 s): long videos need to be cut into segments and each segment indexed. Audio likewise (180 s).
- PDFs: only the first 6 pages per request; longer documents need page batching.
- Voice: it is the *sound* that is embedded, not a transcript, so it is great for "describe it" queries and weaker for exact names, which still work best typed.
