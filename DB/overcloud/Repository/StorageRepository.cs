using DB.overcloud.Models;
using MySql.Data.MySqlClient;
using System;
using System.Collections.Generic;

namespace DB.overcloud.Repository
{
    public class StorageRepository : IStorageRepository
    {
        private readonly string connectionString;

        public StorageRepository(string connStr)
        {
            connectionString = connStr;
        }
        

        public CloudStorageInfo GetCloud(int cloudStorageNum, string userId)
        {
            using var conn = new MySqlConnection(connectionString);
            conn.Open();

            string query = "SELECT * FROM CloudStorageInfo WHERE cloud_storage_num = @num AND ID = @id";

            using var cmd = new MySqlCommand(query, conn);
            cmd.Parameters.AddWithValue("@num", cloudStorageNum);
            cmd.Parameters.AddWithValue("@id", userId);

            using var reader = cmd.ExecuteReader();

            if (reader.Read())
            {
                return new CloudStorageInfo
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
                };
            }

            return null;
        }

        public bool AddCloudStorage(CloudStorageInfo info, string userId)
        {
            using var conn = new MySqlConnection(connectionString);
            conn.Open();

            // SERIALIZABLE: 동일 cloud account를 동시에 신규 등록할 때 gap lock으로 phantom 방지
            using var transaction = conn.BeginTransaction(System.Data.IsolationLevel.Serializable);

            try
            {
                // Step 1: 이 유저가 이미 같은 클라우드 계정을 등록했는지 확인
                string dupCheckQuery = @"SELECT COUNT(*) FROM CloudStorageInfo
                    WHERE cloud_type = @type AND account_id = @account_id AND ID = @id";

                using var dupCmd = new MySqlCommand(dupCheckQuery, conn, transaction);
                dupCmd.Parameters.AddWithValue("@type", info.CloudType);
                dupCmd.Parameters.AddWithValue("@account_id", info.AccountId);
                dupCmd.Parameters.AddWithValue("@id", info.ID);

                if (Convert.ToInt64(dupCmd.ExecuteScalar()) > 0)
                {
                    transaction.Rollback();
                    return false;
                }

                // Step 2: 같은 실제 클라우드 계정(다른 유저 포함)이 존재하는지 확인
                // FOR UPDATE: 해당 행/범위 잠금 → 동시 신규 등록 시 한 쪽만 먼저 진행
                // 기존 설계: 같은 account_id + cloud_type은 동일 cloud_storage_num 공유
                // (예: ojw7336@naver.com OneDrive → num=3을 여러 유저가 공유)
                string existingNumQuery = @"SELECT cloud_storage_num FROM CloudStorageInfo
                    WHERE cloud_type = @type AND account_id = @account_id
                    LIMIT 1 FOR UPDATE";

                using var existCmd = new MySqlCommand(existingNumQuery, conn, transaction);
                existCmd.Parameters.AddWithValue("@type", info.CloudType);
                existCmd.Parameters.AddWithValue("@account_id", info.AccountId);

                var existingNum = existCmd.ExecuteScalar();

                string insertQuery;
                if (existingNum != null)
                {
                    // 기존 cloud_storage_num 재사용 → 공유 설계 유지
                    info.CloudStorageNum = Convert.ToInt32(existingNum);
                    insertQuery = @"INSERT INTO CloudStorageInfo
                        (cloud_storage_num, ID, cloud_type, account_id, account_password, total_capacity, used_capacity, refresh_token, client_id, client_secret)
                        VALUES (@num, @id, @type, @accountId, @accountPw, @total, @used, @refresh, @clientId, @clientSecret)";
                }
                else
                {
                    // 완전히 새로운 클라우드 계정 → AUTO_INCREMENT로 번호 자동 할당
                    // 기존 MAX+1 방식의 TOCTOU 제거: DB 엔진이 원자적으로 처리
                    insertQuery = @"INSERT INTO CloudStorageInfo
                        (ID, cloud_type, account_id, account_password, total_capacity, used_capacity, refresh_token, client_id, client_secret)
                        VALUES (@id, @type, @accountId, @accountPw, @total, @used, @refresh, @clientId, @clientSecret)";
                }

                using var cmd = new MySqlCommand(insertQuery, conn, transaction);
                if (existingNum != null)
                    cmd.Parameters.AddWithValue("@num", info.CloudStorageNum);
                cmd.Parameters.AddWithValue("@id", info.ID);
                cmd.Parameters.AddWithValue("@type", info.CloudType);
                cmd.Parameters.AddWithValue("@accountId", info.AccountId);
                cmd.Parameters.AddWithValue("@accountPw", info.AccountPassword);
                cmd.Parameters.AddWithValue("@total", info.TotalCapacity);
                cmd.Parameters.AddWithValue("@used", info.UsedCapacity);
                cmd.Parameters.AddWithValue("@refresh", info.RefreshToken ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@clientId", info.ClientId ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@clientSecret", info.ClientSecret ?? (object)DBNull.Value);

                if (cmd.ExecuteNonQuery() <= 0)
                {
                    transaction.Rollback();
                    return false;
                }

                if (existingNum == null)
                {
                    // AUTO_INCREMENT로 자동 할당된 번호 회수
                    info.CloudStorageNum = Convert.ToInt32(
                        new MySqlCommand("SELECT LAST_INSERT_ID()", conn, transaction).ExecuteScalar());
                }

                // Step 3: 협업 클라우드 계정 등록 (동일 트랜잭션 유지)
                if (info.ID != userId)
                {
                    string selectCoopNumQuery = @"SELECT coop_num FROM CoopUserInfo
                        WHERE coop_id = @coopId AND user_id = @userId";

                    using var selectCmd = new MySqlCommand(selectCoopNumQuery, conn, transaction);
                    selectCmd.Parameters.AddWithValue("@coopId", info.ID);
                    selectCmd.Parameters.AddWithValue("@userId", userId);

                    object result = selectCmd.ExecuteScalar();
                    if (result == null)
                    {
                        transaction.Rollback();
                        return false;
                    }

                    string insertCoopQuery = @"INSERT INTO CoopStorageInfo
                        (coop_num, cloud_storage_num, ID)
                        VALUES (@coopNum, @cloudNum, @id)";

                    using var insertCoopCmd = new MySqlCommand(insertCoopQuery, conn, transaction);
                    insertCoopCmd.Parameters.AddWithValue("@coopNum", Convert.ToInt32(result));
                    insertCoopCmd.Parameters.AddWithValue("@cloudNum", info.CloudStorageNum);
                    insertCoopCmd.Parameters.AddWithValue("@id", info.ID);

                    if (insertCoopCmd.ExecuteNonQuery() <= 0)
                    {
                        transaction.Rollback();
                        return false;
                    }
                }

                transaction.Commit();
                return true;
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        public bool DeleteCloudStorage(int cloudStorageNum, string userId)
        {
            using var conn = new MySqlConnection(connectionString);
            conn.Open();

            using var transaction = conn.BeginTransaction();

            try
            {
                // Step 1: CoopStorageInfo에서 존재 여부 확인
                string checkCoopQuery = @"SELECT COUNT(*) FROM CoopStorageInfo 
                                        WHERE cloud_storage_num = @num AND ID = @id";

                using var checkCmd = new MySqlCommand(checkCoopQuery, conn, transaction);
                checkCmd.Parameters.AddWithValue("@num", cloudStorageNum);
                checkCmd.Parameters.AddWithValue("@id", userId);

                long count = (long)checkCmd.ExecuteScalar();

                if (count > 0)
                {
                    // Step 2: CoopStorageInfo에서 먼저 삭제
                    string deleteCoopQuery = @"DELETE FROM CoopStorageInfo 
                                            WHERE cloud_storage_num = @num AND ID = @id";

                    using var deleteCoopCmd = new MySqlCommand(deleteCoopQuery, conn, transaction);
                    deleteCoopCmd.Parameters.AddWithValue("@num", cloudStorageNum);
                    deleteCoopCmd.Parameters.AddWithValue("@id", userId);
                    deleteCoopCmd.ExecuteNonQuery();
                }

                // Step 3: CloudStorageInfo 삭제 (공통)
                string deleteCloudQuery = @"DELETE FROM CloudStorageInfo 
                                            WHERE cloud_storage_num = @num AND ID = @id";

                using var deleteCloudCmd = new MySqlCommand(deleteCloudQuery, conn, transaction);
                deleteCloudCmd.Parameters.AddWithValue("@num", cloudStorageNum);
                deleteCloudCmd.Parameters.AddWithValue("@id", userId);

                int affected = deleteCloudCmd.ExecuteNonQuery();

                transaction.Commit();

                return affected > 0;
            }
            catch
            {
                transaction.Rollback();
                return false;
            }
        }

        public bool account_save(CloudStorageInfo one_cloud)
        {
            using var conn = new MySqlConnection(connectionString);
            conn.Open();

            string query = @"UPDATE CloudStorageInfo SET 
                                total_capacity = @total,
                                used_capacity = @used
                            WHERE cloud_storage_num = @cloudNum AND ID = @id";

            using var cmd = new MySqlCommand(query, conn);
            cmd.Parameters.AddWithValue("@total", one_cloud.TotalCapacity);
            cmd.Parameters.AddWithValue("@used", one_cloud.UsedCapacity);
            cmd.Parameters.AddWithValue("@cloudNum", one_cloud.CloudStorageNum);
            cmd.Parameters.AddWithValue("@id", one_cloud.ID);

            return cmd.ExecuteNonQuery() > 0;
        }

        public bool UpdateRefreshToken(int cloudStorageNum, string userId, string refreshToken)
        {
            using var conn = new MySqlConnection(connectionString);
            conn.Open();

            string query = @"UPDATE CloudStorageInfo
                            SET refresh_token = @token
                            WHERE cloud_storage_num = @id AND ID = @userId";

            using var cmd = new MySqlCommand(query, conn);
            cmd.Parameters.AddWithValue("@token", refreshToken);
            cmd.Parameters.AddWithValue("@id", cloudStorageNum);
            cmd.Parameters.AddWithValue("@userId", userId);

            return cmd.ExecuteNonQuery() > 0;
        }

        // [Lost Update 방지] 현재 값을 읽지 않고 DB에서 직접 delta 적용
        // used_capacity = used_capacity + @delta 는 단일 원자적 연산이므로
        // 동시 업로드가 발생해도 각 delta가 순서대로 누적됨
        public bool IncrementUsedCapacity(int cloudStorageNum, string userId, long deltaKB)
        {
            using var conn = new MySqlConnection(connectionString);
            conn.Open();

            string query = @"UPDATE CloudStorageInfo
                SET used_capacity = used_capacity + @delta
                WHERE cloud_storage_num = @cloudNum AND ID = @id";

            using var cmd = new MySqlCommand(query, conn);
            cmd.Parameters.AddWithValue("@delta", deltaKB);
            cmd.Parameters.AddWithValue("@cloudNum", cloudStorageNum);
            cmd.Parameters.AddWithValue("@id", userId);

            return cmd.ExecuteNonQuery() > 0;
        }
    }
}