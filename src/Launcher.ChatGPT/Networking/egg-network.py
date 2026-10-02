"""Egg Launcher HTTPS compatibility client. Uses verified TLS inside the caller's sandbox."""
import argparse
import hashlib
import http.client
import json
import os
import queue
import socket
import ssl
import sys
import subprocess
import tempfile
import threading
import time
import urllib.error
import urllib.parse
import urllib.request


class CompatibilityError(Exception):
    def __init__(self, code):
        super().__init__(code)
        self.code = code


class HttpsRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        validate_url(newurl)
        return super().redirect_request(req, fp, code, msg, headers, newurl)


def validate_url(url):
    parsed = urllib.parse.urlsplit(url)
    if parsed.scheme != "https" or not parsed.hostname or parsed.username or parsed.password:
        raise ValueError("Only HTTPS URLs without embedded credentials are supported")


def classify(error):
    cause = getattr(error, "reason", error)
    if isinstance(error, CompatibilityError):
        return error.code
    if isinstance(error, http.client.IncompleteRead):
        return "incomplete_download"
    if isinstance(error, urllib.error.HTTPError):
        return "http_error"
    if isinstance(cause, ssl.SSLCertVerificationError):
        return "certificate_error"
    if isinstance(cause, ssl.SSLError):
        return "tls_error"
    if isinstance(cause, (TimeoutError, socket.timeout)):
        return "timeout"
    if isinstance(cause, socket.gaierror):
        return "dns_error"
    if isinstance(cause, PermissionError):
        return "permission_denied"
    if isinstance(cause, ConnectionRefusedError):
        return "connection_refused"
    if isinstance(error, FileExistsError):
        return "destination_exists"
    if isinstance(error, ValueError):
        return "invalid_request"
    return "request_failed"


def notify(event, **values):
    print(json.dumps({"event": event, "at": time.monotonic(), **values}, ensure_ascii=True), flush=True)


def supervise(request_timeout, read_timeout):
    # A separate worker makes the request deadline enforceable even when DNS,
    # TLS or response-header parsing blocks inside the standard library.
    events = queue.Queue()
    temporary = None
    result = {"ok": False, "code": "request_failed", "tls": ssl.OPENSSL_VERSION,
              "proxy_present": bool(os.environ.get("HTTPS_PROXY"))}
    phase = "request"
    deadline = time.monotonic() + request_timeout
    with subprocess.Popen([sys.executable, os.path.abspath(__file__), *sys.argv[1:],
                           "--internal-worker"], stdout=subprocess.PIPE,
                          stderr=subprocess.DEVNULL, text=True, encoding="utf-8",
                          creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0) as worker:
        def drain():
            try:
                for line in worker.stdout:
                    events.put(json.loads(line))
            except (ValueError, OSError):
                pass
            finally:
                events.put(None)
        reader = threading.Thread(target=drain, daemon=True)
        reader.start()
        try:
            while True:
                remaining = None if deadline is None else max(0, deadline - time.monotonic())
                try:
                    event = events.get(timeout=remaining)
                except queue.Empty:
                    result.update(code="timeout", timeout_phase=phase)
                    break
                if event is None:
                    break
                if deadline is not None and event["at"] > deadline:
                    if event["event"] == "temporary":
                        temporary = event["path"]
                    result.update(code="timeout", timeout_phase=phase)
                    break
                if event["event"] == "result":
                    result = event["result"]
                    temporary = None
                    break
                if event["event"] == "temporary":
                    temporary = event["path"]
                elif event["event"] == "receiving":
                    phase = "read"
                    deadline = event["at"] + read_timeout
                elif event["event"] == "progress":
                    result["bytes_read"] = event["bytes_read"]
                    deadline = event["at"] + read_timeout
                elif event["event"] == "finalizing":
                    # Local file validation/publication is not network waiting.
                    deadline = None
        finally:
            if worker.poll() is None:
                worker.kill()
            worker.wait()
            reader.join()
            worker.stdout.close()
            # The worker may have created its temporary file immediately before
            # termination. Drain its final ownership notification before cleanup.
            while not events.empty():
                event = events.get_nowait()
                if event and event["event"] == "temporary":
                    temporary = event["path"]
            if temporary:
                try:
                    os.unlink(temporary)
                    result["temporary_file_removed"] = True
                except FileNotFoundError:
                    result["temporary_file_removed"] = True
                except OSError:
                    result["temporary_file_removed"] = False
    print(json.dumps(result, ensure_ascii=True))
    return 0 if result["ok"] else 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("url")
    parser.add_argument("--head", action="store_true")
    parser.add_argument("--output", help="Download destination; existing files are never overwritten")
    parser.add_argument("--timeout", type=int, default=30, help="Fallback for request and idle-read timeouts in seconds (1-300); not a total download limit")
    parser.add_argument("--request-timeout", type=int, help="Total budget through final response headers, including redirects (1-300 seconds)")
    parser.add_argument("--read-timeout", type=int, help="Maximum wait without received data (1-300 seconds); no total download time limit")
    parser.add_argument("--internal-worker", action="store_true", help=argparse.SUPPRESS)
    parser.add_argument("--max-bytes", type=int, default=268435456, help="Download safety bound")
    parser.add_argument("--preview-bytes", type=int, default=16384, help="Text preview bound (0-65536)")
    args = parser.parse_args()
    request_timeout = args.request_timeout if args.request_timeout is not None else args.timeout
    read_timeout = args.read_timeout if args.read_timeout is not None else args.timeout
    result = {"ok": False, "tls": ssl.OPENSSL_VERSION, "proxy_present": bool(os.environ.get("HTTPS_PROXY")), "code": "request_failed"}
    temporary = None
    phase = "request"
    try:
        validate_url(args.url)
        if not all(1 <= value <= 300 for value in (args.timeout, request_timeout, read_timeout)) or not 0 <= args.preview_bytes <= 65536 or args.max_bytes <= 0:
            raise ValueError("Invalid timeout or byte limit")
        if args.head and args.output:
            raise ValueError("--head cannot be combined with --output")
        destination = os.path.abspath(args.output) if args.output else None
        if destination and os.path.lexists(destination):
            raise FileExistsError("Destination already exists; choose a new filename")
        if not args.internal_worker:
            return supervise(request_timeout, read_timeout)
        proxies = {key: os.environ[name] for key, name in (("http", "HTTP_PROXY"), ("https", "HTTPS_PROXY")) if os.environ.get(name)}
        result["proxy_bypassed"] = urllib.request.proxy_bypass(urllib.parse.urlsplit(args.url).hostname)
        if args.head and proxies.get("https") and not result["proxy_bypassed"]:
            proxy = urllib.parse.urlsplit(proxies["https"])
            if proxy.scheme not in ("http", "https") or not proxy.hostname:
                raise ValueError("Unsupported HTTPS proxy")
            result["proxy_tcp"] = "failed"
            with socket.create_connection((proxy.hostname, proxy.port or (443 if proxy.scheme == "https" else 80)), timeout=min(request_timeout, 3)):
                result["proxy_tcp"] = "ok"
        client = urllib.request.build_opener(urllib.request.ProxyHandler(proxies),
            urllib.request.HTTPSHandler(context=ssl.create_default_context()), HttpsRedirect())
        request = urllib.request.Request(args.url, method="HEAD" if args.head else "GET", headers={"User-Agent": "Egg-Launcher-Network/0.9.4"})
        with client.open(request, timeout=request_timeout) as response:
            # Socket timeouts complement the supervisor's strict request and
            # idle deadlines. The HTTPResponse belongs to this worker only.
            if not args.head:
                response_socket = response.fp.raw._sock
                response_socket.settimeout(read_timeout)
            phase = "read"
            notify("receiving")
            result["http_status"] = response.status
            result["content_type"] = response.headers.get("Content-Type", "")
            if not args.head:
                count = 0
                preview = bytearray()
                digest = hashlib.sha256()
                expected = None
                if destination:
                    # Chunked framing takes precedence over Content-Length.
                    length = response.headers.get("Content-Length")
                    if length is not None and not response.headers.get("Transfer-Encoding"):
                        if not length.strip().isascii() or not length.strip().isdigit():
                            raise CompatibilityError("invalid_response_length")
                        expected = int(length)
                        result["expected_bytes"] = expected
                        if expected > args.max_bytes:
                            raise CompatibilityError("download_too_large")
                    fd, temporary = tempfile.mkstemp(prefix=".egg-download-", dir=os.path.dirname(destination))
                    notify("temporary", path=temporary)
                    stream = os.fdopen(fd, "wb")
                else:
                    stream = None
                try:
                    while True:
                        chunk = response.read1(65536)
                        if not chunk:
                            break
                        count += len(chunk)
                        result["bytes_read"] = count
                        notify("progress", bytes_read=count)
                        if count > args.max_bytes:
                            raise CompatibilityError("download_too_large")
                        if stream:
                            stream.write(chunk)
                            digest.update(chunk)
                        else:
                            preview.extend(chunk[:max(0, args.preview_bytes - len(preview))])
                            if count > args.preview_bytes:
                                result["preview_truncated"] = True
                                break
                finally:
                    if stream:
                        stream.close()
                notify("finalizing")
                result["bytes_read"] = count
                if destination:
                    # Compare only after the response body has actually been read.
                    if expected is not None and count != expected:
                        raise CompatibilityError("incomplete_download")
                    # Same-directory rename is atomic; on Windows it refuses an existing destination.
                    if os.name == "nt":
                        os.rename(temporary, destination)
                    else:
                        # Atomic no-replace publication also on Unix; avoid a check/rename race.
                        os.link(temporary, destination)
                        os.unlink(temporary)
                    temporary = None
                    result["saved"] = True
                    result["sha256"] = digest.hexdigest()
                else:
                    result["text"] = preview.decode(response.headers.get_content_charset() or "utf-8", errors="replace")
            result["ok"] = True
            result["code"] = "ok"
    except Exception as error:
        result["code"] = classify(error)
        if result["code"] == "timeout":
            result["timeout_phase"] = phase
        # Do not echo URLs, queries, proxy credentials, response bodies, or exception messages.
        result["error_type"] = type(error).__name__
        if isinstance(error, urllib.error.HTTPError):
            result["http_status"] = error.code
    finally:
        if temporary:
            try:
                os.unlink(temporary)
                result["temporary_file_removed"] = True
            except OSError:
                result["temporary_file_removed"] = False
    if args.internal_worker:
        notify("result", result=result)
    else:
        print(json.dumps(result, ensure_ascii=True))
    return 0 if result["ok"] else 1


if __name__ == "__main__":
    sys.exit(main())
