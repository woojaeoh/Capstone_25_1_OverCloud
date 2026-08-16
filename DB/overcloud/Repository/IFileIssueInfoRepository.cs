using DB.overcloud.Models;

namespace DB.overcloud.Repository
{
    public interface IFileIssueRepository
    {
        int AddIssue(FileIssueInfo issue);
        List<FileIssueInfo> GetAllIssues(string ID);
        List<FileIssueInfo> GetIssuesByFileId(int fileId);
        FileIssueInfo? GetIssueById(int issueId);
        bool UpdateIssue(FileIssueInfo issue);
        bool UpdateIssueStatus(int issueId, string status);
        bool AssignIssue(int issueId, string assignedTo);
        bool DeleteIssue(int issueId);
    }
}