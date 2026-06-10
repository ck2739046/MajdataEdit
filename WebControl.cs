using System.IO;
using System.Net.Http;
using System.Text;

namespace MajdataEdit;

internal static class WebControl
{
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