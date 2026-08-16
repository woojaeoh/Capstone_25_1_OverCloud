using DB.overcloud.Models;
using System;
using System.Collections.Generic;
using MySql.Data.MySqlClient;

namespace DB.overcloud.Repository
{
    public class FileIssueRepository : IFileIssueRepository
    {
        private readonly string connectionString;

        public FileIssueRepository(string connectionString)
        {
            this.connectionString = connectionString;
        }

        public int AddIssue(FileIssueInfo issue)
        {
            using var conn = new MySqlConnection(connectionString);
            conn.Open();

            string query = @"INSERT INTO FileIssueInfo 
            (ID, title, description, created_by, assigned_to, status, created_at, due_date)
            VALUES 
            (@id, @title, @desc, @createdBy, @assignedTo, @status, @createdAt, @dueDate);
            SELECT LAST_INSERT_ID();";

            using var cmd = new MySqlCommand(query, conn);

            cmd.Parameters.AddWithValue("@id", issue.ID);
            cmd.Parameters.AddWithValue("@title", issue.Title);
            cmd.Parameters.AddWithValue("@desc", issue.Description ?? "");
            cmd.Parameters.AddWithValue("@createdBy", issue.CreatedBy);
            cmd.Parameters.AddWithValue("@assignedTo", (object?)issue.AssignedTo ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@status", issue.Status);
            cmd.Parameters.AddWithValue("@createdAt", issue.CreatedAt);
            cmd.Parameters.AddWithValue("@dueDate", (object?)issue.DueDate ?? DBNull.Value);

            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        public List<FileIssueInfo> GetAllIssues(string ID)
        {
            var list = new List<FileIssueInfo>();
            using var conn = new MySqlConnection(connectionString);
            conn.Open();

            string query = "SELECT * FROM FileIssueInfo WHERE ID = @id ORDER BY created_at DESC";

            using var cmd = new MySqlCommand(query, conn);
            cmd.Parameters.AddWithValue("@id", ID);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new FileIssueInfo
                {
                    IssueId = reader.GetInt32("issue_id"),
                    ID = reader.GetString("ID"),
                    Title = reader.GetString("title"),
                    Description = reader.GetString("description"),
                    CreatedBy = reader.GetString("created_by"),
                    AssignedTo = reader.IsDBNull(reader.GetOrdinal("assigned_to")) ? null : reader.GetString("assigned_to"),
                    Status = reader.GetString("status"),
                    CreatedAt = reader.GetDateTime("created_at"),
                    DueDate = reader.IsDBNull(reader.GetOrdinal("due_date")) ? null : reader.GetDateTime("due_date")
                });
            }

            return list;
        }

        public List<FileIssueInfo> GetIssuesByFileId(int fileId)
        {
            var issues = new List<FileIssueInfo>();

            using var conn = new MySqlConnection(connectionString);
            conn.Open();

            string query = @"
            SELECT fii.* 
            FROM FileIssueInfo fii
            JOIN FileIssueMapping fim ON fii.issue_id = fim.issue_id
            WHERE fim.file_id = @fileId
            ORDER BY fii.created_at DESC";

            using var cmd = new MySqlCommand(query, conn);
            cmd.Parameters.AddWithValue("@fileId", fileId);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var issue = new FileIssueInfo
                {
                    IssueId = Convert.ToInt32(reader["issue_id"]),
                    ID = reader["ID"].ToString(),
                    Title = reader["title"].ToString(),
                    Description = reader["description"].ToString(),
                    CreatedBy = reader["created_by"].ToString(),
                    AssignedTo = reader["assigned_to"] == DBNull.Value ? null : reader["assigned_to"].ToString(),
                    Status = reader["status"].ToString(),
                    CreatedAt = Convert.ToDateTime(reader["created_at"]),
                    DueDate = reader["due_date"] == DBNull.Value ? null : Convert.ToDateTime(reader["due_date"])
                };

                issues.Add(issue);
            }

            return issues;
        }

        public FileIssueInfo? GetIssueById(int issueId)
        {
            using var conn = new MySqlConnection(connectionString);
            conn.Open();

            string query = "SELECT * FROM FileIssueInfo WHERE issue_id = @id";

            using var cmd = new MySqlCommand(query, conn);
            cmd.Parameters.AddWithValue("@id", issueId);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return null;

            return new FileIssueInfo
            {
                IssueId = reader.GetInt32("issue_id"),
                ID = reader.GetString("ID"),
                Title = reader.GetString("title"),
                Description = reader.GetString("description"),
                CreatedBy = reader.GetString("created_by"),
                AssignedTo = reader.IsDBNull(reader.GetOrdinal("assigned_to")) ? null : reader.GetString("assigned_to"),
                Status = reader.GetString("status"),
                CreatedAt = reader.GetDateTime("created_at"),
                DueDate = reader.IsDBNull(reader.GetOrdinal("due_date")) ? null : reader.GetDateTime("due_date")
            };
        }

        public bool UpdateIssue(FileIssueInfo issue)
        {
            using var conn = new MySqlConnection(connectionString);
            conn.Open();

            string query = @"
                UPDATE FileIssueInfo
                SET 
                    title = @title,
                    description = @description,
                    assigned_to = @assignedTo,
                    status = @status,
                    due_date = @dueDate
                WHERE issue_id = @issueId";

            using var cmd = new MySqlCommand(query, conn);
            
            cmd.Parameters.AddWithValue("@title", issue.Title);
            cmd.Parameters.AddWithValue("@description", issue.Description ?? "");
            cmd.Parameters.AddWithValue("@assignedTo", string.IsNullOrWhiteSpace(issue.AssignedTo) ? DBNull.Value : issue.AssignedTo);
            cmd.Parameters.AddWithValue("@status", issue.Status);
            cmd.Parameters.AddWithValue("@dueDate", (object?)issue.DueDate ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@issueId", issue.IssueId);

            return cmd.ExecuteNonQuery() > 0;
        }

        public bool UpdateIssueStatus(int issueId, string status)
        {
            using var conn = new MySqlConnection(connectionString);
            conn.Open();

            string query = "UPDATE FileIssueInfo SET status = @status WHERE issue_id = @id";
            using var cmd = new MySqlCommand(query, conn);
            cmd.Parameters.AddWithValue("@status", status);
            cmd.Parameters.AddWithValue("@id", issueId);

            return cmd.ExecuteNonQuery() > 0;
        }

        public bool AssignIssue(int issueId, string assignedTo)
        {
            using var conn = new MySqlConnection(connectionString);
            conn.Open();

            string query = "UPDATE FileIssueInfo SET assigned_to = @assigned WHERE issue_id = @id";
            using var cmd = new MySqlCommand(query, conn);
            cmd.Parameters.AddWithValue("@assigned", assignedTo);
            cmd.Parameters.AddWithValue("@id", issueId);

            return cmd.ExecuteNonQuery() > 0;
        }

        public bool DeleteIssue(int issueId)
        {
            using var conn = new MySqlConnection(connectionString);
            conn.Open();

            string query = "DELETE FROM FileIssueInfo WHERE issue_id = @id";
            using var cmd = new MySqlCommand(query, conn);
            cmd.Parameters.AddWithValue("@id", issueId);

            return cmd.ExecuteNonQuery() > 0;
        }
    }
}