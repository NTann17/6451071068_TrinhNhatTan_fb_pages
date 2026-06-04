namespace core_service.Services;

public class TransientException : Exception
{
    public TransientException()
    {
    }

    public TransientException(string message) : base(message)
    {
    }

    public TransientException(string message, Exception inner) : base(message, inner)
    {
    }
}
