using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ReleaseNotesHelper.App.Ai;

public class GeminiClient
{
    // RNH_P0_2026_06_18
    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(120)
    };

    public async Task<string> GenerateAsync(string apiKey, string prompt)
    {
        var url = "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent";

        var body = new
        {
            contents = new[]
            {
                new
                {
                    parts = new[]
                    {
                        new { text = prompt }
                    }
                }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("x-goog-api-key", apiKey);
        request.Content = new StringContent(
            JsonSerializer.Serialize(body),
            Encoding.UTF8,
            "application/json");

        using var response = await _httpClient.SendAsync(request);
        var responseText = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(BuildSafeAiErrorMessage(response.StatusCode, responseText));

        using var document = JsonDocument.Parse(responseText);

        return document.RootElement
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString() ?? "";
    }

    private static string BuildSafeAiErrorMessage(HttpStatusCode statusCode, string responseText)
    {
        var message = ExtractProviderErrorMessage(responseText);
        message = RedactSensitiveData(message);
        message = TrimForLog(message, 2000);

        return $"AI request failed. Status={(int)statusCode} {statusCode}. Message={message}";
    }

    private static string ExtractProviderErrorMessage(string responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
            return "Empty provider response.";

        try
        {
            using var document = JsonDocument.Parse(responseText);
            var root = document.RootElement;

            if (root.TryGetProperty("error", out var error))
            {
                var parts = new List<string>();

                if (error.TryGetProperty("status", out var status))
                    parts.Add(status.GetString() ?? "");

                if (error.TryGetProperty("code", out var code))
                    parts.Add("code=" + code.ToString());

                if (error.TryGetProperty("message", out var message))
                    parts.Add(message.GetString() ?? "");

                var joined = string.Join("; ", parts.Where(x => !string.IsNullOrWhiteSpace(x)));
                if (!string.IsNullOrWhiteSpace(joined))
                    return joined;
            }
        }
        catch
        {
            // Provider did not return JSON or returned malformed JSON.
        }

        return responseText;
    }

    private static string RedactSensitiveData(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        var result = value;
        result = Regex.Replace(result, "(?i)(api[_-]?key|x-goog-api-key|token|password|secret)\\s*[:=]\\s*[\\\"']?[^\\\"'\\s,}]+", "$1=***");
        result = Regex.Replace(result, @"AIza[0-9A-Za-z_\-]{20,}", "AIza***");
        result = Regex.Replace(result, @"(?i)Bearer\s+[0-9A-Za-z._\-]+", "Bearer ***");

        return result;
    }

    private static string TrimForLog(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            return value;

        return value[..maxLength] + "... [truncated]";
    }

}