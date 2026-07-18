#!/usr/bin/env bash
# Vendor the protovalidate protos (rule schema + conformance cases) at a pinned commit.
#
# The library schema (proto/protovalidate/buf/validate/validate.proto) drives Celly.Protovalidate;
# the conformance cases (proto/protovalidate-testing/**) drive the conformance executor. Both are
# codegen'd to C# at build time by Grpc.Tools — nothing needs regenerating here.
#
# Conformance is verified with buf's official runner (not part of `dotnet test`):
#   go install github.com/bufbuild/protovalidate/tools/protovalidate-conformance@<pinned>
#   protovalidate-conformance -- dotnet run -c Release --project tests/Celly.Protovalidate.Conformance
set -euo pipefail

PIN="3807e3d1c38b48295eae269e2f3b97cee668edd3"   # protovalidate pinned commit
REPO="https://github.com/bufbuild/protovalidate.git"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
git clone --quiet "$REPO" "$tmp"
git -C "$tmp" checkout --quiet "$PIN"

rm -rf "$ROOT/proto/protovalidate" "$ROOT/proto/protovalidate-testing"
mkdir -p "$ROOT/proto/protovalidate" "$ROOT/proto/protovalidate-testing"
cp -R "$tmp/proto/protovalidate/buf"          "$ROOT/proto/protovalidate/"
cp -R "$tmp/proto/protovalidate-testing/buf"  "$ROOT/proto/protovalidate-testing/"
echo "$PIN" > "$ROOT/proto/protovalidate/VENDORED_COMMIT.txt"

echo "Vendored protovalidate @ $PIN"
