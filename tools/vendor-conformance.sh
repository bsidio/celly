#!/usr/bin/env bash
# Vendors cel-spec protos and conformance testdata into this repo.
#
# - Copies the canonical cel.expr protos into proto/ (consumed by Grpc.Tools codegen).
# - Converts tests/simple/testdata/*.textproto to binary testdata/*.binpb via
#   `protoc --encode`, because Google.Protobuf C# has no textproto parser.
#
# Requires: git, protoc. Run from anywhere; operates on the repo root.
set -euo pipefail

CEL_SPEC_REPO="https://github.com/cel-expr/cel-spec.git"
CEL_SPEC_COMMIT="59505c14f3187e6eb9684fbd3d07146f614c6148"  # 2026-07-06

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

echo "Cloning cel-spec @ ${CEL_SPEC_COMMIT}..."
git init -q "$WORK/cel-spec"
git -C "$WORK/cel-spec" remote add origin "$CEL_SPEC_REPO"
git -C "$WORK/cel-spec" fetch -q --depth 1 origin "$CEL_SPEC_COMMIT"
git -C "$WORK/cel-spec" checkout -q FETCH_HEAD

SPEC="$WORK/cel-spec"

echo "Copying protos..."
rm -rf "$ROOT/proto/cel"
mkdir -p "$ROOT/proto/cel/expr/conformance/test" \
         "$ROOT/proto/cel/expr/conformance/proto2" \
         "$ROOT/proto/cel/expr/conformance/proto3"
cp "$SPEC"/proto/cel/expr/{syntax,checked,value,eval,explain}.proto "$ROOT/proto/cel/expr/"
cp "$SPEC"/proto/cel/expr/conformance/test/{simple,suite}.proto "$ROOT/proto/cel/expr/conformance/test/"
cp "$SPEC"/proto/cel/expr/conformance/proto2/*.proto "$ROOT/proto/cel/expr/conformance/proto2/"
cp "$SPEC"/proto/cel/expr/conformance/proto3/*.proto "$ROOT/proto/cel/expr/conformance/proto3/"

echo "Encoding testdata textprotos to binpb..."
mkdir -p "$ROOT/testdata"
rm -f "$ROOT/testdata"/*.binpb
count=0
for tp in "$SPEC"/tests/simple/testdata/*.textproto; do
  name="$(basename "$tp" .textproto)"
  # TestAllTypes protos are needed so protoc can resolve messages embedded in google.protobuf.Any.
  protoc --proto_path="$SPEC/proto" \
         --encode=cel.expr.conformance.test.SimpleTestFile \
         cel/expr/conformance/test/simple.proto \
         cel/expr/conformance/proto2/test_all_types.proto \
         cel/expr/conformance/proto2/test_all_types_extensions.proto \
         cel/expr/conformance/proto3/test_all_types.proto \
         < "$tp" > "$ROOT/testdata/$name.binpb"
  count=$((count + 1))
done

{
  echo "Vendored from $CEL_SPEC_REPO @ $CEL_SPEC_COMMIT"
  echo "Files: $count textprotos encoded as SimpleTestFile binpb."
  echo "Refresh with tools/vendor-conformance.sh — do not edit by hand."
} > "$ROOT/testdata/VENDORED.txt"

echo "Done: $count testdata files encoded."
