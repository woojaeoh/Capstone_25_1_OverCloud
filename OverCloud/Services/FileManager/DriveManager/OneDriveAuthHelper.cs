using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Identity.Client; // Microsoft Authentication Library (MSAL) 필요
using DB.overcloud.Models;
using MySql.Data.MySqlClient;
using System.Diagnostics;
using System.Net;
using System.Text;


namespace OverCloud.Services.FileManager.DriveManager
{
    public static class OneDriveAuthHelper
    {
        private const string CredentialFile = "C:\\key\\onedrive_credential.json";
        private const string Authority = "https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize";// 개인 Microsoft 계정은 "consumers" 

        
        public static async Task<(string email, string refreshToken, string clientId, string clientSecret)> AuthorizeAsync(string dummyId)
        {

            var config = OneDriveCredentialConfig.Load(CredentialFile);
            
            string ClientId = config.client_id; // 너가 Azure 등록하면서 받은 Client ID
            string RedirectUri = config.redirect_uri;
            string scopeString = string.Join(" ", config.scopes);

            // 1. 브라우저로 사용자 인증 (캐시된 로그인 세션 무시하고 매번 계정 선택 화면 표시)
            string authUrl = $"{Authority}?client_id={ClientId}&response_type=code&redirect_uri={RedirectUri}&response_mode=query&scope={scopeString}&state=12345&prompt=select_account";
            Console.WriteLine("브라우저 열기: " + authUrl);
            Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });


            //Process.Start(new ProcessStartInfo //시크릿 모드로 열기
            //{
            //    FileName = "chrome.exe",
            //    Arguments = $"--incognito {authUrl}",
            //    UseShellExecute = true
            //});

            string code = await GetAuthCodeAsync(RedirectUri);
            if (string.IsNullOrEmpty(code))
            {
                Console.WriteLine("❌ code가 null 또는 빈 문자열입니다");
                return (null, null, null, null);
            }

            // 3. Access Token, Refresh Token 요청
            var tokens = await RequestTokensAsync(ClientId, code, RedirectUri, scopeString);
            if (tokens == (null,null))
            {
                Console.WriteLine("❌ 토큰 요청 실패");
                return (null, null, null, null);
            }


            //4. 이메일 정보 가져오기
            string email = await GetUserEmailAsync(tokens.Item1);
            if (string.IsNullOrEmpty(email))
            {
                Console.WriteLine("❌ 사용자 이메일 조회 실패");
                return (null, null, null, null);
            }

            Console.WriteLine("✅ OneDrive 인증 성공: " + email);
            return (email, tokens.Item2, ClientId, null);
        }

        private static async Task<string> GetAuthCodeAsync(string redirectUri)
        {
            using (var listener = new HttpListener())
            {
                listener.Prefixes.Add(redirectUri);
                listener.Start();



                // 3. 리디렉션 대기 후 code 추출
                var context = await listener.GetContextAsync();
                var req = context.Request;
                var resp = context.Response;

                string code = req.QueryString["code"];
                const string responseString = "<html><body><h2>인증 성공! 창을 닫아주세요.</h2></body></html>";
                byte[] buffer = Encoding.UTF8.GetBytes(responseString);
                resp.ContentLength64 = buffer.Length;
                await resp.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                resp.OutputStream.Close();

                listener.Stop();
                Console.WriteLine("code: " + code);
                return code;
            }
        }

        private static async Task<(string accessToken,string refreshToken)> RequestTokensAsync(string clientId,string code, string redirectUri,string scopeString)
        {
            // 4. 토큰 요청
            using var client = new HttpClient();
            var parameters = new Dictionary<string, string>
            {
                { "client_id", clientId },
                { "scope", scopeString },
                { "code", code },
                { "redirect_uri", redirectUri },
                { "grant_type", "authorization_code" }
            };
  
            try
            {
                var response = await client.PostAsync("https://login.microsoftonline.com/consumers/oauth2/v2.0/token", new FormUrlEncodedContent(parameters));
                var content = await response.Content.ReadAsStringAsync();
            
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine("❌ 토큰 요청 실패 - DB 저장 불가");
                    return (null, null);
                }

                var json = JsonDocument.Parse(content);
                string accessToken = json.RootElement.GetProperty("access_token").GetString();
                string refreshToken = json.RootElement.GetProperty("refresh_token").GetString();

                return (accessToken, refreshToken);
            
            }
            catch (Exception ex)
            {
                Console.WriteLine($"토큰 요청 중 예외 발생: {ex.Message}");
                return (null, null);
            }
           
        }


        private static async Task<string> GetUserEmailAsync(string accessToken)
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

            try
            {
                var response = await client.GetAsync("https://graph.microsoft.com/v1.0/me");
                var content = await response.Content.ReadAsStringAsync();
                Console.WriteLine("📡 OneDrive 사용자 정보 응답: " + content);
                var json = JsonDocument.Parse(content);

                return json.RootElement.GetProperty("userPrincipalName").GetString();
            }
            catch (Exception ex)
            {
                Console.WriteLine(" 이메일 조회 중 예외: " + ex.Message);
                return null;
            }
        }

    }
}
