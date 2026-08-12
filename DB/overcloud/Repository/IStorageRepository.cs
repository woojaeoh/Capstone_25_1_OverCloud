using DB.overcloud.Models;

namespace DB.overcloud.Repository
{
    public interface IStorageRepository
    {
        CloudStorageInfo GetCloud(int cloudStorageNum, string userId);
        bool AddCloudStorage(CloudStorageInfo info, string userId);
        bool DeleteCloudStorage(int cloudStorageNum, string userId);
        bool account_save(CloudStorageInfo one_cloud);
        bool UpdateRefreshToken(int cloudStorageNum, string userId, string refreshToken);
        bool IncrementUsedCapacity(int cloudStorageNum, string userId, long deltaKB);
    }
}