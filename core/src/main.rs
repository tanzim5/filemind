use filemind_core::scan::{scan, summarize};
use std::{path::PathBuf, time::Instant};

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    match args.first().map(String::as_str) {
        Some("scan") if args.len() >= 2 => {
            let all = args.iter().any(|a| a == "--all");
            let root = PathBuf::from(&args[1]);
            let t = Instant::now();
            let found = scan(&root, all);
            let secs = t.elapsed().as_secs_f64();
            let s = summarize(&found);
            println!("{}", serde_json::json!({ "seconds": secs, "files": s.files, "bytes": s.bytes, "by_kind": s.by_kind }));
        }
        _ => eprintln!("usage: filemind-core scan <folder> [--all]"),
    }
}
