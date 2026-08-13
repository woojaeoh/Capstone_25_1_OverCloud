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

        bool UpdateOnlineStatus(string userId, string localIp, bool isOnline);
        string GetLocalIp(string targetUserId);
    }
}
