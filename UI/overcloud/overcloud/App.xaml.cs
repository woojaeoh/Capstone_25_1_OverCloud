using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Web;
using OverCloud.Services;
using OverCloud.transfer_manager;
using Microsoft.Win32;
using overcloud.Windows;

namespace overcloud
{
    public partial class App : System.Windows.Application
    {
        private LoginController _controller;
        public static TransferManager TransferManager { get; set; }

        public static List<(string userId, string cloudFileId, int fileId)> PendingDownloads { get; private set; } = new();

        private const string MutexName = "OverCloudSingleInstance";
        private const string PipeName = "OverCloudPipe";

        // 앱 생존 기간과 동일하게 Mutex를 유지해야 단일 인스턴스 보장됨
        private Mutex _singleInstanceMutex;

        public static event Action DownloadRequestReceived;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            bool isNew;
            _singleInstanceMutex = new Mutex(true, MutexName, out isNew);

            if (isNew)
            {
                StartPipeServer();
                ParseArgs(e.Args);

                _controller = new LoginController(DbConfig.ConnectionString);
                var loginWindow = new overcloud.Views.LoginWindow(_controller);
                loginWindow.Show();
            }
            else
            {
                _singleInstanceMutex.Dispose();
                Shutdown();
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // 앱 종료 시 온라인 상태 해제 + LAN 서버 종료
            if (_controller?.user_id != null)
            {
                // presence API도 best-effort로 시도 — 종료 흐름을 API 응답까지 기다리게 하면
                // 서버가 안 떠있을 때 앱 종료가 지연될 수 있어 대기하지 않는다.
                // DB 직접 갱신은 그대로 남겨 항상 온라인 상태가 해제되도록 보장한다.
                _ = OverCloud.Services.OverCloudApiClient.UpdatePresenceAsync(null, false);
                _controller.AccountRepository.UpdateOnlineStatus(_controller.user_id, null, false);
                _controller.LanTransferService.StopListening();
            }

            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
            base.OnExit(e);
        }

        private void ParseArgs(string[] args)
        {
            if (args.Length > 0)
            {
                try
                {
                    string fullUrl = args[0];
                    if (fullUrl.StartsWith("overcloud://"))
                    {
                        int index = fullUrl.IndexOf("?link=");
                        if (index >= 0)
                        {
                            string encodedLinkParam = fullUrl.Substring(index + 6);
                            string linkParam = Uri.UnescapeDataString(encodedLinkParam);

                            string[] fileItems = linkParam.Split('|');
                            foreach (var item in fileItems)
                            {
                                var parts = item.Split(',');
                                if (parts.Length >= 3)
                                {
                                    string userId = parts[0];
                                    string cloudFileId = parts[1];
                                    int fileId = int.Parse(parts[2]);

                                    PendingDownloads.Add((userId, cloudFileId, fileId));
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show("링크 파싱 실패: " + ex.Message);
                }
            }
        }

        private void StartPipeServer()
        {
            Task.Run(() =>
            {
                while (true)
                {
                    try
                    {   
                        using (var server = new NamedPipeServerStream(
                            PipeName,
                            PipeDirection.In,
                            NamedPipeServerStream.MaxAllowedServerInstances,
                            PipeTransmissionMode.Byte,
                            PipeOptions.Asynchronous))
                        {
                            server.WaitForConnection();

                            using (var reader = new StreamReader(server))
                            {
                                string incoming = reader.ReadLine();
                                if (!string.IsNullOrEmpty(incoming))
                                {
                                    ParseArgs(new[] { incoming });

                                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                                    {
                                        DownloadRequestReceived?.Invoke();
                                    });
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        {
                            System.Windows.MessageBox.Show($"파이프 서버 오류: {ex.Message}");
                        });
                    }
                }
            });
        }
    }
}
