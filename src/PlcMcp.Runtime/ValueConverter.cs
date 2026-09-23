using System.Globalization;
using System.Text.Json;
using PlcMcp.Contracts.Models;

namespace PlcMcp.Runtime;

public static class ValueConverter
{
    public static bool TryConvertAndValidate(
        object? rawValue,
        TagDefinition tag,
        out object? convertedValue,
        out string failureReason)
    {
        convertedValue = null;
        failureReason = string.Empty;

        if (rawValue is null)
        {
            failureReason = $"Null value is not allowed for tag '{tag.Name}'.";
            return false;
        }

        object? unwrapped = rawValue;
        if (rawValue is JsonElement jsonElement)
        {
            switch (jsonElement.ValueKind)
            {
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    failureReason = $"Null value is not allowed for tag '{tag.Name}'.";
                    return false;
                case JsonValueKind.True:
                    unwrapped = true;
                    break;
                case JsonValueKind.False:
                    unwrapped = false;
                    break;
                case JsonValueKind.Number:
                    if (jsonElement.TryGetInt64(out var intVal))
                    {
                        unwrapped = intVal;
                    }
                    else if (jsonElement.TryGetDouble(out var doubleVal))
                    {
                        unwrapped = doubleVal;
                    }
                    else
                    {
                        failureReason = $"Invalid JSON number for tag '{tag.Name}'.";
                        return false;
                    }
                    break;
                case JsonValueKind.String:
                    unwrapped = jsonElement.GetString();
                    break;
                default:
                    failureReason = $"Unsupported JSON value kind '{jsonElement.ValueKind}' for tag '{tag.Name}'.";
                    return false;
            }
        }

        switch (tag.DataType)
        {
            case PlcDataType.Bool:
            {
                if (unwrapped is bool b)
                {
                    convertedValue = b;
                    return true;
                }

                if (unwrapped is string strBool)
                {
                    if (string.Equals(strBool, "true", StringComparison.OrdinalIgnoreCase))
                    {
                        convertedValue = true;
                        return true;
                    }
                    if (string.Equals(strBool, "false", StringComparison.OrdinalIgnoreCase))
                    {
                        convertedValue = false;
                        return true;
                    }

                    failureReason = $"Value '{strBool}' is not a valid boolean for tag '{tag.Name}'. Only true/false or \"true\"/\"false\" are accepted.";
                    return false;
                }

                failureReason = $"Value of type '{unwrapped?.GetType().Name}' cannot be converted to boolean tag '{tag.Name}'. Only boolean or \"true\"/\"false\" are accepted.";
                return false;
            }

            case PlcDataType.String:
            {
                if (unwrapped is null)
                {
                    failureReason = $"Null value is not allowed for string tag '{tag.Name}'.";
                    return false;
                }

                convertedValue = unwrapped.ToString() ?? string.Empty;
                return true;
            }

            case PlcDataType.Int16:
            case PlcDataType.UInt16:
            case PlcDataType.Int32:
            case PlcDataType.UInt32:
            case PlcDataType.Real:
            case PlcDataType.Double:
            {
                if (unwrapped is null)
                {
                    failureReason = $"Null value is not allowed for numeric tag '{tag.Name}'.";
                    return false;
                }

                if (unwrapped is bool)
                {
                    failureReason = $"Boolean value cannot be converted to numeric tag '{tag.Name}'.";
                    return false;
                }

                if (IsIntegerType(tag.DataType))
                {
                    if (!TryConvertInteger(unwrapped, tag.DataType, out var intObj, out var intErr))
                    {
                        failureReason = $"Failed to convert value to {tag.DataType} for tag '{tag.Name}': {intErr}";
                        return false;
                    }

                    var numVal = Convert.ToDouble(intObj, CultureInfo.InvariantCulture);
                    if (tag.Minimum.HasValue && numVal < tag.Minimum.Value)
                    {
                        failureReason = $"Value {numVal} is below the minimum {tag.Minimum.Value} for tag '{tag.Name}'.";
                        return false;
                    }

                    if (tag.Maximum.HasValue && numVal > tag.Maximum.Value)
                    {
                        failureReason = $"Value {numVal} is above the maximum {tag.Maximum.Value} for tag '{tag.Name}'.";
                        return false;
                    }

                    convertedValue = intObj;
                    return true;
                }
                else
                {
                    if (!TryConvertFloatingPoint(unwrapped, tag.DataType, out var fpObj, out var fpErr))
                    {
                        failureReason = $"Failed to convert value to {tag.DataType} for tag '{tag.Name}': {fpErr}";
                        return false;
                    }

                    var numVal = Convert.ToDouble(fpObj, CultureInfo.InvariantCulture);
                    if (tag.Minimum.HasValue && numVal < tag.Minimum.Value)
                    {
                        failureReason = $"Value {numVal} is below the minimum {tag.Minimum.Value} for tag '{tag.Name}'.";
                        return false;
                    }

                    if (tag.Maximum.HasValue && numVal > tag.Maximum.Value)
                    {
                        failureReason = $"Value {numVal} is above the maximum {tag.Maximum.Value} for tag '{tag.Name}'.";
                        return false;
                    }

                    convertedValue = fpObj;
                    return true;
                }
            }

            default:
                failureReason = $"Unsupported PlcDataType '{tag.DataType}' for tag '{tag.Name}'.";
                return false;
        }
    }

    private static bool IsIntegerType(PlcDataType dataType) =>
        dataType is PlcDataType.Int16 or PlcDataType.UInt16 or PlcDataType.Int32 or PlcDataType.UInt32;

    private static bool TryConvertInteger(
        object value,
        PlcDataType dataType,
        out object? result,
        out string error)
    {
        result = null;
        error = string.Empty;

        if (value is string str)
        {
            if (string.Equals(str, "NaN", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(str, "Infinity", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(str, "+Infinity", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(str, "-Infinity", StringComparison.OrdinalIgnoreCase))
            {
                error = "NaN and Infinity are not allowed.";
                return false;
            }

            if (str.Contains('.') || str.Contains('e') || str.Contains('E'))
            {
                error = $"Fractional or scientific string '{str}' cannot be converted to integer type {dataType}.";
                return false;
            }

            switch (dataType)
            {
                case PlcDataType.Int16:
                    if (short.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s))
                    {
                        result = s;
                        return true;
                    }
                    break;
                case PlcDataType.UInt16:
                    if (ushort.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out var us))
                    {
                        result = us;
                        return true;
                    }
                    break;
                case PlcDataType.Int32:
                    if (int.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                    {
                        result = i;
                        return true;
                    }
                    break;
                case PlcDataType.UInt32:
                    if (uint.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ui))
                    {
                        result = ui;
                        return true;
                    }
                    break;
            }

            error = $"String '{str}' is not a valid {dataType} or is out of integer bounds.";
            return false;
        }

        if (value is float or double or decimal)
        {
            var d = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (double.IsNaN(d) || double.IsInfinity(d))
            {
                error = "NaN and Infinity are not allowed.";
                return false;
            }

            if (d % 1 != 0)
            {
                error = $"Floating-point value {d} with fractional part cannot be converted to integer type {dataType}.";
                return false;
            }

            try
            {
                switch (dataType)
                {
                    case PlcDataType.Int16:
                        result = checked((short)d);
                        return true;
                    case PlcDataType.UInt16:
                        result = checked((ushort)d);
                        return true;
                    case PlcDataType.Int32:
                        result = checked((int)d);
                        return true;
                    case PlcDataType.UInt32:
                        result = checked((uint)d);
                        return true;
                }
            }
            catch (OverflowException)
            {
                error = $"Value {d} is out of range for {dataType}.";
                return false;
            }
        }

        try
        {
            switch (dataType)
            {
                case PlcDataType.Int16:
                    result = Convert.ToInt16(value, CultureInfo.InvariantCulture);
                    return true;
                case PlcDataType.UInt16:
                    result = Convert.ToUInt16(value, CultureInfo.InvariantCulture);
                    return true;
                case PlcDataType.Int32:
                    result = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                    return true;
                case PlcDataType.UInt32:
                    result = Convert.ToUInt32(value, CultureInfo.InvariantCulture);
                    return true;
            }
        }
        catch (OverflowException)
        {
            error = $"Value is out of range for {dataType}.";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        error = $"Cannot convert value to {dataType}.";
        return false;
    }

    private static bool TryConvertFloatingPoint(
        object value,
        PlcDataType dataType,
        out object? result,
        out string error)
    {
        result = null;
        error = string.Empty;

        if (value is string str)
        {
            if (string.Equals(str, "NaN", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(str, "Infinity", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(str, "+Infinity", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(str, "-Infinity", StringComparison.OrdinalIgnoreCase))
            {
                error = "NaN and Infinity are not allowed.";
                return false;
            }

            if (dataType == PlcDataType.Real)
            {
                if (float.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) &&
                    !float.IsNaN(f) && !float.IsInfinity(f))
                {
                    result = f;
                    return true;
                }
            }
            else
            {
                if (double.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) &&
                    !double.IsNaN(d) && !double.IsInfinity(d))
                {
                    result = d;
                    return true;
                }
            }

            error = $"String '{str}' is not a valid floating-point number for {dataType}.";
            return false;
        }

        try
        {
            if (dataType == PlcDataType.Real)
            {
                var f = Convert.ToSingle(value, CultureInfo.InvariantCulture);
                if (float.IsNaN(f) || float.IsInfinity(f))
                {
                    error = "NaN and Infinity are not allowed.";
                    return false;
                }
                result = f;
                return true;
            }
            else
            {
                var d = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (double.IsNaN(d) || double.IsInfinity(d))
                {
                    error = "NaN and Infinity are not allowed.";
                    return false;
                }
                result = d;
                return true;
            }
        }
        catch (OverflowException)
        {
            error = $"Value is out of range for {dataType}.";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
