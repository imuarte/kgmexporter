"""
Tiny proxy that forwards kgmexporter .kgmap uploads to archive.org S3.

Client POSTs to:
  POST /upload?filename=<name>     [body = raw .kgmap bytes]
  Headers:
    X-Auth: <shared token>                 (required if PROXY_TOKEN is set)
"""
import os
import json
import time
import requests
from flask import Flask, request, jsonify, abort

ACCESS = os.environ["IA_ACCESS"]
SECRET = os.environ["IA_SECRET"]
ITEM = os.environ.get("IA_ITEM", "kogama-maps-kgmexporter")
SHARED_TOKEN = os.environ.get("PROXY_TOKEN", "")
PUT_ATTEMPTS = int(os.environ.get("IA_PUT_ATTEMPTS", "3"))
PUT_READ_TIMEOUT = int(os.environ.get("IA_PUT_READ_TIMEOUT", "180"))
VERIFY_DELAYS = (2, 5, 10, 20, 30)

app = Flask(__name__)
# Cap incoming uploads at 200 MB so a runaway client can't OOM the VM.
app.config["MAX_CONTENT_LENGTH"] = 200 * 1024 * 1024


@app.route("/health")
def health():
    return {"ok": True, "item": ITEM}


def archive_file_exists(filename, timeout=15):
    try:
        head = requests.head(f"https://archive.org/download/{ITEM}/{filename}",
                             timeout=timeout, allow_redirects=True)
        return head.status_code == 200
    except requests.RequestException as ex:
        app.logger.warning("archive HEAD failed for %s: %s", filename, ex)
        return False


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
    existed_before = archive_file_exists(filename)
    if not force and existed_before:
        return jsonify(status="already_exists",
                       item_url=f"https://archive.org/details/{ITEM}"), 200

    headers = {
        "authorization": f"LOW {ACCESS}:{SECRET}",
        "x-archive-auto-make-bucket": "1",
        "x-archive-queue-derive": "0",
        "x-archive-meta-mediatype": "data",
        "x-archive-meta-title": "Kogama maps saved by kgmexporter",
        "x-archive-meta-description":
            "Kogama .kgmap world files preserved with kgmexporter "
            "before the 2026-05-29 KoGaMa shutdown.",
        "x-archive-meta01-collection": "opensource",
        "x-archive-meta01-subject": "Kogama",
        "x-archive-meta02-subject": "kgmexporter",
        "x-archive-meta03-subject": "game preservation",
        "content-type": "application/octet-stream",
    }

    # archive.org S3 requires an explicit Content-Length on PUTs and rejects
    # chunked transfer-encoding, so we materialise the body once in memory.
    # The 200 MB Flask cap above protects against runaway clients OOMing the VM.
    body = request.get_data()
    headers["content-length"] = str(len(body))

    url = f"https://s3.us.archive.org/{ITEM}/{filename}"
    last_error = None
    for attempt in range(1, PUT_ATTEMPTS + 1):
        try:
            resp = requests.put(url, headers=headers, data=body,
                                allow_redirects=True,
                                timeout=(30, PUT_READ_TIMEOUT))
            if resp.ok:
                return jsonify(status="uploaded",
                               item_url=f"https://archive.org/details/{ITEM}"), 200

            response_body = (resp.text or "")[:300]
            app.logger.warning("archive PUT failed for %s attempt %s/%s: %s %s",
                               filename, attempt, PUT_ATTEMPTS,
                               resp.status_code, response_body)
            if resp.status_code not in (408, 409, 429, 500, 502, 503, 504):
                return jsonify(status="failed", code=resp.status_code,
                               body=response_body), 502
            last_error = f"{resp.status_code}: {response_body}"
        except requests.RequestException as ex:
            last_error = str(ex)
            app.logger.warning("archive PUT exception for %s attempt %s/%s: %s",
                               filename, attempt, PUT_ATTEMPTS, ex)

        # archive.org may accept the object but close or stall before returning
        # a usable S3 response. Treat a visible object as success.
        if not existed_before and wait_for_archive_file(filename):
            return jsonify(status="uploaded", verified=True,
                           item_url=f"https://archive.org/details/{ITEM}"), 200

        if attempt < PUT_ATTEMPTS:
            time.sleep(5 * attempt)

    return jsonify(status="failed", error=last_error or "archive PUT failed"), 502


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
