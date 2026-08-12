using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DB.overcloud.Models;
using System.IO;
using DB.overcloud.Repository;
using OverCloud.Services.FileManager.DriveManager;
using OverCloud.Services.StorageManager;
using System.Diagnostics;



namespace OverCloud.Services.FileManager
{
    public class FileUploadManager
    {
        private readonly AccountService accountService;
        private readonly IFileRepository repo_file;
        private readonly CloudTierManager cloudTierManager;
        private readonly QuotaManager quotaManager;
        private readonly IStorageRepository storageRepository;
        private readonly List<ICloudFileService> cloudServices;

        public FileUploadManager(
            AccountService accountService,
            QuotaManager quotaManager,
            IStorageRepository storageRepository,
            IFileRepository repo_file,
            List<ICloudFileService> cloudServices,
            CloudTierManager cloudTierManager)
        {
            this.accountService = accountService;
            this.quotaManager = quotaManager; 
            this.storageRepository = storageRepository;
            this.repo_file = repo_file;
            this.cloudServices = cloudServices;
            this.cloudTierManager = cloudTierManager;
        }

        public async Task<bool> file_upload(string file_name, int target_parent_file_id, string userId)
        {
            Console.WriteLine($"file_upload: {userId}");
            ulong fileSize = (ulong)new FileInfo(file_name).Length;

            // 1. 업로드 가능한 스토리지 선택
            var bestStorage = cloudTierManager.SelectBestStorage(fileSize/ 1024, userId);

            string cloudType = bestStorage.CloudType;

            // 2. 클라우드 타입에 맞는 서비스 찾기
            var service = cloudServices.FirstOrDefault(s => s.GetType().Name.Contains(cloudType));
            if (service == null)
            {
                Console.WriteLine($"❌ 지원되지 않는 클라우드: {cloudType}");
                return false;
            }

            // 3. 파일 업로드
            var cloudFileId = await service.UploadFileAsync(bestStorage, file_name, userId);
            if (string.IsNullOrEmpty(cloudFileId)) return false;

            // 4. 파일 정보 저장
            var fileInfo = new FileInfo(file_name);
            CloudFileInfo file = new CloudFileInfo
            {
                FileName = fileInfo.Name,
                FileSize = (ulong)((fileInfo.Length)/1024),
                UploadedAt = DateTime.Now,
                CloudStorageNum = bestStorage.CloudStorageNum,
                ParentFolderId = target_parent_file_id, // 최상위 업로드라면 -1 ,일단은 파일만처리, 나중에는 폴더까지 
                IsFolder = false,
                CloudFileId = cloudFileId,
                ID = userId
            };

            // 5. DB 저장
            repo_file.addfile(file);


            // 6. 업로드 후 용량 갱신
            quotaManager.UpdateQuotaAfterUploadOrDelete(bestStorage.CloudStorageNum, (ulong)((fileInfo.Length)/1024), true, userId);

            return true;
        }

        public async Task<bool> Upload_Distributed(string file_name, int parentFolderId, string userId)
        {
            var fileInfo = new FileInfo(file_name);
            ulong fileSize = (ulong)fileInfo.Length;
            string fileName = fileInfo.Name;

            var storagePlan = cloudTierManager.GetStoragePlan(fileSize/1024, userId);
            if (storagePlan == null)
            {
                Console.WriteLine("❌ 전체 저장소 용량 부족");
                return false;
            }

            List<CloudStorageInfo> select = storagePlan;

            //리스트를 하나의 스토리지로 가져와야함.
          //  CloudStorageInfo select0 = select[0];


            //  논리 파일 먼저 등록
            CloudFileInfo logical = new CloudFileInfo
            {
                FileName = fileName,
                FileSize = (ulong)(fileInfo.Length / 1024),
                UploadedAt = DateTime.Now,
                ParentFolderId = parentFolderId,
                IsFolder = false,
                IsDistributed = true,
                CloudStorageNum= -1, //문제
                ID = userId 
            };

            int logicalFileId = repo_file.AddFileAndReturnId(logical);
            Console.WriteLine(logicalFileId);
            using FileStream source = new FileStream(file_name, FileMode.Open, FileAccess.Read);
            ulong remainingBytes = fileSize;
            int chunkIndex = 0;
            List<CloudFileInfo> uploadedChunks = new();

            foreach (var cloud in storagePlan)
            {
                ulong availableBytes = (ulong)(cloud.TotalCapacity - cloud.UsedCapacity) * 1024; //바이트로 비교
                if (availableBytes == 0) continue;

                ulong chunkSize = Math.Min(availableBytes, remainingBytes); //스토리지 남은 용량만큼 자르거나, 스토리지 공간에 들어갈 수 있는경우 파일크기로 사이즈 분할.
                byte[] buffer = new byte[chunkSize];
                int read = await source.ReadAsync(buffer, 0, buffer.Length);
                if (read == 0) break;

                Console.WriteLine($"[{userId}/{fileName}/chunk{chunkIndex}] 버퍼 할당 후 — 힙: {GC.GetTotalMemory(false) / 1024 / 1024}MB / 피크: {Process.GetCurrentProcess().PeakWorkingSet64 / 1024 / 1024}MB");

                // 서비스 찾기
                var service = cloudServices.FirstOrDefault(s => s.GetType().Name.Contains(cloud.CloudType));
                if (service == null)
                {
                    Console.WriteLine($"❌ 클라우드 서비스 없음: {cloud.CloudType}");
                    return false;
                }

                // 업로드용 임시 파일 생성
                string tempDir = Path.GetTempPath(); //임의 경로 지정 
                string distribute_fileName = $"{fileName}.part{chunkIndex}";
                string tempFile = Path.Combine(tempDir, distribute_fileName);

                await File.WriteAllBytesAsync(tempFile, buffer); //버퍼 크기만큼(분산저장 chunk크기만큼) 잘라서 파일 쓰기.
                string cloudFileId = await service.UploadFileAsync(cloud, tempFile, userId); // 잘린 파일 업로드
                File.Delete(tempFile); //업로드 후 로컬에서 파일 삭제.

                Console.WriteLine($"[{userId}/{fileName}/chunk{chunkIndex}] 업로드 완료 후 — 힙: {GC.GetTotalMemory(false) / 1024 / 1024}MB / 피크: {Process.GetCurrentProcess().PeakWorkingSet64 / 1024 / 1024}MB");


                // 조각 파일 등록
                CloudFileInfo chunk = new CloudFileInfo
                {
                    FileName = $"{fileName}.part{chunkIndex}",
                    FileSize = (ulong)(read / 1024), //kb단위
                    UploadedAt = DateTime.Now,
                    CloudStorageNum = cloud.CloudStorageNum,
                    ParentFolderId = -2,
                    IsFolder = false,
                    CloudFileId = cloudFileId,
                    RootFileId = logicalFileId,
                    ChunkIndex = chunkIndex,
                    ChunkSize = (ulong)read,
                    ID= userId
                };

                repo_file.addfile(chunk);
                quotaManager.UpdateQuotaAfterUploadOrDelete(cloud.CloudStorageNum, chunk.FileSize, true, userId);
                uploadedChunks.Add(chunk);

                chunkIndex++;
                remainingBytes -= (ulong)read;

                if (remainingBytes == 0) break;
            }

            source.Close();

            if (remainingBytes == 0)
            {
         
                Console.WriteLine("✅ 분산 저장 완료");
                return true;
            }
            else
            {
                Console.WriteLine("❌ 일부 조각 실패 - 롤백 필요");
                // TODO: uploadedChunks 순회하며 삭제
                return false;
            }
        }





    }
}