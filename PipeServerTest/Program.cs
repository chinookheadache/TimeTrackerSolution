using ScreenshotShared.Messaging;
using System;
using System.Threading.Tasks;

class Program
{
    static async Task Main()
    {
        Console.WriteLine("Starting PipeServerTest...");

        var server = new PipeServer("ScreenshotPipe");
        server.MessageReceived += (msg) =>
        {
            Console.WriteLine($"Server received: Command={msg.Command}, Value={msg.Value}");
        };

        // Start server
        _ = server.StartAsync();

        Console.WriteLine("PipeServer running. Press Enter to send a test broadcast.");
        Console.ReadLine();

        // Broadcast a message to all connected clients
        await server.SendMessage(new PipeMessage
        {
            Event = "ServerBroadcast",
            Value = "Hello from Server"
        });

        Console.WriteLine("Broadcast sent. Press Enter to exit.");
        Console.ReadLine();

        server.Stop();
    }
}
