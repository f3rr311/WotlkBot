using System;
using System.Net.Sockets;
using System.Threading;
using WotlkClient.Clients;
using WotlkClient.Constants;
using WotlkClient.Shared;

namespace WotlkClient.Network
{
    public class PacketLoop
    {
        Thread loop;
        int dataSize;
        byte[] data;
        ServiceType ServiceStatus;
        LogonServerClient tClient;
        WorldServerClient wClient;
        bool Connected = new bool();

        Socket tSocket;
        string prefix;

        public PacketLoop(LogonServerClient client, Socket socket, string _prefix)
        {
            tClient = client;
            tSocket = socket;
            ServiceStatus = ServiceType.Logon;
            prefix = _prefix;
        }

        public PacketLoop(WorldServerClient client, Socket socket, string _prefix)
        {
            wClient = client;
            tSocket = socket;
            ServiceStatus = ServiceType.World;
            prefix = _prefix;
        }

        public void Start()
        {
            loop = new Thread(Loop);
            loop.IsBackground = true;
            loop.Start();
        }

        public void Stop()
        {
            if (loop != null)
                loop.Abort();
        }

        void Loop()
        {
            if (ServiceStatus == ServiceType.Logon)
            {
                Connected = tClient.Connected;
            }

            else if (ServiceStatus == ServiceType.World)
            {
                Connected = wClient.Connected;
            }

            while (Connected)
            {

                if (ServiceStatus == ServiceType.Logon)
                {
                    if (!tSocket.Connected)
                    {
                        tClient.Connected = false;
                        Log.WriteLine(LogType.Error, "Disconnected from Logon Server", prefix);
                        return;
                    }
                    while (tSocket.Available > 0)
                    {
                        try
                        {
                            data = OnReceive(tSocket.Available);
                            tClient.HandlePacket(new PacketIn(data, true));
                        }
                        catch (Exception ex)    // Server dc'd us most likely ;P
                        {
                        }
                    }
                }
                else if (ServiceStatus == ServiceType.World)
                {
                    if (!tSocket.Connected)
                    {
                        wClient.Connected = false;
                        Log.WriteLine(LogType.Error, "Disconnected from World Server", prefix);
                        return;
                    }
                    try
                    {
                        // WoW SMSG header is ARC4-encrypted and VARIABLE length: normally
                        // 2-byte size + 2-byte opcode, but for packets >= 0x8000 the size is
                        // 3 bytes with the high bit of the first byte set. The old code always
                        // read a 2-byte size, so the first big packet (Stormwind's initial
                        // object update) desynced the cipher stream and every packet after it
                        // — including spell-damage logs — decoded to garbage.
                        byte[] h0 = OnReceive(1);
                        wClient.mCrypt.Decrypt(h0, 0, 1);
                        int size;
                        if ((h0[0] & 0x80) != 0)
                        {
                            byte[] rest = OnReceive(2);
                            wClient.mCrypt.Decrypt(rest, 0, 2);
                            size = ((h0[0] & 0x7F) << 16) | (rest[0] << 8) | rest[1];
                        }
                        else
                        {
                            byte[] h1 = OnReceive(1);
                            wClient.mCrypt.Decrypt(h1, 0, 1);
                            size = (h0[0] << 8) | h1[0];
                        }
                        data = OnReceive(size);               // opcode(2) + body
                        wClient.mCrypt.Decrypt(data, 0, 2);   // decrypt the opcode only
                        PacketIn packet = new PacketIn(data);
                        wClient.HandlePacket(packet);
                    }
                    catch (Exception ex)    // Server dc'd us most likely ;P
                    {
                    }
                }
            }
        }

        public byte[] OnReceive(int mSize)
        {
            byte[] data = new byte[mSize];

            try
            {
                int readSoFar = 0;

                if (ServiceStatus == ServiceType.Logon)
                {
                    do
                    {
                        tSocket.Poll(10, SelectMode.SelectRead);

                        if (tSocket.Available > 0)
                        {
                            int read = tSocket.Receive(data, readSoFar, mSize - readSoFar, SocketFlags.None);
                            readSoFar += read;
                            Thread.Sleep(10);
                        }
                    }
                    while (readSoFar < mSize);
                }

                else if (ServiceStatus == ServiceType.World)
                {
                    do
                    {
                        tSocket.Poll(10, SelectMode.SelectRead);

                        if (tSocket.Available > 0)
                        {
                            int read = tSocket.Receive(data, readSoFar, mSize - readSoFar, SocketFlags.None);
                            readSoFar += read;
                            Thread.Sleep(10);
                        }
                        else
                        {
                            //Log.WriteLine(LogType.Error, "ouch!");
                        }
                    }
                    while (readSoFar < mSize);

                }

            }

            catch (Exception ex)
            {
            }

            return data;

        }

        private int parseSize(byte[] SizeBytes)
        {
            try
            {
                if (ServiceStatus == ServiceType.Logon)
                {
                    tClient.mCrypt.Decrypt(SizeBytes, 2);
                }
                else if (ServiceStatus == ServiceType.World)
                {
                    wClient.mCrypt.Decrypt(SizeBytes, 0, 2);
                }
                int size = ((SizeBytes[0] * 256) + SizeBytes[1]);
                return size;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private void decryptData(byte[] Data)
        {
            if (ServiceStatus == ServiceType.Logon)
            {
                tClient.mCrypt.Decrypt(Data, 2);
            }

            else if (ServiceStatus == ServiceType.World)
            {
                wClient.mCrypt.Decrypt(Data, 0, 2);
            }

        }
    }
}
