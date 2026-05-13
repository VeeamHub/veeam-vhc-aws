namespace VeeamVhcAws.Ui;

/// <summary>
/// Result of a single connectivity test performed by ConnectionTester.
/// </summary>
public class ConnectionTestResult
{
    public string Target { get; init; } = "";
    public string Type { get; init; } = "";
    public bool Success { get; init; }
    public string ErrorMessage { get; init; } = "";

    /// <summary>
    /// User-friendly hint appended based on common error patterns.
    /// </summary>
    public string ErrorHint { get; init; } = "";
}
