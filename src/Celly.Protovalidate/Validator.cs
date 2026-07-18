using System.Collections;
using Buf.Validate;
using Celly;
using Celly.Extensions;
using Celly.Interpreter;
using Celly.Protobuf;
using Celly.Values;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Celly.Protovalidate;

/// <summary>
/// Validates protobuf messages against their <c>buf.validate</c> rules, evaluating the rules'
/// embedded CEL with the Celly engine. Build one validator for a set of message descriptors and
/// reuse it; it is safe for concurrent use.
/// </summary>
public sealed class Validator
{
    private readonly ProtoTypeRegistry _registry;
    private readonly CelEnv _env;
    private readonly Dictionary<string, CelProgram> _programs = new();
    private readonly object _programLock = new();
    private readonly Dictionary<string, List<FieldDescriptor>> _extensionsByType = new();

    /// <summary>Creates a validator that understands the given proto files' message types.</summary>
    public Validator(params FileDescriptor[] fileDescriptors)
    {
        _registry = ProtoTypeRegistry.FromFiles(fileDescriptors);
        foreach (var file in fileDescriptors)
        {
            CollectExtensions(file.Extensions.UnorderedExtensions);
            foreach (var message in file.MessageTypes)
            {
                CollectNestedExtensions(message);
            }
        }

        _env = CelEnv.Create(new CelEnvSettings
        {
            TypeProvider = _registry,
            Adapter = _registry,
            Libraries =
            [
                OptionalsLibrary.Instance, StringsLibrary.Instance, MathLibrary.Instance,
                EncodersLibrary.Instance, ProtovalidateFunctions.Create(_registry),
            ],
        });
    }

    /// <summary>Validates a message; returns all violations (empty ⇒ valid).</summary>
    public IReadOnlyList<Violation> Validate(IMessage message)
    {
        var violations = new List<Violation>();
        ValidateMessage(message, violations, []);
        return violations;
    }

    private void ValidateMessage(IMessage message, List<Violation> violations, IReadOnlyList<FieldPathElement> path)
    {
        var oneofMembers = new HashSet<string>();
        var messageRules = message.Descriptor.GetOptions()?.GetExtension(ValidateExtensions.Message);
        if (messageRules is not null)
        {
            // Message-level violations carry only a rule_id at the top level; when the message is
            // nested (recursed into), they point at its path.
            var messageField = path.Count > 0 ? new FieldPath { Elements = { path } } : null;
            var self = _registry.AdaptMessage(message);
            foreach (var rule in messageRules.Cel)
            {
                var violation = EvalRule(rule, self, self, self, rule.Id);
                if (violation is not null)
                {
                    violation.Field = messageField;
                    violations.Add(violation);
                }
            }

            foreach (var expression in messageRules.CelExpression)
            {
                var violation = EvalExpression(expression, self);
                if (violation is not null)
                {
                    violation.Field = messageField;
                    violations.Add(violation);
                }
            }

            // message.oneof: at most one of the named fields may be set (exactly one if required).
            foreach (var oneof in messageRules.Oneof)
            {
                if (oneof.Fields.Count == 0)
                {
                    throw new ValidationCompileException(
                        $"at least one field must be specified in oneof rule for the message {message.Descriptor.FullName}");
                }

                var seen = new HashSet<string>();
                var setCount = 0;
                foreach (var name in oneof.Fields)
                {
                    var member = message.Descriptor.FindFieldByName(name)
                        ?? throw new ValidationCompileException(
                            $"field {name} not found in message {message.Descriptor.FullName}");
                    if (!seen.Add(name))
                    {
                        throw new ValidationCompileException(
                            $"duplicate {name} in oneof rule for the message {message.Descriptor.FullName}");
                    }

                    oneofMembers.Add(name);
                    if (IsPopulated(member, message))
                    {
                        setCount++;
                    }
                }

                if (setCount > 1 || (oneof.Required && setCount == 0))
                {
                    violations.Add(new Violation { RuleId = "message.oneof" });
                }
            }
        }

        // (buf.validate.oneof).required: one of the proto oneof's members must be set.
        foreach (var oneof in message.Descriptor.Oneofs)
        {
            if (oneof.IsSynthetic)
            {
                continue;
            }

            var oneofRules = oneof.GetOptions()?.GetExtension(ValidateExtensions.Oneof);
            if (oneofRules?.Required == true && oneof.Accessor.GetCaseFieldDescriptor(message) is null)
            {
                var oneofPath = new FieldPath { Elements = { path } };
                oneofPath.Elements.Add(new FieldPathElement { FieldName = oneof.Name });
                violations.Add(new Violation { Field = oneofPath, RuleId = "required" });
            }
        }

        foreach (var field in message.Descriptor.Fields.InFieldNumberOrder())
        {
            ValidateField(message, field, violations, path, oneofMembers);
        }
    }

    private void ValidateField(
        IMessage message, FieldDescriptor field, List<Violation> violations,
        IReadOnlyList<FieldPathElement> path, IReadOnlySet<string> oneofMembers)
    {
        var rules = field.GetOptions()?.GetExtension(ValidateExtensions.Field);
        var populated = IsPopulated(field, message);
        var fieldPath = Append(path, Element(field));

        // A field named in a message.oneof rule has its own rules ignored while it is unset.
        if (!populated && oneofMembers.Contains(field.Name))
        {
            return;
        }

        if (rules is not null)
        {
            CheckRuleType(rules, field);

            if (rules.Ignore == Ignore.Always)
            {
                return; // ignore_always short-circuits everything, including required
            }

            if (rules.Required && !populated)
            {
                violations.Add(new Violation
                {
                    Field = new FieldPath { Elements = { fieldPath } },
                    Rule = RulePath("required"),
                    RuleId = "required",
                });
                return;
            }

            if (!populated && (rules.Ignore == Ignore.IfZeroValue || field.HasPresence))
            {
                return; // unset presence-tracking field (or ignore_if_zero): skip everything
            }

            EvaluateRuleSet(rules, _registry.AdaptField(message, field), field.Accessor.GetValue(message),
                field, fieldPath, [], forKey: false, violations);
        }

        if (populated)
        {
            RecurseIntoMessages(field, message, fieldPath, violations);
        }
    }

    // Evaluate a FieldRules against a single 'this' value at the given path (also handles the
    // per-element recursion for repeated.items and map.keys/values rules).
    // A repeated item or map key/value: presence is by zero-value (no proto field presence), so
    // ignore/required apply against the value itself before its rules run.
    private void EvaluateElement(
        FieldRules rules, CelValue value, object? raw, FieldDescriptor field,
        IReadOnlyList<FieldPathElement> path, IReadOnlyList<FieldPathElement> rulePrefix,
        bool forKey, List<Violation> violations)
    {
        if (rules.Ignore == Ignore.Always)
        {
            return;
        }

        // `required` is satisfied for collection elements — the element is present by virtue of
        // being in the list/map. Only `ignore` affects whether an element's rules run.
        if (IsZeroValue(value) && rules.Ignore == Ignore.IfZeroValue)
        {
            return;
        }

        EvaluateRuleSet(rules, value, raw, field, path, rulePrefix, forKey, violations);
    }

    private static Violation AnyViolation(
        IReadOnlyList<FieldPathElement> path, IReadOnlyList<FieldPathElement> rulePrefix,
        FieldDescriptor typeField, IMessage ruleMessage, string ruleField, string ruleId, bool forKey)
    {
        var rule = new FieldPath { Elements = { rulePrefix } };
        rule.Elements.Add(Element(typeField));
        rule.Elements.Add(Element(ruleMessage.Descriptor.FindFieldByName(ruleField)));
        return new Violation
        {
            Field = new FieldPath { Elements = { path } },
            Rule = rule,
            RuleId = ruleId,
            ForKey = forKey,
        };
    }

    private void EvaluateRuleSet(
        FieldRules rules, CelValue thisValue, object? raw, FieldDescriptor field,
        IReadOnlyList<FieldPathElement> path, IReadOnlyList<FieldPathElement> rulePrefix,
        bool forKey, List<Violation> violations)
    {
        var typeOneof = ((IMessage)rules).Descriptor.Oneofs.FirstOrDefault(o => o.Name == "type");
        var typeField = typeOneof?.Accessor.GetCaseFieldDescriptor(rules);
        if (typeField?.Accessor.GetValue(rules) is IMessage ruleMessage)
        {
            var rulesValue = _registry.AdaptMessage(ruleMessage);
            var ruleFields = ruleMessage.Descriptor.Fields.InFieldNumberOrder()
                .Concat(_extensionsByType.GetValueOrDefault(ruleMessage.Descriptor.FullName, []));

            foreach (var ruleField in ruleFields)
            {
                if (!IsPopulated(ruleField, ruleMessage))
                {
                    continue;
                }

                var predefined = ruleField.GetOptions()?.GetExtension(ValidateExtensions.Predefined);
                if (predefined is null)
                {
                    continue;
                }

                var ruleValue = _registry.AdaptField(ruleMessage, ruleField);
                foreach (var rule in predefined.Cel)
                {
                    var violation = EvalRule(rule, thisValue, rulesValue, ruleValue, rule.Id);
                    if (violation is not null)
                    {
                        violation.Field = new FieldPath { Elements = { path } };
                        violation.Rule = new FieldPath { Elements = { rulePrefix } };
                        violation.Rule.Elements.Add(Element(typeField));
                        violation.Rule.Elements.Add(RuleElement(ruleField));
                        violation.ForKey = forKey;
                        violations.Add(violation);
                    }
                }
            }

            // enum.defined_only is not embedded CEL — the value must be a declared enum constant.
            if (ruleMessage is EnumRules { DefinedOnly: true } && field.EnumType is { } enumType
                && thisValue is IntValue enumValue && enumType.FindValueByNumber((int)enumValue.Value) is null)
            {
                var rule = new FieldPath { Elements = { rulePrefix } };
                rule.Elements.Add(Element(typeField));
                rule.Elements.Add(Element(ruleMessage.Descriptor.FindFieldByName("defined_only")));
                violations.Add(new Violation
                {
                    Field = new FieldPath { Elements = { path } },
                    Rule = rule,
                    RuleId = "enum.defined_only",
                    ForKey = forKey,
                });
            }

            // any.in / any.not_in are special rules over the Any's type_url (no embedded CEL).
            if (ruleMessage is AnyRules anyRules && raw is Google.Protobuf.WellKnownTypes.Any any)
            {
                if (anyRules.In.Count > 0 && !anyRules.In.Contains(any.TypeUrl))
                {
                    violations.Add(AnyViolation(path, rulePrefix, typeField, ruleMessage, "in", "any.in", forKey));
                }

                if (anyRules.NotIn.Contains(any.TypeUrl))
                {
                    violations.Add(AnyViolation(path, rulePrefix, typeField, ruleMessage, "not_in", "any.not_in", forKey));
                }
            }

            // repeated.items: apply the item rules to each element. Rule path gains [repeated, items].
            if (ruleMessage is RepeatedRules { Items: { } items } && thisValue is ListValue list)
            {
                var prefix = RulePrefix(rulePrefix, typeField, ruleMessage.Descriptor, "items");
                var rawList = raw as IList;
                for (var i = 0; i < list.Elements.Count; i++)
                {
                    EvaluateElement(items, list.Elements[i], rawList?[i], field, WithIndex(path, i), prefix, false, violations);
                }
            }

            // map.keys / map.values. Rule path gains [map, keys] or [map, values].
            if (ruleMessage is MapRules mapRules && thisValue is MapValue map)
            {
                foreach (var key in map.Keys)
                {
                    var keyPath = WithSubscript(path, key, field);
                    if (mapRules.Keys is { } keyRules)
                    {
                        var prefix = RulePrefix(rulePrefix, typeField, ruleMessage.Descriptor, "keys");
                        EvaluateElement(keyRules, key, null, field, keyPath, prefix, true, violations);
                    }

                    if (mapRules.Values is { } valueRules)
                    {
                        var prefix = RulePrefix(rulePrefix, typeField, ruleMessage.Descriptor, "values");
                        EvaluateElement(valueRules, map.Get(key), null, field, keyPath, prefix, false, violations);
                    }
                }
            }
        }

        for (var i = 0; i < rules.CelExpression.Count; i++)
        {
            var violation = EvalExpression(rules.CelExpression[i], thisValue);
            if (violation is not null)
            {
                var ruleElement = Element(FieldRules.Descriptor.FindFieldByName("cel_expression"));
                ruleElement.Index = (ulong)i;
                violation.Field = new FieldPath { Elements = { path } };
                violation.Rule = new FieldPath { Elements = { rulePrefix } };
                violation.Rule.Elements.Add(ruleElement);
                violation.ForKey = forKey;
                violations.Add(violation);
            }
        }

        for (var i = 0; i < rules.Cel.Count; i++)
        {
            var rule = rules.Cel[i];
            var violation = EvalRule(rule, thisValue, thisValue, thisValue, rule.Id);
            if (violation is not null)
            {
                violation.Field = new FieldPath { Elements = { path } };
                violation.Rule = new FieldPath { Elements = { rulePrefix } };
                violation.Rule.Elements.Add(CelElement(FieldRules.Descriptor, i));
                violation.ForKey = forKey;
                violations.Add(violation);
            }
        }
    }

    // The rule path element for the i-th custom `cel` rule: the `cel` field with index i.
    private static FieldPathElement CelElement(MessageDescriptor rulesType, int index)
    {
        var element = Element(rulesType.FindFieldByName("cel"));
        element.Index = (ulong)index;
        return element;
    }

    // Recurse into message-typed values (singular, repeated elements, map values) to validate their
    // own rules, even when the containing field carries no rules.
    private void RecurseIntoMessages(
        FieldDescriptor field, IMessage message, IReadOnlyList<FieldPathElement> fieldPath, List<Violation> violations)
    {
        var rules = field.GetOptions()?.GetExtension(ValidateExtensions.Field);
        if (field.IsMap)
        {
            var valueField = field.MessageType.Fields.InFieldNumberOrder()[1];
            if (valueField.FieldType is FieldType.Message or FieldType.Group && !IsWellKnown(valueField.MessageType)
                && rules?.Map?.Values?.Ignore != Ignore.Always)
            {
                foreach (DictionaryEntry entry in (IDictionary)field.Accessor.GetValue(message))
                {
                    ValidateMessage((IMessage)entry.Value!, violations, WithSubscript(fieldPath, _registry.NativeToValue(entry.Key), field));
                }
            }
        }
        else if (field.IsRepeated)
        {
            if (field.FieldType is FieldType.Message or FieldType.Group && !IsWellKnown(field.MessageType)
                && rules?.Repeated?.Items?.Ignore != Ignore.Always)
            {
                var list = (IList)field.Accessor.GetValue(message);
                for (var i = 0; i < list.Count; i++)
                {
                    ValidateMessage((IMessage)list[i]!, violations, WithIndex(fieldPath, i));
                }
            }
        }
        else if (field.FieldType is FieldType.Message or FieldType.Group && !IsWellKnown(field.MessageType)
                 && field.Accessor.GetValue(message) is IMessage sub)
        {
            ValidateMessage(sub, violations, fieldPath);
        }
    }

    // Evaluate one rule's CEL. Returns a Violation (rule_id set) on failure, null on pass.
    private Violation? EvalRule(Rule rule, CelValue thisValue, CelValue rulesValue, CelValue ruleValue, string ruleId) =>
        EvalExpression(rule.Expression, thisValue, rulesValue, ruleValue, ruleId, rule.Message);

    // The `cel_expression` shorthand: a bare bool/string expression whose rule_id is the text itself.
    private Violation? EvalExpression(string expression, CelValue thisValue) =>
        EvalExpression(expression, thisValue, thisValue, thisValue, expression, "");

    private Violation? EvalExpression(
        string expression, CelValue thisValue, CelValue rulesValue, CelValue ruleValue, string ruleId, string message)
    {
        var program = Compile(expression);
        var now = DateTimeOffset.UtcNow;
        var result = program.Eval(new ValueActivation(new Dictionary<string, CelValue>
        {
            ["this"] = thisValue,
            ["rules"] = rulesValue,
            ["rule"] = ruleValue,
            // protovalidate binds `now` (current time) for timestamp.within / *_now rules.
            ["now"] = TimestampValue.Of(now.ToUnixTimeSeconds(), now.Nanosecond),
        }));

        return result switch
        {
            BoolValue { Value: true } => null,
            StringValue { Value: "" } => null,
            BoolValue { Value: false } => new Violation { RuleId = ruleId, Message = message },
            StringValue s => new Violation { RuleId = ruleId, Message = s.Value },
            ErrorValue e => throw new ValidationException(e.Message),
            _ => throw new ValidationException($"rule '{ruleId}' did not evaluate to bool or string"),
        };
    }

    private CelProgram Compile(string expression)
    {
        lock (_programLock)
        {
            if (!_programs.TryGetValue(expression, out var program))
            {
                _programs[expression] = program = _env.Compile(expression);
            }

            return program;
        }
    }

    // Rule paths for top-level FieldRules fields (required, cel, …) resolve against FieldRules.
    private static FieldPath RulePath(string fieldName) =>
        new() { Elements = { Element(FieldRules.Descriptor.FindFieldByName(fieldName)) } };

    private static FieldPathElement Element(FieldDescriptor field) => new()
    {
        FieldNumber = field.FieldNumber,
        FieldName = field.Name,
        // Delimited (editions) fields report TYPE_MESSAGE via ToProto but are TYPE_GROUP by kind.
        FieldType = field.FieldType == FieldType.Group
            ? Google.Protobuf.Reflection.FieldDescriptorProto.Types.Type.Group
            : field.ToProto().Type,
    };

    // Rule-path prefix when descending into repeated.items / map.keys / map.values:
    // append the container rule field (repeated/map) and its sub-field (items/keys/values).
    private static IReadOnlyList<FieldPathElement> RulePrefix(
        IReadOnlyList<FieldPathElement> prefix, FieldDescriptor typeField, MessageDescriptor ruleMessage, string subField)
    {
        var result = new List<FieldPathElement>(prefix)
        {
            Element(typeField),
            Element(ruleMessage.FindFieldByName(subField)),
        };
        return result;
    }

    // Extension rule fields render with the bracketed full name protovalidate uses, e.g.
    // "[buf.validate.conformance.cases.int32_abs_in_proto2]".
    private static FieldPathElement RuleElement(FieldDescriptor field) => field.IsExtension
        ? new FieldPathElement { FieldNumber = field.FieldNumber, FieldName = $"[{field.FullName}]", FieldType = field.ToProto().Type }
        : Element(field);

    private void CollectExtensions(IEnumerable<FieldDescriptor> extensions)
    {
        foreach (var ext in extensions)
        {
            if (!_extensionsByType.TryGetValue(ext.ExtendeeType.FullName, out var list))
            {
                _extensionsByType[ext.ExtendeeType.FullName] = list = [];
            }

            list.Add(ext);
        }
    }

    private void CollectNestedExtensions(MessageDescriptor message)
    {
        CollectExtensions(message.Extensions.UnorderedExtensions);
        foreach (var nested in message.NestedTypes)
        {
            CollectNestedExtensions(nested);
        }
    }

    private static IReadOnlyList<FieldPathElement> Append(IReadOnlyList<FieldPathElement> path, FieldPathElement element)
    {
        var result = new List<FieldPathElement>(path) { element };
        return result;
    }

    // A repeated/map subscript attaches to the LAST path element (the field itself), e.g. val[1].
    private static IReadOnlyList<FieldPathElement> WithIndex(IReadOnlyList<FieldPathElement> path, int index)
    {
        var result = new List<FieldPathElement>(path);
        var last = result[^1].Clone();
        last.Index = (ulong)index;
        result[^1] = last;
        return result;
    }

    private static IReadOnlyList<FieldPathElement> WithSubscript(
        IReadOnlyList<FieldPathElement> path, CelValue key, FieldDescriptor mapField)
    {
        var result = new List<FieldPathElement>(path);
        var last = result[^1].Clone();
        var entry = mapField.MessageType.Fields.InFieldNumberOrder();
        last.KeyType = entry[0].ToProto().Type;
        last.ValueType = entry[1].ToProto().Type;
        switch (key)
        {
            case BoolValue b: last.BoolKey = b.Value; break;
            case UintValue u: last.UintKey = u.Value; break;
            case IntValue i: last.IntKey = i.Value; break;
            case StringValue s: last.StringKey = s.Value; break;
        }

        result[^1] = last;
        return result;
    }

    private static bool IsPopulated(FieldDescriptor field, IMessage message)
    {
        if (field.IsRepeated || field.IsMap)
        {
            return field.Accessor.GetValue(message) is IEnumerable e && e.Cast<object>().Any();
        }

        if (field.HasPresence)
        {
            return field.Accessor.HasValue(message);
        }

        return !IsZeroScalar(field.Accessor.GetValue(message));
    }

    private static bool IsZeroValue(CelValue value) => value switch
    {
        BoolValue b => !b.Value,
        IntValue i => i.Value == 0,
        UintValue u => u.Value == 0,
        DoubleValue d => d.Value == 0,
        StringValue s => s.Value.Length == 0,
        BytesValue by => by.Span.Length == 0,
        NullValue => true,
        ListValue l => l.Elements.Count == 0,
        MapValue m => m.Keys.Count == 0,
        _ => false,
    };

    private static bool IsZeroScalar(object? value) => value switch
    {
        null => true,
        bool b => !b,
        int i => i == 0,
        long l => l == 0,
        uint u => u == 0,
        ulong u => u == 0,
        float f => f == 0,
        double d => d == 0,
        string s => s.Length == 0,
        ByteString bs => bs.Length == 0,
        Enum e => Convert.ToInt64(e) == 0,
        _ => false,
    };

    private static bool IsWellKnown(MessageDescriptor descriptor) =>
        descriptor.File.Package == "google.protobuf";

    private static readonly Dictionary<string, string> WrapperRuleTypes = new()
    {
        ["google.protobuf.FloatValue"] = "float", ["google.protobuf.DoubleValue"] = "double",
        ["google.protobuf.Int32Value"] = "int32", ["google.protobuf.Int64Value"] = "int64",
        ["google.protobuf.UInt32Value"] = "uint32", ["google.protobuf.UInt64Value"] = "uint64",
        ["google.protobuf.BoolValue"] = "bool", ["google.protobuf.StringValue"] = "string",
        ["google.protobuf.BytesValue"] = "bytes",
    };

    // protovalidate rejects rules whose type doesn't match the field (e.g. timestamp rules on a
    // string field) at compile time.
    private static void CheckRuleType(FieldRules rules, FieldDescriptor field)
    {
        var typeOneof = ((IMessage)rules).Descriptor.Oneofs.FirstOrDefault(o => o.Name == "type");
        var typeField = typeOneof?.Accessor.GetCaseFieldDescriptor(rules);
        if (typeField is not null && typeField.Name != ExpectedRuleType(field))
        {
            throw new ValidationCompileException("mismatched rule type and field type");
        }
    }

    private static string? ExpectedRuleType(FieldDescriptor field)
    {
        if (field.IsMap)
        {
            return "map";
        }

        if (field.IsRepeated)
        {
            return "repeated";
        }

        return field.FieldType switch
        {
            FieldType.Float => "float", FieldType.Double => "double",
            FieldType.Int32 => "int32", FieldType.Int64 => "int64",
            FieldType.UInt32 => "uint32", FieldType.UInt64 => "uint64",
            FieldType.SInt32 => "sint32", FieldType.SInt64 => "sint64",
            FieldType.Fixed32 => "fixed32", FieldType.Fixed64 => "fixed64",
            FieldType.SFixed32 => "sfixed32", FieldType.SFixed64 => "sfixed64",
            FieldType.Bool => "bool", FieldType.String => "string", FieldType.Bytes => "bytes",
            FieldType.Enum => "enum",
            FieldType.Message or FieldType.Group => field.MessageType.FullName switch
            {
                "google.protobuf.Duration" => "duration",
                "google.protobuf.Timestamp" => "timestamp",
                "google.protobuf.Any" => "any",
                "google.protobuf.FieldMask" => "field_mask",
                var name when WrapperRuleTypes.TryGetValue(name, out var t) => t,
                _ => null,
            },
            _ => null,
        };
    }
}

/// <summary>Thrown when a rule's CEL expression errors at evaluation time.</summary>
public sealed class ValidationException(string message) : Exception(message);

/// <summary>Thrown when a message's rules are invalid (e.g. rule type doesn't match the field).</summary>
public sealed class ValidationCompileException(string message) : Exception(message);
