using System.IO;
using System.Net.Http;
using System.Text;

namespace MajdataEdit;

internal static class WebControl
{
    private static readonly HttpClient _client = new();

    public static string RequestPOST(string url, string data = "")
    {
        try
        {
            var webRequest = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(data, Encoding.UTF8)
            };

            using var response = _client.Send(webRequest);
            using var reader = new StreamReader(response.Content.ReadAsStream());

            return reader.ReadToEnd();
        }
        catch
        {
            return "ERROR";
        }
    }
}