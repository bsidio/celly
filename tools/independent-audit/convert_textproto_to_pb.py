#!/usr/bin/env python3
"""Reproduces the accusatory report's step 1: convert every cel-spec textproto to binary
protobuf using Python's text_format parser (Google's C# lib has no text_format API).
Output: one <name>.binpb per test file, byte-serialized cel.expr.conformance.test.SimpleTestFile.
"""
import sys, os, glob
sys.path.insert(0, os.path.join(os.path.dirname(__file__), 'pygen'))
from google.protobuf import text_format
from cel.expr.conformance.test import simple_pb2
# import proto2/proto3 TestAllTypes so text_format can resolve Any payloads & message types
from cel.expr.conformance.proto2 import test_all_types_pb2 as _p2
from cel.expr.conformance.proto2 import test_all_types_extensions_pb2 as _p2e
from cel.expr.conformance.proto3 import test_all_types_pb2 as _p3

spec_testdata, out_dir = sys.argv[1], sys.argv[2]
total_files = total_tests = 0
for tp in sorted(glob.glob(os.path.join(spec_testdata, '*.textproto'))):
    name = os.path.splitext(os.path.basename(tp))[0]
    with open(tp) as f:
        text = f.read()
    msg = simple_pb2.SimpleTestFile()
    text_format.Parse(text, msg)                    # <-- the report's key step
    n = sum(len(sec.test) for sec in msg.section)
    total_files += 1; total_tests += n
    with open(os.path.join(out_dir, name + '.binpb'), 'wb') as f:
        f.write(msg.SerializeToString())
print(f"converted {total_files} files, {total_tests} tests via Python text_format")
