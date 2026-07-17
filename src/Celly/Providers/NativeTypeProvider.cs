using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Celly.Types;
using Celly.Values;

namespace Celly.Providers;

/// <summary>
/// Reflection-based <see cref="ITypeProvider"/>/<see cref="ITypeAdapter"/> for plain .NET types
/// (classes, records, structs, enums). Registered types get CEL message semantics: construction
/// (<c>My.Ns.Person{name: 'amy'}</c>), typed field access for the checker, <c>has()</c>
/// presence, and field-wise equality — no protobuf required.
///
/// Field names match the property name verbatim and its snake_case form
/// (<c>user.first_name</c> and <c>user.FirstName</c> both resolve to <c>FirstName</c>).
/// </summary>
[RequiresUnreferencedCode("NativeTypeProvider reflects over registered types; not compatible with trimming.")]
[RequiresDynamicCode("NativeTypeProvider constructs generic collections at runtime; not compatible with Native AOT.")]
public sealed class NativeTypeProvider : ITypeProvider, ITypeAdapter
{
    private readonly Dictionary<string, RegisteredType> _types = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, RegisteredType> _byClrType = [];
    private readonly Dictionary<string, CelValue> _idents = new(StringComparer.Ordinal);

    private sealed class RegisteredType
    {
        public required string Name { get; init; }

        public required Type ClrType { get; init; }

        /// <summary>CEL field name (verbatim + snake_case alias) → property.</summary>
        public required Dictionary<string, PropertyInfo> Fields { get; init; }

        /// <summary>Distinct properties in declaration order (for equality/zero tests).</summary>
        public required IReadOnlyList<PropertyInfo> Properties { get; init; }
    }

    public static NativeTypeProvider FromTypes(params Type[] types)
    {
        var provider = new NativeTypeProvider();
        foreach (var type in types)
        {
            provider.Register(type);
        }

        return provider;
    }

    private void Register(Type type)
    {
        if (type.IsEnum)
        {
            RegisterEnum(type);
            return;
        }

        if (_byClrType.ContainsKey(type) || !IsRegisterableClass(type))
        {
            return;
        }

        var properties = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .ToArray();
        var fields = new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);
        foreach (var property in properties)
        {
            fields[property.Name] = property;
            var snake = ToSnakeCase(property.Name);
            fields.TryAdd(snake, property);
        }

        var name = CelTypeName(type);
        var registered = new RegisteredType
        {
            Name = name,
            ClrType = type,
            Fields = fields,
            Properties = properties,
        };
        _types[name] = registered;
        _byClrType[type] = registered;
        _idents[name] = new TypeValue(CelType.Struct(name));

        // Auto-register reachable field types so nested objects work without ceremony.
        foreach (var property in properties)
        {
            foreach (var reachable in ReachableTypes(property.PropertyType))
            {
                Register(reachable);
            }
        }
    }

    private void RegisterEnum(Type type)
    {
        var name = CelTypeName(type);
        if (_idents.ContainsKey(name))
        {
            return;
        }

        // CEL enums are ints; constants resolve as <Enum.FullName>.<NAME>.
        _idents[name] = new TypeValue(CelType.Int);
        foreach (var value in Enum.GetValues(type))
        {
            _idents[$"{name}.{value}"] = IntValue.Of(Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private static bool IsRegisterableClass(Type type) =>
        (type.IsClass || (type.IsValueType && !type.IsPrimitive && !type.IsEnum))
        && type != typeof(string) && type != typeof(decimal)
        && type != typeof(DateTime) && type != typeof(DateTimeOffset) && type != typeof(TimeSpan)
        && type != typeof(Guid) && type != typeof(byte[]) && type != typeof(object)
        && !typeof(System.Collections.IEnumerable).IsAssignableFrom(type)
        && !typeof(CelValue).IsAssignableFrom(type);

    private static IEnumerable<Type> ReachableTypes(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.IsEnum || IsRegisterableClass(type))
        {
            yield return type;
        }
        else if (type.IsArray && type.GetElementType() is { } element)
        {
            foreach (var t in ReachableTypes(element))
            {
                yield return t;
            }
        }
        else if (type.IsGenericType)
        {
            foreach (var arg in type.GetGenericArguments())
            {
                foreach (var t in ReachableTypes(arg))
                {
                    yield return t;
                }
            }
        }
    }

    private static string CelTypeName(Type type) => (type.FullName ?? type.Name).Replace('+', '.');

    public static string ToSnakeCase(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && (!char.IsUpper(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
                {
                    sb.Append('_');
                }

                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    // ---- ITypeProvider ---------------------------------------------------------------------------

    public CelType? FindStructType(string name) =>
        _types.ContainsKey(name) ? CelType.Struct(name) : null;

    public CelType? FindStructFieldType(string messageName, string fieldName)
    {
        if (!_types.TryGetValue(messageName, out var registered)
            || !registered.Fields.TryGetValue(fieldName, out var property))
        {
            return null;
        }

        return MapClrType(property.PropertyType);
    }

    public CelValue? FindIdent(string name) => _idents.GetValueOrDefault(name);

    public CelValue NewValue(string messageName, IReadOnlyList<KeyValuePair<string, CelValue>> fields)
    {
        if (!_types.TryGetValue(messageName, out var registered))
        {
            return new ErrorValue($"unknown type: '{messageName}'");
        }

        // Validate field names up front.
        foreach (var (name, _) in fields)
        {
            if (!registered.Fields.ContainsKey(name))
            {
                return new ErrorValue($"no_such_field '{name}'");
            }
        }

        var byProperty = new Dictionary<PropertyInfo, CelValue>();
        foreach (var (name, value) in fields)
        {
            byProperty[registered.Fields[name]] = value;
        }

        try
        {
            var instance = Construct(registered, byProperty);
            return instance is ErrorValue error ? error : NativeToValue(instance.ToNative());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new ErrorValue($"cannot construct {messageName}: {ex.Message}");
        }
    }

    private CelValue Construct(RegisteredType registered, Dictionary<PropertyInfo, CelValue> fields)
    {
        // Prefer a parameterless constructor + property sets; otherwise bind the primary
        // constructor's parameters by (case-insensitive) name — covering records.
        var parameterless = registered.ClrType.GetConstructor(Type.EmptyTypes);
        object instance;
        var consumed = new HashSet<PropertyInfo>();
        if (parameterless is not null)
        {
            instance = parameterless.Invoke(null);
        }
        else
        {
            var ctor = registered.ClrType.GetConstructors()
                .OrderByDescending(c => c.GetParameters().Length)
                .FirstOrDefault();
            if (ctor is null)
            {
                return new ErrorValue($"type '{registered.Name}' has no usable constructor");
            }

            var parameters = ctor.GetParameters();
            var args = new object?[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                var parameter = parameters[i];
                var match = fields.Keys.FirstOrDefault(p =>
                    string.Equals(p.Name, parameter.Name, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    var converted = ToClr(fields[match], parameter.ParameterType, out var error);
                    if (error is not null)
                    {
                        return error;
                    }

                    args[i] = converted;
                    consumed.Add(match);
                }
                else
                {
                    args[i] = parameter.HasDefaultValue
                        ? parameter.DefaultValue
                        : parameter.ParameterType.IsValueType
                            ? Activator.CreateInstance(parameter.ParameterType)
                            : null;
                }
            }

            instance = ctor.Invoke(args);
        }

        foreach (var (property, value) in fields)
        {
            if (consumed.Contains(property))
            {
                continue;
            }

            if (!property.CanWrite)
            {
                return new ErrorValue($"field '{property.Name}' of '{registered.Name}' is read-only");
            }

            var converted = ToClr(value, property.PropertyType, out var setError);
            if (setError is not null)
            {
                return setError;
            }

            property.SetValue(instance, converted);
        }

        return new NativeObjectValue(instance, this);
    }

    // ---- CLR type mapping ------------------------------------------------------------------------

    internal CelType MapClrType(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            // Nullable<T> maps like a proto wrapper: T-or-null.
            return CelType.Opaque("wrapper", MapClrType(underlying));
        }

        if (type == typeof(bool))
        {
            return CelType.Bool;
        }

        if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(sbyte))
        {
            return CelType.Int;
        }

        if (type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort) || type == typeof(byte))
        {
            return CelType.Uint;
        }

        if (type == typeof(double) || type == typeof(float) || type == typeof(decimal))
        {
            return CelType.Double;
        }

        if (type == typeof(string) || type == typeof(Guid))
        {
            return CelType.String;
        }

        if (type == typeof(byte[]))
        {
            return CelType.Bytes;
        }

        if (type == typeof(DateTimeOffset) || type == typeof(DateTime))
        {
            return CelType.Timestamp;
        }

        if (type == typeof(TimeSpan))
        {
            return CelType.Duration;
        }

        if (type.IsEnum)
        {
            return CelType.Int;
        }

        if (_byClrType.TryGetValue(type, out var registered))
        {
            return CelType.Struct(registered.Name);
        }

        if (type.IsArray && type.GetElementType() is { } element)
        {
            return CelType.List(MapClrType(element));
        }

        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            var args = type.GetGenericArguments();
            if (args.Length == 2 && typeof(System.Collections.IDictionary).IsAssignableFrom(type))
            {
                return CelType.Map(MapClrType(args[0]), MapClrType(args[1]));
            }

            if (args.Length == 1 && typeof(System.Collections.IEnumerable).IsAssignableFrom(type))
            {
                return CelType.List(MapClrType(args[0]));
            }
        }

        if (typeof(System.Collections.IDictionary).IsAssignableFrom(type))
        {
            return CelType.MapDyn;
        }

        if (typeof(System.Collections.IEnumerable).IsAssignableFrom(type))
        {
            return CelType.ListDyn;
        }

        return CelType.Dyn;
    }

    /// <summary>CEL value → CLR value for a property/parameter slot; error out-param on mismatch.</summary>
    private object? ToClr(CelValue value, Type target, out ErrorValue? error)
    {
        error = null;
        if (Nullable.GetUnderlyingType(target) is { } underlying)
        {
            if (value is NullValue)
            {
                return null;
            }

            return ToClr(value, underlying, out error);
        }

        switch (value)
        {
            case NullValue when !target.IsValueType:
                return null;
            case BoolValue b when target == typeof(bool):
                return b.Value;
            case IntValue i when target.IsEnum:
                return Enum.ToObject(target, i.Value);
            case IntValue i:
                return ConvertInteger(i.Value, target, ref error);
            case UintValue u:
                return ConvertUnsigned(u.Value, target, ref error);
            case DoubleValue d when target == typeof(double):
                return d.Value;
            case DoubleValue d when target == typeof(float):
                return (float)d.Value;
            case DoubleValue d when target == typeof(decimal):
                return (decimal)d.Value;
            case StringValue s when target == typeof(string):
                return s.Value;
            case StringValue s when target == typeof(Guid):
                return Guid.TryParse(s.Value, out var guid)
                    ? guid
                    : Fail(ref error, $"invalid Guid: '{s.Value}'");
            case BytesValue by when target == typeof(byte[]):
                return by.ToByteArray();
            case TimestampValue ts when target == typeof(DateTimeOffset):
                return DateTimeOffset.FromUnixTimeSeconds(ts.Data.Seconds).AddTicks(ts.Data.Nanos / 100);
            case TimestampValue ts when target == typeof(DateTime):
                return DateTimeOffset.FromUnixTimeSeconds(ts.Data.Seconds).AddTicks(ts.Data.Nanos / 100).UtcDateTime;
            case DurationValue dur when target == typeof(TimeSpan):
                return TimeSpan.FromSeconds(dur.Data.Seconds) + TimeSpan.FromTicks(dur.Data.Nanos / 100);
            case NativeObjectValue native when target.IsAssignableFrom(native.ToNative()!.GetType()):
                return native.ToNative();
            case ListValue list:
                return ConvertList(list, target, ref error);
            case MapValue map:
                return ConvertMap(map, target, ref error);
            default:
                return Fail(ref error, $"cannot convert {value.Type.Name} to {target.Name}");
        }
    }

    private static object? ConvertInteger(long value, Type target, ref ErrorValue? error)
    {
        if (target == typeof(long))
        {
            return value;
        }

        if (target == typeof(int))
        {
            return value is >= int.MinValue and <= int.MaxValue ? (int)value : Fail(ref error, "range error");
        }

        if (target == typeof(short))
        {
            return value is >= short.MinValue and <= short.MaxValue ? (short)value : Fail(ref error, "range error");
        }

        if (target == typeof(sbyte))
        {
            return value is >= sbyte.MinValue and <= sbyte.MaxValue ? (sbyte)value : Fail(ref error, "range error");
        }

        return Fail(ref error, $"cannot convert int to {target.Name}");
    }

    private static object? ConvertUnsigned(ulong value, Type target, ref ErrorValue? error)
    {
        if (target == typeof(ulong))
        {
            return value;
        }

        if (target == typeof(uint))
        {
            return value <= uint.MaxValue ? (uint)value : Fail(ref error, "range error");
        }

        if (target == typeof(ushort))
        {
            return value <= ushort.MaxValue ? (ushort)value : Fail(ref error, "range error");
        }

        if (target == typeof(byte))
        {
            return value <= byte.MaxValue ? (byte)value : Fail(ref error, "range error");
        }

        return Fail(ref error, $"cannot convert uint to {target.Name}");
    }

    private object? ConvertList(ListValue list, Type target, ref ErrorValue? error)
    {
        Type element;
        if (target.IsArray)
        {
            element = target.GetElementType()!;
        }
        else if (target.IsGenericType && target.GetGenericArguments() is [var single])
        {
            element = single;
        }
        else
        {
            return Fail(ref error, $"cannot convert list to {target.Name}");
        }

        var array = Array.CreateInstance(element, list.Elements.Count);
        for (var i = 0; i < list.Elements.Count; i++)
        {
            var converted = ToClr(list.Elements[i], element, out error);
            if (error is not null)
            {
                return null;
            }

            array.SetValue(converted, i);
        }

        if (target.IsArray)
        {
            return array;
        }

        var listType = typeof(List<>).MakeGenericType(element);
        if (!target.IsAssignableFrom(listType))
        {
            return Fail(ref error, $"cannot convert list to {target.Name}");
        }

        var result = (System.Collections.IList)Activator.CreateInstance(listType)!;
        foreach (var item in array)
        {
            result.Add(item);
        }

        return result;
    }

    private object? ConvertMap(MapValue map, Type target, ref ErrorValue? error)
    {
        if (!target.IsGenericType || target.GetGenericArguments() is not [var keyType, var valueType])
        {
            return Fail(ref error, $"cannot convert map to {target.Name}");
        }

        var dictType = typeof(Dictionary<,>).MakeGenericType(keyType, valueType);
        if (!target.IsAssignableFrom(dictType))
        {
            return Fail(ref error, $"cannot convert map to {target.Name}");
        }

        var result = (System.Collections.IDictionary)Activator.CreateInstance(dictType)!;
        foreach (var key in map.Keys)
        {
            map.TryGet(key, out var entryValue);
            var clrKey = ToClr(key, keyType, out error);
            if (error is not null)
            {
                return null;
            }

            var clrValue = ToClr(entryValue, valueType, out error);
            if (error is not null)
            {
                return null;
            }

            result[clrKey!] = clrValue;
        }

        return result;
    }

    private static object? Fail(ref ErrorValue? error, string message)
    {
        error = new ErrorValue(message);
        return null;
    }

    // ---- ITypeAdapter (CLR → CEL) -----------------------------------------------------------------

    public CelValue NativeToValue(object? value)
    {
        switch (value)
        {
            case null:
                return NullValue.Instance;
            case CelValue cel:
                return cel;
            case decimal dec:
                return DoubleValue.Of((double)dec);
            case DateTimeOffset dto:
                return AdaptTimestamp(dto);
            case DateTime dt:
                return AdaptTimestamp(new DateTimeOffset(dt.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(dt, DateTimeKind.Utc)
                    : dt.ToUniversalTime(), TimeSpan.Zero));
            case TimeSpan span:
                // C# integer division/remainder truncate toward zero with matching signs,
                // which is exactly the seconds+nanos invariant.
                return DurationValue.Of(
                    span.Ticks / TimeSpan.TicksPerSecond,
                    (int)(span.Ticks % TimeSpan.TicksPerSecond * 100));
            case Guid guid:
                return StringValue.Of(guid.ToString());
            case Enum enumValue:
                return IntValue.Of(Convert.ToInt64(enumValue, System.Globalization.CultureInfo.InvariantCulture));
            case System.Collections.IDictionary dict:
            {
                var pairs = new List<KeyValuePair<CelValue, CelValue>>();
                foreach (System.Collections.DictionaryEntry entry in dict)
                {
                    pairs.Add(new(NativeToValue(entry.Key), NativeToValue(entry.Value)));
                }

                return MapValue.Build(pairs);
            }

            case string or byte[]:
                return NativeTypeAdapter.Instance.NativeToValue(value);
            case System.Collections.IEnumerable seq:
            {
                var elements = new List<CelValue>();
                foreach (var item in seq)
                {
                    elements.Add(NativeToValue(item));
                }

                return ListValue.Of(elements);
            }

            default:
                if (_byClrType.ContainsKey(value.GetType()))
                {
                    return new NativeObjectValue(value, this);
                }

                return NativeTypeAdapter.Instance.NativeToValue(value);
        }
    }

    private static CelValue AdaptTimestamp(DateTimeOffset dto)
    {
        var utcTicks = dto.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
        var seconds = Math.DivRem(utcTicks, TimeSpan.TicksPerSecond, out var tickRemainder);
        if (tickRemainder < 0)
        {
            seconds--;
            tickRemainder += TimeSpan.TicksPerSecond;
        }

        return TimestampValue.Of(seconds, (int)(tickRemainder * 100));
    }

    // ---- the value --------------------------------------------------------------------------------

    /// <summary>A registered .NET object as a CEL struct value.</summary>
    public sealed class NativeObjectValue : CelValue, IStructValue, IZeroTester
    {
        private readonly object _instance;
        private readonly NativeTypeProvider _provider;
        private readonly RegisteredType _registered;

        internal NativeObjectValue(object instance, NativeTypeProvider provider)
        {
            _instance = instance;
            _provider = provider;
            _registered = provider._byClrType[instance.GetType()];
        }

        public override CelType Type => CelType.Struct(_registered.Name);

        public override object ToNative() => _instance;

        public CelValue GetField(string name)
        {
            if (!_registered.Fields.TryGetValue(name, out var property))
            {
                return new ErrorValue($"no_such_field '{name}'");
            }

            return _provider.NativeToValue(property.GetValue(_instance));
        }

        public CelValue HasField(string name)
        {
            if (!_registered.Fields.TryGetValue(name, out var property))
            {
                return new ErrorValue($"no_such_field '{name}'");
            }

            // Proto3-like presence: non-null, non-default scalars, non-empty aggregates.
            var raw = property.GetValue(_instance);
            return BoolValue.Of(raw switch
            {
                null => false,
                bool b => b,
                string s => s.Length > 0,
                byte[] by => by.Length > 0,
                System.Collections.IDictionary d => d.Count > 0,
                System.Collections.ICollection c => c.Count > 0,
                System.Collections.IEnumerable e => e.Cast<object?>().Any(),
                _ when raw.GetType().IsEnum => Convert.ToInt64(raw, System.Globalization.CultureInfo.InvariantCulture) != 0,
                IConvertible => !Equals(raw, Activator.CreateInstance(raw.GetType())),
                _ => true,
            });
        }

        public bool IsZeroValue() =>
            _registered.Properties.All(p => HasField(p.Name) is BoolValue { Value: false });

        public override bool EqualTo(CelValue other)
        {
            if (other is not NativeObjectValue native || native._registered.Name != _registered.Name)
            {
                return false;
            }

            // Field-wise CEL equality (IEEE NaN semantics), mirroring proto message equality.
            foreach (var property in _registered.Properties)
            {
                var a = _provider.NativeToValue(property.GetValue(_instance));
                var b = _provider.NativeToValue(property.GetValue(native._instance));
                if (a is ErrorValue || b is ErrorValue || !a.EqualTo(b))
                {
                    return false;
                }
            }

            return true;
        }

        public override string ToString() => $"{_registered.Name}{{{_instance}}}";
    }
}
