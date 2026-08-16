using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using OverCloud.Services;
using overcloud.CloudApi;

namespace overcloud.transfer_manager
{
    // Phase 4 — 스토리지 삭제(재분배) 오케스트레이션. 서버는 계획/기록만 담당하고(select-storage/confirm-upload와
    // 동일한 계약), 실제 다운로드(옛 클라우드)→업로드(새 클라우드)는 여기서 CloudApi 토큰 클라이언트로 직접
    // 수행한다(5.1 — 서버는 바이트를 중계하지 않는다).
    public static class StorageRedistributionManager
    {
        // oldCloudType: 삭제 대상 스토리지의 CloudType. 호출부가 이미 CloudStorageInfo로 들고 있어서
        // (예: DeleteAccountWindow의 선택된 계정) plan 응답엔 중복으로 안 내려받는다.
        public static async Task<(bool Success, string Message)> DeleteStorageAsync(int cloudStorageNum, string ownerId, string oldCloudType)
        {
            var plan = await OverCloudApiClient.GetRedistributionPlanAsync(cloudStorageNum, ownerId);
            if (plan == null)
                return (false, "재분배 계획을 가져오지 못했습니다.");

            // 분산 파일 조각(RootFileId가 있는 행)도 서버 plan에서 일반 파일과 동일하게 취급되므로
            // 여기서 따로 걸러내지 않는다 — RelocateFileAsync가 조각 하나를 통째 파일처럼 옮긴다.
            string oldProvider = ProviderOf(oldCloudType);
            if (plan.Files.Count > 0 && oldProvider == null)
                return (false, $"지원되지 않는 클라우드: {oldCloudType}");

            var failedFiles = new List<string>();

            foreach (var file in plan.Files)
            {
                bool moved = await RelocateFileAsync(cloudStorageNum, ownerId, oldProvider, file);
                if (!moved)
                    failedFiles.Add(file.FileName);
            }

            if (failedFiles.Count > 0)
                return (false, $"{failedFiles.Count}개 파일 재분배에 실패했습니다({string.Join(", ", failedFiles)}). 다시 시도해주세요.");

            return await OverCloudApiClient.DeleteStorageAsync(cloudStorageNum, ownerId);
        }

        // 옛 클라우드에서 다운로드 → 후보 스토리지를 순서대로 시도(첫 성공에서 종료) → relocate-confirm →
        // 옛 클라우드에서 실제 삭제(베스트 에포트). 후보 하나가 실패하면 서버 재호출 없이 다음 후보로 넘어간다.
        private static async Task<bool> RelocateFileAsync(int oldCloudStorageNum, string ownerId, string oldProvider, RedistributionFilePlan file)
        {
            var oldAccessToken = await OverCloudApiClient.GetOAuthAccessTokenAsync(oldProvider, ownerId, oldCloudStorageNum);
            if (string.IsNullOrEmpty(oldAccessToken))
            {
                Console.WriteLine($"❌ [{file.FileName}] 옛 클라우드 access token 조회 실패");
                return false;
            }

            string tempPath = Path.Combine(Path.GetTempPath(), $"redistribute_{Guid.NewGuid()}_{file.FileName}");
            bool downloaded = oldProvider == "google"
                ? await GoogleDriveTokenClient.DownloadAsync(oldAccessToken, file.CloudFileId, tempPath)
                : await OneDriveTokenClient.DownloadAsync(oldAccessToken, file.CloudFileId, tempPath);

            if (!downloaded)
            {
                Console.WriteLine($"❌ [{file.FileName}] 옛 클라우드에서 다운로드 실패");
                return false;
            }

            try
            {
                foreach (var candidate in file.Candidates)
                {
                    string newProvider = ProviderOf(candidate.CloudType);
                    if (newProvider == null)
                        continue;

                    var newAccessToken = await OverCloudApiClient.GetOAuthAccessTokenAsync(newProvider, ownerId, candidate.CloudStorageNum);
                    if (string.IsNullOrEmpty(newAccessToken))
                        continue;

                    string newCloudFileId = newProvider == "google"
                        ? await GoogleDriveTokenClient.UploadAsync(newAccessToken, tempPath)
                        : await OneDriveTokenClient.UploadAsync(newAccessToken, tempPath);

                    if (string.IsNullOrEmpty(newCloudFileId))
                    {
                        Console.WriteLine($"❌ [{file.FileName}] {candidate.CloudType} 업로드 실패, 다음 후보 시도");
                        continue;
                    }

                    bool confirmed = await OverCloudApiClient.RelocateConfirmAsync(file.FileId, candidate.CloudStorageNum, newCloudFileId);
                    if (!confirmed)
                    {
                        Console.WriteLine($"❌ [{file.FileName}] relocate-confirm 실패 — 새 클라우드엔 올라갔지만 서버 기록 실패, 업로드분 정리 시도");
                        _ = newProvider == "google"
                            ? GoogleDriveTokenClient.DeleteAsync(newAccessToken, newCloudFileId)
                            : OneDriveTokenClient.DeleteAsync(newAccessToken, newCloudFileId);
                        continue;
                    }

                    // 옛 클라우드에서 실제 삭제(베스트 에포트) — 실패해도 메타데이터는 이미 새 위치로
                    // 옮겨졌으므로 재분배 자체는 성공으로 친다. 다만 옛 클라우드 용량이 정리 안 될 수 있음.
                    bool oldDeleted = oldProvider == "google"
                        ? await GoogleDriveTokenClient.DeleteAsync(oldAccessToken, file.CloudFileId)
                        : await OneDriveTokenClient.DeleteAsync(oldAccessToken, file.CloudFileId);
                    if (!oldDeleted)
                        Console.WriteLine($"⚠️ [{file.FileName}] 옛 클라우드에서 삭제 실패 — 메타데이터는 이미 새 위치로 이전됨, 수동 정리 필요할 수 있음");

                    return true;
                }

                Console.WriteLine($"❌ [{file.FileName}] 모든 후보 스토리지 업로드 실패");
                return false;
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
        }

        private static string ProviderOf(string cloudType) => cloudType switch
        {
            "GoogleDrive" => "google",
            "OneDrive" => "onedrive",
            _ => null
        };
    }
}
