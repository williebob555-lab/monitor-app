namespace MonitorApp.Contracts;

public sealed class ContractValidationResult
{
    private static readonly ContractValidationResult Successful = new([]);

    private ContractValidationResult(IReadOnlyList<string> errors)
    {
        Errors = errors;
    }

    public bool IsValid => Errors.Count == 0;

    public IReadOnlyList<string> Errors { get; }

    internal static ContractValidationResult Success() => Successful;

    internal static ContractValidationResult Failure(List<string> errors) =>
        new(errors.AsReadOnly());
}
