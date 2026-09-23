namespace PlcMcp.Engineering.Hmi;

public sealed class HmiValidationException : Exception
{
    public HmiValidationResult ValidationResult { get; }

    public HmiValidationException(HmiValidationResult validationResult)
        : base(FormatMessage(validationResult))
    {
        ValidationResult = validationResult;
    }

    private static string FormatMessage(HmiValidationResult result)
    {
        var firstErrors = string.Join("; ", result.Errors.Take(3).Select(e => $"[{e.Code}] {e.Component}:{e.Identifier} - {e.Message}"));
        return $"HMI manifest validation failed with {result.Errors.Count} error(s): {firstErrors}";
    }
}
