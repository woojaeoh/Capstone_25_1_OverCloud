using DB.overcloud.Models;
using Google.Protobuf.WellKnownTypes;
using MySql.Data.MySqlClient;
using System;
using System.Collections.Generic;

namespace DB.overcloud.Repository
{
    public class FileRepository : IFileRepository
    {
        private readonly string connectionString;

        public FileRepository(string connStr)
        {
            connectionString = connStr;
        }

        public List<CloudFileInfo> GetAllFileInfo(int fileId)
        {
            var list = new List<CloudFileInfo>();

            using var conn = new MySqlConnection(connectionString);
            conn.Open();

            string query = "SELECT * FROM CloudFileInfo WHERE parent_folder_id = @parent";
            using var cmd = new MySqlCommand(query, conn);
            cmd.Parameters.AddWithValue("@parent", fileId);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new CloudFileInfo
                {
                    FileId = Convert.ToInt32(reader["file_id"]),
                    FileName = reader["file_name"].ToString(),
                    FileSize = Convert.ToUInt32(reader["file_size"]),
                    UploadedAt = Convert.ToDateTime(reader["uploaded_at"]),
                    CloudStorageNum = Convert.ToInt32(reader["cloud_storage_num"]),
                    ID = reader["ID"].ToString(),
                    ParentFolderId = Convert.ToInt32(reader["parent_folder_id"]),
                    IsFolder = Convert.ToBoolean(reader["is_folder"]),
                    CloudFileId = reader["cloud_file_id"]?.ToString(),
                    RootFileId = reader["root_file_id"] is DBNull ? null : Convert.ToInt32(reader["root_file_id"]),
                    ChunkIndex = reader["chunk_index"] is DBNull ? null : Convert.ToInt32(reader["chunk_index"]),
                    ChunkSize = reader["chunk_size"] is DBNull ? null : Convert.ToUInt64(reader["chunk_size"]),
                    IsDistributed = Convert.ToBoolean(reader["is_distributed"])
                });
            }

            return list;
        }

        public bool addfile(CloudFileInfo file_info)
        {
            using var conn = new MySqlConnection(connectionString);
            conn.Open();

            string query = @"INSERT INTO CloudFileInfo 
                (file_name, file_size, uploaded_at, cloud_storage_num, ID, parent_folder_id, is_folder, cloud_file_id, root_file_id, chunk_index, chunk_size, is_distributed)
                VALUES 
                (@name, @size, @time, @storage, @id, @parent, @folder, @cloud, @rootId, @chunkIndex, @chunkSize, @isDistributed)";

            using var cmd = new MySqlCommand(query, conn);

            cmd.Parameters.AddWithValue("@name", file_info.FileName);
            cmd.Parameters.AddWithValue("@size", file_info.FileSize);
            cmd.Parameters.AddWithValue("@time", file_info.UploadedAt);
            cmd.Parameters.AddWithValue("@storage", file_info.CloudStorageNum);
            cmd.Parameters.AddWithValue("@id", file_info.ID);
            cmd.Parameters.AddWithValue("@parent", file_info.ParentFolderId);
            cmd.Parameters.AddWithValue("@folder", file_info.IsFolder);
            cmd.Parameters.AddWithValue("@cloud", file_info.CloudFileId ?? "");
            cmd.Parameters.AddWithValue("@rootId", file_info.RootFileId.HasValue ? file_info.RootFileId : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@chunkIndex", file_info.ChunkIndex.HasValue ? file_info.ChunkIndex : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@chunkSize", file_info.ChunkSize.HasValue ? file_info.ChunkSize : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@isDistributed", file_info.IsDistributed);

            return cmd.ExecuteNonQuery() > 0;
        }

        public bool DeleteFile(int fileId)
        {
            using var conn = new MySqlConnection(connectionString);
            conn.Open();

            string query = "DELETE FROM CloudFileInfo WHERE file_id = @id";
            using var cmd = new MySqlCommand(query, conn);
            cmd.Parameters.AddWithValue("@id", fileId);

            return cmd.ExecuteNonQuery() > 0;
        }

        public CloudFileInfo GetFileById(int fileId)
        {
            using var conn = new MySqlConnection(connectionString);
            conn.Open();

            string query = "SELECT * FROM CloudFileInfo WHERE file_id = @id";
            using var cmd = new MySqlCommand(query, conn);
            cmd.Parameters.AddWithValue("@id", fileId);

            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return new CloudFileInfo
                {
                    FileId = Convert.ToInt32(reader["file_id"]),
                    FileName = reader["file_name"].ToString(),
                    FileSize = Convert.ToUInt32(reader["file_size"]),
                    UploadedAt = Convert.ToDateTime(reader["uploaded_at"]),
                    CloudStorageNum = Convert.ToInt32(reader["cloud_storage_num"]),
                    ID = reader["ID"].ToString(),
                    ParentFolderId = Convert.ToInt32(reader["parent_folder_id"]),
                    IsFolder = Convert.ToBoolean(reader["is_folder"]),
                    CloudFileId = reader["cloud_file_id"]?.ToString(),
                    RootFileId = reader["root_file_id"] is DBNull ? null : Convert.ToInt32(reader["root_file_id"]),
                    ChunkIndex = reader["chunk_index"] is DBNull ? null : Convert.ToInt32(reader["chunk_index"]),
                    ChunkSize = reader["chunk_size"] is DBNull ? (ulong?)null : Convert.ToUInt64(reader["chunk_size"]),
                    IsDistributed = Convert.ToBoolean(reader["is_distributed"])
                };
            }

            return null;
        }

        public List<CloudFileInfo> all_file_list(int fileId, string user_id)
        {
            var result = new List<CloudFileInfo>();

            using var conn = new MySqlConnection(connectionString);
            conn.Open();

            string query = @"
                SELECT * FROM CloudFileInfo
                WHERE parent_folder_id = @fileId
                AND ID = @user_id;";

            using var cmd = new MySqlCommand(query, conn);
            cmd.Parameters.AddWithValue("@fileId", fileId);
            cmd.Parameters.AddWithValue("@user_id", user_id);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new CloudFileInfo
                {
                    FileId = Convert.ToInt32(reader["file_id"]),
                    FileName = reader["file_name"].ToString(),
                    FileSize = Convert.ToUInt32(reader["file_size"]),
                    UploadedAt = Convert.ToDateTime(reader["uploaded_at"]),
                    CloudStorageNum = Convert.ToInt32(reader["cloud_storage_num"]),
                    ID = reader["ID"].ToString(),
                    ParentFolderId = Convert.ToInt32(reader["parent_folder_id"]),
                    IsFolder = Convert.ToBoolean(reader["is_folder"]),
                    CloudFileId = reader["cloud_file_id"]?.ToString(),
                    RootFileId = reader["root_file_id"] is DBNull ? null : Convert.ToInt32(reader["root_file_id"]),
                    ChunkIndex = reader["chunk_index"] is DBNull ? null : Convert.ToInt32(reader["chunk_index"]),
                    ChunkSize = reader["chunk_size"] is DBNull ? null : Convert.ToUInt64(reader["chunk_size"]),
                    IsDistributed = Convert.ToBoolean(reader["is_distributed"])
                });
            }

            return result;
        }

        public List<CloudFileInfo> all_file_list_paged(int fileId, string user_id, int lastFileId, int pageSize)
        {
            var result = new List<CloudFileInfo>();

            using var conn = new MySqlConnection(connectionString);
            conn.Open();

            string query = @"
                SELECT * FROM CloudFileInfo USE INDEX (idx_folder_user_fileid)
                WHERE parent_folder_id = @fileId
                AND ID = @user_id
                AND file_id > @lastFileId
                ORDER BY file_id
                LIMIT @pageSize;";

            using var cmd = new MySqlCommand(query, conn);
            cmd.Parameters.AddWithValue("@fileId", fileId);
            cmd.Parameters.AddWithValue("@user_id", user_id);
            cmd.Parameters.AddWithValue("@lastFileId", lastFileId);
            cmd.Parameters.AddWithValue("@pageSize", pageSize);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new CloudFileInfo
                {
                    FileId = Convert.ToInt32(reader["file_id"]),
                    FileName = reader["file_name"].ToString(),
                    FileSize = Convert.ToUInt32(reader["file_size"]),
                    UploadedAt = Convert.ToDateTime(reader["uploaded_at"]),
                    CloudStorageNum = Convert.ToInt32(reader["cloud_storage_num"]),
                    ID = reader["ID"].ToString(),
                    ParentFolderId = Convert.ToInt32(reader["parent_folder_id"]),
                    IsFolder = Convert.ToBoolean(reader["is_folder"]),
                    CloudFileId = reader["cloud_file_id"]?.ToString(),
                    RootFileId = reader["root_file_id"] is DBNull ? null : Convert.ToInt32(reader["root_file_id"]),
                    ChunkIndex = reader["chunk_index"] is DBNull ? null : Convert.ToInt32(reader["chunk_index"]),
                    ChunkSize = reader["chunk_size"] is DBNull ? null : Convert.ToUInt64(reader["chunk_size"]),
                    IsDistributed = Convert.ToBoolean(reader["is_distributed"])
                });
            }

            return result;
        }

        public List<CloudFileInfo> all_file_list_full(int fileId, string user_id)
        {
            const int pageSize = 1000;
            var result = new List<CloudFileInfo>();
            int lastFileId = 0;

            while (true)
            {
                var page = all_file_list_paged(fileId, user_id, lastFileId, pageSize);
                if (page.Count == 0) break;

                result.AddRange(page);
                lastFileId = page[page.Count - 1].FileId;

                if (page.Count < pageSize) break;
            }

            return result;
        }

        public CloudFileInfo specific_file_info(int fileId)
        {
            using var conn = new MySqlConnection(connectionString);
            conn.Open();

            string query = "SELECT * FROM CloudFileInfo WHERE file_id = @id";
            using var cmd = new MySqlCommand(query, conn);
            cmd.Parameters.AddWithValue("@id", fileId);

            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return new CloudFileInfo
                {
                    FileId = Convert.ToInt32(reader["file_id"]),
                    FileName = reader["file_name"].ToString(),
                    FileSize = Convert.ToUInt32(reader["file_size"]),
                    UploadedAt = Convert.ToDateTime(reader["uploaded_at"]),
                    CloudStorageNum = Convert.ToInt32(reader["cloud_storage_num"]),
                    ID = reader["ID"].ToString(),
                    ParentFolderId = Convert.ToInt32(reader["parent_folder_id"]),
                    IsFolder = Convert.ToBoolean(reader["is_folder"]),
                    CloudFileId = reader["cloud_file_id"]?.ToString(),
                    RootFileId = reader["root_file_id"] is DBNull ? null : Convert.ToInt32(reader["root_file_id"]),
                    ChunkIndex = reader["chunk_index"] is DBNull ? null : Convert.ToInt32(reader["chunk_index"]),
                    ChunkSize = reader["chunk_size"] is DBNull ? (ulong?)null : Convert.ToUInt64(reader["chunk_size"]),
                    IsDistributed = Convert.ToBoolean(reader["is_distributed"])
                };
            }

            return null; // 찾는 파일이 없는 경우
        }

        public List<CloudFileInfo> GetAllFileInfo(string file_direc)
        {
            var result = new List<CloudFileInfo>();

            using var conn = new MySqlConnection(connectionString);
            conn.Open();

            // 1. 폴더 이름으로 해당 폴더의 file_id 찾기
            string folderQuery = "SELECT file_id FROM CloudFileInfo WHERE file_name = @name AND is_folder = true LIMIT 1";
            using var folderCmd = new MySqlCommand(folderQuery, conn);
            folderCmd.Parameters.AddWithValue("@name", file_direc);

            object folderIdObj = folderCmd.ExecuteScalar();
            if (folderIdObj == null) return result; // 폴더가 존재하지 않음

            int parentId = Convert.ToInt32(folderIdObj);

            // 2. 해당 폴더에 포함된 모든 파일/폴더 조회
            string childQuery = "SELECT * FROM CloudFileInfo WHERE parent_folder_id = @parent";
            using var childCmd = new MySqlCommand(childQuery, conn);
            childCmd.Parameters.AddWithValue("@parent", parentId);

            using var reader = childCmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new CloudFileInfo
                {
                    FileId = Convert.ToInt32(reader["file_id"]),
                    FileName = reader["file_name"].ToString(),
                    FileSize = Convert.ToUInt32(reader["file_size"]),
                    UploadedAt = Convert.ToDateTime(reader["uploaded_at"]),
                    CloudStorageNum = Convert.ToInt32(reader["cloud_storage_num"]),
                    ID = reader["ID"].ToString(),
                    ParentFolderId = Convert.ToInt32(reader["parent_folder_id"]),
                    IsFolder = Convert.ToBoolean(reader["is_folder"]),
                    CloudFileId = reader["cloud_file_id"]?.ToString(),
                    RootFileId = reader["root_file_id"] is DBNull ? null : Convert.ToInt32(reader["root_file_id"]),
                    ChunkIndex = reader["chunk_index"] is DBNull ? null : Convert.ToInt32(reader["chunk_index"]),
                    ChunkSize = reader["chunk_size"] is DBNull ? (ulong?)null : Convert.ToUInt64(reader["chunk_size"]),
                    IsDistributed = Convert.ToBoolean(reader["is_distributed"])
                });
            }

            return result;
        }

        public int add_folder(CloudFileInfo file_info)
        {
            using (MySqlConnection conn = new MySqlConnection(connectionString))
            {
                conn.Open();

                string query = @"
                    INSERT INTO CloudFileInfo
                        (file_name, file_size, cloud_storage_num, parent_folder_id, is_folder, ID)
                    VALUES
                        (@file_name, 0, @cloud_storage_num, @parent_folder_id, 1, @ID);
                    SELECT LAST_INSERT_ID();";

                using (MySqlCommand cmd = new MySqlCommand(query, conn))
                {
                    cmd.Parameters.AddWithValue("@file_name", file_info.FileName);
                    cmd.Parameters.AddWithValue("@cloud_storage_num", file_info.CloudStorageNum);
                    cmd.Parameters.AddWithValue("@parent_folder_id", file_info.ParentFolderId);
                    cmd.Parameters.AddWithValue("@ID", file_info.ID);

                    object result = cmd.ExecuteScalar();

                    if (result != null && int.TryParse(result.ToString(), out int insertedId))
                    {
                        return insertedId;
                    }
                    else
                    {
                        return -1;
                    }
                }
            }
        }

        public bool change_name(CloudFileInfo file_info)
        {
            using var conn = new MySqlConnection(connectionString);
            conn.Open();

            string query = @"
                UPDATE CloudFileInfo
                SET file_name = @name
                WHERE file_id = @id";

            using var cmd = new MySqlCommand(query, conn);
            cmd.Parameters.AddWithValue("@name", file_info.FileName);
            cmd.Parameters.AddWithValue("@id", file_info.FileId);

            return cmd.ExecuteNonQuery() > 0;
        }

        public bool change_dir(CloudFileInfo file_info)
        {
            using var conn = new MySqlConnection(connectionString);
            conn.Open();

            string query = @"
                UPDATE CloudFileInfo
                SET parent_folder_id = @parent
                WHERE file_id = @id";

            using var cmd = new MySqlCommand(query, conn);
            cmd.Parameters.AddWithValue("@parent", file_info.ParentFolderId);
            cmd.Parameters.AddWithValue("@id", file_info.FileId);

            return cmd.ExecuteNonQuery() > 0;
        }

        public int AddFileAndReturnId(CloudFileInfo file_info)
        {
            using var conn = new MySqlConnection(connectionString);
            conn.Open();

            string query = @"
                INSERT INTO CloudFileInfo 
                (file_name, file_size, uploaded_at, cloud_storage_num, ID, parent_folder_id, is_folder, cloud_file_id, root_file_id, chunk_index, chunk_size, is_distributed) 
                VALUES 
                (@name, @size, @time, @storage, @id, @parent, @folder, @cloud, @rootId, @chunkIndex, @chunkSize, @isDistributed);
                SELECT LAST_INSERT_ID();";

            using var cmd = new MySqlCommand(query, conn);
            cmd.Parameters.AddWithValue("@name", file_info.FileName);
            cmd.Parameters.AddWithValue("@size", Convert.ToUInt32(file_info.FileSize));
            cmd.Parameters.AddWithValue("@time", file_info.UploadedAt);
            cmd.Parameters.AddWithValue("@storage", file_info.CloudStorageNum);
            cmd.Parameters.AddWithValue("@id", file_info.ID);
            cmd.Parameters.AddWithValue("@parent", file_info.ParentFolderId);
            cmd.Parameters.AddWithValue("@folder", file_info.IsFolder);
            cmd.Parameters.AddWithValue("@cloud", file_info.CloudFileId ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@rootId", file_info.RootFileId.HasValue ? file_info.RootFileId : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@chunkIndex", file_info.ChunkIndex.HasValue ? file_info.ChunkIndex : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@chunkSize", file_info.ChunkSize.HasValue ? file_info.ChunkSize : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@isDistributed", file_info.IsDistributed);

            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        public List<CloudFileInfo> GetChunksByRootFileId(int rootFileId)
        {
            var result = new List<CloudFileInfo>();
            using var conn = new MySqlConnection(connectionString);
            conn.Open();

            string query = "SELECT * FROM CloudFileInfo WHERE root_file_id = @root ORDER BY chunk_index";
            using var cmd = new MySqlCommand(query, conn);
            cmd.Parameters.AddWithValue("@root", rootFileId);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new CloudFileInfo
                {
                    FileId = Convert.ToInt32(reader["file_id"]),
                    FileName = reader["file_name"].ToString(),
                    FileSize = Convert.ToUInt32(reader["file_size"]),
                    UploadedAt = Convert.ToDateTime(reader["uploaded_at"]),
                    CloudStorageNum = Convert.ToInt32(reader["cloud_storage_num"]),
                    ID = reader["ID"].ToString(),
                    ParentFolderId = Convert.ToInt32(reader["parent_folder_id"]),
                    IsFolder = Convert.ToBoolean(reader["is_folder"]),
                    CloudFileId = reader["cloud_file_id"]?.ToString(),
                    RootFileId = reader["root_file_id"] is DBNull ? null : Convert.ToInt32(reader["root_file_id"]),
                    ChunkIndex = reader["chunk_index"] is DBNull ? null : Convert.ToInt32(reader["chunk_index"]),
                    ChunkSize = reader["chunk_size"] is DBNull ? (ulong?)null : Convert.ToUInt64(reader["chunk_size"])
                });
            }

            return result;
        }

        public List<CloudFileInfo> GetFilesByStorageNum(int cloudStorageNum)
        {
            var files = new List<CloudFileInfo>();

            using var conn = new MySqlConnection(connectionString);
            conn.Open();

            string query = "SELECT * FROM CloudFileInfo WHERE cloud_storage_num = @storage";
            using var cmd = new MySqlCommand(query, conn);
            cmd.Parameters.AddWithValue("@storage", cloudStorageNum);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                files.Add(new CloudFileInfo
                {
                    FileId = Convert.ToInt32(reader["file_id"]),
                    FileName = reader["file_name"].ToString(),
                    FileSize = Convert.ToUInt32(reader["file_size"]),
                    UploadedAt = Convert.ToDateTime(reader["uploaded_at"]),
                    CloudStorageNum = Convert.ToInt32(reader["cloud_storage_num"]),
                    ID = reader["ID"].ToString(),
                    ParentFolderId = Convert.ToInt32(reader["parent_folder_id"]),
                    IsFolder = Convert.ToBoolean(reader["is_folder"]),
                    CloudFileId = reader["cloud_file_id"]?.ToString(),
                    RootFileId = reader["root_file_id"] is DBNull ? null : Convert.ToInt32(reader["root_file_id"]),
                    ChunkIndex = reader["chunk_index"] is DBNull ? null : Convert.ToInt32(reader["chunk_index"]),
                    ChunkSize = reader["chunk_size"] is DBNull ? (ulong?)null : Convert.ToUInt64(reader["chunk_size"]),
                    IsDistributed = Convert.ToBoolean(reader["is_distributed"])
                });
            }

            return files;
        }

        public void updateFile(CloudFileInfo file)
        {
            using var conn = new MySqlConnection(connectionString);
            conn.Open();

            string query = @"
                UPDATE CloudFileInfo
                SET 
                    cloud_storage_num = @cloudStorageNum,
                    cloud_file_id = @cloudFileId
                WHERE 
                    file_id = @fileId
            ";

            using var cmd = new MySqlCommand(query, conn);
            cmd.Parameters.AddWithValue("@cloudStorageNum", file.CloudStorageNum);
            cmd.Parameters.AddWithValue("@cloudFileId", file.CloudFileId);
            cmd.Parameters.AddWithValue("@fileId", file.FileId);

            cmd.ExecuteNonQuery();

            // MySqlConnection 기본 AutoCommit 모드라서 별도의 transaction.Commit() 불필요
            // 하지만, 트랜잭션을 명시적으로 사용 중이라면, transaction.Commit() 추가해야 함
        }

        public List<CloudFileInfo> FileSearchByName(int cloudStorageNum, string userId, string searchTerm)
        {
            var files = new List<CloudFileInfo>();

            using var conn = new MySqlConnection(connectionString);
            conn.Open();

            string query = @"
                SELECT * FROM CloudFileInfo
                WHERE cloud_storage_num = @cloudStorageNum
                AND ID = @userId
                AND file_name LIKE CONCAT('%', @searchTerm, '%')";

            using var cmd = new MySqlCommand(query, conn);
            cmd.Parameters.AddWithValue("@cloudStorageNum", cloudStorageNum);
            cmd.Parameters.AddWithValue("@userId", userId);
            cmd.Parameters.AddWithValue("@searchTerm", searchTerm);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                files.Add(new CloudFileInfo
                {
                    FileId = Convert.ToInt32(reader["file_id"]),
                    FileName = reader["file_name"].ToString(),
                    FileSize = Convert.ToUInt32(reader["file_size"]),
                    UploadedAt = Convert.ToDateTime(reader["uploaded_at"]),
                    CloudStorageNum = Convert.ToInt32(reader["cloud_storage_num"]),
                    ID = reader["ID"].ToString(),
                    ParentFolderId = Convert.ToInt32(reader["parent_folder_id"]),
                    IsFolder = Convert.ToBoolean(reader["is_folder"]),
                    CloudFileId = reader["cloud_file_id"]?.ToString(),
                    RootFileId = reader["root_file_id"] is DBNull ? null : Convert.ToInt32(reader["root_file_id"]),
                    ChunkIndex = reader["chunk_index"] is DBNull ? null : Convert.ToInt32(reader["chunk_index"]),
                    ChunkSize = reader["chunk_size"] is DBNull ? null : Convert.ToUInt64(reader["chunk_size"]),
                    IsDistributed = Convert.ToBoolean(reader["is_distributed"])
                });
            }

            return files;
        }

        public List<CloudFileInfo> FindByFileName(string fileName, string ID)
        {
            var files = new List<CloudFileInfo>();

            using var conn = new MySqlConnection(connectionString);
            conn.Open();

            string query = "SELECT * FROM CloudFileInfo WHERE ID = @id AND file_name LIKE CONCAT('%', @fileName, '%')";

            using var cmd = new MySqlCommand(query, conn);
            cmd.Parameters.AddWithValue("@id", ID);
            cmd.Parameters.AddWithValue("@fileName", fileName);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                files.Add(new CloudFileInfo
                {
                    FileId = Convert.ToInt32(reader["file_id"]),
                    FileName = reader["file_name"].ToString(),
                    FileSize = Convert.ToUInt32(reader["file_size"]),
                    UploadedAt = Convert.ToDateTime(reader["uploaded_at"]),
                    CloudStorageNum = Convert.ToInt32(reader["cloud_storage_num"]),
                    ID = reader["ID"].ToString(),
                    ParentFolderId = Convert.ToInt32(reader["parent_folder_id"]),
                    IsFolder = Convert.ToBoolean(reader["is_folder"]),
                    CloudFileId = reader["cloud_file_id"]?.ToString(),
                    RootFileId = reader["root_file_id"] is DBNull ? null : Convert.ToInt32(reader["root_file_id"]),
                    ChunkIndex = reader["chunk_index"] is DBNull ? null : Convert.ToInt32(reader["chunk_index"]),
                    ChunkSize = reader["chunk_size"] is DBNull ? (ulong?)null : Convert.ToUInt64(reader["chunk_size"]),
                    IsDistributed = Convert.ToBoolean(reader["is_distributed"])
                });
            }

            return files;
        }

        public string GetFullPath(int fileId)
        {
            var file = GetFileById(fileId);
            if (file == null) return string.Empty;

            var pathParts = new List<string>();
            while (file.ParentFolderId != -2)
            {
                pathParts.Insert(0, file.FileName);
                file = GetFileById(file.ParentFolderId);
            }

            return string.Join("/", pathParts);
        }


    }
}
