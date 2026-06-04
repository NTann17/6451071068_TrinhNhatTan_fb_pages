using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

class Program
{
    static async Task Main()
    {
        var client = new HttpClient();
        var key = "AIzaSyCoFU3yz7n_Cc0Lt5ZcGfNKZfY7UQAmbYs";
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-flash-latest:generateContent?key={key}";
        
        var requestBody = new
        {
            contents = new[] { new { parts = new[] { new { text = "hello" } } } }
        };
        var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
        
        try {
            var response = await client.PostAsync(url, content);
            response.EnsureSuccessStatusCode();
            Console.WriteLine("Success");
        } catch(Exception ex) {
            Console.WriteLine(ex.ToString());
        }
    }
}
