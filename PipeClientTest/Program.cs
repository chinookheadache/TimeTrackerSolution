using ScreenshotShared.Messaging;
using System;
using System.Threading.Tasks;

class Program
{
    static async Task Main()
    {
        Console.WriteLine("Starting PipeClientTest...");

        var client = new PipeClient("ScreenshotPipe");
        client.MessageReceived += (msg) =>
        {
            Console.WriteLine($"Client received: Event={msg.Event}, Value={msg.Value}");
        };

        await client.ConnectAsync();

        // Send a test command to the server
        client.SendMessage(new PipeMessage
        {
            Command = "Hello",
            Value = "Ping from Client"
        });

        Console.WriteLine("Sent Hello to server. Waiting for server broadcast...");
        Console.ReadLine();
    }
}
