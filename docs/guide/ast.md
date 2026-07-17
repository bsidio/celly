# Working with ASTs

Celly treats the AST as a first-class artifact: you can inspect it, convert it to and from
the canonical `cel.expr` protos, turn it back into source text, and evaluate ASTs that
arrived from elsewhere.

## Inspect and traverse

```csharp
using Celly.Ast;

var ast = env.Parse("a.b + c && list.exists(x, x > threshold)").Ast!;

AstTools.ReferencedVariables(ast.Expr);   // {"a", "c", "list", "threshold"} — free roots,
                                          // comprehension variables excluded
AstTools.CalledFunctions(ast.Expr);       // {"_+_", "_&&_", "_>_", ...}
AstTools.DescendantsAndSelf(ast.Expr);    // pre-order walk of every node
AstTools.Children(node);                  // direct children of one node
```

Typical uses: validating that a stored policy only references allowed variables, computing
which inputs to fetch before evaluation, static linting.

After a successful `env.Check(ast)`, the AST also carries:

- `ast.TypeMap` — every expression id → deduced `CelType`
- `ast.ReferenceMap` — resolved variable names and matched overload ids per call

## Proto interop (`Celly.Protobuf`)

`AstConverter` converts losslessly between Celly's AST and the canonical `cel.expr`
protos — node ids, source positions, macro-call records, types, and references all
round-trip:

```csharp
using Celly.Protobuf;
using Google.Protobuf;

// Native → proto → bytes: cache or ship the compiled policy
var parsedProto = AstConverter.ToParsedExpr(ast);          // cel.expr.ParsedExpr
byte[] blob = parsedProto.ToByteArray();

// Bytes → proto → native → evaluate (no re-parse needed)
var restored = AstConverter.FromParsedExpr(Cel.Expr.ParsedExpr.Parser.ParseFrom(blob));
var program = env.Program(restored);

// Checked ASTs carry the type + reference maps
env.Check(ast);
Cel.Expr.CheckedExpr checkedProto = AstConverter.ToCheckedExpr(ast);
var rehydrated = AstConverter.FromCheckedExpr(checkedProto);  // IsChecked == true
```

Because `cel.expr` is the interchange format of the whole CEL ecosystem, this is the bridge
to everything else: store `CheckedExpr` blobs in a policy database, accept ASTs produced by
a cel-go service, or hand Celly-compiled expressions to another runtime.

Individual pieces are exposed too: `AstConverter.ToProto`/`FromProto` for bare `Expr`
nodes, and `AstConverter.ToProtoType`/`TypeConverter.ToCelType` for the type model.

## Unparse (AST → source text)

```csharp
using Celly.Ast;

var ast = env.Parse("[1, 2].all(x, x > 0) && has(m.f)").Ast!;
Unparser.Unparse(ast);   // "[1, 2].all(x, x > 0) && has(m.f)"
```

- Macro expansions render in their **original call form** (the AST records the pre-expansion
  call), so `all`/`exists`/`has`/`cel.bind` come back as written, not as raw comprehensions.
- Parenthesization is minimal-but-correct: `1 + (2 * 3)` unparses as `1 + 2 * 3`, while
  `(1 + 2) * 3` and `a - (b - c)` keep their required parens.
- Non-identifier field names use backtick escaping (`m.`content-type``).
- Guaranteed stable: `Unparse(Parse(Unparse(x)))` is a fixed point (tested).

Typical uses: policy pretty-printing, showing users a normalized form of what they wrote,
generating expressions programmatically and rendering them.

## Build ASTs programmatically

The AST node types are public with ordinary constructors (`ConstExpr`, `CallExpr`, …);
ids just need to be unique within one expression. Construct a tree, wrap it in
`CelAbstractSyntax`, and hand it to `env.Program(...)` — or unparse it to source.
