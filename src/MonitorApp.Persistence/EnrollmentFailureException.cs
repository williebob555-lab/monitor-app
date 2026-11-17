namespace MonitorApp.Persistence;

public enum EnrollmentFailure
{
    InvalidRequest,
    NotFound,
    Expired,
    AlreadyUsed,
    Cancelled,
    Unauthorized
}

public sealed class EnrollmentFailureException : Exception
{
    public EnrollmentFailureException(EnrollmentFailure failure, string safeMessage)
        : base(safeMessage) => Failure = failure;

    public EnrollmentFailure Failure { get; }
}
