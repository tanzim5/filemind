# File Mind privacy policy

*Last updated: 2026-11-23. (Draft: read it through, add your contact address, and host it at a public URL before submitting to the Microsoft Store.)*

File Mind is a program that runs on your own PC. It does not have an account system, advertising or analytics.

## What stays on your PC
- The folders you add are read to build a search index: file names, sizes, dates, colours, small preview pictures, and
  numbers (vectors) that describe what each file looks like or says.
- The index, previews and settings are stored on your PC (under `%LOCALAPPDATA%\File Mind`, or in the folder you can see in
  Settings > Storage). Nothing in them is sent to us or to anyone else.
- Your files are never copied, uploaded or changed by indexing. File Mind only changes a file when you ask it to move, copy
  or delete it, and deleted files go to the Recycle Bin.
- Searching, the viewer, Discover and Duplicates all run on your PC.

## What File Mind downloads
The first time it runs, File Mind downloads the programs and models it needs:
- **PyTorch** from `download.pytorch.org` (the engine's AI library),
- the **search model** and the **speech models** from `huggingface.co`.

These downloads send ordinary web requests (your IP address, the file requested). No information about you or your files is
included. After that, File Mind works offline.

## Updates
If "Check for updates" is on (Settings > About), File Mind asks `api.github.com` once a day for the newest release. Only a
standard web request is sent. Turn it off at any time.

## Microphone
Voice search uses your microphone only while you press the microphone button. By default the speech is turned into text on
your PC. If you choose the Windows speech option in Settings, Windows' own speech service handles it under Microsoft's
privacy terms.

## Your control
Settings > Storage shows everything File Mind keeps and lets you delete the safe parts. Forgetting a folder removes its part
of the index. Deleting the File Mind data folder removes everything. Uninstalling removes the program.

## Contact
*(add your email or a link to the GitHub issues page here)*
