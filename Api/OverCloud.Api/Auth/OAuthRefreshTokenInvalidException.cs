namespace OverCloud.Api.Auth
{
    // refresh token이 만료/폐기된 경우(provider가 invalid_grant를 반환) — 재연동이 필요한 정상적인
    // 실패 케이스이므로, 그 외의 서버측 설정 오류(InvalidOperationException)와 구분해서 처리한다.
    // Google/OneDrive/Dropbox 등 provider 공통으로 사용.
    public class OAuthRefreshTokenInvalidException : Exception
    {
        public OAuthRefreshTokenInvalidException(string message) : base(message) { }
    }
}
