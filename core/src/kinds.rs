//! What a file is, judged by its extension. Mirrors `app/extract.py` and adds the document, spreadsheet,
//! presentation and creative-asset families so they can be indexed later.

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord, serde::Serialize)]
#[serde(rename_all = "lowercase")]
pub enum Kind {
    Image,
    Video,
    Audio,
    Document,
    Presentation,
    Spreadsheet,
    Creative,
    Code,
    Other,
}

impl Kind {
    pub fn name(self) -> &'static str {
        match self {
            Kind::Image => "image",
            Kind::Video => "video",
            Kind::Audio => "audio",
            Kind::Document => "document",
            Kind::Presentation => "presentation",
            Kind::Spreadsheet => "spreadsheet",
            Kind::Creative => "creative",
            Kind::Code => "code",
            Kind::Other => "other",
        }
    }

    /// The three kinds the current Python engine indexes.
    pub fn is_media(self) -> bool {
        matches!(self, Kind::Image | Kind::Video | Kind::Audio)
    }
}

/// `ext` is lower-case without the dot.
pub fn kind_of(ext: &str) -> Kind {
    match ext {
        "jpg" | "jpeg" | "png" | "webp" | "bmp" | "gif" | "tif" | "tiff" => Kind::Image,
        "mp4" | "mov" | "mkv" | "webm" | "avi" | "m4v" | "wmv" | "flv" => Kind::Video,
        "mp3" | "wav" | "m4a" | "flac" | "ogg" | "aac" | "opus" | "wma" => Kind::Audio,
        "pdf" | "docx" | "doc" | "odt" | "rtf" | "txt" | "md" | "html" | "htm" | "srt" | "vtt" | "log" => Kind::Document,
        "pptx" | "ppt" | "odp" | "key" => Kind::Presentation,
        "xlsx" | "xls" | "ods" | "csv" | "tsv" => Kind::Spreadsheet,
        "psd" | "psb" | "ai" | "svg" | "eps" | "indd" | "fig" | "sketch" | "xd" | "blend" | "afdesign" => Kind::Creative,
        "py" | "js" | "ts" | "tsx" | "jsx" | "css" | "json" | "yaml" | "yml" | "toml" | "sql" | "sh" | "bat" | "ps1"
        | "c" | "cpp" | "h" | "cs" | "java" | "go" | "rs" | "php" | "rb" | "xml" | "ini" | "cfg" | "ipynb" => Kind::Code,
        _ => Kind::Other,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn families() {
        assert_eq!(kind_of("jpg"), Kind::Image);
        assert_eq!(kind_of("mkv"), Kind::Video);
        assert_eq!(kind_of("flac"), Kind::Audio);
        assert_eq!(kind_of("pdf"), Kind::Document);
        assert_eq!(kind_of("pptx"), Kind::Presentation);
        assert_eq!(kind_of("xlsx"), Kind::Spreadsheet);
        assert_eq!(kind_of("psd"), Kind::Creative);
        assert_eq!(kind_of("zip"), Kind::Other);
    }

    #[test]
    fn media_matches_the_python_engine() {
        assert!(kind_of("png").is_media());
        assert!(!kind_of("pdf").is_media());
    }
}
