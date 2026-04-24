using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

class ChatServer
{
    static TcpListener? server;
    static List<TcpClient> clients = new List<TcpClient>();
    static Dictionary<TcpClient, string> clientNames = new Dictionary<TcpClient, string>();

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

        try
        {
            // First message received will be the user's name
            int bytesRead = stream.Read(buffer, 0, buffer.Length);
            if (bytesRead == 0) return;

            string userName = Encoding.UTF8.GetString(buffer, 0, bytesRead);
            clientNames[client] = userName;

            Console.WriteLine(userName + " joined the chat.");

            while (true)
            {
                bytesRead = stream.Read(buffer, 0, buffer.Length);

                if (bytesRead == 0)
                {
                    Console.WriteLine(userName + " disconnected.");
                    break;
                }

                string message = Encoding.UTF8.GetString(buffer, 0, bytesRead).Trim();
                string fullMessage;

                if (int.TryParse(message, out int number))
                {
                    fullMessage = userName + " sent a number: " + number;
                }
                else
                {
                    fullMessage = userName + ": \"" + message + "\" is not valid — only integers are accepted.";
                }

                Console.WriteLine("Received: " + fullMessage);
                Broadcast(fullMessage);
            }
        }
        catch
        {
            if (clientNames.ContainsKey(client))
            {
                Console.WriteLine(clientNames[client] + " disconnected unexpectedly.");
            }
        }
        finally
        {
            clients.Remove(client);
            clientNames.Remove(client);
            client.Close();
        }
    }

    static void Broadcast(string message)
    {
        byte[] data = Encoding.UTF8.GetBytes(message);

        foreach (var client in clients)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                stream.Write(data, 0, data.Length);
            }
            catch
            {
                // Ignore errors for disconnected clients
            }
        }
    }
}