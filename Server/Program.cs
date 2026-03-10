using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

class ChatServer
{
    static TcpListener? server;
    static List<TcpClient> clients = new List<TcpClient>();

    static void Main()
    {
        server = new TcpListener(IPAddress.Any, 5007);
        server.Start();
        Console.WriteLine("Chat server started on port 5007.");

        while (true)
        {
            TcpClient client = server.AcceptTcpClient();
            clients.Add(client);
            Console.WriteLine("New client connected.");

            Thread t = new Thread(HandleClient);
            t.Start(client);
        }
    }
    
    static void HandleClient(object obj)
    {
        TcpClient client = (TcpClient)obj;
        NetworkStream stream = client.GetStream();
        byte[] buffer = new byte[1024];
        //int bytesRead;

        while (true)
        {
            int bytesRead = stream.Read(buffer, 0, buffer.Length);
            string message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
            Console.WriteLine("Received message: " + message);
            Broadcast(message);

        }
    }
    static void Broadcast(string message)
    {
        byte[] data = Encoding.UTF8.GetBytes(message);
        foreach(var client in clients)
        {
            NetworkStream stream = client.GetStream();
            stream.Write(data, 0, data.Length);
        }
    }
}