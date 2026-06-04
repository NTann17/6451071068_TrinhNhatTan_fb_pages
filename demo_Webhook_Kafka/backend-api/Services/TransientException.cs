namespace backend_api.Services;

public sealed class TransientException : Exception
{
    public TransientException(string message) : base(message)
    {
    }

    public TransientException(string message, Exception innerException) : base(message, innerException)
    {
    }
}