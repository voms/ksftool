using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KsfCompanion
{
    /// <summary>The game turned the password down.</summary>
    sealed class RconAuthException : Exception
    {
        public RconAuthException() : base("the game didn't accept the remote console password") { }
    }

    /// <summary>
    /// Valve's Source RCON protocol (TCP): little-endian packets of size, id, type, a null-terminated body and an empty
    /// string. https://developer.valvesoftware.com/wiki/Source_RCON_Protocol
    /// </summary>
    sealed class RconClient : IDisposable
    {
        public const int Auth = 3, AuthResponse = 2, ExecCommand = 2, ResponseValue = 0;
        const int MaxPacket = 1 << 20;

        readonly TcpClient tcp;
        readonly Stream stream;
        int nextId;

        RconClient(TcpClient tcp, Stream stream)
        {
            this.tcp = tcp;
            this.stream = stream;
        }

        /// <summary>Connects and logs in. Throws RconAuthException for a wrong password, and SocketException / IOException otherwise.</summary>
        public static async Task<RconClient> ConnectAsync(string host, int port, string password, TimeSpan timeout, CancellationToken cancel)
        {
            var tcp = new TcpClient { NoDelay = true };
            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                limit.CancelAfter(timeout);
                await tcp.ConnectAsync(host, port, limit.Token).ConfigureAwait(false);
                var client = new RconClient(tcp, tcp.GetStream());
                await client.LogInAsync(password, limit.Token).ConfigureAwait(false);
                return client;
            }
            catch
            {
                tcp.Dispose();
                throw;
            }
        }

        async Task LogInAsync(string password, CancellationToken cancel)
        {
            var id = NextId();
            await WriteAsync(id, Auth, password, cancel).ConfigureAwait(false);
            while (true)
            {
                // An empty response value comes first, then the answer: our id, or -1 for a wrong password.
                var packet = await ReadAsync(cancel).ConfigureAwait(false);
                if (packet.Type != AuthResponse) continue;
                if (packet.Id == -1) throw new RconAuthException();
                if (packet.Id == id) return;
            }
        }

        /// <summary>
        /// Runs a command and returns everything it printed. A long answer comes in several packets, so an empty
        /// response value is sent right after the command: the game answers it once the command's output is all sent.
        /// </summary>
        public async Task<string> ExecuteAsync(string command, TimeSpan timeout, CancellationToken cancel)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            limit.CancelAfter(timeout);
            int id = NextId(), end = NextId();
            await WriteAsync(id, ExecCommand, command, limit.Token).ConfigureAwait(false);
            await WriteAsync(end, ResponseValue, "", limit.Token).ConfigureAwait(false);
            var output = new StringBuilder();
            while (true)
            {
                var packet = await ReadAsync(limit.Token).ConfigureAwait(false);
                if (packet.Id == end) return output.ToString();
                // Anything else is left over from an earlier command (or the extra packet after an end marker).
                if (packet.Id == id && packet.Type == ResponseValue) output.Append(packet.Body);
            }
        }

        int NextId()
        {
            nextId = nextId >= int.MaxValue - 1 ? 1 : nextId + 1;
            return nextId;
        }

        public static byte[] Encode(int id, int type, string body)
        {
            var text = Encoding.UTF8.GetBytes(body ?? "");
            var packet = new byte[4 + 4 + 4 + text.Length + 2];
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(0), packet.Length - 4);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4), id);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8), type);
            text.CopyTo(packet, 12);
            return packet;
        }

        async Task WriteAsync(int id, int type, string body, CancellationToken cancel)
        {
            var packet = Encode(id, type, body);
            await stream.WriteAsync(packet, cancel).ConfigureAwait(false);
            await stream.FlushAsync(cancel).ConfigureAwait(false);
        }

        public readonly struct Packet
        {
            public Packet(int id, int type, string body)
            {
                Id = id;
                Type = type;
                Body = body;
            }

            public int Id { get; }
            public int Type { get; }
            public string Body { get; }
        }

        async Task<Packet> ReadAsync(CancellationToken cancel) => await ReadPacketAsync(stream, cancel).ConfigureAwait(false);

        public static async Task<Packet> ReadPacketAsync(Stream stream, CancellationToken cancel)
        {
            var header = new byte[4];
            await stream.ReadExactlyAsync(header, cancel).ConfigureAwait(false);
            var size = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (size < 10 || size > MaxPacket) throw new InvalidDataException($"not a remote console packet (size {size})");
            var data = new byte[size];
            await stream.ReadExactlyAsync(data, cancel).ConfigureAwait(false);
            var bodyLength = Array.IndexOf(data, (byte)0, 8);
            if (bodyLength < 0) bodyLength = data.Length;
            return new Packet(BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0)), BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4)),
                Encoding.UTF8.GetString(data, 8, bodyLength - 8));
        }

        public void Dispose()
        {
            stream.Dispose();
            tcp?.Dispose();
        }
    }
}
