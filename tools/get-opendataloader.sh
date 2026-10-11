#!/usr/bin/env bash
# Downloads the OpenDataLoader PDF reader (Apache-2.0) used by the morning run.
# It runs locally inside our own process / container (rule 2); hybrid (AI) mode stays off.
set -euo pipefail
VERSION="${ODL_VERSION:-2.5.11}"
DIR="$(cd "$(dirname "$0")" && pwd)/opendataloader"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
mkdir -p "$DIR"
curl -fsSL --retry 3 -o "$TMP/odl.zip" \
  "https://github.com/opendataloader-project/opendataloader-pdf/releases/download/v${VERSION}/opendataloader-pdf-cli-${VERSION}.zip"
unzip -q -o "$TMP/odl.zip" -d "$TMP/x"
cp "$TMP/x/opendataloader-pdf-cli-${VERSION}.jar" "$DIR/opendataloader-pdf.jar"
cp "$TMP/x/LICENSE" "$DIR/LICENSE"
echo "OpenDataLoader ${VERSION} -> $DIR/opendataloader-pdf.jar"
