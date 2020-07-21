using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Ticker.Client
{
    class Client
    {
        TcpClient _client = new TcpClient();
        Thread _thread { get; set; }
        public PacketHandler PacketHandler { get; set; }
        public Client(PacketHandler handler)
        {
            PacketHandler = handler;

            _client.Connect(IPAddress.Parse("192.168.1.9"), 123);

            _client.SendTimeout = 10000;

            _thread = new Thread(new ThreadStart(Run));
            _thread.Start();

        }
        public void Close()
        {
            _client.Close();
        }
        public void SendPacket(Packet Packet)
        {
            var bytes = Packet.Serialize();
            _client.GetStream().Write(bytes, 0, bytes.Length);
        }
        public void Run()
        {
            try
            {
                while (_client.Connected)
                {
                    PacketHandler.ProcessStream(_client.GetStream());
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
        }
    }
}
