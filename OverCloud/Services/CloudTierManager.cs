using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DB.overcloud.Models;
using DB.overcloud.Repository;

namespace OverCloud.Services
{
    public class CloudTierManager
    {

        private readonly IAccountRepository accountRepository;     

        public CloudTierManager(IAccountRepository accountRepository)
        {
            this.accountRepository = accountRepository;
 
        }

        public CloudStorageInfo SelectBestStorage(ulong fileSizeKB, string userId) //kb단위로 호출
        {

            var clouds = accountRepository.GetAllAccounts(userId);
            if (clouds == null)
            {
                System.Diagnostics.Debug.WriteLine("❌ 클라우드 계정 없음");
                return null;
            }
                
                  // 사전 정의된 티어 순서
            var ordered = clouds
                .OrderBy(c => GetTierValue(c.CloudType))  //1순위: 티어 순서 
                .ThenByDescending(c => c.TotalCapacity - c.UsedCapacity); // 같은 티어일 땐 여유 공간 많은 순

            foreach (var cloud in ordered)
            {
                var remaining = (long)cloud.TotalCapacity - (long)cloud.UsedCapacity;
                Console.WriteLine($"🧪 클라우드: {cloud.CloudType}, 잔여용량: {remaining}KB");

                if (remaining >= (long)fileSizeKB) // KB 단위 맞추기
                    return cloud;
            }

            Console.WriteLine("❌ 저장 가능한 클라우드가 없습니다.");
            return null; // 저장 가능한 클라우드 없음
        }

        private int GetTierValue(string cloudType)
        {
            return cloudType switch
            {
                "OneDrive" => 1,
                "GoogleDrive" => 2,
                "Dropbox" => 3,
                _ => 99 // 기타 클라우드
            };
        }

        public List<CloudStorageInfo> GetStoragePlan(ulong totalFileSizeKB, string userId)
        {
            var clouds = accountRepository.GetAllAccounts(userId);
            if (clouds == null || clouds.Count == 0)
            {
                Console.WriteLine("❌ 등록된 클라우드 계정이 없습니다.");
                return null;
            }

            // 1. 티어 순 + 여유 공간 많은 순으로 정렬
            var ordered = clouds
                .OrderBy(c => GetTierValue(c.CloudType))
                .ThenByDescending(c => c.TotalCapacity - c.UsedCapacity)
                .ToList();

            // 2. 분산 저장이 가능한지 확인하며 누적
            List<CloudStorageInfo> selected = new();
            ulong totalAvailableBytes = 0;

            foreach (var cloud in ordered)
            {
                long remainingKB = (long)cloud.TotalCapacity - (long)cloud.UsedCapacity;


                if (remainingKB <= 0) continue;

                ulong remainingBytes = (ulong)(remainingKB * 1024);

                selected.Add(cloud);
                totalAvailableBytes += remainingBytes;

                Console.WriteLine($"선택된 클라우드: {cloud.CloudType}, 잔여: {remainingKB} KB");

                if (totalAvailableBytes >= totalFileSizeKB*1024)
                    break;
            }

            // 3. 최종 확인
            if (totalAvailableBytes < totalFileSizeKB* 1024 )
            {
                Console.WriteLine(" 분산 저장 불가: 전체 클라우드 용량 부족");
                return null;
            }


            return selected;
        }

        
        //계정 추가한 스토리지들의 남은 용량 총합 리턴( 바이트)
        public ulong GetTotalRemainingQuotaInBytes(string userId)
        {
            var clouds = accountRepository.GetAllAccounts(userId);
            if (clouds == null || clouds.Count == 0)
                return 0;

            ulong totalAvailableBytes = 0;

            foreach (var cloud in clouds)
            {
                ulong remainingKB = (ulong)(cloud.TotalCapacity - cloud.UsedCapacity);
                totalAvailableBytes += remainingKB * 1024; // KB → byte
            }

            return totalAvailableBytes;
        }


        public List<CloudStorageInfo> GetCandidateStorages(ulong fileSizeKB, string userId,int excludeCloudStorageNum)
        {
            var clouds = accountRepository.GetAllAccounts(userId);
            if (clouds == null || clouds.Count == 0)
            {
                Console.WriteLine("❌ 클라우드 계정 없음");
                return new List<CloudStorageInfo>();
            }

            var candidates = clouds
                 .Where(c => c.CloudStorageNum != excludeCloudStorageNum)  // 현재 계정 제외
                .Where(c => ((long)c.TotalCapacity - (long)c.UsedCapacity) >= (long)fileSizeKB)
                .OrderBy(c => GetTierValue(c.CloudType))  // 티어 순서
                .ThenByDescending(c => c.TotalCapacity - c.UsedCapacity)  // 같은 티어면 여유공간 큰 순
                .ToList();

            foreach (var c in candidates)
            {
                var remaining = (long)c.TotalCapacity - (long)c.UsedCapacity;
                Console.WriteLine($"🧪 재분배 후보: {c.CloudType}, 잔여용량: {remaining}KB");
            }

            if (candidates.Count == 0)
            {
                Console.WriteLine("❌ 재분배 가능한 스토리지가 없습니다.");
            }

            return candidates;
        }




    }


}