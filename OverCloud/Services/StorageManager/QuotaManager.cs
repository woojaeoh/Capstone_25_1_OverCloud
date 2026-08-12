using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using DB.overcloud.Models;
using DB.overcloud.Repository;
using OverCloud.Services.FileManager.DriveManager;

namespace OverCloud.Services.StorageManager
{
    public class QuotaManager
    {

        private readonly IEnumerable<ICloudFileService> cloudServices;
        // private readonly DropboxService dropboxService;
        //private readonly OneDriveService oneDriveService;
        private readonly IAccountRepository accountRepository;
        private readonly IStorageRepository storageRepository;
        private readonly IFileRepository fileRepository;
        private readonly CloudTierManager cloudTierManager;

        public QuotaManager(IEnumerable<ICloudFileService> cloudServices, IStorageRepository storageRepo, IAccountRepository accountRepo, IFileRepository fileRepository, CloudTierManager cloudTierManager)
        {
            storageRepository = storageRepo;
            accountRepository = accountRepo;
            this.fileRepository = fileRepository;
            this.cloudServices = cloudServices;
            this.cloudTierManager = cloudTierManager;
        }

        //계정에 있는 모든 스토리지의 용량 업데이트

        public bool UpdateAggregatedStorageForUser(string userId) //여기서 넘기는 userId는 overcloud계정의 id임요.
        {
            // 1. 해당 계정이 가진 모든 클라우드 가져오기
            var cloudList = accountRepository.GetAllAccounts(userId);
            if (cloudList == null || cloudList.Count == 0)
                return false;

            //            int userNum = cloudList.First().UserNum;

            // 2. 합산
            ulong totalSize = cloudList.Aggregate(0UL, (acc, c) => acc + c.TotalCapacity);
            ulong usedSize = cloudList.Aggregate(0UL, (acc, c) => acc + c.UsedCapacity);


            // 3. DB 업데이트
            return accountRepository.UpdateAccountUsage(userId, totalSize, usedSize);
        }





        // 계정에 있는 특정 클라우드 하나만 용량 업데이트 (일단은 구글 드라이브 한정 DB에 업데이트)
        public async Task<bool> SaveDriveQuotaToDB(string userId, int CloudStorageNum) //오버클라우드 userid를 넘겨줌.
        {

            // 1. userEmail에 맞는 클라우드 타입 찾기
            var cloudInfo = storageRepository.GetCloud(CloudStorageNum, userId);
            if (cloudInfo == null)
            {
                Console.WriteLine("❌ 해당 이메일에 맞는 클라우드 정보를 찾을 수 없습니다.");
                return false;
            }

            string cloudType = cloudInfo.CloudType;


            // 2. 클라우드 타입에 맞는 서비스 찾기
            var service = cloudServices.FirstOrDefault(s =>
                s.GetType().Name.StartsWith(cloudType)); // "GoogleDriveService", "DropboxService" 같은 이름 비교

            if (service == null)
            {
                Console.WriteLine("❌ 해당 클라우드에 맞는 서비스 구현체를 찾을 수 없습니다.");
                return false;
            }

            // 3. 해당 클라우드에 API 호출
            var (total, used) = await service.GetDriveQuotaAsync(CloudStorageNum, userId);


            // 5. TotalCapacity, UsedCapacity만 업데이트 (KB단위)
            cloudInfo.TotalCapacity = (total / 1024);
            cloudInfo.UsedCapacity = (used / 1024);

            // 💡 메모리 세션도 갱신
            StorageSessionManager.SetQuota(
                cloudStorageNum: cloudInfo.CloudStorageNum,
                accountId: cloudInfo.AccountId,
                cloudType: cloudInfo.CloudType,
                totalKB: cloudInfo.TotalCapacity,
                usedKB: cloudInfo.UsedCapacity
            );

            // 3. 저장
            return storageRepository.account_save(cloudInfo);
        }



        // [Lost Update 방지] 업로드/삭제 시 용량 갱신
        // 기존 방식: 메모리에서 절댓값 읽기 → 수정 → DB에 덮어쓰기
        //   → 동시 업로드 시 두 스레드가 같은 초기값을 읽어 한쪽 변경이 소실됨
        // 변경 방식: delta를 메모리/DB에 직접 누적
        //   메모리: lock으로 직렬화된 AdjustUsedCapacity
        //   DB:     used_capacity = used_capacity + @delta (원자적 연산)
        public void UpdateQuotaAfterUploadOrDelete(int cloudStorageNum, ulong fileSizeKB, bool isUpload, string userId)
        {
            long deltaKB = isUpload ? (long)fileSizeKB : -(long)fileSizeKB;

            StorageSessionManager.AdjustUsedCapacity(cloudStorageNum, deltaKB);

            bool dbResult = storageRepository.IncrementUsedCapacity(cloudStorageNum, userId, deltaKB);
            Console.WriteLine(dbResult ? "✅ DB 저장 성공" : "❌ DB 저장 실패");
        }


        public ulong GetTotalRemainingQuotaInBytes_Delete_Account(string userId, int cloudStroageNum)
        {
            var clouds = accountRepository.GetAllAccounts(userId);
            if (clouds == null || clouds.Count == 0)
                return 0;

            ulong totalAvailableBytes = 0;

            foreach (var cloud in clouds)
            {
                ulong remainingKB = cloud.TotalCapacity - cloud.UsedCapacity;
                totalAvailableBytes += remainingKB * 1024; // KB → byte
            }

            var delete_cloud = storageRepository.GetCloud(cloudStroageNum, userId);
            ulong remainingKB_Delete_Cloud = delete_cloud.TotalCapacity - delete_cloud.UsedCapacity;
            totalAvailableBytes -= remainingKB_Delete_Cloud * 1024;

            return totalAvailableBytes;
        }

        public ulong AllFilelistSize(int CloudStorageNum)
        {
            var files = fileRepository.GetFilesByStorageNum(CloudStorageNum);

            ulong totalSize = 0;

            foreach (var file in files)
            {
                totalSize += (ulong)file.FileSize;  // file.Size를 ulong으로 캐스팅
            }

            return totalSize;
        }



        public async Task<bool> AccountFile_Redistribution(int cloudStorageNum, string userId)
        {
            // 1. 삭제될 계정의 파일 목록 조회
            var filesToRedistribute = fileRepository.GetFilesByStorageNum(cloudStorageNum);
            if (filesToRedistribute == null || filesToRedistribute.Count == 0)
            {
                Console.WriteLine("재분배할 파일이 없습니다.");
                return true; // 아무 파일도 없으므로 성공 처리
            }

            foreach (var file in filesToRedistribute)
            {
                try
                {
                    //db에서 다른건 똑같이하고 fileId랑 cloudstroagenum,cloudfile_id만 바꾸고 ,

                    if (file.IsDistributed)
                    {
                        bool success = await RedistributeDistributedFile(file.FileId, file.ParentFolderId, file.ID);

                        if (!success)
                        {
                            Console.WriteLine("분산파일 재분배 실패");
                        }
                        continue;
                    }

                    var cloud = storageRepository.GetCloud(cloudStorageNum, userId);
                    var sourceService = cloudServices.FirstOrDefault(s => s.GetType().Name.Contains(cloud.CloudType));
                    if (sourceService == null)
                    {
                        Console.WriteLine($"❌ 클라우드 서비스 없음 (cloudType: {cloud.CloudType})");
                        continue;
                    }

                    string tempPath = Path.GetTempFileName();
                    bool downloaded = await sourceService.DownloadFileAsync(file.CloudStorageNum, file.CloudFileId, tempPath, userId);
                    if (!downloaded)
                    {
                        Console.WriteLine($"❌ 파일 다운로드 실패: {file.FileName}");
                        continue;
                    }

                    var candidateStorages = cloudTierManager.GetCandidateStorages(file.FileSize / 1024, userId, cloudStorageNum);
                    if (candidateStorages == null)
                    {
                        Console.WriteLine($"❌ 적절한 스토리지가 없음. 파일: {file.FileName}");
                        File.Delete(tempPath);
                        continue;
                    }

                    bool uploadSuccess = false;

                    foreach (var bestStorage in candidateStorages)
                    {
                        var targetService = cloudServices.FirstOrDefault(s => s.GetType().Name.Contains(bestStorage.CloudType));
                        if (targetService == null)
                        {
                            Console.WriteLine($"❌ 대상 클라우드 서비스 없음: {bestStorage.CloudType}");
                            continue;
                        }

                        string newCloudFileId = await targetService.UploadFileAsync(bestStorage, tempPath, userId);
                        if (!string.IsNullOrEmpty(newCloudFileId))
                        {
                            // 업로드 성공 처리
                            file.CloudStorageNum = bestStorage.CloudStorageNum;
                            file.CloudFileId = newCloudFileId;
                            fileRepository.updateFile(file);
                            UpdateQuotaAfterUploadOrDelete(bestStorage.CloudStorageNum, file.FileSize / 1024, true, userId);
                            uploadSuccess = true;
                            Console.WriteLine($"✅ 파일 재분배 성공: {file.FileName} -> {bestStorage.CloudType}");
                            await SaveDriveQuotaToDB(userId, file.CloudStorageNum);
                            break; // 반복문 종료 (업로드 성공)
                        }

                        Console.WriteLine($"❌ 업로드 실패: {file.FileName} 대상: {bestStorage.CloudType}");
                    }

                    File.Delete(tempPath); // 임시 파일 삭제

                    if (!uploadSuccess)
                    {
                        Console.WriteLine($"❌ 모든 스토리지 업로드 실패: {file.FileName}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ 예외 발생: {ex.Message}");
                }
            }

            // 7. 모든 파일 재분배 후 집계 갱신
            UpdateAggregatedStorageForUser(userId);
            return true;
        }



        //분산 파일 재분배
        public async Task<bool> RedistributeDistributedFile(int rootFileId, int parentFolderId, string userId)
        {
            // 1. 분산 파일 조각 리스트 조회
            var chunks = fileRepository.GetChunksByRootFileId(rootFileId);
            if (chunks == null || chunks.Count == 0)
            {
                Console.WriteLine($"❌ 분산 파일 조각 없음 (rootFileId: {rootFileId})");
                return false;
            }

            // 2. 임시 병합 파일 경로 생성
            string tempMergePath = Path.Combine(Path.GetTempPath(), $"redistribute_merged_{Guid.NewGuid()}.tmp");

            try
            {
                using (var mergedStream = new FileStream(tempMergePath, FileMode.Create, FileAccess.Write))
                {
                    foreach (var chunk in chunks.OrderBy(c => c.ChunkIndex))
                    {
                        var cloud = storageRepository.GetCloud(chunk.CloudStorageNum,userId);
                        var service = cloudServices.FirstOrDefault(s => s.GetType().Name.Contains(cloud.CloudType));
                        if (service == null)
                        {
                            Console.WriteLine($"❌ 클라우드 서비스 없음 (cloudType: {cloud.CloudType})");
                            return false;
                        }

                        string tempChunkPath = Path.GetTempFileName();
                        bool downloaded = await service.DownloadFileAsync(chunk.CloudStorageNum, chunk.CloudFileId, tempChunkPath, userId);
                        if (!downloaded)
                        {
                            Console.WriteLine($"❌ 다운로드 실패: {chunk.FileName}");
                            return false;
                        }

                        byte[] buffer = await File.ReadAllBytesAsync(tempChunkPath);
                        await mergedStream.WriteAsync(buffer, 0, buffer.Length);
                        File.Delete(tempChunkPath);
                    }
                }

                // 3. 병합된 파일 분산 업로드 로직 직접 포함
                var fileInfo = new FileInfo(tempMergePath);
                ulong fileSizeKB = (ulong)fileInfo.Length/ 1024;
                string fileName = fileInfo.Name;

                var onefile = fileRepository.GetFileById(rootFileId);

                var candidateStorages = cloudTierManager.GetCandidateStorages(fileSizeKB , userId, onefile.CloudStorageNum);
                if (candidateStorages == null || candidateStorages.Count == 0)
                {
                    Console.WriteLine("❌ 전체 저장소 용량 부족");
                    File.Delete(tempMergePath);
                    return false;
                }

              //  List<CloudStorageInfo> select = storagePlan;

                // 논리 파일 등록
                CloudFileInfo logical = new CloudFileInfo
                {
                    FileName = fileName,
                    FileSize = fileSizeKB,
                    UploadedAt = DateTime.Now,
                    ParentFolderId = parentFolderId,
                    IsFolder = false,
                    IsDistributed = true,
                    CloudStorageNum = -1,
                    ID = userId
                };
                int logicalFileId = fileRepository.AddFileAndReturnId(logical);

                using FileStream source = new FileStream(tempMergePath, FileMode.Open, FileAccess.Read);
                ulong remainingBytes = (ulong)fileInfo.Length;
                int chunkIndex = 0;

                List<CloudFileInfo> uploadedChunks = new();

                foreach (var cloud in candidateStorages)
                {
                    ulong availableBytes = (ulong)(cloud.TotalCapacity - cloud.UsedCapacity) * 1024;
                    if (availableBytes == 0) continue;

                    ulong chunkSize = Math.Min(availableBytes, remainingBytes);
                    byte[] buffer = new byte[chunkSize];
                    int read = await source.ReadAsync(buffer, 0, buffer.Length);
                    if (read == 0) break;

                    var targetService = cloudServices.FirstOrDefault(s => s.GetType().Name.Contains(cloud.CloudType));
                    if (targetService == null)
                    {
                        Console.WriteLine($"❌ 대상 클라우드 서비스 없음: {cloud.CloudType}");
                        File.Delete(tempMergePath);
                        return false;
                    }

                    string tempFile = Path.GetTempFileName();
                    await File.WriteAllBytesAsync(tempFile, buffer);
                    string cloudFileId = await targetService.UploadFileAsync(cloud, tempFile, userId);
                    File.Delete(tempFile);

                    CloudFileInfo chunk = new CloudFileInfo
                    {
                        FileName = $"{fileName}.part{chunkIndex}",
                        FileSize = (ulong)(read / 1024),
                        UploadedAt = DateTime.Now,
                        CloudStorageNum = cloud.CloudStorageNum,
                        ParentFolderId = -2,
                        IsFolder = false,
                        CloudFileId = cloudFileId,
                        RootFileId = logicalFileId,
                        ChunkIndex = chunkIndex,
                        ChunkSize = (ulong)read,
                        ID = userId
                    };

                    fileRepository.addfile(chunk);
                    UpdateQuotaAfterUploadOrDelete(cloud.CloudStorageNum, chunk.FileSize, true,userId);
                    uploadedChunks.Add(chunk);

                    chunkIndex++;
                    remainingBytes -= (ulong)read;
                    if (remainingBytes == 0) break;
                }

                source.Close();
                File.Delete(tempMergePath);

                if (remainingBytes == 0)
                {
                    Console.WriteLine("✅ 분산 파일 재분배 성공 (rootFileId: {0})", rootFileId);
                    return true;
                }
                else
                {
                    Console.WriteLine("❌ 일부 조각 업로드 실패 - 롤백 필요 (rootFileId: {0})", rootFileId);
                    // TODO: uploadedChunks 순회하며 삭제 구현 가능
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ 예외 발생: {ex.Message}");
                if (File.Exists(tempMergePath)) File.Delete(tempMergePath);
                return false;
            }
        }


    }
}