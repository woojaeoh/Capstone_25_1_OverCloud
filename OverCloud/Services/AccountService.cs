using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DB.overcloud.Models;
using DB.overcloud.Repository;
using MySqlX.XDevAPI;
using System.Security.AccessControl;
using OverCloud.Services.FileManager.DriveManager;
using OverCloud.Services.StorageManager;

namespace OverCloud.Services
{
    public class AccountService //프론트 <-> 프로그램 함수 호출
    {
        private readonly IAccountRepository accountRepository; //OverClour의 계정 정보 
        private readonly IStorageRepository storageRepository; // OverCloud계정 하나 안에 또 다른 계정들의 정보
        private readonly QuotaManager quotaManager;
        
        public AccountService(IAccountRepository accountRepo, IStorageRepository storageRepo ,QuotaManager quotaMgr)
        {
            accountRepository = accountRepo;
            storageRepository = storageRepo;
            quotaManager = quotaMgr;
        }

        // 오버클라우드 계정에 새로운 계정 추가 (UI에서 호출)
        // Phase 4 — GoogleDrive/OneDrive는 이제 overcloud.transfer_manager.StorageAddManager(신규 API
        // 기반 오케스트레이션, 로컬 client_secret 의존 없음)로 이관됨. 이 메서드는 AddAccountWindow에서
        // 더 이상 그 두 타입으로 호출되지 않고 Dropbox 전용 경로로만 남아있다(Dropbox 자체 이관은 범위 밖).
        public async Task<bool> Add_Cloud_Storage(CloudStorageInfo storage, string userId) //storage가 협업, userid가 개인오버클라우드 id
        {
            //협업 클라우드 아이디를 넘겨줌
            
            if (storage.CloudType == "GoogleDrive")
            {
                var (email, refreshToken, clientId, clientSecret) = await GoogleAuthHelper.AuthorizeAsync(storage.AccountId);
                storage.AccountId = email;
                storage.RefreshToken = refreshToken;
                storage.ClientId = clientId;
                storage.ClientSecret = clientSecret;
                Console.WriteLine("구글 계정 추가중...");
            }
            else if (storage.CloudType == "OneDrive")
            {
                var (email, refreshToken, clientId, clientSecret) = await OneDriveAuthHelper.AuthorizeAsync(storage.AccountId);
                storage.AccountId = email;
                storage.RefreshToken = refreshToken;
                storage.ClientId = clientId;
                storage.ClientSecret = clientSecret;
                Console.WriteLine("원드라이브 계정 추가중...");
            }
            else if (storage.CloudType == "Dropbox")
            {
                var (appKey, appSecret, refreshToken) = await DropboxAuthHelper.LoadDropboxCredentialsAsync();
                storage.AccountId = storage.ID;
                storage.RefreshToken = refreshToken;
                storage.ClientId = appKey;
                storage.ClientSecret = appSecret;
                Console.WriteLine("드롭박스 계정 추가중...");
            }

            bool result = storageRepository.AddCloudStorage(storage, userId);

            if (result)
            {
                var clouds = accountRepository.GetAllAccounts(storage.ID);
                var OneCloud = clouds.FirstOrDefault(c => c.AccountId == storage.AccountId);
                if (OneCloud == null)
                {
                    Console.WriteLine("❌ 계정 추가 직후 방금 추가한 계정을 다시 찾지 못함 — 용량 갱신 생략");
                    return result;
                }

                //계정 추가 성공시 바로 용량 업데이트 호출
                await quotaManager.SaveDriveQuotaToDB(storage.ID, OneCloud.CloudStorageNum);

                // ⭐ StorageSessionManager에도 반영 (옵션)
                StorageSessionManager.Quotas.Add(new CloudQuotaInfo
                {
                    CloudStorageNum = storage.CloudStorageNum,
                    CloudType = storage.CloudType,
                    TotalCapacityKB = storage.TotalCapacity,
                    UsedCapacityKB = storage.UsedCapacity
                });

                // 전체 합산 용량 업데이트도 할 수 있음
                quotaManager.UpdateAggregatedStorageForUser(storage.ID);
            }

            return result;
        }

        // 오버클라우드 계정에 있던 클라우드 하나 삭제 (UI에서 호출)
        public async Task<bool> Delete_Cloud_Storage(int cloudStorageNum, string userId)
        {
            var target = storageRepository.GetCloud(cloudStorageNum, userId);
            if (target == null)
            {
                Console.WriteLine($" 삭제 실패 : cloudStorageNum {cloudStorageNum}에 해당하는 클라우드 계정이 없습니다.");
                return false;
            }

            //삭제할 계정을 제외한 나머지 스토리지의 용량정보.
            ulong storageCapacity = quotaManager.GetTotalRemainingQuotaInBytes_Delete_Account(userId, target.CloudStorageNum);

            //해당 스토리지의 파일들의 크기합산.
            ulong filesSize = quotaManager.AllFilelistSize(target.CloudStorageNum);

            //공간이 남는다면 재분배 해야함.
            if (storageCapacity > filesSize)
            {
                bool redistributionResult = await quotaManager.AccountFile_Redistribution(target.CloudStorageNum, userId);
                if (!redistributionResult)
                {
                    Console.WriteLine("❌ 파일 재분배 실패로 삭제 중단");
                    return false;
                }
            }
            else
            {
                Console.WriteLine("❌ 파일을 재분배할 공간이 부족합니다");
                return false;
            }

                //var deleteCloud = storageRepository.GetCloud(cloudStorageNum,userId);

                //Thread.Sleep(1);
                bool result = storageRepository.DeleteCloudStorage(cloudStorageNum, userId);
            if (result)
            {
                StorageSessionManager.RemoveQuota(target.AccountId, target.CloudType);
                Console.WriteLine($" 클라우드 계정 삭제 성공 : cloudStorageNum {cloudStorageNum}");

                quotaManager.UpdateAggregatedStorageForUser(userId);
                //메모리 세션에서 해당 계정 제거.
            }
            else
            {
                Console.WriteLine($" 클라우드 계정 삭제 실패 : cloudStorageNum {cloudStorageNum}");
            }

            return result;
        }

        // 오버클라우드 계정 안의 모든 계정 정보 조회 (UI에서 호출)
        public List<CloudStorageInfo> Get_Clouds_For_User(string userId)
        {
            return accountRepository.GetAllAccounts(userId);
        }
   

 

    }
}
