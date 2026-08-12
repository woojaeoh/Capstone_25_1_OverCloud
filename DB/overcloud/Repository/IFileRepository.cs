using DB.overcloud.Models;

namespace DB.overcloud.Repository
{
    public interface IFileRepository
    {
        List<CloudFileInfo> GetAllFileInfo(int fileId);
        bool addfile(CloudFileInfo file_info);
        bool DeleteFile(int fileId);
        CloudFileInfo GetFileById(int fileId);
        List<CloudFileInfo> all_file_list(int fileId, string user_id);
        List<CloudFileInfo> all_file_list_paged(int fileId, string user_id, int lastFileId, int pageSize);
        List<CloudFileInfo> all_file_list_full(int fileId, string user_id);
        CloudFileInfo specific_file_info(int fileId);
        List<CloudFileInfo> GetAllFileInfo(string file_direc);

        int add_folder(CloudFileInfo file_info);

        bool change_name(CloudFileInfo file_info);
        bool change_dir(CloudFileInfo file_info);

        int AddFileAndReturnId(CloudFileInfo file_info);
        public List<CloudFileInfo> GetChunksByRootFileId(int rootFileId);
        public List<CloudFileInfo> GetFilesByStorageNum(int cloudStorageNum);

        public void updateFile(CloudFileInfo file_info);
        List<CloudFileInfo> FindByFileName(string fileName, string ID);
        string GetFullPath(int fileId);
    }
}
