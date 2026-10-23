using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CCL.Core.Net;

/// <summary>一次 JSON API 调用的结果。状态码原样保留，便于识别 401 上的业务错误码。</summary>
public sealed record ApiResponse(int StatusCode, JsonObject? Body, string BodyText)
{
    public bool IsSuccess => StatusCode is >= 200 and < 300;

    public string? Error => Body?["error"]?.GetValue<string>();

    public string? ErrorDescription =>
        Body?["error_description"]?.GetValue<string>() ?? Body?["errorMessage"]?.GetValue<string>();
}

public interface IApiClient
{
    Task<ApiResponse> PostFormAsync(Uri uri, IReadOnlyDictionary<string, string> form, CancellationToken token);

    Task<ApiResponse> PostJsonAsync(Uri uri, JsonNode body, CancellationToken token);

    Task<ApiResponse> GetAsync(Uri uri, string? bearerToken, CancellationToken token);
}

/// <summary>面向 JSON 接口的轻量客户端。下载走 <see cref="CclHttp"/>，这里只处理小报文。</summary>
public sealed class JsonApiClient : IApiClient, IDisposable
{
    public static string UserAgent { get; } =
        $"CrosshairClientLauncher/{typeof(JsonApiClient).Assembly.GetName().Version?.ToString(3) ?? "0.1.0"}";

    private readonly HttpClient _client;
    private bool _disposed;

    public JsonApiClient(TimeSpan? timeout = null)
    {
        _client = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(30)
        };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public Task<ApiResponse> PostFormAsync(Uri uri, IReadOnlyDictionary<string, string> form,
        CancellationToken token) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new FormUrlEncodedContent(form)
        }, token);

    public Task<ApiResponse> PostJsonAsync(Uri uri, JsonNode body, CancellationToken token) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        }, token);

    public Task<ApiResponse> GetAsync(Uri uri, string? bearerToken, CancellationToken token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (bearerToken is { Length: > 0 })
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        return SendAsync(request, token);
    }

    private async Task<ApiResponse> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using (request)
        {
            using var response = await _client.SendAsync(request, token).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

            JsonObject? body = null;
            if (!string.IsNullOrWhiteSpace(text))
            {
                try
                {
                    body = JsonNode.Parse(text) as JsonObject;
                }
                catch (JsonException)
                {
                    body = null;
                }
            }

            return new ApiResponse((int)response.StatusCode, body, text);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client.Dispose();
    }
}
