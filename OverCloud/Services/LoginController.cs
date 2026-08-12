using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using DB.overcloud.Models;
using DB.overcloud.Repository;
using OverCloud.Services.FileManager;
using OverCloud.Services.FileManager.DriveManager;
using OverCloud.Services.StorageManager;

namespace OverCloud.Services
{
    public class LoginController
    {
        public AccountService AccountService { get; }
        public FileUploadManager FileUploadManager { get; }
        public FileDownloadManager FileDownloadManager { get; }
        public FileDeleteManager FileDeleteManager { get; }
        public FileCopyManager FileCopyManager { get; }
        public QuotaManager QuotaManager { get; }
        public IFileRepository FileRepository { get; }
        public CloudTierManager CloudTierManager { get; }
        //public TransferManager TransferManager { get; }

        public AccountRepository AccountRepository { get; }
        public CoopUserRepository CoopUserRepository { get; }
        public CooperationManager CooperationManager { get; }

        public IFileIssueCommentRepository FileIssueCommentRepository { get; }

        public IFileIssueRepository FileIssueRepository { get; }

        public IFileIssueMappingRepository FileIssueMappingRepository { get; }

        public LanTransferService LanTransferService { get; }

        public string user_id;

        public LoginController(string connectionString) {
            var connStr = connectionString;
            var storageRepo = new StorageRepository(connStr);

            AccountRepository = new AccountRepository(connStr);
            FileRepository = new FileRepository(connStr);
            CoopUserRepository = new CoopUserRepository(connStr);
            CooperationManager = new CooperationManager(CoopUserRepository);

            var tokenFactory = new TokenProviderFactory();

            // 이 부분은 CloudStorageNum 구분된 인스턴스 방식으로 확장 가능
            var googleSvc = new GoogleDriveService(tokenFactory.CreateGoogleTokenProvider(), storageRepo, AccountRepository);
            var oneDriveSvc = new OneDriveService(tokenFactory.CreateOneDriveTokenRefresher(), storageRepo, AccountRepository);
            var dropboxSvc = new DropboxService(tokenFactory.CreateDropboxTokenRefresher(), storageRepo, AccountRepository);
            var cloudSvcs = new List<ICloudFileService> { googleSvc, oneDriveSvc, dropboxSvc };

            CloudTierManager = new CloudTierManager(AccountRepository);
            QuotaManager = new QuotaManager(cloudSvcs, storageRepo, AccountRepository,FileRepository,CloudTierManager);
            AccountService = new AccountService(AccountRepository, storageRepo, QuotaManager);
            FileUploadManager = new FileUploadManager(AccountService, QuotaManager, storageRepo, FileRepository, cloudSvcs, CloudTierManager);
            FileDownloadManager = new FileDownloadManager(FileRepository, AccountRepository, cloudSvcs, storageRepo);
            FileDeleteManager = new FileDeleteManager(AccountRepository, QuotaManager, storageRepo, FileRepository, cloudSvcs);
            FileCopyManager = new FileCopyManager(FileRepository, CloudTierManager, cloudSvcs, QuotaManager, AccountRepository, FileUploadManager, storageRepo);

            //TransferManager = new TransferManager(FileUploadManager, FileDownloadManager, CloudTierManager);

            FileIssueCommentRepository = new FileIssueCommentRepository(connStr);
            FileIssueRepository = new FileIssueRepository(connStr);
            FileIssueMappingRepository = new FileIssueMappingRepository(connStr);

            LanTransferService = new LanTransferService(AccountRepository);
        }

    }

}