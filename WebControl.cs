using System.IO;
using System.Net.Http;
using System.Text;

namespace MajdataEdit;

internal static class WebControl
{
    // 使用 IP 字面量而非 localhost，避免解析到 IPv6
    public const string ViewUrl = "http://127.0.0.1:8013/";

    public static string RequestPOST(string url, string data = "")
    {
        try
        {
            using var client = new HttpClient();

            var webRequest = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(data, Encoding.UTF8)
            };

            var response = client.Send(webRequest);
            using var reader = new StreamReader(response.Content.ReadAsStream());

            return reader.ReadToEnd();
        }
        catch
        {
            return "ERROR";
        }
    }


}