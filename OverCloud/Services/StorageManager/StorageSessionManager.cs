using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using DB.overcloud.Models;
using DB.overcloud.Repository;

namespace OverCloud.Services.StorageManager
{
    public class CloudQuotaInfo
    {
     
        public string AccountId { get; set; }
        public int CloudStorageNum { get; set; }
        public string CloudType { get; set; }
        public ulong TotalCapacityKB { get; set; }
        public ulong UsedCapacityKB { get; set; }
    }

    public static class StorageSessionManager
    {
        private static readonly object _lock = new object();

        public static List<CloudQuotaInfo> Quotas { get; set; } = new List<CloudQuotaInfo>();

        public static void SetQuota(int cloudStorageNum, string accountId, string cloudType, ulong totalKB, ulong usedKB)
        {
            lock (_lock)
            {
                var existing = Quotas.FirstOrDefault(q =>
                    q.CloudStorageNum == cloudStorageNum ||
                    (q.AccountId == accountId && q.CloudType == cloudType)
                );

                if (existing != null)
                {
                    existing.TotalCapacityKB = totalKB;
                    existing.UsedCapacityKB = usedKB;
                    existing.CloudStorageNum = cloudStorageNum;
                }
                else
                {
                    Quotas.Add(new CloudQuotaInfo
                    {
                        CloudStorageNum = cloudStorageNum,
                        AccountId = accountId,
                        CloudType = cloudType,
                        TotalCapacityKB = totalKB,
                        UsedCapacityKB = usedKB
                    });
                }
            }
        }

        // [Lost Update 방지] 절댓값 덮어쓰기 대신 delta로 메모리 용량 갱신
        // 여러 업로드 스레드가 동시에 호출해도 lock으로 직렬화
        public static void AdjustUsedCapacity(int cloudStorageNum, long deltaKB)
        {
            lock (_lock)
            {
                var quota = Quotas.FirstOrDefault(q => q.CloudStorageNum == cloudStorageNum);
                if (quota == null) return;

                long newUsed = (long)quota.UsedCapacityKB + deltaKB;
                quota.UsedCapacityKB = (ulong)Math.Max(0, newUsed);
            }
        }

        public static void RemoveQuota(string accountId, string cloudType)
        {
            lock (_lock)
            {
                Quotas.RemoveAll(q => q.AccountId == accountId && q.CloudType == cloudType);
            }
        }

        public static ulong GetTotalAvailableCapacityKB()
        {
            lock (_lock)
            {
                ulong total = 0;
                foreach (var q in Quotas)
                {
                    if (q.TotalCapacityKB > q.UsedCapacityKB)
                        total += (q.TotalCapacityKB - q.UsedCapacityKB);
                }
                return total;
            }
        }

        public static (ulong totalKB, ulong usedKB) GetAggregatedUsage()
        {
            lock (_lock)
            {
                ulong total = 0, used = 0;
                foreach (var q in Quotas)
                {
                    total += q.TotalCapacityKB;
                    used += q.UsedCapacityKB;
                }
                return (total, used);
            }
        }

        public static void Clear()
        {
            lock (_lock)
            {
                Quotas.Clear();
            }
        }

        public static void InitializeFromDatabase(List<CloudStorageInfo> storages)
        {
            lock (_lock)
            {
                Quotas.Clear();
                foreach (var s in storages)
                {
                    Quotas.Add(new CloudQuotaInfo
                    {
                        CloudStorageNum = s.CloudStorageNum,
                        AccountId = s.AccountId,
                        CloudType = s.CloudType,
                        TotalCapacityKB = s.TotalCapacity,
                        UsedCapacityKB = s.UsedCapacity
                    });
                }
                Console.WriteLine($"✅ StorageSessionManager 초기화 완료: {Quotas.Count}개 로드됨");
            }
        }
    }



}