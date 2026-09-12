#!/usr/bin/env bash
# Packs a published tianwen-fits or tianwen-gui tree into a macOS .app bundle and a .dmg, signs
# both, and notarizes the image when the Apple credentials are present.
#
# There is no Mac in the loop: this runs on the macos-latest leg of a release (the `dmg` job in
# .github/workflows/dotnet.yml), and the only Apple-specific tools it needs -- codesign, sips,
# iconutil, hdiutil, notarytool, stapler -- are all on that runner.
#
# Two modes exist for machines that are not Macs, because most of what can go wrong here is not
# Apple-specific: --validate-only checks the templates, the entitlements, the icons and this script
# with nothing but bash and python (CI runs it on every push, beside the MSIX validation), and
# --bundle-only assembles the .app from a publish tree and stops before the tools that need macOS.
#
# Signing is DEVELOPER ID when MACOS_SIGN_IDENTITY names one, AD-HOC otherwise. Ad-hoc is what
# `dotnet publish` already puts on the binary (Apple silicon refuses to run unsigned code at all),
# so the result runs everywhere -- via System Settings > Privacy & Security > Open Anyway for a
# downloaded copy, since Gatekeeper cannot verify the developer. Notarization needs Developer ID
# and an App Store Connect API key; without either it is skipped and says so. README.md has the
# five secrets and how to mint them.
#
# Usage:
#   build-dmg.sh --app fits|gui --publish-dir DIR --version X.Y.Z [--build B] --out FILE.dmg
#   build-dmg.sh --app fits|gui --publish-dir DIR --version X.Y.Z [--build B] --bundle-only --out DIR
#   build-dmg.sh --validate-only
#
# Environment (all optional):
#   MACOS_SIGN_IDENTITY      "Developer ID Application: Name (TEAMID)"; absent = ad-hoc
#   MACOS_NOTARY_KEY         path to the App Store Connect API key (.p8)
#   MACOS_NOTARY_KEY_ID      its key id
#   MACOS_NOTARY_ISSUER_ID   its issuer id
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../.." && pwd)"

app=""
publish_dir=""
version=""
build=""
out=""
mode="dmg"

while [ $# -gt 0 ]; do
  case "$1" in
    --app) app="$2"; shift 2 ;;
    --publish-dir) publish_dir="$2"; shift 2 ;;
    --version) version="$2"; shift 2 ;;
    --build) build="$2"; shift 2 ;;
    --out) out="$2"; shift 2 ;;
    --bundle-only) mode="bundle"; shift ;;
    --validate-only) mode="validate"; shift ;;
    -h|--help) sed -n '2,32p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

# python3 on the runners and on Linux; plain python on the Windows dev box (CLAUDE.md).
py="$(command -v python3 || command -v python || true)"
[ -n "$py" ] || { echo "python is required (plist checks and the icon extractor)" >&2; exit 2; }

log() { printf '%s\n' "$*"; }
die() { printf 'error: %s\n' "$*" >&2; exit 1; }
have() { command -v "$1" >/dev/null 2>&1; }

# ---------------------------------------------------------------------------------------------
# App metadata: everything that differs between the two bundles, in one place.
# ---------------------------------------------------------------------------------------------
describe_app() {
  case "$1" in
    fits)
      exe="tianwen-fits"
      display="Astro Photo Viewer"
      icon="$repo/src/TianWen.UI.FitsViewer/Resources/MilkyWay.ico"
      template="$here/tianwen-fits.Info.plist.in"
      ;;
    gui)
      exe="tianwen-gui"
      display="TianWen"
      icon="$repo/src/TianWen.UI.Gui/Resources/HelixNebula.ico"
      template="$here/tianwen-gui.Info.plist.in"
      ;;
    *) die "--app must be fits or gui (got '$1')" ;;
  esac
}

# Substitute the two placeholders into a template and prove the result is a plist.
render_plist() {
  local tmpl="$1" ver="$2" bld="$3" dest="$4"
  sed -e "s/@VERSION@/$ver/g" -e "s/@BUILD@/$bld/g" "$tmpl" > "$dest"
  "$py" - "$dest" <<'EOF'
import plistlib, sys
with open(sys.argv[1], 'rb') as f:
    d = plistlib.load(f)
for key in ('CFBundleExecutable', 'CFBundleIdentifier', 'CFBundleShortVersionString', 'CFBundleVersion'):
    if not d.get(key):
        raise SystemExit(f'{sys.argv[1]}: {key} is missing or empty')
if '@' in d['CFBundleShortVersionString'] or '@' in d['CFBundleVersion']:
    raise SystemExit(f'{sys.argv[1]}: a placeholder survived substitution')
EOF
}

# Is this file a Mach-O (thin or fat)? By magic number, so no dependency on `file`.
is_macho() {
  local magic
  magic="$(od -An -tx1 -N4 "$1" 2>/dev/null | tr -d ' \n')"
  case "$magic" in
    feedface|cefaedfe|feedfacf|cffaedfe|cafebabe|bebafeca) return 0 ;;
    *) return 1 ;;
  esac
}

# ---------------------------------------------------------------------------------------------
# --validate-only: what CI checks on every push, on ubuntu, in about a second.
# ---------------------------------------------------------------------------------------------
if [ "$mode" = "validate" ]; then
  bash -n "$0" || die "this script does not parse"
  tmp="$(mktemp -d)"
  trap 'rm -rf "$tmp"' EXIT
  for a in fits gui; do
    describe_app "$a"
    [ -f "$template" ] || die "missing template $template"
    [ -f "$icon" ] || die "missing icon $icon"
    render_plist "$template" "1.2.3" "1.2.3" "$tmp/$a.plist"
    "$py" "$here/ico-to-iconset.py" "$icon" "$tmp/$a.png" >/dev/null
    log "ok: $a -> $(sed -n 's/.*<string>\(.*\)<\/string>.*/\1/p' "$tmp/$a.plist" | sed -n 3p) ($exe), icon frame extracted"
  done
  "$py" - "$here/entitlements.plist" <<'EOF'
import plistlib, sys
with open(sys.argv[1], 'rb') as f:
    d = plistlib.load(f)
if d.get('com.apple.security.cs.allow-jit'):
    raise SystemExit('entitlements: allow-jit is granted; a NativeAOT binary must not ask for it')
print(f'ok: entitlements parse ({len(d)} granted)')
EOF
  log "ok: packaging/macos validates"
  exit 0
fi

# ---------------------------------------------------------------------------------------------
# Bundle.
# ---------------------------------------------------------------------------------------------
[ -n "$app" ] || die "--app is required"
[ -n "$publish_dir" ] || die "--publish-dir is required"
[ -n "$version" ] || die "--version is required"
[ -n "$out" ] || die "--out is required"
[ -d "$publish_dir" ] || die "publish dir not found: $publish_dir"
build="${build:-$version}"
describe_app "$app"
[ -f "$publish_dir/$exe" ] || die "no $exe in $publish_dir (is this the right publish tree?)"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
bundle="$work/$display.app"
contents="$bundle/Contents"
mkdir -p "$contents/MacOS" "$contents/Resources"

# The WHOLE publish tree goes into Contents/MacOS, data files included (models/, the notices, the
# fonts). AppContext.BaseDirectory is the executable's directory, and that is where ModelResolver,
# BundledFonts, SkyMapTab's milkyway raster and the licence attachments look; moving them to
# Resources would be tidier and would break every one of those lookups.
#
# This comment used to end "codesign seals everything under Contents/ as a resource either way, so
# nothing here is left out of the signature". That is FALSE for this one directory, and believing
# it cost two release runs. Contents/MacOS is nested=true in codesign's default resource rules, so
# nothing in it is sealed as a resource: every file there is treated as CODE and must carry its own
# signature. The signing block below therefore signs all of them, not just the Mach-Os.
cp -R "$publish_dir/." "$contents/MacOS/"
chmod +x "$contents/MacOS/$exe"

# Debug artefacts, dropped before anything is signed. They would sign fine now, but they have no
# runtime value in a shipped bundle (symbolication uses the raw publish/ artifact, never the .app)
# and the .dSYM alone is 34.8 of the tree's 177 MB.
#
# The .pdb files belong to the referenced assemblies (AI, AI.Imaging, Hosting.Contracts,
# RemoteClient, UI.Abstractions, UI.Shared): the entry assembly opts out with DebugType=none in the
# two viewer csprojs, but that property is per-project and does not reach them. The .dSYM is the
# bundle the AOT publish leaves beside the binary.
find "$contents/MacOS" -name '*.pdb' -delete
find "$contents/MacOS" -name '*.dSYM' -type d -prune -exec rm -rf {} +
render_plist "$template" "$version" "$build" "$contents/Info.plist"
printf 'APPL????' > "$contents/PkgInfo"

# Icon: the .ico's 256 px PNG frame, resampled to the sizes an .iconset wants. The 512 and 1024
# entries are upscaled from 256, which is soft but not wrong; a larger source frame in the .ico
# is all it takes to fix that.
if have sips && have iconutil; then
  iconset="$work/AppIcon.iconset"
  mkdir -p "$iconset"
  "$py" "$here/ico-to-iconset.py" "$icon" "$work/icon-256.png" >/dev/null
  for size in 16 32 128 256 512; do
    sips -z "$size" "$size" "$work/icon-256.png" --out "$iconset/icon_${size}x${size}.png" >/dev/null
    double=$((size * 2))
    sips -z "$double" "$double" "$work/icon-256.png" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
  done
  iconutil -c icns "$iconset" -o "$contents/Resources/AppIcon.icns"
else
  log "warning: sips/iconutil not available; the bundle gets no icon (fine off macOS)"
fi

log "bundled $display.app ($exe $version, build $build) from $publish_dir"

if [ "$mode" = "bundle" ]; then
  mkdir -p "$out"
  rm -rf "${out:?}/$display.app"
  cp -R "$bundle" "$out/"
  log "bundle-only: $out/$display.app"
  exit 0
fi

# ---------------------------------------------------------------------------------------------
# Sign: everything inside Contents/MacOS first, then the executable, then the bundle. Inner-most
# first is Apple's rule (--deep is deprecated for good reason: it re-signs in an order that can
# invalidate what it just signed). Ad-hoc signatures cannot carry a timestamp, so that flag is
# conditional.
#
# EVERY file, not only the Mach-Os, for the nested=true reason given where the tree is copied in.
# An unsigned data file under Contents/MacOS fails the signing of the EXECUTABLE, several commands
# before any verify, with "code object is not signed at all / In subcomponent: <that file>".
#
# Measured on macos-latest over seven bundle shapes (2026-09-12), after two release runs died here:
# leaving the data files unsigned failed naming whichever one the walk reached first --
# TianWen.AI.Imaging.pdb, then LICENSE.EXCEPTION once the pdbs were stripped, then LICENSE, then
# milkyway.bgra.lz -- which is what says this is one CLASS, not one bad file. Signing them all
# passed. So did moving them to Contents/Resources (Apple's own layout, and the thing that breaks
# AppContext.BaseDirectory). Dropping --deep from the verify below fixed nothing, because the
# failure is not in the verify. codesign keeps a non-Mach-O file's signature in an extended
# attribute, which is why it survives the cp -R and hdiutil below.
# ---------------------------------------------------------------------------------------------
have codesign || die "codesign is required from here on; use --bundle-only elsewhere"
identity="${MACOS_SIGN_IDENTITY:--}"
sign_args=(--force --sign "$identity" --options runtime)
if [ "$identity" != "-" ]; then
  sign_args+=(--timestamp)
  log "signing with Developer ID: $identity"
else
  log "signing AD-HOC (no MACOS_SIGN_IDENTITY); Gatekeeper will need Open Anyway on another Mac"
fi

signed_code=0
signed_data=0
while IFS= read -r -d '' f; do
  [ "$f" = "$contents/MacOS/$exe" ] && continue
  # Classified before signing, purely so the log line below says what was in there; signing a
  # Mach-O keeps its magic, but reading it first keeps the count honest whatever codesign does.
  if is_macho "$f"; then
    signed_code=$((signed_code + 1))
  else
    signed_data=$((signed_data + 1))
  fi
  codesign "${sign_args[@]}" "$f"
done < <(find "$contents/MacOS" -type f -print0)
codesign "${sign_args[@]}" --entitlements "$here/entitlements.plist" "$contents/MacOS/$exe"
codesign "${sign_args[@]}" --entitlements "$here/entitlements.plist" "$bundle"
codesign --verify --deep --strict --verbose=2 "$bundle"
log "signed $signed_code Mach-O files, $signed_data data files, the executable and the bundle"

# ---------------------------------------------------------------------------------------------
# Disk image: the .app beside an /Applications link, compressed, then signed like the app.
# ---------------------------------------------------------------------------------------------
have hdiutil || die "hdiutil is required to build the image"
staging="$work/dmg-root"
mkdir -p "$staging"
cp -R "$bundle" "$staging/"
ln -s /Applications "$staging/Applications"
mkdir -p "$(dirname "$out")"
rm -f "$out"
hdiutil create -volname "$display" -srcfolder "$staging" -ov -format UDZO -quiet "$out"
if [ "$identity" != "-" ]; then
  codesign --force --sign "$identity" --timestamp "$out"
fi
log "wrote $out ($(du -h "$out" | cut -f1))"

# ---------------------------------------------------------------------------------------------
# Notarize and staple, when there is something to notarize with.
# ---------------------------------------------------------------------------------------------
if [ "$identity" = "-" ]; then
  log "not notarizing: an ad-hoc signature cannot be notarized"
elif [ -z "${MACOS_NOTARY_KEY:-}" ] || [ -z "${MACOS_NOTARY_KEY_ID:-}" ] || [ -z "${MACOS_NOTARY_ISSUER_ID:-}" ]; then
  log "not notarizing: MACOS_NOTARY_KEY / _KEY_ID / _ISSUER_ID are not all set"
else
  log "submitting to the notary service (this waits for Apple)..."
  xcrun notarytool submit "$out" \
    --key "$MACOS_NOTARY_KEY" --key-id "$MACOS_NOTARY_KEY_ID" --issuer "$MACOS_NOTARY_ISSUER_ID" \
    --wait
  xcrun stapler staple "$out"
  # The assessment Gatekeeper itself makes on a downloaded image; a failure here is the one a user
  # would see, so it fails the job rather than the user.
  spctl -a -t open --context context:primary-signature -v "$out"
  log "notarized and stapled $out"
fi
