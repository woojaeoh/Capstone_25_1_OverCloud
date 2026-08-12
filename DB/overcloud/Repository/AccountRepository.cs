using DB.overcloud.Models;
using MySql.Data.MySqlClient;
using System;
using System.Collections.Generic;

namespace DB.overcloud.Repository
{
    public class AccountRepository : IAccountRepository
    {
        private readonly string connectionString;
        private readonly IStorageRepository cloudService;

        public AccountRepository(string connStr)
        {
            connectionString = connStr;
            cloudService = new StorageRepository(connStr);
        }

        public List<CloudStorageInfo> GetAllAccounts(string ID)
        {
            var result = new List<CloudStorageInfo>();

            using var conn = new MySqlConnection(connectionString);
            conn.Open();

            string storageQuery = "SELECT * FROM CloudStorageInfo WHERE ID = @id AND cloud_storage_num != -1";

            using var cmd = new MySqlCommand(storageQuery, conn);
            cmd.Parameters.AddWithValue("@id", ID);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new CloudStorageInfo
                {
                    CloudStorageNum = Convert.ToInt32(reader["cloud_storage_num"]),
                    ID = reader["ID"].ToString(),
                    CloudType = reader["cloud_type"].ToString(),
                    AccountId = reader["account_id"].ToString(),
                    AccountPassword = reader["account_password"].ToString(),
                    TotalCapacity = reader["total_capacity"] != DBNull.Value ? Convert.ToUInt64(reader["total_capacity"]) : 0,
                    UsedCapacity = reader["used_capacity"] != DBNull.Value ? Convert.ToUInt64(reader["used_capacity"]) : 0,
                    RefreshToken = reader["refresh_token"]?.ToString(),
                    ClientId = reader["client_id"]?.ToString(),
                    ClientSecret = reader["client_secret"]?.ToString()
                });
            }

            return result;
        }

        public bool DeleteAccountById(string ID)
        {
            using var conn = new MySqlConnection(connectionString);
            conn.Open();

            string query = "DELETE FROM Account WHERE ID = @id";
            using var cmd = new MySqlCommand(query, conn);
            cmd.Parameters.AddWithValue("@id", ID);

            return cmd.ExecuteNonQuery() > 0;
        }

        public bool UpdateAccountUsage(string ID, ulong totalSize, ulong usedSize)
        {
            using var conn = new MySqlConnection(connectionString);
            conn.Open();

            string query = "UPDATE Account SET total_size = @total_size, used_size = @used_size WHERE ID = @id";

            using var cmd = new MySqlCommand(query, conn);
            cmd.Parameters.AddWithValue("@total_size", totalSize);
            cmd.Parameters.AddWithValue("@used_size", usedSize);
            cmd.Parameters.AddWithValue("@id", ID);

            return cmd.ExecuteNonQuery() > 0;
        }

        public bool assign_overcloud(string ID, string password, string salt)
        {
            using var conn = new MySqlConnection(connectionString);
            conn.Open();

            using var transaction = conn.BeginTransaction();

            try
            {
                // 1. Account 테이블에 사용자 삽입
                string insertAccountQuery = @"INSERT INTO Account (ID, password, salt) VALUES (@id, @pw, @salt)";
                using var cmd = new MySqlCommand(insertAccountQuery, conn, transaction);
                cmd.Parameters.AddWithValue("@id", ID);
                cmd.Parameters.AddWithValue("@pw", password);
                cmd.Parameters.AddWithValue("@salt", salt);
                cmd.ExecuteNonQuery();

                // 2. CloudStorageInfo에 시스템 전용 더미 계정 삽입
                string insertDummyStorageQuery = @"INSERT INTO CloudStorageInfo
                    (cloud_storage_num, ID, cloud_type, account_id, account_password, total_capacity, used_capacity)
                    VALUES
                    (-1, @id, 'SYSTEM', '', '', 0, 0)";

                using var dummyCmd = new MySqlCommand(insertDummyStorageQuery, conn, transaction);
                dummyCmd.Parameters.AddWithValue("@id", ID);
                dummyCmd.ExecuteNonQuery();

                transaction.Commit();
                return true;
            }
            catch
            {
                transaction.Rollback();
                return false;
            }
        }
        
        public string login_overcloud(string ID, string password)
        {
            using var conn = new MySqlConnection(connectionString);
            conn.Open();

            string query = "SELECT ID FROM Account WHERE ID = @id AND password = @pw LIMIT 1";
            using var cmd = new MySqlCommand(query, conn);
            cmd.Parameters.AddWithValue("@id", ID);
            cmd.Parameters.AddWithValue("@pw", password);

            var result = cmd.ExecuteScalar();
            return result != null ? result.ToString() : null;
        }

        public string get_salt_by_id(string ID)
        {
            using var conn = new MySqlConnection(connectionString);
            conn.Open();

            string query = "SELECT salt FROM Account WHERE ID = @id LIMIT 1";
            using var cmd = new MySqlCommand(query, conn);
            cmd.Parameters.AddWithValue("@id", ID);

            var result = cmd.ExecuteScalar();
            return result != null ? result.ToString() : null;
        }

        public string get_password_by_id(string ID)
        {
            using var conn = new MySqlConnection(connectionString);
            conn.Open();

            string query = "SELECT password FROM Account WHERE ID = @id LIMIT 1";
            using var cmd = new MySqlCommand(query, conn);
            cmd.Parameters.AddWithValue("@id", ID);

            var result = cmd.ExecuteScalar();
            return result != null ? result.ToString() : null;
        }

        // 로그인 시: localIp 저장 + is_online=1
        // 앱 종료 시: localIp=NULL + is_online=0
        public bool UpdateOnlineStatus(string userId, string localIp, bool isOnline)
        {
            using var conn = new MySqlConnection(connectionString);
            conn.Open();

            string query = "UPDATE Account SET local_ip = @ip, is_online = @online WHERE ID = @id";
            using var cmd = new MySqlCommand(query, conn);
            cmd.Parameters.AddWithValue("@ip", isOnline ? (object)localIp : DBNull.Value);
            cmd.Parameters.AddWithValue("@online", isOnline ? 1 : 0);
            cmd.Parameters.AddWithValue("@id", userId);

            return cmd.ExecuteNonQuery() > 0;
        }

        // is_online=1인 상대방의 IP 반환. 오프라인이면 null
        public string GetLocalIp(string targetUserId)
        {
            using var conn = new MySqlConnection(connectionString);
            conn.Open();

            string query = "SELECT local_ip FROM Account WHERE ID = @id AND is_online = 1 LIMIT 1";
            using var cmd = new MySqlCommand(query, conn);
            cmd.Parameters.AddWithValue("@id", targetUserId);

            var result = cmd.ExecuteScalar();
            return result != null && result != DBNull.Value ? result.ToString() : null;
        }


    }
}
