"""Copy a video still into the Windows image clipboard from pywebview."""
import base64
import os

_PREFIX = "data:image/png;base64,"
_MAX_ENCODED = 64 * 1024 * 1024


def decode_png_frame(data_url):
    if not isinstance(data_url, str) or not data_url.startswith(_PREFIX):
        raise ValueError("Expected a PNG video frame")
    encoded = data_url[len(_PREFIX):]
    if not encoded or len(encoded) > _MAX_ENCODED:
        raise ValueError("Video frame is empty or too large")
    try:
        data = base64.b64decode(encoded, validate=True)
    except ValueError as exc:
        raise ValueError("Invalid video frame") from exc
    if not data.startswith(b"\x89PNG\r\n\x1a\n"):
        raise ValueError("Invalid PNG video frame")
    return data


def copy_png_frame(window, data_url):
    if os.name != "nt":
        raise RuntimeError("Desktop image clipboard is available on Windows")
    data = decode_png_frame(data_url)
    import clr
    clr.AddReference("System.Drawing")
    clr.AddReference("System.Windows.Forms")
    from System import Action, Array, Byte
    from System.IO import MemoryStream
    from System.Drawing import Bitmap
    from System.Windows.Forms import Clipboard

    def copy_on_ui_thread():
        stream = MemoryStream(Array[Byte](data))
        image = Bitmap(stream)
        try:
            Clipboard.SetImage(image)
        finally:
            image.Dispose()
            stream.Dispose()

    window.native.Invoke(Action(copy_on_ui_thread))
    return {"copied": True}
