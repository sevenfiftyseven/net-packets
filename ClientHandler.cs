using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Ticker.Server
{
    class ClientHandler
    {
        internal Thread _thread { get; set; }
        internal TcpClient _client { get; set; }
        internal PacketHandler handler = new PacketHandler();
        internal ClientHandler(TcpClient client)
        {
            _client = client;

            _thread = new Thread(new ThreadStart(Run));
            _thread.Start();

        }
        public void SendPacket(Packet Packet)
        {
            var bytes = Packet.Serialize();
            _client.GetStream().Write(bytes, 0, bytes.Length);
        }
        public void Run()
        {
            Console.WriteLine("Thread Spawned for Client: " + _client.Client.RemoteEndPoint);

            // Let the client know we accepted the initial connection

            try
            {
                while (_client.Connected)
                {
                    var stream = _client.GetStream();
                    handler.ProcessStream(stream);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                Server.Clients.Remove(this);
            }

            Console.WriteLine("Connection Closed for: " + _client.Client.RemoteEndPoint);
        }

    }
}
