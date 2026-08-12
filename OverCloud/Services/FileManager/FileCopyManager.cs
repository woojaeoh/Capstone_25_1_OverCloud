using System;
using DB.overcloud.Models;
using DB.overcloud.Repository;
using System.Threading.Tasks;
using OverCloud.Services.StorageManager;
using System.IO;
using OverCloud.Services.FileManager.DriveManager;

namespace OverCloud.Services.FileManager
{
    public class FileCopyManager
    {
        private readonly IFileRepository fileRepository;
        private readonly CloudTierManager cloudTierManager;
        private readonly List<ICloudFileService> cloudServices;
        private readonly QuotaManager quotaManager;
        private readonly IAccountRepository accountRepository;
        private readonly FileUploadManager fileUploadManager;
        private readonly IStorageRepository storageRepository;

        public FileCopyManager(
            IFileRepository fileRepository,
            CloudTierManager cloudTierManager,
            List<ICloudFileService> cloudServices,
            QuotaManager quotaManager,
            IAccountRepository accountRepository,
            FileUploadManager fileUploadManager,
            IStorageRepository storageRepository
            )
        {
            this.fileRepository = fileRepository;
            this.cloudTierManager = cloudTierManager;
            this.cloudServices = cloudServices;
            this.quotaManager = quotaManager;
            this.accountRepository = accountRepository;
            this.fileUploadManager = fileUploadManager;
            this.storageRepository = storageRepository;
        }

        public async Task<bool> Copy_File(int copy_target_file_id, int target_parent_file_id, string userId)
        {
            // 1. 복사할 파일 정보 조회
            var originalFile = fileRepository.GetFileById(copy_target_file_id);
            if (originalFile == null)
            {
                Console.WriteLine(" 복사할 원본 파일을 찾을 수 없습니다.");
                return false;
            }

            if (originalFile.IsDistributed)
            {
                bool result = await Copy_DistributedFile(copy_target_file_id, target_parent_file_id, userId);
                return result;
            }

            // 1.원본 파일이 있는 클라우드 정보
            var sourceCloud = storageRepository.GetCloud(originalFile.CloudStorageNum,userId);
            //var sourceCloud = allAccounts.FirstOrDefault(c => c.CloudStorageNum == originalFile.CloudStorageNum);
            if (sourceCloud == null)
            {
                Console.WriteLine("원본 클라우드 계정 없음");
                return false;
            }

            var sourceService = cloudServices.FirstOrDefault(s => s.GetType().Name.Contains(sourceCloud.CloudType));
            if (sourceService == null)
            {
                Console.WriteLine("원본 클라우드 서비스 없음");
                return false;
            }

                  // 업로드용 임시 파일 생성
            string tempPath = Path.GetTempFileName(); //임의 경로 지정
            bool downloaded = await sourceService.DownloadFileAsync(originalFile.CloudStorageNum, originalFile.CloudFileId, tempPath, userId);
            //임의 경로로 다운로드 
            if (!downloaded)
            {
                Console.WriteLine("다운로드 실패");
                File.Delete(tempPath);
                return false;
            }

            // 1. 업로드 가능한 스토리지 선택
            var bestStorage = cloudTierManager.SelectBestStorage(originalFile.FileSize / 1024, userId); //ulong 자료형의 KB단위로 건네줘야함.

            if (bestStorage == null)
            {
                Console.WriteLine(" 복사할 저장공간이 부족합니다.");
                File.Delete(tempPath);
                return false;
            }

            var targetService = cloudServices.FirstOrDefault(s => s.GetType().Name.Contains(bestStorage.CloudType));
            if (targetService == null)
            {
                Console.WriteLine(" 대상 클라우드 서비스 없음");
                File.Delete(tempPath);
                return false;
            }

                // ✅ 4. 재업로드
            string newCloudFileId = await targetService.UploadFileAsync(bestStorage , tempPath, userId);
            if (string.IsNullOrEmpty(newCloudFileId))
            {
                Console.WriteLine(" 업로드 실패");
                File.Delete(tempPath);
                return false;
            }


            // 4. 복사할 파일을 이동 할 스토리지로 새롭게 데이터 생성.
            var fileInfo = new FileInfo(tempPath);
            ulong fileSize = (ulong)fileInfo.Length;

            CloudFileInfo copiedFile = new CloudFileInfo
            {
                FileName = originalFile.FileName,
                FileSize = fileSize / 1024,
                UploadedAt = DateTime.Now,
                CloudStorageNum = bestStorage.CloudStorageNum,
                ParentFolderId = target_parent_file_id, // 최상위 업로드라면 -1 ,일단은 파일만처리, 나중에는 폴더까지 
                IsFolder = false,
                CloudFileId = newCloudFileId,
                ID =userId
            };

            // 5. DB 저장
            fileRepository.addfile(copiedFile);

            // 6. 업로드 후 용량 갱신
            quotaManager.UpdateQuotaAfterUploadOrDelete(bestStorage.CloudStorageNum, fileSize / 1024, true, userId);


            //임시 파일 삭제
            File.Delete(tempPath);

            Console.WriteLine("파일 복사 완료! good");
            return true;
        }

        //분산 파일인 경우 복사.
        public async Task<bool> Copy_DistributedFile(int rootFileId, int targetParentFolderId, string userId)
        {
            // 1. 조각 리스트 조회
            var chunks = fileRepository.GetChunksByRootFileId(rootFileId);
            if (chunks == null || chunks.Count == 0)
            {
                Console.WriteLine("❌ 분산 파일 조각을 찾을 수 없습니다.");
                return false;
            }

            // 2. 조각들을 순서대로 다운로드하여 병합
            string tempMergePath = Path.Combine(Path.GetTempPath(), $"copy_merged_{Guid.NewGuid()}.tmp");
            using var mergedStream = new FileStream(tempMergePath, FileMode.Create, FileAccess.Write);

            foreach (var chunk in chunks.OrderBy(c => c.ChunkIndex))
            {
                var cloud = storageRepository.GetCloud(chunk.CloudStorageNum, userId);
            //        .FirstOrDefault(c => c.CloudStorageNum == chunk.CloudStorageNum);
                if (cloud == null)
                {
                    Console.WriteLine("클라우드 정보 없음");
                    return false;
                }

                var service = cloudServices.FirstOrDefault(s => s.GetType().Name.Contains(cloud.CloudType));
                if (service == null)
                {
                    Console.WriteLine("❌ 클라우드 서비스 없음");
                    return false;
                }

                string tempChunkPath = Path.GetTempFileName();

                bool downloaded = await service.DownloadFileAsync(chunk.CloudStorageNum, chunk.CloudFileId, tempChunkPath, userId);

                if (!downloaded)
                {
                    Console.WriteLine($"❌ 조각 다운로드 실패: part{chunk.ChunkIndex}");
                    return false;
                }

                byte[] buffer = await File.ReadAllBytesAsync(tempChunkPath);
                await mergedStream.WriteAsync(buffer, 0, buffer.Length);
                File.Delete(tempChunkPath);
            }

            mergedStream.Close();

            // 3. 병합된 파일을 새 경로로 분산 업로드
            var fileName = chunks.First().FileName.Split(".part")[0]; // 원래 파일 이름 복구
            string renamedPath = Path.Combine(Path.GetTempPath(), fileName);
            File.Move(tempMergePath, renamedPath, true);

            bool uploadResult = await fileUploadManager.Upload_Distributed(renamedPath, targetParentFolderId, userId);
            File.Delete(renamedPath);

            return uploadResult;
        }



    }
}
