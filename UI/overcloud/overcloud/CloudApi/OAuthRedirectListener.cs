using System;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace overcloud.CloudApi
{
    // Phase 4 — 스토리지 추가 인터랙티브 OAuth 공통 헬퍼. Google/OneDrive 둘 다 "로컬 포트에서 리다이렉트를
    // 기다렸다가 code 쿼리 파라미터를 캡처"하는 동일한 방식을 쓰므로 한 곳에 모았다.
    public static class OAuthRedirectListener
    {
        public static async Task<string> GetAuthCodeAsync(string redirectUri)
        {
            using var listener = new HttpListener();
            listener.Prefixes.Add(redirectUri);
            listener.Start();

            var context = await listener.GetContextAsync();
            var req = context.Request;
            var resp = context.Response;

            string code = req.QueryString["code"];
            string error = req.QueryString["error"];

            string responseString = string.IsNullOrEmpty(error)
                ? "<html><body><h2>인증 성공! 창을 닫아주세요.</h2></body></html>"
                : $"<html><body><h2>인증 실패: {error}</h2></body></html>";
            byte[] buffer = Encoding.UTF8.GetBytes(responseString);
            resp.ContentLength64 = buffer.Length;
            await resp.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            resp.OutputStream.Close();

            listener.Stop();

            if (!string.IsNullOrEmpty(error))
            {
                Console.WriteLine($"❌ OAuth 인증 실패: {error}");
                return null;
            }
            return code;
        }
    }
}
