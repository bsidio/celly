// Differential-fuzzer cel-go side: read expressions (one per line), evaluate each against the
// same fixed activation the .NET side uses, and print one canonical result per line. The
// normalizer here MUST match tools/differential/Program.cs byte-for-byte.
package main

import (
	"bufio"
	"encoding/base64"
	"encoding/hex"
	"fmt"
	"math"
	"os"
	"sort"
	"strings"

	"github.com/google/cel-go/cel"
	"github.com/google/cel-go/common/types"
	"github.com/google/cel-go/common/types/ref"
	"github.com/google/cel-go/common/types/traits"
)

func env() *cel.Env {
	e, err := cel.NewEnv(
		cel.Variable("i", cel.IntType),
		cel.Variable("j", cel.IntType),
		cel.Variable("u", cel.UintType),
		cel.Variable("d", cel.DoubleType),
		cel.Variable("b", cel.BoolType),
		cel.Variable("s", cel.StringType),
		cel.Variable("li", cel.ListType(cel.IntType)),
		cel.Variable("ls", cel.ListType(cel.StringType)),
		cel.Variable("m", cel.MapType(cel.StringType, cel.IntType)),
	)
	if err != nil {
		panic(err)
	}
	return e
}

func activation() map[string]any {
	return map[string]any{
		"i":  int64(7),
		"j":  int64(-3),
		"u":  uint64(42),
		"d":  2.5,
		"b":  true,
		"s":  "hello",
		"li": []int64{1, 2, 3, 4},
		"ls": []string{"a", "bb"},
		"m":  map[string]int64{"a": 1, "b": 2},
	}
}

func evalOne(e *cel.Env, act map[string]any, expr string) string {
	ast, iss := e.Parse(expr)
	if iss != nil && iss.Err() != nil {
		return "ERROR"
	}
	prg, err := e.Program(ast)
	if err != nil {
		return "ERROR"
	}
	out, _, err := prg.Eval(act)
	if err != nil {
		return "ERROR"
	}
	return normalize(out)
}

func normalize(v ref.Val) string {
	switch v.Type() {
	case types.BoolType:
		if v.Value().(bool) {
			return "b:1"
		}
		return "b:0"
	case types.IntType:
		return fmt.Sprintf("i:%d", v.Value().(int64))
	case types.UintType:
		return fmt.Sprintf("u:%d", v.Value().(uint64))
	case types.DoubleType:
		return fmt.Sprintf("d:%016x", math.Float64bits(v.Value().(float64)))
	case types.StringType:
		return "s:" + base64.StdEncoding.EncodeToString([]byte(v.Value().(string)))
	case types.BytesType:
		return "y:" + hex.EncodeToString(v.Value().([]byte))
	case types.NullType:
		return "null"
	case types.TypeType:
		return "t:" + v.(ref.Val).(*types.Type).TypeName()
	case types.ListType:
		lister := v.(traits.Lister)
		var parts []string
		it := lister.Iterator()
		for it.HasNext() == types.True {
			parts = append(parts, normalize(it.Next()))
		}
		return "l:[" + strings.Join(parts, ",") + "]"
	case types.MapType:
		mapper := v.(traits.Mapper)
		var pairs []string
		it := mapper.Iterator()
		for it.HasNext() == types.True {
			k := it.Next()
			val := mapper.Get(k)
			pairs = append(pairs, normalize(k)+"="+normalize(val))
		}
		sort.Strings(pairs)
		return "m:{" + strings.Join(pairs, ",") + "}"
	default:
		if types.IsError(v) || types.IsUnknown(v) {
			return "ERROR"
		}
		return "OTHER:" + v.Type().TypeName()
	}
}

func main() {
	if len(os.Args) < 2 {
		fmt.Fprintln(os.Stderr, "usage: celgo <exprfile>")
		os.Exit(2)
	}
	data, err := os.ReadFile(os.Args[1])
	if err != nil {
		panic(err)
	}
	e := env()
	act := activation()
	w := bufio.NewWriter(os.Stdout)
	defer w.Flush()
	for _, line := range strings.Split(strings.TrimRight(string(data), "\n"), "\n") {
		fmt.Fprintln(w, safeEval(e, act, line))
	}
}

// safeEval guards against cel-go panicking on a pathological expression — that would itself be a
// finding, but we record it as a distinct token rather than crashing the harness.
func safeEval(e *cel.Env, act map[string]any, expr string) (result string) {
	defer func() {
		if r := recover(); r != nil {
			result = "PANIC"
		}
	}()
	return evalOne(e, act, expr)
}
