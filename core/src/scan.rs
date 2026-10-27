//! Parallel folder scan. Same rules as `indexer.scan` in Python: skip build/system folders, partial downloads and
//! stub files, never follow symlinks or junctions (so a loop cannot trap the walk), and ignore unreadable entries.

use crate::kinds::{kind_of, Kind};
use std::collections::BTreeMap;
use std::path::{Path, PathBuf};
use std::time::SystemTime;

const SKIP_DIRS: &[&str] = &["node_modules", ".git", "__pycache__", "$recycle.bin", "system volume information", ".venv", "venv"];
const SKIP_FILES: &[&str] = &["desktop.ini", "thumbs.db", ".ds_store"];
const SKIP_EXT: &[&str] = &["crdownload", "part", "tmp", "partial"];
pub const MIN_BYTES: u64 = 4096;

#[derive(Debug, Clone, serde::Serialize)]
pub struct Found {
    pub path: PathBuf,
    pub kind: Kind,
    pub size: u64,
    /// Seconds since the Unix epoch, as the Python engine stores it.
    pub mtime: f64,
}

#[derive(Debug, Default, serde::Serialize)]
pub struct Summary {
    pub files: usize,
    pub bytes: u64,
    pub by_kind: BTreeMap<&'static str, usize>,
}

/// `all_kinds = false` keeps only image/video/audio, which is what the Python engine indexes today.
pub fn scan(root: &Path, all_kinds: bool) -> Vec<Found> {
    let walker = jwalk::WalkDir::new(root)
        .follow_links(false)
        .skip_hidden(false)
        .process_read_dir(|_, _, _, children| {
            children.retain(|c| match c {
                Ok(e) => !(e.file_type().is_dir() && SKIP_DIRS.contains(&e.file_name().to_string_lossy().to_lowercase().as_str())),
                Err(_) => false,
            });
        });

    let mut out = Vec::new();
    for entry in walker.into_iter().flatten() {
        if !entry.file_type().is_file() {
            continue;
        }
        let name = entry.file_name().to_string_lossy().to_lowercase();
        if SKIP_FILES.contains(&name.as_str()) {
            continue;
        }
        let ext = name.rsplit_once('.').map(|(_, e)| e).unwrap_or("");
        if SKIP_EXT.contains(&ext) {
            continue;
        }
        let Ok(meta) = entry.metadata() else { continue };
        if meta.len() < MIN_BYTES {
            continue;
        }
        let kind = kind_of(ext);
        if !all_kinds && !kind.is_media() {
            continue;
        }
        let mtime = meta
            .modified()
            .ok()
            .and_then(|t| t.duration_since(SystemTime::UNIX_EPOCH).ok())
            .map_or(0.0, |d| d.as_secs_f64());
        out.push(Found { path: entry.path(), kind, size: meta.len(), mtime });
    }
    out
}

pub fn summarize(found: &[Found]) -> Summary {
    let mut s = Summary::default();
    for f in found {
        s.files += 1;
        s.bytes += f.size;
        *s.by_kind.entry(f.kind.name()).or_default() += 1;
    }
    s
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;

    fn put(dir: &Path, name: &str, len: usize) {
        fs::create_dir_all(dir).unwrap();
        fs::write(dir.join(name), vec![0u8; len]).unwrap();
    }

    #[test]
    fn applies_the_same_rules_as_python() {
        let tmp = std::env::temp_dir().join(format!("fm_scan_{}", std::process::id()));
        let _ = fs::remove_dir_all(&tmp);
        put(&tmp, "a.jpg", 5000);
        put(&tmp, "stub.jpg", 100); // too small
        put(&tmp, "movie.mp4.crdownload", 9000); // unfinished
        put(&tmp, "report.pdf", 9000);
        put(&tmp.join("node_modules"), "x.png", 9000); // skipped folder
        put(&tmp.join("sub").join("deep"), "song.flac", 9000);

        let media = scan(&tmp, false);
        let mut names: Vec<_> = media.iter().map(|f| f.path.file_name().unwrap().to_string_lossy().into_owned()).collect();
        names.sort();
        assert_eq!(names, ["a.jpg", "song.flac"]);

        let all = scan(&tmp, true);
        assert_eq!(all.len(), 3); // plus report.pdf
        let _ = fs::remove_dir_all(&tmp);
    }
}
