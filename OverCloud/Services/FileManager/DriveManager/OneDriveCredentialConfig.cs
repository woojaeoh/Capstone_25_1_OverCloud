using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DB.overcloud.Models;
using System.IO;
using DB.overcloud.Repository;
using System.Text.Json;



namespace OverCloud.Services.FileManager.DriveManager
{
    public class OneDriveCredentialConfig
    {
        public string client_id { get; set; }
        public string redirect_uri { get; set; }
        public List<string> scopes { get; set; }

        public static OneDriveCredentialConfig Load(string filePath)
        {
            var json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<OneDriveCredentialConfig>(json);
        }
    }
}