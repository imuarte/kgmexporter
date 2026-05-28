"""
Tiny proxy that forwards kgmexporter .kgmap uploads to archive.org S3.

Client POSTs to:
  POST /upload?filename=<name>[&skip_check=1][&force=1]   [body = raw .kgmap bytes]
  Headers:
    X-Auth: <shared token>                 (required if PROXY_TOKEN is set)

Returns:
  200 {"status":"uploaded", ...}          - archive.org confirmed the file is listed
  200 {"status":"already_exists", ...}    - file was already on the item
  202 {"status":"pending", ...}           - S3 PUT was accepted but the file is not
                                            yet visible in the item metadata; client
                                            should treat as "uploaded, indexing"
  502 {"status":"failed", ...}            - PUT failed and the file did not appear
"""
import json
import logging
import os
import time
from logging.handlers import RotatingFileHandler

import requests
from flask import Flask, abort, jsonify, request

ACCESS = os.environ["IA_ACCESS"]
SECRET = os.environ["IA_SECRET"]
ITEM = os.environ.get("IA_ITEM", "kogama-maps-kgmexporter")
SHARED_TOKEN = os.environ.get("PROXY_TOKEN", "")
PUT_ATTEMPTS = int(os.environ.get("IA_PUT_ATTEMPTS", "3"))
PUT_READ_TIMEOUT = int(os.environ.get("IA_PUT_READ_TIMEOUT", "600"))
VERIFY_DELAYS = (2, 5, 15)
UPLOAD_LOG_PATH = os.environ.get("KGMEXPORTER_UPLOAD_LOG",
                                 "/var/log/kgmexporter-proxy/uploads.jsonl")

app = Flask(__name__)
# 1 GB cap so a runaway client cannot fill the VM disk, but well above any real .kgmap.
app.config["MAX_CONTENT_LENGTH"] = 1024 * 1024 * 1024

# JSONL log of every attempt so we can reconcile "client thinks it uploaded N"
# against "archive.org actually has N" after the fact. journald is not enough.
_upload_logger = logging.getLogger("kgmexporter.uploads")
_upload_logger.setLevel(logging.INFO)
_upload_logger.propagate = False
try:
    os.makedirs(os.path.dirname(UPLOAD_LOG_PATH), exist_ok=True)
    _handler = RotatingFileHandler(UPLOAD_LOG_PATH,
                                   maxBytes=20 * 1024 * 1024,
                                   backupCount=4)
    _handler.setFormatter(logging.Formatter("%(message)s"))
    _upload_logger.addHandler(_handler)
except OSError as ex:
    app.logger.warning("upload log disabled (%s): %s", UPLOAD_LOG_PATH, ex)


def log_upload(event):
    event["ts"] = time.time()
    try:
        _upload_logger.info(json.dumps(event, separators=(",", ":")))
    except (TypeError, ValueError):
        pass


@app.route("/health")
def health():
    return {"ok": True, "item": ITEM}


def archive_file_exists(filename, timeout=15):
    """Authoritative existence check via the archive.org metadata API.

    The old version hit /download/ with allow_redirects=True, which could land
    on a 200 item page even when the file did not exist, producing false
    "already uploaded" and false "verified" results. The metadata API returns
    a JSON descriptor for the file or 404 - no redirects involved.
    """
    try:
        r = requests.get(
            f"https://archive.org/metadata/{ITEM}/files/{filename}",
            timeout=timeout, allow_redirects=False)
    except requests.RequestException as ex:
        app.logger.warning("archive metadata check failed for %s: %s", filename, ex)
        return False

    if r.status_code != 200 or not r.content:
        return False
    try:
        data = r.json()
    except ValueError:
        return False
    # archive.org returns {} for unknown files at this endpoint, and an object
    # with at least 'name'/'size'/'result' for real files.
    return bool(data.get("name") or data.get("size") or data.get("result"))


def wait_for_archive_file(filename):
    for delay in VERIFY_DELAYS:
        if archive_file_exists(filename):
            return True
        time.sleep(delay)
    return archive_file_exists(filename)


@app.route("/upload", methods=["POST"])
def upload():
    if SHARED_TOKEN and request.headers.get("X-Auth") != SHARED_TOKEN:
        abort(401)

    filename = request.args.get("filename", "").strip()
    if not filename or "/" in filename or "\\" in filename:
        abort(400, "filename query param required and must not contain slashes")

    force = request.args.get("force") in ("1", "true", "yes")
    skip_check = request.args.get("skip_check") in ("1", "true", "yes")

    existed_before = False if (force or skip_check) else archive_file_exists(filename)
    if not force and existed_before:
        log_upload({"event": "skip", "filename": filename, "reason": "already_exists"})
        return jsonify(status="already_exists",
                       item_url=f"https://archive.org/details/{ITEM}"), 200

    content_length = request.content_length
    headers = {
        "authorization": f"LOW {ACCESS}:{SECRET}",
        "x-archive-auto-make-bucket": "1",
        "x-archive-queue-derive": "0",
        "content-type": "application/octet-stream",
    }
    if content_length is not None:
        headers["content-length"] = str(content_length)

    # Stream the request body straight into the S3 PUT. The old code did
    # request.get_data() which materialised the full file in RAM before sending
    # to archive.org - that was roughly doubling wall-clock upload time. We can
    # only do this on the first attempt (the stream is one-shot); retries fall
    # back to the post-failure verifier instead of re-reading the body.
    url = f"https://s3.us.archive.org/{ITEM}/{filename}"
    last_error = None
    last_status = None

    for attempt in range(1, PUT_ATTEMPTS + 1):
        try:
            if attempt == 1:
                data = request.stream
            else:
                # Body already consumed - cannot resend. Use a verify-only pass.
                data = None

            if data is not None:
                resp = requests.put(url, headers=headers, data=data,
                                    allow_redirects=True,
                                    timeout=(30, PUT_READ_TIMEOUT))
                last_status = resp.status_code
                response_body = (resp.text or "")[:300]
                if resp.ok:
                    # Even a 2xx from S3 does not guarantee the file is listed
                    # on the item yet. Confirm with the metadata API before
                    # telling the client "uploaded".
                    if wait_for_archive_file(filename):
                        log_upload({"event": "uploaded", "filename": filename,
                                    "size": content_length, "attempt": attempt,
                                    "http": resp.status_code})
                        return jsonify(status="uploaded",
                                       item_url=f"https://archive.org/details/{ITEM}"), 200
                    log_upload({"event": "pending", "filename": filename,
                                "size": content_length, "attempt": attempt,
                                "http": resp.status_code,
                                "note": "S3 accepted but file not visible in metadata yet"})
                    return jsonify(status="pending",
                                   item_url=f"https://archive.org/details/{ITEM}",
                                   note="archive.org accepted the upload but has not "
                                        "indexed it yet; check the item page in a few "
                                        "minutes"), 202

                app.logger.warning("archive PUT failed for %s attempt %s/%s: %s %s",
                                   filename, attempt, PUT_ATTEMPTS,
                                   resp.status_code, response_body)
                log_upload({"event": "put_failed", "filename": filename,
                            "attempt": attempt, "http": resp.status_code,
                            "body": response_body})

                if resp.status_code not in (408, 409, 429, 500, 502, 503, 504):
                    # Fatal client/auth error - no point retrying.
                    return jsonify(status="failed", code=resp.status_code,
                                   body=response_body), 502
                last_error = f"{resp.status_code}: {response_body}"
        except requests.RequestException as ex:
            last_error = str(ex)
            app.logger.warning("archive PUT exception for %s attempt %s/%s: %s",
                               filename, attempt, PUT_ATTEMPTS, ex)
            log_upload({"event": "put_exception", "filename": filename,
                        "attempt": attempt, "error": str(ex)[:300]})

        # archive.org occasionally accepts the bytes but closes the connection
        # before returning a usable response. Verify against the metadata API.
        if not existed_before and wait_for_archive_file(filename):
            log_upload({"event": "uploaded_after_fail", "filename": filename,
                        "attempt": attempt})
            return jsonify(status="uploaded", verified=True,
                           item_url=f"https://archive.org/details/{ITEM}"), 200

        if attempt < PUT_ATTEMPTS:
            time.sleep(5 * attempt)

    log_upload({"event": "failed", "filename": filename,
                "error": last_error, "last_http": last_status})
    return jsonify(status="failed", error=last_error or "archive PUT failed"), 502


@app.route("/verify-listing")
def verify_listing():
    """Return archive.org's authoritative view of the item's file list.

    Useful for reconciling local "I uploaded N files" against "archive.org has N".
    Public read - does not require X-Auth.
    """
    try:
        r = requests.get(f"https://archive.org/metadata/{ITEM}", timeout=30)
        r.raise_for_status()
        data = r.json()
    except requests.RequestException as ex:
        return jsonify(error=str(ex)), 502
    except ValueError as ex:
        return jsonify(error=f"non-json metadata: {ex}"), 502

    files = data.get("files") or []
    return jsonify(item=ITEM,
                   file_count=len(files),
                   item_last_updated=data.get("item_last_updated"),
                   pending_tasks=data.get("pending_tasks"),
                   tasks=data.get("tasks"),
                   item_size=data.get("item_size"),
                   sample=[{"name": f.get("name"),
                            "size": f.get("size"),
                            "mtime": f.get("mtime")}
                           for f in files[:20]])


@app.route("/tasks")
def tasks():
    """Surface archive.org's task queue for the item.

    Useful when an upload looks "stuck" - lets the user (or an admin script)
    see if catalogd has pending or running tasks against the item.
    """
    try:
        r = requests.get(
            "https://catalogd.archive.org/services/tasks.php",
            params={"identifier": ITEM, "limit": 50},
            timeout=30)
        if r.headers.get("content-type", "").startswith("application/json"):
            return jsonify(r.json()), r.status_code
        return jsonify(status_code=r.status_code, body=r.text[:1000]), r.status_code
    except requests.RequestException as ex:
        return jsonify(error=str(ex)), 502


@app.route("/cleanup-metadata", methods=["POST"])
def cleanup_metadata():
    if SHARED_TOKEN and request.headers.get("X-Auth") != SHARED_TOKEN:
        abort(401)

    current = requests.get(f"https://archive.org/metadata/{ITEM}", timeout=30)
    current.raise_for_status()
    metadata = current.json().get("metadata") or {}
    remove_fields = [
        "kogama-game-id",
        "kogama-owner-username",
        "kogama-region",
        "kogama-title",
    ]
    patch = [
        {"op": "remove", "path": f"/{field}"}
        for field in remove_fields
        if field in metadata
    ]
    if not patch:
        return jsonify(status="clean", removed=[]), 200

    resp = requests.post(
        f"https://archive.org/metadata/{ITEM}",
        data={
            "-target": "metadata",
            "-patch": json.dumps(patch),
            "access": ACCESS,
            "secret": SECRET,
            "priority": "-5",
        },
        timeout=60,
    )
    body = resp.json() if resp.headers.get("content-type", "").startswith("application/json") else {"body": resp.text[:300]}
    if resp.ok and body.get("success"):
        return jsonify(status="queued", removed=[p["path"][1:] for p in patch], response=body), 200
    return jsonify(status="failed", code=resp.status_code, response=body), 502


if __name__ == "__main__":
    app.run(host="0.0.0.0", port=8080)
