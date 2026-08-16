using System.Windows;
using System.Collections.Generic;
using DB.overcloud.Models;
//using static overcloud.temp_class.TempClass;
using OverCloud.Services;
using DB.overcloud.Repository;
using System.Diagnostics;
using SourceChord.FluentWPF;

namespace overcloud.Views
{
    public partial class DeleteAccountWindow : AcrylicWindow
    {
        private LoginController _controller;
        private string _user_id;    
        private bool _is_shared; 

        public DeleteAccountWindow(LoginController controller, string user_id, bool is_shared)
        {
            InitializeComponent();
            _controller = controller;
            _user_id = user_id;
            _is_shared = is_shared;

            LoadAccounts();
        }

        // 생성자에서 fire-and-forget으로 호출(생성자는 async일 수 없음) — 다른 async void UI 핸들러와 동일한 패턴.
        private async void LoadAccounts()
        {
            System.Diagnostics.Debug.WriteLine("계정 불러오기 시작");

            List<CloudStorageInfo> allAccounts = new();

            if (_is_shared)
            {
                // 협업 클라우드에 속한 모든 계정 불러오기
                List<string> coopIds = await OverCloudApiClient.GetMyCoopAccountsAsync() ?? new List<string>();
                foreach (var coopId in coopIds)
                {
                    var accounts = _controller.AccountService.Get_Clouds_For_User(coopId);
                    allAccounts.AddRange(accounts);
                }
            }
            else
            {
                // 일반 개인 계정만 불러오기
                allAccounts = _controller.AccountService.Get_Clouds_For_User(_user_id);
            }

            AccountListBox.ItemsSource = allAccounts;
            Debug.WriteLine("계정 목록 출력 완료");
        }



        private async void ConfirmDelete_Click(object sender, RoutedEventArgs e)
        {
            var selectedAccount = AccountListBox.SelectedItem as CloudStorageInfo;
            if (selectedAccount == null)
            {
                System.Windows.MessageBox.Show("삭제할 계정을 선택해 주세요.");
                return;
            }

            // userNum을 이용해 삭제 처리
            int CloudStorageNum = selectedAccount.CloudStorageNum;

            // 소유자는 _user_id(로그인한 본인)가 아니라 selectedAccount.ID다 — 협업 계정 스토리지(_is_shared)를
            // 지울 때 _user_id를 쓰면 서버가 그 협업 계정 소유가 아닌 걸로 판단해 항상 실패한다(기존 버그 수정).
            var (result, message) = await overcloud.transfer_manager.StorageRedistributionManager
                .DeleteStorageAsync(CloudStorageNum, selectedAccount.ID, selectedAccount.CloudType);

            System.Windows.MessageBox.Show(result ? "계정 삭제 성공" : (message ?? "계정 삭제 실패"));
            if (result)
                this.Close();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}
