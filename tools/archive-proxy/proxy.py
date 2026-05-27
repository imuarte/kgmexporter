"""
Tiny proxy that forwards kgmexporter .kgmap uploads to archive.org S3.

Client POSTs to:
  POST /upload?filename=<name>     [body = raw .kgmap bytes]
  Headers:
    X-Auth: <shared token>                 (required if PROXY_TOKEN is set)
"""
import os
import json
import requests
from flask import Flask, request, jsonify, abort

ACCESS = os.environ["IA_ACCESS"]
SECRET = os.environ["IA_SECRET"]
ITEM = os.environ.get("IA_ITEM", "kogama-maps-kgmexporter")
SHARED_TOKEN = os.environ.get("PROXY_TOKEN", "")

app = Flask(__name__)
# Cap incoming uploads at 200 MB so a runaway client can't OOM the VM.
app.config["MAX_CONTENT_LENGTH"] = 200 * 1024 * 1024


@app.route("/health")
def health():
    return {"ok": True, "item": ITEM}


@app.route("/upload", methods=["POST"])
def upload():
    if SHARED_TOKEN and request.headers.get("X-Auth") != SHARED_TOKEN:
        abort(401)

    filename = request.args.get("filename", "").strip()
    if not filename or "/" in filename or "\\" in filename:
        abort(400, "filename query param required and must not contain slashes")

    force = request.args.get("force") in ("1", "true", "yes")
    if not force:
        head = requests.head(f"https://archive.org/download/{ITEM}/{filename}",
                             timeout=15, allow_redirects=True)
        if head.status_code == 200:
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
    resp = requests.put(url, headers=headers, data=body,
                        allow_redirects=True, timeout=600)
    if resp.ok:
        return jsonify(status="uploaded",
                       item_url=f"https://archive.org/details/{ITEM}"), 200
    body = (resp.text or "")[:300]
    return jsonify(status="failed", code=resp.status_code, body=body), 502


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
