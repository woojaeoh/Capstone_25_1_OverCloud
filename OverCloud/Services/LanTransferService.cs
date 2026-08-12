using System;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DB.overcloud.Repository;

namespace OverCloud.Services
{
    public class LanTransferService
    {
        public const int Port = 9000;
        private const int ChunkSize = 1024 * 1024; // 1MB씩 읽어서 진행률 업데이트

        private readonly IAccountRepository _accountRepository;
        private TcpListener _listener;
        private CancellationTokenSource _cts;

        // 수신 완료 시 UI에 알리기 위한 이벤트
        public event Action<string, string> FileReceived; // (fileName, savePath)

        public LanTransferService(IAccountRepository accountRepository)
        {
            _accountRepository = accountRepository;
        }

        // 현재 머신의 LAN IP 반환 (로그인 시 DB에 저장할 값)
        public static string GetLocalIp()
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                var props = ni.GetIPProperties();
                if (props.GatewayAddresses.Count == 0) continue;

                foreach (var addr in props.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                        return addr.Address.ToString();
                }
            }
            return "127.0.0.1";
        }

        // 로그인 시 1회 호출 — 백그라운드에서 수신 대기
        public void StartListening()
        {
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Any, Port);
            _listener.Start();
            Console.WriteLine($"[LAN] 수신 대기 시작 (포트 {Port})");

            Task.Run(async () =>
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                        _ = HandleIncomingAsync(client);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[LAN] 서버 오류: {ex.Message}");
                    }
                }
            });
        }

        // 앱 종료 시 호출
        public void StopListening()
        {
            _cts?.Cancel();
            _listener?.Stop();
        }

        // ── 수신 처리 ──────────────────────────────────────────────

        private async Task HandleIncomingAsync(TcpClient client)
        {
            using (client)
            using (var stream = client.GetStream())
            {
                try
                {
                    // 프로토콜: [파일명 길이 4B][파일명 UTF-8][파일크기 8B][파일 데이터]

                    // 1. 파일명 수신
                    byte[] nameLenBuf = new byte[4];
                    await stream.ReadExactlyAsync(nameLenBuf, 0, 4);
                    int nameLen = BitConverter.ToInt32(nameLenBuf, 0);

                    byte[] nameBuf = new byte[nameLen];
                    await stream.ReadExactlyAsync(nameBuf, 0, nameLen);
                    string fileName = Encoding.UTF8.GetString(nameBuf);

                    // 2. 파일 크기 수신
                    byte[] sizeBuf = new byte[8];
                    await stream.ReadExactlyAsync(sizeBuf, 0, 8);
                    long fileSize = BitConverter.ToInt64(sizeBuf, 0);

                    // 3. 다운로드 폴더에 저장
                    string savePath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        "Downloads", fileName);

                    using var fileStream = new FileStream(savePath, FileMode.Create, FileAccess.Write);
                    byte[] buffer = new byte[ChunkSize];
                    long received = 0;

                    while (received < fileSize)
                    {
                        int toRead = (int)Math.Min(ChunkSize, fileSize - received);
                        int read = await stream.ReadAsync(buffer, 0, toRead);
                        if (read == 0) break;
                        await fileStream.WriteAsync(buffer, 0, read);
                        received += read;
                    }

                    Console.WriteLine($"[LAN] 수신 완료: {fileName} ({received / 1024 / 1024}MB)");
                    FileReceived?.Invoke(fileName, savePath);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[LAN] 수신 오류: {ex.Message}");
                }
            }
        }

        // ── 송신 ───────────────────────────────────────────────────

        // onProgress: 0~100 진행률 콜백 (UI 스레드에서 ViewModel에 연결)
        public async Task<bool> SendFileAsync(string targetUserId, string filePath, Action<int> onProgress)
        {
            // 1. DB에서 상대 IP 조회
            string targetIp = _accountRepository.GetLocalIp(targetUserId);
            if (targetIp == null)
            {
                Console.WriteLine($"[LAN] {targetUserId} 오프라인 또는 같은 네트워크 아님");
                return false;
            }

            var fileInfo = new FileInfo(filePath);
            long fileSize = fileInfo.Length;
            byte[] nameBytes = Encoding.UTF8.GetBytes(fileInfo.Name);

            try
            {
                using var client = new TcpClient();
                // 3초 내 연결 안 되면 실패 처리
                var connectTask = client.ConnectAsync(targetIp, Port);
                if (await Task.WhenAny(connectTask, Task.Delay(3000)) != connectTask)
                    throw new TimeoutException("연결 타임아웃");

                using var stream = client.GetStream();

                // 1. 파일명 전송
                await stream.WriteAsync(BitConverter.GetBytes(nameBytes.Length), 0, 4);
                await stream.WriteAsync(nameBytes, 0, nameBytes.Length);

                // 2. 파일 크기 전송
                await stream.WriteAsync(BitConverter.GetBytes(fileSize), 0, 8);

                // 3. 파일 데이터 전송 + 실제 진행률 콜백
                using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
                byte[] buffer = new byte[ChunkSize];
                long sent = 0;
                int read;

                while ((read = await fileStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await stream.WriteAsync(buffer, 0, read);
                    sent += read;
                    onProgress?.Invoke((int)(sent * 100 / fileSize));
                }

                onProgress?.Invoke(100);
                Console.WriteLine($"[LAN] 전송 완료: {fileInfo.Name} → {targetIp}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LAN] 전송 오류: {ex.Message}");
                return false;
            }
        }
    }
}
