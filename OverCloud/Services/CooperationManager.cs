using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using DB.overcloud.Models;
using DB.overcloud.Repository;
using OverCloud.Services.FileManager.DriveManager;

namespace OverCloud.Services
{
    public class CooperationManager
    {
        private CoopUserRepository _coopUserRepository;
        public CooperationManager(CoopUserRepository coopUserRepository)
        {
            _coopUserRepository = coopUserRepository;
        }


        //협업 참가
        public bool Join_cooperation_Cloud_Storage_UI_to_pro(string user_id_target, string password, string user_id_mine)
        {

            return _coopUserRepository.Join_cooperation_Cloud_Storage_pro_to_DB(user_id_target,  password, user_id_mine);

        }
        
        //협업계정 생성
        public bool Add_cooperation_Cloud_Storage_UI_to_pro(string user_id_insert, string password, string user_id_mine)
        {

            return _coopUserRepository.Add_cooperation_Cloud_Storage_pro_to_DB(user_id_insert, password, user_id_mine);

        }

        //협업 탈퇴.
        public bool Delete_cooperation_Cloud_Storage_UI_to_pro(string user_id_insert, string user_id_mine)
        {

            return _coopUserRepository.Delete_cooperation_Cloud_Storage_pro_to_DB( user_id_insert,  user_id_mine);

        }


    }

}