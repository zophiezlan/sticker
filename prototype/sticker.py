"""
sticker.py — open images as floating, background-removed desktop stickers.

Usage:
    python sticker.py img1.jpg [img2.png ...]
    python sticker.py --no-matte img.png     # skip background removal
    python sticker.py --restore              # reopen last session's stickers
    python sticker.py --model birefnet-general img.jpg  # pick matting model

If an instance is already running, new launches hand their files to it and
exit immediately — no second model load, stickers appear near-instantly.

Controls (cursor must be over the SUBJECT — transparent areas click through):
    Drag                 move
    Scroll / + and -     resize  (Ctrl+scroll = fine steps)
    Shift+scroll         opacity
    R / Shift+R          rotate 15 degrees
    F                    flip horizontal
    D / double-click     toggle original <-> cutout
    S                    save cutout as PNG
    Esc / middle-click   close
    Right-click          full menu

Requires: pip install "rembg[cpu]" PyQt6 pillow onnxruntime
First run downloads the matting model (~180 MB) to rembg's cache (~/.u2net,
a historical folder name — all models go there).
"""

import sys
import os
import json
import hashlib
from pathlib import Path

from PyQt6.QtCore import Qt, QPoint
from PyQt6.QtGui import QPixmap, QAction, QGuiApplication, QTransform
from PyQt6.QtWidgets import QApplication, QWidget, QLabel, QMenu, QFileDialog
from PyQt6.QtNetwork import QLocalServer, QLocalSocket

CACHE_DIR = Path.home() / ".sticker_cache"
STATE_FILE = CACHE_DIR / "session.json"
SOCKET_NAME = "sticker-app-single-instance"
DEFAULT_MODEL = os.environ.get("STICKER_MODEL", "isnet-general-use")
MIN_SIZE = 64
MAX_SIZE = 4096

MODEL = DEFAULT_MODEL
_sessions: dict = {}


def _model_session(model: str):
    if model not in _sessions:
        from rembg import new_session
        _sessions[model] = new_session(model)
    return _sessions[model]


def matted_path(src: Path, model: str) -> Path:
    """Cache key from path + mtime + model, so edits and model swaps re-matte."""
    key = f"{src.resolve()}|{src.stat().st_mtime_ns}|{model}"
    h = hashlib.sha1(key.encode()).hexdigest()[:16]
    return CACHE_DIR / f"{src.stem}.{h}.png"


def remove_background(src: Path, model: str, force: bool = False) -> Path:
    out = matted_path(src, model)
    if out.exists() and not force:
        return out
    from rembg import remove  # lazy: model load is slow
    from PIL import Image
    CACHE_DIR.mkdir(exist_ok=True)
    result = remove(Image.open(src), session=_model_session(model))
    result.save(out)
    return out


class StickerWindow(QWidget):
    instances: list["StickerWindow"] = []
    _quitting = False

    def __init__(self, image_path: Path, source_path: Path,
                 no_matte: bool = False, state: dict | None = None):
        super().__init__()
        self.source_path = source_path
        self.no_matte = no_matte
        self.showing_original = False
        self._matted = QPixmap(str(image_path))
        if self._matted.isNull():
            raise ValueError(f"Could not load image: {image_path}")
        self._original: QPixmap | None = None
        self._base = self._matted

        self.rotation = 0
        self.flipped = False
        self.opacity = 1.0
        self._drag_offset = None

        self.setWindowFlags(
            Qt.WindowType.FramelessWindowHint
            | Qt.WindowType.WindowStaysOnTopHint
            | Qt.WindowType.Tool  # no taskbar entry
        )
        self.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground)

        self.label = QLabel(self)
        self.label.setScaledContents(True)

        screen = QGuiApplication.primaryScreen().availableGeometry()
        self.scale = min(1.0, screen.width() * 0.4 / self._base.width(),
                         screen.height() * 0.4 / self._base.height())

        if state:
            self.scale = state.get("scale", self.scale)
            self.rotation = state.get("rotation", 0)
            self.flipped = state.get("flipped", False)
            self.opacity = state.get("opacity", 1.0)

        self._render()
        self.setWindowOpacity(self.opacity)

        if state and "x" in state:
            self.move(state["x"], state["y"])
            if not state.get("on_top", True):
                self.setWindowFlags(self.windowFlags()
                                    & ~Qt.WindowType.WindowStaysOnTopHint)
        else:
            offset = 30 * (len(StickerWindow.instances) % 10)
            self.move(screen.center() - QPoint(self.width() // 2 - offset,
                                               self.height() // 2 - offset))

        StickerWindow.instances.append(self)
        self.show()
        self.activateWindow()

    # --- rendering ---
    def _render(self):
        t = QTransform()
        if self.flipped:
            t.scale(-1, 1)
        if self.rotation:
            t.rotate(self.rotation)
        pm = (self._base.transformed(t, Qt.TransformationMode.SmoothTransformation)
              if not t.isIdentity() else self._base)
        w = max(MIN_SIZE, min(MAX_SIZE, int(pm.width() * self.scale)))
        self.scale = w / pm.width()  # keep scale consistent after clamping
        scaled = pm.scaled(w, max(1, int(w * pm.height() / pm.width())),
                           Qt.AspectRatioMode.KeepAspectRatio,
                           Qt.TransformationMode.SmoothTransformation)
        self.label.setPixmap(scaled)
        self.label.resize(scaled.size())
        self.resize(scaled.size())

    def _update(self, keep_center: bool = True):
        center = self.geometry().center()
        self._render()
        if keep_center:
            self.move(center - QPoint(self.width() // 2, self.height() // 2))
        StickerWindow.save_session()

    # --- session persistence ---
    def state(self) -> dict:
        return {
            "source": str(self.source_path),
            "no_matte": self.no_matte,
            "x": self.x(), "y": self.y(),
            "scale": self.scale,
            "rotation": self.rotation,
            "flipped": self.flipped,
            "opacity": self.opacity,
            "on_top": bool(self.windowFlags() & Qt.WindowType.WindowStaysOnTopHint),
        }

    @classmethod
    def save_session(cls):
        if cls._quitting:
            return
        try:
            CACHE_DIR.mkdir(exist_ok=True)
            STATE_FILE.write_text(json.dumps(
                [w.state() for w in cls.instances], indent=2))
        except OSError as ex:
            print(f"Could not save session: {ex}", file=sys.stderr)

    @classmethod
    def close_all(cls):
        cls.save_session()       # keep session so --restore brings them back
        cls._quitting = True
        QApplication.quit()

    # --- drag ---
    def mousePressEvent(self, e):
        if e.button() == Qt.MouseButton.LeftButton:
            self._drag_offset = e.globalPosition().toPoint() - self.pos()
        elif e.button() == Qt.MouseButton.MiddleButton:
            self.close()

    def mouseMoveEvent(self, e):
        if self._drag_offset is not None:
            self.move(e.globalPosition().toPoint() - self._drag_offset)

    def mouseReleaseEvent(self, e):
        self._drag_offset = None
        StickerWindow.save_session()

    def mouseDoubleClickEvent(self, e):
        if e.button() == Qt.MouseButton.LeftButton:
            self._toggle_original()

    # --- wheel: resize / opacity ---
    def wheelEvent(self, e):
        delta = e.angleDelta().y() or e.angleDelta().x()
        if delta == 0:
            return
        up = delta > 0
        if e.modifiers() & Qt.KeyboardModifier.ShiftModifier:
            self._set_opacity(self.opacity + (0.08 if up else -0.08))
        else:
            fine = e.modifiers() & Qt.KeyboardModifier.ControlModifier
            factor = 1.05 if fine else 1.15
            self._resize_by(factor if up else 1 / factor)
        e.accept()

    def _resize_by(self, factor: float):
        self.scale *= factor
        self._update()

    def _set_opacity(self, value: float):
        self.opacity = max(0.15, min(1.0, value))
        self.setWindowOpacity(self.opacity)
        StickerWindow.save_session()

    # --- keys ---
    def keyPressEvent(self, e):
        key, mods = e.key(), e.modifiers()
        shift = bool(mods & Qt.KeyboardModifier.ShiftModifier)
        if key == Qt.Key.Key_Escape:
            self.close()
        elif key in (Qt.Key.Key_Plus, Qt.Key.Key_Equal):
            self._resize_by(1.15)
        elif key in (Qt.Key.Key_Minus, Qt.Key.Key_Underscore):
            self._resize_by(1 / 1.15)
        elif key == Qt.Key.Key_R:
            self._rotate(-15 if shift else 15)
        elif key == Qt.Key.Key_F:
            self._flip()
        elif key == Qt.Key.Key_D:
            self._toggle_original()
        elif key == Qt.Key.Key_S:
            self._save_png()

    # --- actions ---
    def _rotate(self, degrees: int):
        self.rotation = (self.rotation + degrees) % 360
        self._update()

    def _flip(self):
        self.flipped = not self.flipped
        self._update()

    def _toggle_original(self):
        if self.no_matte:
            return
        self.showing_original = not self.showing_original
        if self.showing_original and self._original is None:
            self._original = QPixmap(str(self.source_path))
        self._base = self._original if self.showing_original else self._matted
        self._update()

    def _save_png(self):
        default = self.source_path.with_name(self.source_path.stem + "-cutout.png")
        dest, _ = QFileDialog.getSaveFileName(
            self, "Save cutout", str(default), "PNG image (*.png)")
        if not dest:
            return
        t = QTransform()
        if self.flipped:
            t.scale(-1, 1)
        if self.rotation:
            t.rotate(self.rotation)
        pm = (self._matted.transformed(t, Qt.TransformationMode.SmoothTransformation)
              if not t.isIdentity() else self._matted)
        pm.save(dest, "PNG")

    def _rematte(self):
        try:
            out = remove_background(self.source_path, MODEL, force=True)
        except Exception as ex:
            print(f"Re-matte failed: {ex}", file=sys.stderr)
            return
        self._matted = QPixmap(str(out))
        if not self.showing_original:
            self._base = self._matted
        self._update()

    def _toggle_on_top(self, checked: bool):
        flags = self.windowFlags()
        if checked:
            flags |= Qt.WindowType.WindowStaysOnTopHint
        else:
            flags &= ~Qt.WindowType.WindowStaysOnTopHint
        self.setWindowFlags(flags)
        self.show()  # re-show required after flag change
        StickerWindow.save_session()

    # --- context menu ---
    def contextMenuEvent(self, e):
        menu = QMenu(self)
        on_top = QAction("Always on top", menu, checkable=True)
        on_top.setChecked(bool(self.windowFlags()
                               & Qt.WindowType.WindowStaysOnTopHint))
        on_top.triggered.connect(self._toggle_on_top)
        menu.addAction(on_top)
        if not self.no_matte:
            menu.addAction(
                "Show original" if not self.showing_original else "Show cutout",
                self._toggle_original)
        menu.addAction("Rotate 90° right\tR", lambda: self._rotate(90))
        menu.addAction("Rotate 90° left\tShift+R", lambda: self._rotate(-90))
        menu.addAction("Flip horizontal\tF", self._flip)
        menu.addAction("Save cutout as PNG...\tS", self._save_png)
        menu.addAction("Re-run background removal", self._rematte)
        menu.addSeparator()
        menu.addAction("Close\tEsc", self.close)
        menu.addAction("Close all stickers", StickerWindow.close_all)
        menu.exec(e.globalPos())

    def closeEvent(self, e):
        if not StickerWindow._quitting:
            StickerWindow.instances.remove(self)
            StickerWindow.save_session()
            if not StickerWindow.instances:
                StickerWindow._quitting = True
                QApplication.quit()
        e.accept()


# --- opening / single instance ---

def open_one(src: Path, no_matte: bool, state: dict | None = None) -> bool:
    if not src.exists():
        print(f"Not found: {src}", file=sys.stderr)
        return False
    try:
        img = src if no_matte else remove_background(src, MODEL)
        StickerWindow(img, source_path=src, no_matte=no_matte, state=state)
        return True
    except Exception as ex:
        print(f"Failed to open {src}: {ex}", file=sys.stderr)
        return False


def restore_session() -> int:
    if not STATE_FILE.exists():
        print("No saved session.", file=sys.stderr)
        return 0
    try:
        entries = json.loads(STATE_FILE.read_text())
    except (OSError, json.JSONDecodeError) as ex:
        print(f"Could not read session: {ex}", file=sys.stderr)
        return 0
    return sum(open_one(Path(s["source"]), s.get("no_matte", False), state=s)
               for s in entries)


def handle_payload(payload: dict):
    if payload.get("restore"):
        restore_session()
    for p in payload.get("paths", []):
        open_one(Path(p), payload.get("no_matte", False))


def try_handoff(payload: dict) -> bool:
    sock = QLocalSocket()
    sock.connectToServer(SOCKET_NAME)
    if not sock.waitForConnected(300):
        return False
    sock.write(json.dumps(payload).encode() + b"\n")
    sock.waitForBytesWritten(1000)
    sock.disconnectFromServer()
    return True


class SingleInstanceServer(QLocalServer):
    def __init__(self):
        super().__init__()
        QLocalServer.removeServer(SOCKET_NAME)  # clear stale socket
        self.listen(SOCKET_NAME)
        self.newConnection.connect(self._on_connection)

    def _on_connection(self):
        sock = self.nextPendingConnection()
        sock.readyRead.connect(lambda s=sock: self._read(s))

    def _read(self, sock):
        try:
            handle_payload(json.loads(bytes(sock.readAll()).decode().strip()))
        except Exception as ex:
            print(f"Bad handoff message: {ex}", file=sys.stderr)


def main():
    global MODEL
    argv = sys.argv[1:]
    no_matte = "--no-matte" in argv
    restore = "--restore" in argv
    if "--model" in argv:
        i = argv.index("--model")
        MODEL = argv[i + 1]
        del argv[i:i + 2]
    paths = [a for a in argv if not a.startswith("--")]

    if not paths and not restore:
        print(__doc__)
        sys.exit(1)

    app = QApplication(sys.argv)
    app.setQuitOnLastWindowClosed(False)  # quit handled in closeEvent/close_all

    payload = {"paths": paths, "no_matte": no_matte, "restore": restore}
    if try_handoff(payload):
        sys.exit(0)  # running instance took over

    server = SingleInstanceServer()  # noqa: F841 — must stay alive

    opened = restore_session() if restore else 0
    opened += sum(open_one(Path(p), no_matte) for p in paths)
    if not opened:
        sys.exit(1)
    sys.exit(app.exec())


if __name__ == "__main__":
    main()
