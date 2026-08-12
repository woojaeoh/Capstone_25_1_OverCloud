using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DB.overcloud.Models;
using DB.overcloud.Repository;
using DB.overcloud.Models;
using OverCloud.Services.FileManager.DriveManager;
using OverCloud.Services.StorageManager;

namespace OverCloud.Services.FileManager
{
    public class FileDeleteManager
    {
        private readonly IAccountRepository accountRepository;
        private readonly QuotaManager quotaManager;
        private readonly IStorageRepository storageRepository;
        private readonly IFileRepository fileRepository;
        private readonly List<ICloudFileService> cloudServices;

       // private readonly Dictionary<string, ICloudFileService> serviceMap;


        public FileDeleteManager(
            IAccountRepository accountRepository,
            QuotaManager quotaManager,
            IStorageRepository storageRepository,
            IFileRepository fileRepository,
            List<ICloudFileService> cloudServices)
        {
            this.accountRepository = accountRepository;
            this.quotaManager = quotaManager;
            this.storageRepository = storageRepository;
            this.fileRepository = fileRepository;
            this.cloudServices = cloudServices;
        }

        // 파일 ID를 기반으로 삭제
        public async Task<bool> Delete_File(int storageNum, int fileId,string userId)
        {
            var file = fileRepository.GetFileById(fileId);
            if (file == null)
            {
                Console.WriteLine("❌ 삭제할 파일이 존재하지 않습니다.");
                return false;
            }


            if (!file.IsFolder)
            {

                var cloudInfo = storageRepository.GetCloud(file.CloudStorageNum, userId);
                    //.GetAllAccounts(userId)
                    //.FirstOrDefault(c => c.CloudStorageNum == file.CloudStorageNum);

                string cloudType = cloudInfo.CloudType;
                var service = cloudServices.FirstOrDefault(s => s.GetType().Name.Contains(cloudType));
                if (service == null)
                {
                    Console.WriteLine($"❌ 지원되지 않는 클라우드: {cloudType}");
                    return false;
                }


                bool apiDeleted = await service.DeleteFileAsync(storageNum, file.CloudFileId, userId);
                if (!apiDeleted)
                {
                    Console.WriteLine("❌ 클라우드 API에서 파일 삭제 실패");
                    return false;
                }
                //파일 DB에서 삭제
                bool dbDeleted = fileRepository.DeleteFile(fileId);


                if (dbDeleted)
                {
                    quotaManager.UpdateQuotaAfterUploadOrDelete(cloudInfo.CloudStorageNum, (ulong)((file.FileSize)/1024), false, userId);
                    await quotaManager.SaveDriveQuotaToDB(userId, file.CloudStorageNum);
                }


                return dbDeleted;
             }

            else
            {
                var fileInfo = storageRepository.GetCloud(file.CloudStorageNum,userId);
                
                bool dbDeleted = fileRepository.DeleteFile(fileId);

                await quotaManager.SaveDriveQuotaToDB(userId, file.CloudStorageNum);

                return dbDeleted;               
            }


        }


        public async Task<bool> Delete_DistributedFile(int logicalFileId, string userId)
        {
            var logicalFile = fileRepository.GetFileById(logicalFileId);
            if (logicalFile == null || !logicalFile.IsDistributed)
            {
                Console.WriteLine("❌ 유효한 논리 파일이 아닙니다.");
                return false;
            }

            // 1. 조각 파일들 가져오기
            var chunks = fileRepository.GetChunksByRootFileId(logicalFileId);
            if (chunks == null || chunks.Count == 0)
            {
                Console.WriteLine("❌ 조각 파일이 존재하지 않습니다.");
                return false;
            }

            bool allSuccess = true;

            foreach (var chunk in chunks)
            {
                var cloudInfo = storageRepository.GetCloud(chunk.CloudStorageNum, userId);
                    //.GetAllAccounts(userId)
                    //.FirstOrDefault(c => c.CloudStorageNum == chunk.CloudStorageNum);

                if (cloudInfo == null)
                {
                    Console.WriteLine($"❌ 클라우드 정보 없음: {chunk.CloudStorageNum}");
                    allSuccess = false;
                    return allSuccess;
                }

                var service = cloudServices.FirstOrDefault(s => s.GetType().Name.Contains(cloudInfo.CloudType));
                if (service == null)
                {
                    Console.WriteLine($"❌ 클라우드 서비스 없음: {cloudInfo.CloudType}");
                    allSuccess = false;
                    continue;
                }

                // 2. 클라우드 API로 삭제
                bool apiDeleted = await service.DeleteFileAsync(cloudInfo.CloudStorageNum, chunk.CloudFileId, userId);
                if (!apiDeleted)
                {
                    Console.WriteLine($"❌ 조각 삭제 실패: {chunk.FileName}");
                    allSuccess = false;
                    return allSuccess;
                }

                // 3. DB에서 삭제
                bool dbDeleted = fileRepository.DeleteFile(chunk.FileId);
                if (!dbDeleted)
                {
                    Console.WriteLine($"❌ DB 삭제 실패: {chunk.FileName}");
                    allSuccess = false;
                }

                //분산 파일 삭제할때도 용량 호출
                await quotaManager.SaveDriveQuotaToDB(userId, chunk.CloudStorageNum);
                
                // 4. 용량 회복
                quotaManager.UpdateQuotaAfterUploadOrDelete(chunk.CloudStorageNum, chunk.FileSize / 1024 , false, userId);
            }

            // 5. 논리 파일 메타데이터 삭제
            bool logicalDeleted = fileRepository.DeleteFile(logicalFileId);
            if (!logicalDeleted)
            {
                Console.WriteLine("❌ 논리 파일 삭제 실패");
                allSuccess = false;
            }

            return allSuccess;
        }


        //// (선택) 폴더 전체 삭제 등 확장 가능
        //public bool DeleteFolderRecursively(int folderId)
        //{
        //    // 하위 파일, 폴더 탐색 및 재귀적 삭제 로직 추가 가능
        //    // (이건 추후 구현)
        //    return false;
        //}
    }


}