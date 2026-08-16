using System.Text.Json;

namespace OverCloud.Api.Auth
{
    // Phase 3 (3-Tier 인증 계층): OneDrive refresh_token -> access_token 교환을 서버로 이관.
    // 기존 클라이언트 로직(OverCloud/Services/FileManager/DriveManager/OneDriveTokenRefresher.cs)과
    // 동일한 교환 로직. OneDrive는 public client라 client_secret은 원래도 쓰지 않는다 —
    // client_id/redirect_uri/scope만 서버 설정(OAuth:OneDrive:ClientId)에서 읽는다.
    public class OneDriveOAuthService
    {
        public const string RedirectUri = "http://localhost:5000/";
        public const string Scope = "offline_access Files.ReadWrite.ALL User.Read";

        private readonly IConfiguration _config;
        private readonly IHttpClientFactory _httpClientFactory;

        public OneDriveOAuthService(IConfiguration config, IHttpClientFactory httpClientFactory)
        {
            _config = config;
            _httpClientFactory = httpClientFactory;
        }

        // 스토리지 추가: authorization code -> access token + refresh token 최초 교환. OneDrive는 public
        // client라 client_secret 자체가 없다(위 refresh 교환과 동일) — 그래도 서버가 대표로 처리하는 이유는
        // client_id/redirect_uri/scope를 한 곳(서버 설정)에서만 관리해 재배포 시 클라이언트 재빌드 없이
        // 바꿀 수 있게 하기 위함.
        public async Task<(string accessToken, string refreshToken, DateTime expiry)> ExchangeAuthorizationCodeAsync(string code)
        {
            var clientId = _config["OAuth:OneDrive:ClientId"]
                ?? throw new InvalidOperationException("OAuth:OneDrive:ClientId 설정이 없습니다.");

            var client = _httpClientFactory.CreateClient();

            var parameters = new Dictionary<string, string>
            {
                { "client_id", clientId },
                { "code", code },
                { "redirect_uri", RedirectUri },
                { "grant_type", "authorization_code" },
                { "scope", Scope }
            };

            var response = await client.PostAsync(
                "https://login.microsoftonline.com/consumers/oauth2/v2.0/token",
                new FormUrlEncodedContent(parameters));

            var content = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"OneDrive authorization code 교환 실패: {content}");

            using var json = JsonDocument.Parse(content);
            string accessToken = json.RootElement.GetProperty("access_token").GetString()!;
            string refreshToken = json.RootElement.GetProperty("refresh_token").GetString()!;
            int expiresIn = json.RootElement.GetProperty("expires_in").GetInt32();

            return (accessToken, refreshToken, DateTime.UtcNow.AddSeconds(expiresIn - 60));
        }

        public async Task<(string accessToken, DateTime expiry)> ExchangeRefreshTokenAsync(string refreshToken)
        {
            var clientId = _config["OAuth:OneDrive:ClientId"]
                ?? throw new InvalidOperationException("OAuth:OneDrive:ClientId 설정이 없습니다.");

            var client = _httpClientFactory.CreateClient();

            var parameters = new Dictionary<string, string>
            {
                { "client_id", clientId },
                { "refresh_token", refreshToken },
                { "redirect_uri", RedirectUri },
                { "grant_type", "refresh_token" },
                { "scope", Scope }
            };

            var response = await client.PostAsync(
                "https://login.microsoftonline.com/consumers/oauth2/v2.0/token",
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
                catch (JsonException) { /* provider 응답이 JSON이 아닌 경우 그대로 아래 예외로 넘김 */ }

                if (errorCode == "invalid_grant")
                    throw new OAuthRefreshTokenInvalidException(
                        "OneDrive refresh token이 만료되었거나 폐기되었습니다. 계정을 재연동해야 합니다.");

                throw new InvalidOperationException($"OneDrive access token 재발급 실패: {content}");
            }

            using var json = JsonDocument.Parse(content);
            string accessToken = json.RootElement.GetProperty("access_token").GetString()!;
            int expiresIn = json.RootElement.GetProperty("expires_in").GetInt32();

            return (accessToken, DateTime.UtcNow.AddSeconds(expiresIn - 60));
        }
    }
}
