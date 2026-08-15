using System.Text.Json;

namespace OverCloud.Api.Auth
{
    // Phase 3 (3-Tier 인증 계층): Google Drive refresh_token -> access_token 교환을 서버로 이관.
    // client_secret은 더 이상 클라이언트/DB가 아니라 서버 설정(OAuth:Google:ClientSecret)에서만 읽는다.
    // 기존 클라이언트 로직(OverCloud/Services/FileManager/DriveManager/GoogleTokenProvider.cs)과
    // 동일한 교환 로직이며, client_secret 출처만 다르다.
    public class GoogleOAuthService
    {
        private readonly IConfiguration _config;
        private readonly IHttpClientFactory _httpClientFactory;

        public GoogleOAuthService(IConfiguration config, IHttpClientFactory httpClientFactory)
        {
            _config = config;
            _httpClientFactory = httpClientFactory;
        }

        public async Task<(string accessToken, DateTime expiry)> ExchangeRefreshTokenAsync(string refreshToken)
        {
            var clientId = _config["OAuth:Google:ClientId"]
                ?? throw new InvalidOperationException("OAuth:Google:ClientId 설정이 없습니다.");
            var clientSecret = _config["OAuth:Google:ClientSecret"]
                ?? throw new InvalidOperationException("OAuth:Google:ClientSecret 설정이 없습니다.");

            var client = _httpClientFactory.CreateClient();

            var parameters = new Dictionary<string, string>
            {
                { "client_id", clientId },
                { "client_secret", clientSecret },
                { "refresh_token", refreshToken },
                { "grant_type", "refresh_token" }
            };

            var response = await client.PostAsync(
                "https://oauth2.googleapis.com/token",
                new FormUrlEncodedContent(parameters));

            var content = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                string? errorCode = null;
                try
                {
                    using var errorJson = JsonDocument.Parse(content);
                    if (errorJson.RootElement.TryGetProperty("error", out var errorProp))
                        errorCode = errorProp.GetString();
                }
                catch (JsonException) { /* Google 응답이 JSON이 아닌 경우 그대로 아래 예외로 넘김 */ }

                if (errorCode == "invalid_grant")
                    throw new OAuthRefreshTokenInvalidException(
                        "Google refresh token이 만료되었거나 폐기되었습니다. 계정을 재연동해야 합니다.");

                throw new InvalidOperationException($"Google access token 재발급 실패: {content}");
            }

            using var json = JsonDocument.Parse(content);
            string accessToken = json.RootElement.GetProperty("access_token").GetString()!;
            int expiresIn = json.RootElement.GetProperty("expires_in").GetInt32();

            return (accessToken, DateTime.UtcNow.AddSeconds(expiresIn - 60));
        }
    }
}
