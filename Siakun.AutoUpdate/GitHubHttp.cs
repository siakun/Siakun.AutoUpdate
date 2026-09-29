using System.Net.Http.Headers;

namespace Siakun.AutoUpdate;

// INTENT: HttpClient의 연결 풀은 재사용하고 요청 헤더는 요청마다 설정한다.
// 호출자가 전달한 클라이언트의 기본 헤더나 수명을 라이브러리에서 바꾸지 않는다.
internal static class GitHubHttp
{
    internal static HttpClient Shared { get; } = new() { Timeout = TimeSpan.FromMinutes(2) };

    internal static async Task<HttpResponseMessage> GetAsync(HttpClient client, string url, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Siakun.AutoUpdate", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
    }

    internal static async Task<string> GetStringAsync(HttpClient client, string url, CancellationToken token)
    {
        // 메타데이터 본문에도 제한을 걸어, 응답 헤더만 받은 채 조회가 끝없이 대기하지 않게 한다.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        using var response = await GetAsync(client, url, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
    }
}
