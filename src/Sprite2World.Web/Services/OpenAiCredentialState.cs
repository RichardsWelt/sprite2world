using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Sprite2World.Application;
using Sprite2World.Infrastructure;

namespace Sprite2World.Web.Services;

public sealed class OpenAiCredentialState(IHttpClientFactory httpClientFactory, IDataProtectionProvider protectionProvider, IOptions<OpenAiOptions> options) : IOpenAiApiKeyProvider
{
    private readonly IDataProtector _protector = protectionProvider.CreateProtector("Sprite2World.OpenAI.UserApiKey.v1");
    private string? _apiKey;

    public string? ApiKey => IsReady ? _apiKey : null;
    public bool IsReady { get; private set; }
    public bool HasConfiguredServerKey => !string.IsNullOrWhiteSpace(options.Value.ApiKey);
    public string Status { get; private set; } = "No API key configured";

    public Task<ApiKeyValidationResult> ValidateConfiguredServerKeyAsync(CancellationToken cancellationToken = default) =>
        ValidateAsync(options.Value.ApiKey, cancellationToken);

    public async Task<ApiKeyValidationResult> ValidateAsync(string? candidate, CancellationToken cancellationToken = default)
    {
        var key = candidate?.Trim();
        if (string.IsNullOrWhiteSpace(key) || key.Length < 20)
            return Fail("Enter a complete OpenAI API key.");

        try
        {
            var http = httpClientFactory.CreateClient("OpenAiCredentialValidation");
            using var request = new HttpRequestMessage(HttpMethod.Get, "models");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var message = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "This API key is invalid or has been revoked.",
                    HttpStatusCode.Forbidden => "This API key does not have the required OpenAI access.",
                    HttpStatusCode.TooManyRequests => "The key was recognized, but its quota or rate limit currently prevents use.",
                    _ => "OpenAI could not validate this key. Please try again."
                };
                return Fail(message);
            }

            _apiKey = key;
            IsReady = true;
            Status = "OpenAI connected";
            return new(true, Status, _protector.Protect(key));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Fail("OpenAI did not respond in time.");
        }
        catch (HttpRequestException)
        {
            return Fail("OpenAI is currently unreachable. Check the connection and try again.");
        }
    }

    public async Task<bool> RestoreAsync(string? protectedToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(protectedToken)) return false;
        try
        {
            var result = await ValidateAsync(_protector.Unprotect(protectedToken), cancellationToken);
            return result.Success;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            Clear();
            Status = "The saved API key could not be restored.";
            return false;
        }
    }

    public void Clear()
    {
        _apiKey = null;
        IsReady = false;
        Status = "No API key configured";
    }

    private ApiKeyValidationResult Fail(string message)
    {
        Status = message;
        return new(false, message, null);
    }
}

public sealed record ApiKeyValidationResult(bool Success, string Message, string? ProtectedToken);
