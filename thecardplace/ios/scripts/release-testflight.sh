#!/usr/bin/env bash
#
# release-testflight.sh — build The Card Place and put it on TestFlight.
#
# Archives the app, exports a signed App Store IPA, validates and uploads it,
# then waits for processing and wires the build up: What-to-Test notes, beta
# groups, and (unless told not to) external Beta App Review.
#
# The App Store Connect API key comes from the signing kit, ~/.signing-kit/
# on this Mac (see RELEASE.md). Overrides, all optional:
#   ASC_KEY_PATH   the .p8 (default $SIGNING_KIT/asc-api-key.p8,
#                  SIGNING_KIT defaulting to ~/.signing-kit)
#   ASC_KEY_ID     default 38ANZ53D9L
#   ASC_ISSUER_ID  default d36bb41d-a198-401d-b36c-2e4fd15163e1
#
# Usage:
#   scripts/release-testflight.sh --notes scripts/RELEASE_NOTES.txt
#   scripts/release-testflight.sh --notes notes.txt --no-external-review
#   scripts/release-testflight.sh --upload-only
#   scripts/release-testflight.sh --wire-only --version 1.0 --build 1 --notes notes.txt
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
IOS_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
SIGNING_KIT="${SIGNING_KIT:-$HOME/.signing-kit}"
export ASC_KEY_PATH="${ASC_KEY_PATH:-$SIGNING_KIT/asc-api-key.p8}"
export ASC_KEY_ID="${ASC_KEY_ID:-38ANZ53D9L}"
export ASC_ISSUER_ID="${ASC_ISSUER_ID:-d36bb41d-a198-401d-b36c-2e4fd15163e1}"

PROJECT="$IOS_DIR/TheCardPlace.xcodeproj"
SCHEME="TheCardPlace"
CONFIG="Release"
BUILD_DIR="$IOS_DIR/build/release"
EXPORT_PLIST="$SCRIPT_DIR/ExportOptions.plist"

NOTES=""
BETA_GROUPS="Internal test,External"
SUBMIT_EXTERNAL=1
WIRE_ONLY=0
UPLOAD_ONLY=0
VERSION=""
BUILD=""

die() { echo "❌ $*" >&2; exit 1; }
step() { echo ""; echo "▶ $*"; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --notes) NOTES="$2"; shift 2;;
    --groups) BETA_GROUPS="$2"; shift 2;;
    --no-external-review) SUBMIT_EXTERNAL=0; shift;;
    --wire-only) WIRE_ONLY=1; shift;;
    --upload-only) UPLOAD_ONLY=1; shift;;
    --version) VERSION="$2"; shift 2;;
    --build) BUILD="$2"; shift 2;;
    -h|--help) grep '^#' "$0" | sed 's/^# \{0,1\}//'; exit 0;;
    *) die "unknown arg: $1";;
  esac
done

[[ -f "$ASC_KEY_PATH" ]] || die "no App Store Connect key at $ASC_KEY_PATH. Recreate the signing kit with: python3 <The-Idea-Place-Projects>/signing/sync-secrets.py pull-kit (or set ASC_KEY_PATH)"
if [[ "$UPLOAD_ONLY" -eq 0 ]]; then
  [[ -n "$NOTES" ]] || die "--notes <file> is required (the What-to-Test text)"
  NOTES="$(cd "$(dirname "$NOTES")" && pwd)/$(basename "$NOTES")"
  [[ -f "$NOTES" ]] || die "notes file not found: $NOTES"
  [[ -s "$NOTES" ]] || die "notes file is empty: $NOTES"
fi


wire_testflight() {
  local ver="$1" bld="$2"
  local args=(--version "$ver" --build "$bld" --notes-file "$NOTES" --groups "$BETA_GROUPS")
  [[ "$SUBMIT_EXTERNAL" -eq 0 ]] && args+=(--no-submit-external)
  step "Wiring TestFlight for $ver ($bld)"
  python3 "$SCRIPT_DIR/wire-testflight.py" "${args[@]}"
}

if [[ "$WIRE_ONLY" -eq 1 ]]; then
  [[ -n "$VERSION" && -n "$BUILD" ]] || die "--wire-only requires --version and --build"
  wire_testflight "$VERSION" "$BUILD"
  echo ""; echo "✅ Done (wire-only)."
  exit 0
fi

# The project is generated from project.yml; regenerate so a source file added
# since the last commit cannot be silently left out of the build.
if command -v xcodegen >/dev/null 2>&1; then
  step "Regenerating the Xcode project"
  (cd "$IOS_DIR" && xcodegen generate)
fi

step "Reading version from project"
SETTINGS="$(xcodebuild -project "$PROJECT" -scheme "$SCHEME" -configuration "$CONFIG" -showBuildSettings 2>/dev/null)"
VERSION="$(echo "$SETTINGS" | awk -F' = ' '/ MARKETING_VERSION /{print $2; exit}')"
BUILD="$(echo "$SETTINGS"   | awk -F' = ' '/ CURRENT_PROJECT_VERSION /{print $2; exit}')"
[[ -n "$VERSION" && -n "$BUILD" ]] || die "could not read MARKETING_VERSION / CURRENT_PROJECT_VERSION"
echo "  Version $VERSION (build $BUILD)"

ARCHIVE="$BUILD_DIR/TheCardPlace-$VERSION-$BUILD.xcarchive"
EXPORT_DIR="$BUILD_DIR/export-$VERSION-$BUILD"
rm -rf "$ARCHIVE" "$EXPORT_DIR"
mkdir -p "$BUILD_DIR"

step "Archiving ($CONFIG, generic iOS device)"
xcodebuild -project "$PROJECT" -scheme "$SCHEME" -configuration "$CONFIG" \
  -destination 'generic/platform=iOS' -archivePath "$ARCHIVE" \
  -allowProvisioningUpdates clean archive

step "Exporting App Store IPA"
xcodebuild -exportArchive -archivePath "$ARCHIVE" \
  -exportOptionsPlist "$EXPORT_PLIST" -exportPath "$EXPORT_DIR" \
  -allowProvisioningUpdates

IPA="$(/usr/bin/find "$EXPORT_DIR" -maxdepth 1 -name '*.ipa' | head -1)"
[[ -n "$IPA" ]] || die "no .ipa produced in $EXPORT_DIR"
echo "  IPA: $IPA"

# altool only finds a key by name, AuthKey_<id>.p8, in a key directory. Give it
# a private temporary one holding a copy of the kit's key, removed on exit,
# rather than leaving a second copy in ~/.appstoreconnect/private_keys.
ALTOOL_KEYS="$(mktemp -d)"
trap 'rm -rf "$ALTOOL_KEYS"' EXIT
chmod 700 "$ALTOOL_KEYS"
cp "$ASC_KEY_PATH" "$ALTOOL_KEYS/AuthKey_${ASC_KEY_ID}.p8"
export API_PRIVATE_KEYS_DIR="$ALTOOL_KEYS"

step "Validating with App Store Connect"
xcrun altool --validate-app --type ios --file "$IPA" --apiKey "$ASC_KEY_ID" --apiIssuer "$ASC_ISSUER_ID"

step "Uploading to App Store Connect"
xcrun altool --upload-app --type ios --file "$IPA" --apiKey "$ASC_KEY_ID" --apiIssuer "$ASC_ISSUER_ID"
echo "  Upload accepted; App Store Connect is now processing the build."

if [[ "$UPLOAD_ONLY" -eq 1 ]]; then
  echo ""; echo "✅ Built and uploaded $VERSION ($BUILD). TestFlight wiring skipped."
  exit 0
fi

wire_testflight "$VERSION" "$BUILD"

echo ""
echo "✅ Release complete: $VERSION ($BUILD)"
echo "   Internal testers can install now; external testers once Beta App Review clears."
