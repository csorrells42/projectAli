using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ali.Modules.Capabilities;

/// <summary>
/// Ali's explicit, fail-closed JSON Schema dialect for callable tool arguments.
/// Provider grammars may guide output, but only this local boundary authorizes arguments.
/// </summary>
internal static class CapabilityJsonSchemaValidator
{
    private const int MaximumValidationDepth = 64;
    private const int MaximumReportedErrors = 64;
    private const int MaximumSchemaMapEntries = 256;
    private const int MaximumSchemaBranches = 64;
    private const int MaximumEnumValues = 256;
    private const int MaximumUniqueItems = 1_024;
    private const int MaximumPatternCharacters = 2_048;
    private const int MaximumAnnotationCharacters = 8_192;
    private const int MaximumValidationOperations = 16_384;
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(100);

    private sealed class ValidationBudget
    {
        private int _remaining = MaximumValidationOperations;

        internal bool IsExhausted => _remaining <= 0;

        internal bool TryConsume()
        {
            if (_remaining <= 0)
            {
                return false;
            }

            _remaining--;
            return true;
        }
    }

    private static readonly HashSet<string> SupportedKeywords = new(StringComparer.Ordinal)
    {
        "$defs",
        "$ref",
        "additionalProperties",
        "allOf",
        "anyOf",
        "const",
        "default",
        "description",
        "enum",
        "items",
        "maxItems",
        "maxLength",
        "maximum",
        "minItems",
        "minLength",
        "minimum",
        "oneOf",
        "pattern",
        "properties",
        "required",
        "title",
        "type",
        "uniqueItems"
    };

    private static readonly HashSet<string> SupportedTypes = new(StringComparer.Ordinal)
    {
        "array",
        "boolean",
        "integer",
        "null",
        "number",
        "object",
        "string"
    };

    internal static bool TryValidateToolArgumentsSchema(JsonElement schema, out string reason)
    {
        if (!TryValidateSchemaDefinition(schema, out reason))
        {
            return false;
        }

        if (schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String
            || !string.Equals(type.GetString(), "object", StringComparison.Ordinal))
        {
            reason = "callable arguments require an explicit object root schema";
            return false;
        }

        return true;
    }

    internal static bool TryValidateSchemaDefinition(JsonElement schema, out string reason)
    {
        var errors = new List<string>();
        ValidateSchemaDefinition(
            schema,
            schema,
            "schema",
            errors,
            0,
            new ValidationBudget());
        reason = errors.FirstOrDefault() ?? string.Empty;
        return errors.Count == 0;
    }

    internal static void Validate(
        JsonElement value,
        JsonElement schema,
        string path,
        List<string> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        var initialErrorCount = errors.Count;
        ValidateSchemaDefinition(
            schema,
            schema,
            path,
            errors,
            0,
            new ValidationBudget());
        if (errors.Count != initialErrorCount)
        {
            return;
        }

        ValidateInstance(
            value,
            schema,
            schema,
            path,
            errors,
            new HashSet<string>(StringComparer.Ordinal),
            0,
            new ValidationBudget());
    }

    private static void ValidateSchemaDefinition(
        JsonElement schema,
        JsonElement rootSchema,
        string path,
        List<string> errors,
        int depth,
        ValidationBudget budget)
    {
        if (!ConsumeOperation(budget, path, errors, "registered-schema"))
        {
            return;
        }

        if (depth > MaximumValidationDepth)
        {
            AddError(errors, $"{path} exceeds Ali's supported registered-schema depth.");
            return;
        }

        if (schema.ValueKind != JsonValueKind.Object)
        {
            AddError(errors, $"{path} must be an explicit registered JSON schema object.");
            return;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var keyword in schema.EnumerateObject())
        {
            if (!names.Add(keyword.Name))
            {
                AddError(errors, $"{path} contains duplicate registered-schema keyword '{keyword.Name}'.");
            }
            else if (!SupportedKeywords.Contains(keyword.Name))
            {
                AddError(errors, $"{path} uses unsupported registered-schema keyword '{keyword.Name}'.");
            }
        }

        ValidateTypeDefinition(schema, path, errors);
        ValidateSchemaArray(schema, "allOf", rootSchema, path, errors, depth, budget);
        ValidateSchemaArray(schema, "anyOf", rootSchema, path, errors, depth, budget);
        ValidateSchemaArray(schema, "oneOf", rootSchema, path, errors, depth, budget);
        ValidateSchemaMap(schema, "properties", rootSchema, path, errors, depth, budget);
        ValidateSchemaMap(schema, "$defs", rootSchema, path, errors, depth, budget);

        if (schema.TryGetProperty("items", out var items))
        {
            ValidateSchemaDefinition(
                items,
                rootSchema,
                $"{path}.items",
                errors,
                depth + 1,
                budget);
        }

        if (schema.TryGetProperty("additionalProperties", out var additional)
            && additional.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            ValidateSchemaDefinition(
                additional,
                rootSchema,
                $"{path}.additionalProperties",
                errors,
                depth + 1,
                budget);
        }

        if (schema.TryGetProperty("required", out var required))
        {
            ValidateUniqueStringArray(required, $"{path}.required", errors);
        }

        if (schema.TryGetProperty("enum", out var enumValues))
        {
            ValidateEnum(enumValues, $"{path}.enum", errors);
        }

        ValidateNonnegativeIntegerKeyword(schema, "minLength", path, errors);
        ValidateNonnegativeIntegerKeyword(schema, "maxLength", path, errors);
        ValidateNonnegativeIntegerKeyword(schema, "minItems", path, errors);
        ValidateNonnegativeIntegerKeyword(schema, "maxItems", path, errors);
        ValidateNumberKeyword(schema, "minimum", path, errors);
        ValidateNumberKeyword(schema, "maximum", path, errors);

        if (schema.TryGetProperty("uniqueItems", out var uniqueItems))
        {
            if (uniqueItems.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                AddError(errors, $"{path}.uniqueItems must be a boolean.");
            }
            else if (uniqueItems.ValueKind == JsonValueKind.True
                     && (!TryGetNonnegativeInteger(schema, "maxItems", out var maximum)
                         || maximum > MaximumUniqueItems))
            {
                AddError(
                    errors,
                    $"{path}.uniqueItems requires maxItems no greater than {MaximumUniqueItems} for bounded validation.");
            }
        }

        if (schema.TryGetProperty("pattern", out var pattern))
        {
            if (pattern.ValueKind != JsonValueKind.String
                || (pattern.GetString()?.Length ?? 0) > MaximumPatternCharacters
                || !TryCreateRegex(pattern.GetString() ?? string.Empty, out _))
            {
                AddError(errors, $"{path}.pattern is not a supported bounded regular expression.");
            }
        }

        ValidateBoundedStringAnnotation(schema, "title", path, errors);
        ValidateBoundedStringAnnotation(schema, "description", path, errors);

        if (schema.TryGetProperty("$ref", out var reference)
            && (reference.ValueKind != JsonValueKind.String
                || !TryResolveLocalReference(rootSchema, reference.GetString(), out _)))
        {
            AddError(errors, $"{path} uses an unresolved or non-local registered schema reference.");
        }
    }

    private static void ValidateTypeDefinition(JsonElement schema, string path, List<string> errors)
    {
        if (!schema.TryGetProperty("type", out var type))
        {
            return;
        }

        if (type.ValueKind == JsonValueKind.String)
        {
            if (!SupportedTypes.Contains(type.GetString() ?? string.Empty))
            {
                AddError(errors, $"{path}.type names an unsupported JSON type.");
            }

            return;
        }

        if (type.ValueKind != JsonValueKind.Array || type.GetArrayLength() == 0)
        {
            AddError(errors, $"{path}.type must be a supported type name or non-empty type array.");
            return;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in type.EnumerateArray())
        {
            var name = candidate.ValueKind == JsonValueKind.String ? candidate.GetString() : null;
            if (name is null || !SupportedTypes.Contains(name) || !names.Add(name))
            {
                AddError(errors, $"{path}.type contains an unsupported or duplicate JSON type.");
            }
        }
    }

    private static void ValidateSchemaArray(
        JsonElement schema,
        string keyword,
        JsonElement rootSchema,
        string path,
        List<string> errors,
        int depth,
        ValidationBudget budget)
    {
        if (!schema.TryGetProperty(keyword, out var branches))
        {
            return;
        }

        if (branches.ValueKind != JsonValueKind.Array
            || branches.GetArrayLength() is 0 or > MaximumSchemaBranches)
        {
            AddError(errors, $"{path}.{keyword} must be a bounded non-empty schema array.");
            return;
        }

        var index = 0;
        foreach (var branch in branches.EnumerateArray())
        {
            ValidateSchemaDefinition(
                branch,
                rootSchema,
                $"{path}.{keyword}[{index++}]",
                errors,
                depth + 1,
                budget);
            if (budget.IsExhausted)
            {
                return;
            }
        }
    }

    private static void ValidateSchemaMap(
        JsonElement schema,
        string keyword,
        JsonElement rootSchema,
        string path,
        List<string> errors,
        int depth,
        ValidationBudget budget)
    {
        if (!schema.TryGetProperty(keyword, out var map))
        {
            return;
        }

        if (map.ValueKind != JsonValueKind.Object)
        {
            AddError(errors, $"{path}.{keyword} must be an object of schemas.");
            return;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in map.EnumerateObject())
        {
            if (names.Count >= MaximumSchemaMapEntries)
            {
                AddError(errors, $"{path}.{keyword} exceeds Ali's bounded schema-entry count.");
                return;
            }

            if (!names.Add(entry.Name))
            {
                AddError(errors, $"{path}.{keyword} contains duplicate entry '{entry.Name}'.");
                continue;
            }

            ValidateSchemaDefinition(
                entry.Value,
                rootSchema,
                $"{path}.{keyword}.{entry.Name}",
                errors,
                depth + 1,
                budget);
            if (budget.IsExhausted)
            {
                return;
            }
        }
    }

    private static void ValidateInstance(
        JsonElement value,
        JsonElement schema,
        JsonElement rootSchema,
        string path,
        List<string> errors,
        HashSet<string> activeReferences,
        int depth,
        ValidationBudget budget)
    {
        if (!ConsumeOperation(budget, path, errors, "tool-argument"))
        {
            return;
        }

        if (depth > MaximumValidationDepth)
        {
            AddError(errors, $"{path} exceeds Ali's supported tool-argument depth.");
            return;
        }

        if (schema.TryGetProperty("$ref", out var reference))
        {
            var referenceText = reference.GetString()!;
            var referenceAtPath = referenceText + "\0" + path;
            if (!activeReferences.Add(referenceAtPath))
            {
                AddError(errors, $"{path} contains a circular registered schema reference.");
                return;
            }

            TryResolveLocalReference(rootSchema, referenceText, out var referencedSchema);
            ValidateInstance(
                value,
                referencedSchema,
                rootSchema,
                path,
                errors,
                activeReferences,
                depth + 1,
                budget);
            activeReferences.Remove(referenceAtPath);
            if (budget.IsExhausted)
            {
                return;
            }
        }

        if (schema.TryGetProperty("allOf", out var allOf))
        {
            foreach (var branch in allOf.EnumerateArray())
            {
                ValidateInstance(
                    value,
                    branch,
                    rootSchema,
                    path,
                    errors,
                    activeReferences,
                    depth + 1,
                    budget);
                if (budget.IsExhausted)
                {
                    return;
                }
            }
        }

        if (schema.TryGetProperty("oneOf", out var oneOf)
            && CountValidBranches(
                value,
                oneOf,
                rootSchema,
                path,
                errors,
                activeReferences,
                depth,
                budget) != 1)
        {
            AddError(errors, $"{path} must match exactly one registered schema branch.");
            return;
        }

        if (schema.TryGetProperty("anyOf", out var anyOf)
            && CountValidBranches(
                value,
                anyOf,
                rootSchema,
                path,
                errors,
                activeReferences,
                depth,
                budget) == 0)
        {
            AddError(errors, $"{path} must match a registered schema branch.");
            return;
        }

        if (schema.TryGetProperty("enum", out var enumValues)
            && !enumValues.EnumerateArray().Any(candidate => JsonElement.DeepEquals(candidate, value)))
        {
            AddError(errors, $"{path} is not one of the registered enum values.");
        }

        if (schema.TryGetProperty("const", out var constant)
            && !JsonElement.DeepEquals(constant, value))
        {
            AddError(errors, $"{path} does not match the registered constant.");
        }

        if (schema.TryGetProperty("type", out var type) && !MatchesType(value, type))
        {
            AddError(errors, $"{path} does not match the registered JSON type.");
            return;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                ValidateObject(value, schema, rootSchema, path, errors, activeReferences, depth, budget);
                break;
            case JsonValueKind.Array:
                ValidateArray(value, schema, rootSchema, path, errors, activeReferences, depth, budget);
                break;
            case JsonValueKind.String:
                ValidateString(value.GetString() ?? string.Empty, schema, path, errors);
                break;
            case JsonValueKind.Number:
                ValidateNumber(value, schema, path, errors);
                break;
        }
    }

    private static void ValidateObject(
        JsonElement value,
        JsonElement schema,
        JsonElement rootSchema,
        string path,
        List<string> errors,
        HashSet<string> activeReferences,
        int depth,
        ValidationBudget budget)
    {
        if (schema.TryGetProperty("required", out var required))
        {
            foreach (var requiredName in required.EnumerateArray().Select(item => item.GetString()!))
            {
                if (!value.TryGetProperty(requiredName, out _))
                {
                    AddError(errors, $"{path}.{requiredName} is required by the registered tool schema.");
                }
            }
        }

        var hasProperties = schema.TryGetProperty("properties", out var properties);
        var hasAdditional = schema.TryGetProperty("additionalProperties", out var additional);
        var instanceNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!instanceNames.Add(property.Name))
            {
                AddError(errors, $"{path}.{property.Name} appears more than once.");
                continue;
            }

            if (hasProperties && properties.TryGetProperty(property.Name, out var propertySchema))
            {
                ValidateInstance(
                    property.Value,
                    propertySchema,
                    rootSchema,
                    $"{path}.{property.Name}",
                    errors,
                    activeReferences,
                    depth + 1,
                    budget);
            }
            else if (hasAdditional && additional.ValueKind == JsonValueKind.False)
            {
                AddError(errors, $"{path}.{property.Name} is not allowed by the registered tool schema.");
            }
            else if (hasAdditional && additional.ValueKind == JsonValueKind.Object)
            {
                ValidateInstance(
                    property.Value,
                    additional,
                    rootSchema,
                    $"{path}.{property.Name}",
                    errors,
                    activeReferences,
                    depth + 1,
                    budget);
            }

            if (budget.IsExhausted)
            {
                return;
            }
        }
    }

    private static void ValidateArray(
        JsonElement value,
        JsonElement schema,
        JsonElement rootSchema,
        string path,
        List<string> errors,
        HashSet<string> activeReferences,
        int depth,
        ValidationBudget budget)
    {
        var length = value.GetArrayLength();
        ValidateCount(schema, "minItems", length, path, "items", minimum: true, errors);
        ValidateCount(schema, "maxItems", length, path, "items", minimum: false, errors);

        if (schema.TryGetProperty("uniqueItems", out var unique)
            && unique.ValueKind == JsonValueKind.True)
        {
            if (length > MaximumUniqueItems)
            {
                AddError(errors, $"{path} exceeds Ali's bounded unique-item validation limit.");
            }
            else
            {
                var values = value.EnumerateArray().ToArray();
                for (var left = 0; left < values.Length; left++)
                {
                    for (var right = left + 1; right < values.Length; right++)
                    {
                        if (JsonElement.DeepEquals(values[left], values[right]))
                        {
                            AddError(errors, $"{path} must contain unique items.");
                            left = values.Length;
                            break;
                        }
                    }
                }
            }
        }

        if (!schema.TryGetProperty("items", out var itemSchema))
        {
            return;
        }

        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            ValidateInstance(
                item,
                itemSchema,
                rootSchema,
                $"{path}[{index++}]",
                errors,
                activeReferences,
                depth + 1,
                budget);
            if (budget.IsExhausted)
            {
                return;
            }
        }
    }

    private static void ValidateString(
        string value,
        JsonElement schema,
        string path,
        List<string> errors)
    {
        var length = value.EnumerateRunes().Count();
        ValidateCount(schema, "minLength", length, path, "characters", minimum: true, errors);
        ValidateCount(schema, "maxLength", length, path, "characters", minimum: false, errors);

        if (schema.TryGetProperty("pattern", out var pattern))
        {
            TryCreateRegex(pattern.GetString()!, out var expression);
            try
            {
                if (!expression!.IsMatch(value))
                {
                    AddError(errors, $"{path} does not match the registered string pattern.");
                }
            }
            catch (RegexMatchTimeoutException)
            {
                AddError(errors, $"{path} could not be validated within the bounded pattern timeout.");
            }
        }
    }

    private static void ValidateNumber(
        JsonElement value,
        JsonElement schema,
        string path,
        List<string> errors)
    {
        CompareBound(value, schema, "minimum", path, errors, minimum: true);
        CompareBound(value, schema, "maximum", path, errors, minimum: false);
    }

    private static int CountValidBranches(
        JsonElement value,
        JsonElement branches,
        JsonElement rootSchema,
        string path,
        List<string> errors,
        HashSet<string> activeReferences,
        int depth,
        ValidationBudget budget)
    {
        var count = 0;
        foreach (var branch in branches.EnumerateArray())
        {
            var branchErrors = new List<string>();
            ValidateInstance(
                value,
                branch,
                rootSchema,
                path,
                branchErrors,
                new HashSet<string>(activeReferences, StringComparer.Ordinal),
                depth + 1,
                budget);
            if (budget.IsExhausted)
            {
                AddError(
                    errors,
                    $"{path} exceeds Ali's bounded tool-argument validation operation count.");
                break;
            }
            if (branchErrors.Count == 0)
            {
                count++;
            }
        }

        return count;
    }

    private static bool MatchesType(JsonElement value, JsonElement type) =>
        type.ValueKind == JsonValueKind.Array
            ? type.EnumerateArray().Any(candidate => MatchesType(value, candidate))
            : type.GetString() switch
            {
                "object" => value.ValueKind == JsonValueKind.Object,
                "array" => value.ValueKind == JsonValueKind.Array,
                "string" => value.ValueKind == JsonValueKind.String,
                "integer" => IsInteger(value),
                "number" => value.ValueKind == JsonValueKind.Number,
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "null" => value.ValueKind == JsonValueKind.Null,
                _ => false
            };

    private static bool IsInteger(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        if (value.TryGetDecimal(out var decimalValue))
        {
            return decimal.Truncate(decimalValue) == decimalValue;
        }

        return value.TryGetDouble(out var doubleValue)
               && double.IsFinite(doubleValue)
               && Math.Truncate(doubleValue) == doubleValue;
    }

    private static void CompareBound(
        JsonElement value,
        JsonElement schema,
        string keyword,
        string path,
        List<string> errors,
        bool minimum)
    {
        if (!schema.TryGetProperty(keyword, out var bound))
        {
            return;
        }

        var comparison = CompareNumbers(value, bound);
        if ((minimum && comparison < 0) || (!minimum && comparison > 0))
        {
            AddError(errors, $"{path} violates registered numeric bound '{keyword}'.");
        }
    }

    private static int CompareNumbers(JsonElement left, JsonElement right)
    {
        if (left.TryGetDecimal(out var leftDecimal) && right.TryGetDecimal(out var rightDecimal))
        {
            return leftDecimal.CompareTo(rightDecimal);
        }

        return left.GetDouble().CompareTo(right.GetDouble());
    }

    private static void ValidateCount(
        JsonElement schema,
        string keyword,
        int actual,
        string path,
        string unit,
        bool minimum,
        List<string> errors)
    {
        if (!TryGetNonnegativeInteger(schema, keyword, out var expected))
        {
            return;
        }

        if ((minimum && actual < expected) || (!minimum && actual > expected))
        {
            AddError(errors, $"{path} must contain {(minimum ? "at least" : "at most")} {expected} {unit}.");
        }
    }

    private static void ValidateUniqueStringArray(
        JsonElement value,
        string path,
        List<string> errors)
    {
        if (value.ValueKind != JsonValueKind.Array
            || value.GetArrayLength() > MaximumSchemaMapEntries)
        {
            AddError(errors, $"{path} must be a bounded array of unique strings.");
            return;
        }

        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || !values.Add(item.GetString()!))
            {
                AddError(errors, $"{path} must contain only unique strings.");
            }
        }
    }

    private static void ValidateEnum(JsonElement value, string path, List<string> errors)
    {
        if (value.ValueKind != JsonValueKind.Array
            || value.GetArrayLength() is 0 or > MaximumEnumValues)
        {
            AddError(errors, $"{path} must be a bounded non-empty array.");
            return;
        }

        var values = value.EnumerateArray().ToArray();
        for (var left = 0; left < values.Length; left++)
        {
            for (var right = left + 1; right < values.Length; right++)
            {
                if (JsonElement.DeepEquals(values[left], values[right]))
                {
                    AddError(errors, $"{path} must contain unique values.");
                    return;
                }
            }
        }
    }

    private static void ValidateNonnegativeIntegerKeyword(
        JsonElement schema,
        string keyword,
        string path,
        List<string> errors)
    {
        if (schema.TryGetProperty(keyword, out _)
            && !TryGetNonnegativeInteger(schema, keyword, out _))
        {
            AddError(errors, $"{path}.{keyword} must be a non-negative integer.");
        }
    }

    private static void ValidateNumberKeyword(
        JsonElement schema,
        string keyword,
        string path,
        List<string> errors)
    {
        if (!schema.TryGetProperty(keyword, out var value))
        {
            return;
        }

        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out var number)
            || !double.IsFinite(number))
        {
            AddError(errors, $"{path}.{keyword} must be a finite number.");
        }
    }

    private static void ValidateBoundedStringAnnotation(
        JsonElement schema,
        string keyword,
        string path,
        List<string> errors)
    {
        if (schema.TryGetProperty(keyword, out var value)
            && (value.ValueKind != JsonValueKind.String
                || (value.GetString()?.Length ?? 0) > MaximumAnnotationCharacters))
        {
            AddError(errors, $"{path}.{keyword} must be a bounded string annotation.");
        }
    }

    private static bool TryGetNonnegativeInteger(
        JsonElement schema,
        string keyword,
        out long value)
    {
        value = 0;
        return schema.TryGetProperty(keyword, out var element)
               && element.ValueKind == JsonValueKind.Number
               && element.TryGetInt64(out value)
               && value >= 0;
    }

    private static bool TryCreateRegex(string pattern, out Regex? expression)
    {
        try
        {
            expression = new Regex(pattern, RegexOptions.CultureInvariant, PatternTimeout);
            return true;
        }
        catch (ArgumentException)
        {
            expression = null;
            return false;
        }
    }

    private static bool TryResolveLocalReference(
        JsonElement rootSchema,
        string? reference,
        out JsonElement schema)
    {
        schema = default;
        if (string.IsNullOrWhiteSpace(reference) || !reference.StartsWith("#/$defs/", StringComparison.Ordinal))
        {
            return false;
        }

        var segments = reference[2..]
            .Split('/')
            .Select(static segment => segment.Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal))
            .ToArray();
        var current = rootSchema;
        var index = 0;
        while (index < segments.Length)
        {
            if (current.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var segment = segments[index++];
            if (segment is "$defs" or "properties")
            {
                if (index >= segments.Length
                    || !current.TryGetProperty(segment, out var map)
                    || map.ValueKind != JsonValueKind.Object
                    || !map.TryGetProperty(segments[index++], out current))
                {
                    return false;
                }
            }
            else if (segment is "allOf" or "anyOf" or "oneOf")
            {
                if (index >= segments.Length
                    || !current.TryGetProperty(segment, out var branches)
                    || branches.ValueKind != JsonValueKind.Array
                    || !int.TryParse(segments[index++], out var branchIndex)
                    || branchIndex < 0
                    || branchIndex >= branches.GetArrayLength())
                {
                    return false;
                }

                current = branches[branchIndex];
            }
            else if (segment is "items" or "additionalProperties")
            {
                if (!current.TryGetProperty(segment, out current))
                {
                    return false;
                }
            }
            else
            {
                return false;
            }
        }

        schema = current;
        return schema.ValueKind == JsonValueKind.Object;
    }

    private static bool ConsumeOperation(
        ValidationBudget budget,
        string path,
        List<string> errors,
        string boundary)
    {
        if (budget.TryConsume())
        {
            return true;
        }

        AddError(
            errors,
            $"{path} exceeds Ali's bounded {boundary} validation operation count.");
        return false;
    }

    private static void AddError(List<string> errors, string error)
    {
        if (errors.Count < MaximumReportedErrors)
        {
            errors.Add(error);
        }
    }
}
