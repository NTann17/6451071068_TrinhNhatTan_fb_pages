using Microsoft.AspNetCore.Http;

namespace webhook_service.Services;

public interface IFacebookSignatureValidator
{
    bool IsValidSignature(IHeaderDictionary headers, string payload);
}