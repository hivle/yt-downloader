import os
import re
import threading
import uuid
from pathlib import Path
from tempfile import NamedTemporaryFile

from flask import Flask, jsonify, render_template, request, send_from_directory, abort
from yt_dlp import YoutubeDL
from yt_dlp.utils import DownloadError

APP_ROOT = Path(__file__).parent.resolve()
DOWNLOAD_DIR = APP_ROOT / "downloads"
COOKIE_DIR = APP_ROOT / ".cookies"
DOWNLOAD_DIR.mkdir(exist_ok=True)
COOKIE_DIR.mkdir(exist_ok=True)

SUPPORTED_BROWSERS = {"chrome", "firefox", "edge", "safari", "brave", "opera", "chromium", "vivaldi"}

app = Flask(__name__)

# job_id -> {status, progress, title, filename, error}
JOBS: dict[str, dict] = {}
JOBS_LOCK = threading.Lock()


def _update_job(job_id: str, **fields) -> None:
    with JOBS_LOCK:
        if job_id in JOBS:
            JOBS[job_id].update(fields)


def _build_format(quality: str, audio_only: bool) -> str:
    if audio_only:
        return "bestaudio/best"
    if quality == "best":
        return "bestvideo*+bestaudio/best"
    if quality.isdigit():
        return (
            f"bestvideo[height<={quality}]+bestaudio/best[height<={quality}]/best"
        )
    return "bestvideo*+bestaudio/best"


def _apply_auth(opts: dict, form, files) -> Path | None:
    """Attach cookie-based auth to yt-dlp options. Returns temp cookie file path if created."""
    browser = (form.get("browser") or "").strip().lower()
    if browser and browser in SUPPORTED_BROWSERS:
        profile = (form.get("browser_profile") or "").strip() or None
        opts["cookiesfrombrowser"] = (browser, profile, None, None) if profile else (browser,)
        return None

    cookies_file = files.get("cookies_file")
    if cookies_file and cookies_file.filename:
        dest = COOKIE_DIR / f"{uuid.uuid4().hex}.txt"
        cookies_file.save(dest)
        opts["cookiefile"] = str(dest)
        return dest
    return None


def _run_download(job_id: str, url: str, ydl_opts: dict, cleanup_cookie: Path | None) -> None:
    def hook(d):
        if d["status"] == "downloading":
            total = d.get("total_bytes") or d.get("total_bytes_estimate") or 0
            downloaded = d.get("downloaded_bytes") or 0
            pct = (downloaded / total * 100) if total else 0
            _update_job(
                job_id,
                status="downloading",
                progress=round(pct, 1),
                speed=d.get("speed"),
                eta=d.get("eta"),
            )
        elif d["status"] == "finished":
            _update_job(job_id, status="processing", progress=100.0)

    ydl_opts = dict(ydl_opts)
    ydl_opts["progress_hooks"] = [hook]

    try:
        with YoutubeDL(ydl_opts) as ydl:
            info = ydl.extract_info(url, download=True)
            if "entries" in info:  # playlist - take first entry for display
                info = info["entries"][0] if info["entries"] else {}
            filename = ydl.prepare_filename(info)
            # Account for post-processing extension changes
            base, _ = os.path.splitext(filename)
            final = filename
            for candidate in (filename, base + ".mp3", base + ".m4a", base + ".mp4", base + ".mkv", base + ".webm"):
                if os.path.exists(candidate):
                    final = candidate
                    break
            rel = os.path.relpath(final, DOWNLOAD_DIR)
            _update_job(
                job_id,
                status="done",
                progress=100.0,
                title=info.get("title"),
                filename=rel,
            )
    except DownloadError as e:
        _update_job(job_id, status="error", error=str(e))
    except Exception as e:  # noqa: BLE001
        _update_job(job_id, status="error", error=f"{type(e).__name__}: {e}")
    finally:
        if cleanup_cookie and cleanup_cookie.exists():
            try:
                cleanup_cookie.unlink()
            except OSError:
                pass


@app.route("/")
def index():
    return render_template("index.html", browsers=sorted(SUPPORTED_BROWSERS))


@app.post("/api/download")
def start_download():
    url = (request.form.get("url") or "").strip()
    if not url:
        return jsonify({"error": "URL is required"}), 400

    quality = request.form.get("quality", "best")
    audio_only = request.form.get("audio_only") == "on"

    job_id = uuid.uuid4().hex
    with JOBS_LOCK:
        JOBS[job_id] = {"status": "queued", "progress": 0.0, "url": url}

    outtmpl = str(DOWNLOAD_DIR / "%(title)s [%(id)s].%(ext)s")
    ydl_opts: dict = {
        "format": _build_format(quality, audio_only),
        "outtmpl": outtmpl,
        "noplaylist": True,
        "restrictfilenames": False,
        "windowsfilenames": True,
        "merge_output_format": "mp4" if not audio_only else None,
        "quiet": True,
        "no_warnings": True,
    }
    if audio_only:
        ydl_opts["postprocessors"] = [
            {"key": "FFmpegExtractAudio", "preferredcodec": "mp3", "preferredquality": "192"}
        ]

    try:
        cookie_path = _apply_auth(ydl_opts, request.form, request.files)
    except Exception as e:  # noqa: BLE001
        return jsonify({"error": f"Auth setup failed: {e}"}), 400

    thread = threading.Thread(
        target=_run_download, args=(job_id, url, ydl_opts, cookie_path), daemon=True
    )
    thread.start()
    return jsonify({"job_id": job_id})


@app.get("/api/status/<job_id>")
def job_status(job_id: str):
    with JOBS_LOCK:
        job = JOBS.get(job_id)
        if not job:
            return jsonify({"error": "unknown job"}), 404
        return jsonify(dict(job))


@app.get("/files/<path:filename>")
def download_file(filename: str):
    # Prevent path traversal
    safe = (DOWNLOAD_DIR / filename).resolve()
    if not str(safe).startswith(str(DOWNLOAD_DIR)):
        abort(404)
    if not safe.exists():
        abort(404)
    return send_from_directory(DOWNLOAD_DIR, filename, as_attachment=True)


if __name__ == "__main__":
    port = int(os.environ.get("PORT", "5000"))
    app.run(host="127.0.0.1", port=port, debug=False)
