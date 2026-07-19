using System.Net.Http;
using System.Threading.Tasks;

namespace Timer.RequestManager.Replay;

/// <summary>
/// HTTP-based replay storage implementation.
/// Uploads and downloads replay files via a configurable Base URL.
/// </summary>
internal sealed class HttpReplayStorage : IReplayStorage
{
    private readonly HttpClient _httpClient;
    private readonly string     _baseUrl;

    public HttpReplayStorage(HttpClient httpClient, string baseUrl)
    {
        _httpClient = httpClient;
        _baseUrl    = baseUrl.TrimEnd('/');
    }

    public async Task<string> UploadAsync(string key, byte[] data)
    {
        var url = $"{_baseUrl}/{key}";

        using var content  = new ByteArrayContent(data);
        using var response = await _httpClient.PutAsync(url, content);

        response.EnsureSuccessStatusCode();

        return url;
    }

    public async Task<byte[]> DownloadAsync(string url)
    {
        return await _httpClient.GetByteArrayAsync(url);
    }

    public async Task DeleteAsync(string url)
    {
        try
        {
            using var response = await _httpClient.DeleteAsync(url);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException)
        {
            // Best-effort: file may already be gone or endpoint may not support DELETE.
        }
    }
}
