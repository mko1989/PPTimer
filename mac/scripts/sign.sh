#!/usr/bin/env bash
# Signs PPTimer.app with a stable, self-signed identity.
#
# macOS remembers privacy permissions (here: PowerPoint Automation) per code signature. Ad-hoc
# signatures change on every build, so each rebuild would ask again. This keeps one certificate in
# its own keychain (~/Library/Keychains/pptimer-signing.keychain-db, created on first use) so the
# signature stays the same. Your login keychain's contents are not touched; the new keychain is
# appended to your keychain search list so codesign can find it.
#
# Set PPTIMER_SIGN_IDENTITY to sign with a real certificate instead (e.g. "Developer ID Application: …").
#
#   mac/scripts/sign.sh path/to/PPTimer.app
set -euo pipefail

APP="$1"

if [[ -n "${PPTIMER_SIGN_IDENTITY:-}" ]]; then
  codesign --force --deep --sign "$PPTIMER_SIGN_IDENTITY" "$APP"
  exit 0
fi

NAME="PPTimer Local Signing"
KEYCHAIN="$HOME/Library/Keychains/pptimer-signing.keychain-db"
PASS="pptimer-local"

if [[ ! -f "$KEYCHAIN" ]]; then
  echo "Creating signing keychain $KEYCHAIN"
  TMP="$(mktemp -d)"
  trap 'rm -rf "$TMP"' EXIT
  cat > "$TMP/cert.cnf" <<EOF
[req]
distinguished_name = dn
x509_extensions = ext
prompt = no
[dn]
CN = $NAME
[ext]
basicConstraints = critical, CA:false
keyUsage = critical, digitalSignature
extendedKeyUsage = critical, codeSigning
EOF
  /usr/bin/openssl req -x509 -newkey rsa:2048 -nodes -days 3650 -config "$TMP/cert.cnf" \
    -keyout "$TMP/key.pem" -out "$TMP/cert.pem" 2>/dev/null
  /usr/bin/openssl pkcs12 -export -inkey "$TMP/key.pem" -in "$TMP/cert.pem" -name "$NAME" \
    -out "$TMP/id.p12" -passout "pass:$PASS"
  security create-keychain -p "$PASS" "$KEYCHAIN"
  security set-keychain-settings "$KEYCHAIN"   # never auto-lock
  security import "$TMP/id.p12" -k "$KEYCHAIN" -P "$PASS" -T /usr/bin/codesign >/dev/null
  security set-key-partition-list -S apple-tool:,apple: -s -k "$PASS" "$KEYCHAIN" >/dev/null
fi

security unlock-keychain -p "$PASS" "$KEYCHAIN"
# codesign only finds identities in keychains on the search list; append ours (login stays first).
if ! security list-keychains -d user | grep -q pptimer-signing; then
  eval "security list-keychains -d user -s $(security list-keychains -d user | tr '\n' ' ') \"$KEYCHAIN\""
fi
codesign --force --deep --keychain "$KEYCHAIN" --sign "$NAME" "$APP"
