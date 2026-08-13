using DB.overcloud.Models;

namespace DB.overcloud.Repository
{
    public interface IAccountRepository
    {
        List<CloudStorageInfo> GetAllAccounts(string ID);
        Task<List<CloudStorageInfo>> GetAllAccountsAsync(string ID);
        bool DeleteAccountById(string ID);
        bool UpdateAccountUsage(string ID, ulong totalSize, ulong usedSize);
        bool assign_overcloud(string ID, string password, string salt);
        string login_overcloud(string ID, string password);
        string get_salt_by_id(string ID);
        string get_password_by_id(string ID);

        bool UpdateOnlineStatus(string userId, string localIp, bool isOnline);
        string GetLocalIp(string targetUserId);

        // 3-Tier 세션 refresh token (Phase 2) — CloudStorageInfo.refresh_token(OAuth)과 무관
        bool SaveRefreshToken(string userId, string refreshTokenHash, DateTime expiry);
        (string? hash, DateTime? expiry) GetRefreshTokenInfo(string userId);
        bool RevokeRefreshToken(string userId);
    }
}
