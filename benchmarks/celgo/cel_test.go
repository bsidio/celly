package celgobench

import (
	"testing"

	"github.com/google/cel-go/cel"
	"github.com/google/cel-go/common/types/ref"
)

// Same two expressions and data as the Celly / Cel.NET benchmarks:
//   Simple:        x + 1 > 3 && name.startsWith('h')
//   Comprehension: items.filter(i, i % 2 == 0).map(i, i * i).exists(i, i > 100)
const simpleExpr = "x + 1 > 3 && name.startsWith('h')"
const compExpr = "items.filter(i, i % 2 == 0).map(i, i * i).exists(i, i > 100)"

func mustCompile(expr string, decls ...cel.EnvOption) cel.Program {
	env, err := cel.NewEnv(decls...)
	if err != nil {
		panic(err)
	}
	ast, iss := env.Compile(expr)
	if iss != nil && iss.Err() != nil {
		panic(iss.Err())
	}
	prg, err := env.Program(ast)
	if err != nil {
		panic(err)
	}
	return prg
}

func items() []int64 {
	xs := make([]int64, 100)
	for i := range xs {
		xs[i] = int64(i)
	}
	return xs
}

func BenchmarkCelGo_Eval_Simple(b *testing.B) {
	prg := mustCompile(simpleExpr,
		cel.Variable("x", cel.IntType),
		cel.Variable("name", cel.StringType))
	act := map[string]any{"x": int64(42), "name": "hello"}
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		var out ref.Val
		out, _, _ = prg.Eval(act)
		_ = out
	}
}

func BenchmarkCelGo_Eval_Comprehension(b *testing.B) {
	prg := mustCompile(compExpr,
		cel.Variable("items", cel.ListType(cel.IntType)))
	act := map[string]any{"items": items()}
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		var out ref.Val
		out, _, _ = prg.Eval(act)
		_ = out
	}
}

func BenchmarkCelGo_Compile_Simple(b *testing.B) {
	b.ReportAllocs()
	for i := 0; i < b.N; i++ {
		_ = mustCompile(simpleExpr,
			cel.Variable("x", cel.IntType),
			cel.Variable("name", cel.StringType))
	}
}
