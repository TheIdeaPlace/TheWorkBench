#!/usr/bin/env python3
"""App Store Connect client for The Card Place's release scripts.

Imported by wire-testflight.py, wire-appstore.py and upload-screenshots.py. It
replaces the asc.py that used to live outside the repository with its own
asc.json; the credentials now come from the signing kit (see signing/README.md
in kellylford/The-Idea-Place-Projects):

    ASC_KEY_PATH    the .p8 key; default $SIGNING_KIT/asc-api-key.p8,
                    where SIGNING_KIT defaults to ~/.signing-kit
    ASC_KEY_ID      default 38ANZ53D9L
    ASC_ISSUER_ID   default d36bb41d-a198-401d-b36c-2e4fd15163e1
    ASC_BUNDLE_ID   default net.theideaplace.TheCardPlace

Only the standard library is needed. The ES256 token is signed with the
cryptography package if it is installed, otherwise with the openssl command
that comes with macOS. Nothing here prints the key or the token.

    python3 scripts/asc.py check    # sign in and print the app's id
"""
import base64, json, os, subprocess, sys, time
from urllib import error, parse, request

API = "https://api.appstoreconnect.apple.com"
KIT = os.path.expanduser(os.environ.get("SIGNING_KIT", "~/.signing-kit"))
KEY_PATH = os.path.expanduser(os.environ.get("ASC_KEY_PATH", os.path.join(KIT, "asc-api-key.p8")))
KEY_ID = os.environ.get("ASC_KEY_ID", "38ANZ53D9L")
ISSUER_ID = os.environ.get("ASC_ISSUER_ID", "d36bb41d-a198-401d-b36c-2e4fd15163e1")
BUNDLE = os.environ.get("ASC_BUNDLE_ID", "net.theideaplace.TheCardPlace")

_token = {"value": None, "expires": 0}


# ── signing ────────────────────────────────────────────────────────────────


def _b64url(data):
    return base64.urlsafe_b64encode(data).rstrip(b"=").decode("ascii")


def _der_to_raw(der, size=32):
    """An ECDSA signature as DER (SEQUENCE of two INTEGERs) -> r || s, as JWS wants."""
    def length(buf, i):
        n = buf[i]
        if n < 0x80:
            return n, i + 1
        count = n & 0x7F
        return int.from_bytes(buf[i + 1:i + 1 + count], "big"), i + 1 + count

    if der[0] != 0x30:
        raise ValueError("signature is not a DER sequence")
    _, i = length(der, 1)
    parts = []
    for _ in range(2):
        if der[i] != 0x02:
            raise ValueError("signature part is not a DER integer")
        n, i = length(der, i + 1)
        parts.append(int.from_bytes(der[i:i + n], "big"))
        i += n
    return b"".join(p.to_bytes(size, "big") for p in parts)


def _sign(message):
    try:
        from cryptography.hazmat.primitives import hashes, serialization
        from cryptography.hazmat.primitives.asymmetric import ec
    except ImportError:
        der = subprocess.run(["openssl", "dgst", "-sha256", "-sign", KEY_PATH],
                             input=message, capture_output=True, check=True).stdout
        return _der_to_raw(der)
    with open(KEY_PATH, "rb") as f:
        key = serialization.load_pem_private_key(f.read(), password=None)
    return _der_to_raw(key.sign(message, ec.ECDSA(hashes.SHA256())))


def token():
    """A bearer token, renewed well before the 20 minutes Apple allows, since
    waiting for a build to process can take longer than one token lasts."""
    now = int(time.time())
    if _token["value"] and now < _token["expires"] - 120:
        return _token["value"]
    if not os.path.isfile(KEY_PATH):
        raise SystemExit(
            f"No App Store Connect key at {KEY_PATH}. Recreate the signing kit with "
            "sync-secrets.py pull-kit (signing/README.md in The-Idea-Place-Projects), "
            "or set ASC_KEY_PATH.")
    expires = now + 15 * 60
    header = {"alg": "ES256", "kid": KEY_ID, "typ": "JWT"}
    claims = {"iss": ISSUER_ID, "iat": now, "exp": expires, "aud": "appstoreconnect-v1"}
    signing_input = (_b64url(json.dumps(header, separators=(",", ":")).encode()) + "."
                     + _b64url(json.dumps(claims, separators=(",", ":")).encode()))
    _token["value"] = signing_input + "." + _b64url(_sign(signing_input.encode("ascii")))
    _token["expires"] = expires
    return _token["value"]


# ── requests ───────────────────────────────────────────────────────────────


def api(method, path, body=None, query=None):
    """One request. path starts with /v1/ or /v2/. Returns (status, json);
    the json is {} when the response has no body. Never raises on an HTTP
    error status; callers check it."""
    url = API + path
    if query:
        url += "?" + parse.urlencode(query)
    data = json.dumps(body).encode() if body is not None else None
    req = request.Request(url, data=data, method=method)
    req.add_header("Authorization", "Bearer " + token())
    if data is not None:
        req.add_header("Content-Type", "application/json")
    try:
        with request.urlopen(req, timeout=60) as r:
            status, raw = r.status, r.read()
    except error.HTTPError as e:
        status, raw = e.code, e.read()
    if not raw:
        return status, {}
    try:
        return status, json.loads(raw)
    except ValueError:
        return status, {"raw": raw.decode("utf-8", "replace")[:500]}


def _ok(status):
    return 200 <= status < 300


def _errors(j):
    errs = j.get("errors") or []
    return "; ".join(f"{e.get('title')}: {e.get('detail')}" for e in errs) or json.dumps(j)[:400]


def _get(path, **query):
    st, j = api("GET", path, query=query or None)
    if not _ok(st):
        raise SystemExit(f"GET {path} failed ({st}): {_errors(j)}")
    return j.get("data")


# ── lookups ────────────────────────────────────────────────────────────────


def find_app():
    """The app's App Store Connect id. The bundle id filter is not guaranteed
    to be an exact match, so the result is checked."""
    apps = _get("/v1/apps", **{"filter[bundleId]": BUNDLE})
    app = next((a for a in apps if a["attributes"].get("bundleId") == BUNDLE), None)
    if app is None:
        raise SystemExit(f"No app in App Store Connect has the bundle id {BUNDLE}.")
    return app["id"]


def find_build(app_id, version, build):
    """The build with this version and build number, or None."""
    found = _get("/v1/builds", **{"filter[app]": app_id, "filter[version]": str(build),
                                  "filter[preReleaseVersion.version]": version, "limit": 10})
    return found[0] if found else None


def find_appstore_version(app_id, version):
    """The App Store version with this version string, or None."""
    found = _get(f"/v1/apps/{app_id}/appStoreVersions",
                 **{"filter[versionString]": version, "limit": 10})
    return next((v for v in found if v["attributes"].get("versionString") == version), None)


def poll_build(app_id, version, build, timeout=1800, interval=30):
    """Wait for the build to finish processing; return its id."""
    deadline = time.time() + timeout
    while True:
        found = find_build(app_id, version, build)
        if found:
            state = found["attributes"].get("processingState")
            print(f"  {version} ({build}): {state}", flush=True)
            if state == "VALID":
                return found["id"]
            if state in ("FAILED", "INVALID"):
                raise SystemExit(f"App Store Connect could not process {version} ({build}): {state}")
        else:
            print(f"  {version} ({build}): not visible yet", flush=True)
        if time.time() > deadline:
            raise SystemExit(f"{version} ({build}) was not ready after {timeout} seconds; "
                             "run again with --wire-only once it has processed.")
        time.sleep(interval)


# ── TestFlight ─────────────────────────────────────────────────────────────


def set_whats_new(build_id, text, locale="en-US"):
    """The build's What to Test notes, created or replaced."""
    locs = _get(f"/v1/builds/{build_id}/betaBuildLocalizations")
    existing = next((x for x in locs if x["attributes"].get("locale") == locale), None)
    if existing:
        st, j = api("PATCH", f"/v1/betaBuildLocalizations/{existing['id']}", body={"data": {
            "type": "betaBuildLocalizations", "id": existing["id"],
            "attributes": {"whatsNew": text}}})
    else:
        st, j = api("POST", "/v1/betaBuildLocalizations", body={"data": {
            "type": "betaBuildLocalizations",
            "attributes": {"locale": locale, "whatsNew": text},
            "relationships": {"build": {"data": {"type": "builds", "id": build_id}}}}})
    if not _ok(st):
        raise SystemExit(f"could not set What to Test ({st}): {_errors(j)}")
    print(f"  What to Test ({locale}): set")


def assign_group(build_id, group_id, name):
    """Add the build to a beta group."""
    st, j = api("POST", f"/v1/betaGroups/{group_id}/relationships/builds",
                body={"data": [{"type": "builds", "id": build_id}]})
    if not _ok(st):
        raise SystemExit(f"could not add the build to {name!r} ({st}): {_errors(j)}")
    print(f"  added to {name!r}")


# Where a build has been handed to Apple, or through it.
_SUBMITTED = {"WAITING_FOR_REVIEW", "IN_REVIEW", "APPROVED"}
_EXTERNAL_OK = {"WAITING_FOR_BETA_REVIEW", "IN_BETA_REVIEW", "BETA_APPROVED",
                "READY_FOR_BETA_TESTING", "IN_BETA_TESTING"}


def submit_external_review(build_id):
    """Submit the build for Beta App Review. A refusal because it is already
    submitted or approved is fine; anything else stops the run."""
    st, j = api("POST", "/v1/betaAppReviewSubmissions", body={"data": {
        "type": "betaAppReviewSubmissions",
        "relationships": {"build": {"data": {"type": "builds", "id": build_id}}}}})
    if _ok(st):
        print("  submitted for Beta App Review")
        return
    rst, rj = api("GET", f"/v1/builds/{build_id}/betaAppReviewSubmission")
    review = ((rj.get("data") or {}).get("attributes") or {}).get("betaReviewState") if _ok(rst) else None
    dst, dj = api("GET", f"/v1/builds/{build_id}/buildBetaDetail")
    external = ((dj.get("data") or {}).get("attributes") or {}).get("externalBuildState") if _ok(dst) else None
    if review in _SUBMITTED or external in _EXTERNAL_OK:
        print(f"  already with Apple: review {review}, external testing {external}")
        return
    raise SystemExit(f"not submitted for Beta App Review ({st}): {_errors(j)}. "
                     f"Review state {review}, external state {external}.")


if __name__ == "__main__":
    if sys.argv[1:] != ["check"]:
        raise SystemExit(__doc__)
    print(f"Key {KEY_ID} from {KEY_PATH}")
    print(f"App: {find_app()} ({BUNDLE})")
