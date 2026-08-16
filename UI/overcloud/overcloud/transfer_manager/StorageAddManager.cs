using System;
using System.Diagnostics;
using System.Threading.Tasks;
using OverCloud.Services;
using overcloud.CloudApi;

namespace overcloud.transfer_manager
{
    // Phase 4 — 스토리지 추가(신규 계정 연동) 오케스트레이션. 로컬 credential.json(client_secret 포함)
    // 의존을 완전히 제거했다 — client_id/redirect_uri/scope는 서버(/api/oauth/{provider}/client-config)에서
    // 받고, authorization code 교환도 서버(/api/oauth/{provider}/exchange-code)가 대신 한다(client_secret은
    // 서버 밖으로 안 나감). 이메일/용량 조회만 클라이언트가 방금 받은 access token으로 직접 한다
    // (5.1 — 서버는 클라우드 제공자에 직접 접속하지 않는다). Dropbox는 이번 범위 밖(기존 결정 유지).
    public static class StorageAddManager
    {
        public static async Task<(bool Success, string Message)> AddAsync(string cloudType, string userId)
        {
            string provider = cloudType switch { "GoogleDrive" => "google", "OneDrive" => "onedrive", _ => null };
            if (provider == null)
                return (false, $"지원되지 않는 클라우드: {cloudType}");

            var config = await OverCloudApiClient.GetOAuthClientConfigAsync(provider);
            if (config == null)
                return (false, "OAuth 설정을 가져오지 못했습니다.");
            var (clientId, redirectUri, scope) = config.Value;

            string authUrl = BuildAuthUrl(provider, clientId, redirectUri, scope);
            Console.WriteLine("브라우저 열기: " + authUrl);
            Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

            string code = await OAuthRedirectListener.GetAuthCodeAsync(redirectUri);
            if (string.IsNullOrEmpty(code))
                return (false, "인증이 취소됐거나 코드를 받지 못했습니다.");

            var tokens = await OverCloudApiClient.ExchangeOAuthCodeAsync(provider, code);
            if (tokens == null)
                return (false, "인증 코드 교환에 실패했습니다.");

            var accountInfo = provider == "google"
                ? await GoogleDriveTokenClient.GetAccountInfoAsync(tokens.Value.accessToken)
                : await OneDriveTokenClient.GetAccountInfoAsync(tokens.Value.accessToken);
            if (accountInfo == null || string.IsNullOrEmpty(accountInfo.Value.email))
                return (false, "계정 정보 조회에 실패했습니다.");

            return await OverCloudApiClient.AddStorageAsync(
                userId, cloudType, accountInfo.Value.email, tokens.Value.refreshToken,
                accountInfo.Value.totalKB, accountInfo.Value.usedKB);
        }

        private static string BuildAuthUrl(string provider, string clientId, string redirectUri, string scope)
        {
            if (provider == "google")
                return "https://accounts.google.com/o/oauth2/v2/auth" +
                    $"?client_id={Uri.EscapeDataString(clientId)}" +
                    $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                    "&response_type=code" +
                    $"&scope={Uri.EscapeDataString(scope)}" +
                    "&access_type=offline&prompt=consent";

            return "https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize" +
                $"?client_id={Uri.EscapeDataString(clientId)}" +
                "&response_type=code" +
                $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                "&response_mode=query" +
                $"&scope={Uri.EscapeDataString(scope)}" +
                "&prompt=select_account";
        }
    }
}
