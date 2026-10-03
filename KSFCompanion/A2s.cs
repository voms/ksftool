using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace KsfCompanion
{
    /// <summary>What a Source server tells a server browser about itself (A2S_INFO).</summary>
    sealed class A2sInfo
    {
        public string Name;
        public string Map;
        /// <summary>Everyone on it, bots included.</summary>
        public int Players;
        public int MaxPlayers;
        public int Bots;
    }

    /// <summary>One player in a Source server's answer to A2S_PLAYER.</summary>
    sealed class A2sPlayer
    {
        public string Name;
        public int Score;
        /// <summary>How long they've been on the server.</summary>
        public double Seconds;
    }

    /// <summary>
    /// Valve's server queries (A2S, over UDP) - what any Source server tells a server browser: its name, map and players.
    /// For KSF's private servers, which ksf.surf doesn't list. A server asks for a challenge number first these days;
    /// the query is sent again with it.
    /// </summary>
    static class A2s
    {
        const byte InfoRequest = 0x54, InfoReply = 0x49, PlayerRequest = 0x55, PlayerReply = 0x44, ChallengeReply = 0x41;
        static readonly byte[] NoChallenge = { 0xFF, 0xFF, 0xFF, 0xFF };

        /// <summary>The server's name, map and player count; null if it doesn't answer in time.</summary>
        public static async Task<A2sInfo> InfoAsync(string address, TimeSpan timeout, CancellationToken cancel)
        {
            var request = Packet(InfoRequest, Encoding.ASCII.GetBytes("Source Engine Query\0"));
            var reply = await QueryAsync(address, request, challenge => request.Concat(challenge).ToArray(), timeout, cancel).ConfigureAwait(false);
            return reply == null ? null : ParseInfo(reply);
        }

        /// <summary>Who's on the server (bots too) and for how long; null if it doesn't answer in time.</summary>
        public static async Task<List<A2sPlayer>> PlayersAsync(string address, TimeSpan timeout, CancellationToken cancel)
        {
            var reply = await QueryAsync(address, Packet(PlayerRequest, NoChallenge), challenge => Packet(PlayerRequest, challenge), timeout, cancel).ConfigureAwait(false);
            return reply == null ? null : ParsePlayers(reply);
        }

        /// <summary>
        /// SurfTimer's replay bots ("WR | name", "WRB #2 | name", "Map | name", "NOF | Map | name", "SurfTimer Replay"),
        /// SourceTV and the like, which a server lists among its players.
        /// </summary>
        public static bool LooksLikeBot(string name) => BotName.IsMatch(name ?? "");
        static readonly Regex BotName = new Regex(@"^(?:WR(?:B|CP|S)?|PR|TOP|NOF|Map)\s*(?:#\d+)?\s*\||SurfTimer Replay|SourceTV|\(Auto-Recording\)|^BOT\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        static byte[] Packet(byte kind, byte[] body) => NoChallenge.Append(kind).Concat(body).ToArray();

        /// <summary>One query, with the challenge round if the server wants one: its reply after the leading FF FF FF FF.</summary>
        static async Task<byte[]> QueryAsync(string address, byte[] request, Func<byte[], byte[]> withChallenge, TimeSpan timeout, CancellationToken cancel)
        {
            if (!IPEndPoint.TryParse(address, out var server) || server.Port == 0) return null;
            using var udp = new UdpClient(server.AddressFamily);
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timer.CancelAfter(timeout);
            try
            {
                udp.Connect(server);
                await udp.SendAsync(request, timer.Token).ConfigureAwait(false);
                var reply = await ReceiveAsync(udp, timer.Token).ConfigureAwait(false);
                // A server can ask again if the first number has run out.
                for (var i = 0; i < 2 && reply != null && reply.Length >= 5 && reply[0] == ChallengeReply; i++)
                {
                    await udp.SendAsync(withChallenge(reply.AsSpan(1, 4).ToArray()), timer.Token).ConfigureAwait(false);
                    reply = await ReceiveAsync(udp, timer.Token).ConfigureAwait(false);
                }
                return reply;
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { return null; }
            catch (SocketException) { return null; }
        }

        /// <summary>A whole reply, put back together if the server split it into several packets.</summary>
        static async Task<byte[]> ReceiveAsync(UdpClient udp, CancellationToken cancel)
        {
            var pieces = new SortedDictionary<int, byte[]>();
            while (true)
            {
                var data = (await udp.ReceiveAsync(cancel).ConfigureAwait(false)).Buffer;
                if (data.Length < 5) continue;
                var header = BinaryPrimitives.ReadInt32LittleEndian(data);
                if (header == -1) return data.AsSpan(4).ToArray();
                // Split: id (its top bit set means compressed, which isn't handled), how many pieces, this one's number,
                // the most a piece holds, then the piece itself.
                if (header != -2 || data.Length < 12) continue;
                if ((BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4)) & 0x80000000) != 0) return null;
                int total = data[8], number = data[9];
                pieces[number] = data.AsSpan(12).ToArray();
                if (pieces.Count < total) continue;
                var whole = pieces.Values.SelectMany(p => p).ToArray();
                return whole.Length > 4 && BinaryPrimitives.ReadInt32LittleEndian(whole) == -1 ? whole.AsSpan(4).ToArray() : null;
            }
        }

        internal static A2sInfo ParseInfo(byte[] reply)
        {
            try
            {
                var r = new Reader(reply);
                if (r.Byte() != InfoReply) return null;
                r.Byte(); // protocol
                var info = new A2sInfo { Name = r.String(), Map = r.String() };
                r.String(); // folder
                r.String(); // game
                r.Short(); // app id
                info.Players = r.Byte();
                info.MaxPlayers = r.Byte();
                info.Bots = r.Byte();
                return info;
            }
            catch (IndexOutOfRangeException) { return null; }
        }

        internal static List<A2sPlayer> ParsePlayers(byte[] reply)
        {
            try
            {
                var r = new Reader(reply);
                if (r.Byte() != PlayerReply) return null;
                r.Byte(); // how many (can be off; the list is read to its end)
                var players = new List<A2sPlayer>();
                while (r.Left > 0)
                {
                    r.Byte(); // index
                    players.Add(new A2sPlayer { Name = r.String(), Score = r.Int(), Seconds = r.Float() });
                }
                return players;
            }
            catch (IndexOutOfRangeException) { return null; }
        }

        /// <summary>Little-endian numbers and zero-ended UTF-8 strings, as A2S has them; past the end throws IndexOutOfRangeException.</summary>
        sealed class Reader
        {
            readonly byte[] data;
            int at;

            public Reader(byte[] data) => this.data = data;
            public int Left => data.Length - at;

            byte[] Take(int count)
            {
                if (count > Left) throw new IndexOutOfRangeException();
                var bytes = data.AsSpan(at, count).ToArray();
                at += count;
                return bytes;
            }

            public byte Byte() => Take(1)[0];
            public short Short() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));
            public int Int() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
            public float Float() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));

            public string String()
            {
                var end = Array.IndexOf(data, (byte)0, at);
                if (end < 0) throw new IndexOutOfRangeException();
                var text = Encoding.UTF8.GetString(data, at, end - at);
                at = end + 1;
                return text;
            }
        }
    }
}
