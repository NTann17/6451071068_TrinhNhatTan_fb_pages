namespace backend_api.Contracts;

public sealed record ApiResponse<T>(bool Success, T? Data, string Message);

public sealed record ApiErrorResponse(string Code, string Message, string? Details = null);

public sealed record CreatePostRequest(string Message);