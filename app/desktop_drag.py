"""Export indexed originals through the Windows shell file-drop format."""
import os
import threading
from pathlib import Path

from . import db

_drag_lock = threading.Lock()


def resolve_files(file_ids):
    if not isinstance(file_ids, list) or not 1 <= len(file_ids) <= 500:
        raise ValueError("Choose between 1 and 500 files to drag")
    paths = []
    for fid in dict.fromkeys(file_ids):
        if type(fid) is not int:
            raise ValueError("Invalid file ID")
        row = db.conn().execute("SELECT path FROM files WHERE id=?", (fid,)).fetchone()
        if row is None or not Path(row["path"]).is_file():
            raise FileNotFoundError("A selected file is missing. Refresh the library and try again.")
        paths.append(str(Path(row["path"]).resolve()))
    return paths


def drag_files(window, file_ids):
    if os.name != "nt":
        raise RuntimeError("Native file dragging is currently available in the Windows desktop app")
    paths = resolve_files(file_ids)
    if not _drag_lock.acquire(blocking=False):
        return {"effect": "busy"}
    try:
        # pywebview calls Python on a worker. OLE dragging must run on the window's STA thread.
        from System import Action
        from System.Collections.Specialized import StringCollection
        from System.Windows.Forms import Control, DataObject, DragDropEffects, MouseButtons

        result = {"effect": "cancelled"}

        def start():
            if (Control.MouseButtons & MouseButtons.Left) != MouseButtons.Left:
                return  # The user released the mouse before the bridge completed.
            files = StringCollection()
            for path in paths:
                files.Add(path)
            data = DataObject()
            data.SetFileDropList(files)
            # Copy-only: importing into an editor or Explorer must not move the original.
            effect = window.native.DoDragDrop(data, DragDropEffects.Copy)
            result["effect"] = str(effect)

        window.native.Invoke(Action(start))
        return result
    finally:
        _drag_lock.release()
