using System.Windows;
using DB.overcloud.Repository;
using OverCloud.Services.FileManager;
using OverCloud.Services.StorageManager;
using OverCloud.Services;
using OverCloud.Services.FileManager.DriveManager;
using OverCloud.transfer_manager;
using DB.overcloud.Models;
using System.IO;
using System;
using Microsoft.Win32;
using Org.BouncyCastle.Crypto.Parameters;

namespace overcloud.Views
{
    public partial class LoginWindow : Window
    {
        private readonly LoginController _controller;

        public LoginWindow(LoginController controller)
        {
            InitializeComponent();
            _controller = controller;
        }

        private async void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            // 아이디, 비밀번호는 나중에 사용할 수 있도록 받아두기만 함
            string userId = IdBox.Text;
            string password = PasswordBox.Password;

            string stored_salt =_controller.AccountRepository.get_salt_by_id(userId);
            if (stored_salt == null)
            {
                Console.WriteLine("저장된 hashed값 x");
                return;
            }

            var hashed = PasswordHasher.HashPassword(userId, password, stored_salt);
            if (hashed == null)
            {
                Console.WriteLine("hashed값 x");
                return;
            }

            var getPassword = _controller.AccountRepository.get_password_by_id(userId);

            if (getPassword == null)
            {
                Console.WriteLine("pw값 없음");
                return;
            }

            if (hashed != getPassword)
            {
                System.Windows.MessageBox.Show("비밀번호가 일치하지 않습니다. 다시 시도해주세요.", "로그인 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            
            string loginResult = _controller.AccountRepository.login_overcloud(userId, hashed);

            if (string.IsNullOrEmpty(loginResult))
            {
                System.Windows.MessageBox.Show("아이디 또는 비밀번호가 올바르지 않습니다. 다시 시도해주세요.", "로그인 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
                return; // 창은 닫지 않고 다시 입력 가능
            }

            //var storages = new AccountRepository(DbConfig.ConnectionString).GetAllAccounts(userId);
            //StorageSessionManager.InitializeFromDatabase(storages);

            // Phase 4 1단계: 신규 API 서버에도 병행으로 로그인해 JWT를 받아둔다.
            // 실패해도(API 서버 미기동 등) 기존 로그인 흐름은 그대로 진행 — LoginAsync가 예외를 삼킴.
            await OverCloud.Services.OverCloudApiClient.LoginAsync(userId, password);

            // 1. 계정 리스트 구성
            var allAccounts = new List<string> { userId };
            allAccounts.AddRange(_controller.CoopUserRepository.connected_cooperation_account_nums(userId));

            // 2. 전체 스토리지 수집
            var allStorages = new List<CloudStorageInfo>();
            foreach (var accId in allAccounts.Distinct())
            {
                var storages = _controller.AccountRepository.GetAllAccounts(accId);
                allStorages.AddRange(storages);
            }

            // 3. 세션 초기화
            StorageSessionManager.InitializeFromDatabase(allStorages);


            App.TransferManager = new TransferManager(_controller.FileUploadManager, _controller.FileDownloadManager);

            _controller.user_id = userId;

            // LAN 전송: 현재 IP를 등록하고 수신 서버 시작.
            // presence API를 우선 시도하고(5.6), 로그인 안 됐거나 호출 실패 시 기존 DB 직접 갱신으로 폴백.
            string localIp = OverCloud.Services.LanTransferService.GetLocalIp();
            bool presenceUpdated = await OverCloud.Services.OverCloudApiClient.UpdatePresenceAsync(localIp, true);
            if (!presenceUpdated)
                _controller.AccountRepository.UpdateOnlineStatus(userId, localIp, true);
            _controller.LanTransferService.StartListening();

            // MainWindow 실행
            var main = new MainWindow(_controller, userId);
            System.Windows.Application.Current.MainWindow = main;
            main.Show();

            this.Close();
        }

        private void RegisterButton_Click(object sender, RoutedEventArgs e)
        {
            // 예시: 회원가입 창 띄우기
            var registerWindow = new RegisterWindow(_controller.AccountRepository); // 따로 만들어진 회원가입 창
            registerWindow.Owner = this;
            registerWindow.ShowDialog();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

    }
}
