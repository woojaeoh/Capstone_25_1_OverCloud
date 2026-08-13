using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace OverCloud.Api.Auth
{
    // JWT 정책은 docs/3TIER_ARCHITECTURE.md 5.5 참고:
    // access token은 신원 증명 용도로만 사용(TTL 짧게), 정지/할당량 같은 authorization 판단은
    // 토큰 내용이 아니라 매 요청마다 DB 최신 상태로 한다 — 이 클래스는 토큰 발급/검증만 담당한다.
    public class JwtTokenService
    {
        private readonly IConfiguration _config;
        private readonly SymmetricSecurityKey _signingKey;

        public JwtTokenService(IConfiguration config)
        {
            _config = config;
            var key = _config["Jwt:Key"]
                ?? throw new InvalidOperationException(
                    "JWT 서명 키가 없습니다. 환경변수 Jwt__Key 를 설정하세요 (32바이트 이상 임의 문자열 권장).");
            _signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
        }

        private int AccessTokenMinutes => int.TryParse(_config["Jwt:AccessTokenMinutes"], out var m) ? m : 20;
        private int RefreshTokenDays => int.TryParse(_config["Jwt:RefreshTokenDays"], out var d) ? d : 14;

        public string Issuer => _config["Jwt:Issuer"] ?? "OverCloud.Api";
        public string Audience => _config["Jwt:Audience"] ?? "OverCloud.Client";

        public string IssueAccessToken(string userId)
        {
            var claims = new[] { new Claim(JwtRegisteredClaimNames.Sub, userId) };
            var creds = new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256);

            var token = new JwtSecurityToken(
                issuer: Issuer,
                audience: Audience,
                claims: claims,
                expires: DateTime.UtcNow.AddMinutes(AccessTokenMinutes),
                signingCredentials: creds);

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        // opaque random refresh token — JWT가 아니라 DB에 해시로 저장해 조회/폐기(revocation)가 가능하게 한다.
        public (string plainToken, string tokenHash, DateTime expiry) IssueRefreshToken()
        {
            var bytes = RandomNumberGenerator.GetBytes(32);
            string plainToken = Convert.ToBase64String(bytes);
            string hash = HashRefreshToken(plainToken);
            DateTime expiry = DateTime.UtcNow.AddDays(RefreshTokenDays);
            return (plainToken, hash, expiry);
        }

        public string HashRefreshToken(string plainToken)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(plainToken));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }
    }
}
