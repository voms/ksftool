using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace KsfCompanion
{
    /// <summary>
    /// The server you're on as the game's "status" showed it: its name, address and players. For a private KSF server
    /// that doesn't answer a server browser's questions: while you're on it, this is what the server list has.
    /// </summary>
    sealed class StatusAnswer
    {
        // "status" lists players as: #  67 "SomePlayer"  [U:1:123456789]  3:53:52  99  0 active
        static readonly Regex PlayerLine = new Regex(@"^#\s*\d+\s+(?:\d+\s+)?""(?<name>.+)""\s+\[U:1:(?<account>\d+)\](?:\s+(?<connected>\d+(?::\d+){1,2})\b)?", RegexOptions.Compiled);
        // ... and counts them: "players : 2 humans, 4 bots (61 max)"
        static readonly Regex CountLine = new Regex(@"^players\s*:\s*(?<humans>\d+) humans?\b", RegexOptions.Compiled);

        public string Name, Address;
        /// <summary>From "players : 2 humans, 4 bots (61 max)" (null until that line comes).</summary>
        public int? Humans;
        public readonly DateTime At = DateTime.Now;
        public readonly List<KsfServerPlayer> Players = new List<KsfServerPlayer>();

        /// <summary>A player's line of "status" (not a bot's): their name, Steam account and time on the server as printed ("48:58").</summary>
        public static (string Name, uint Account, string Connected)? Player(string line)
        {
            var match = PlayerLine.Match(line);
            if (!match.Success || !uint.TryParse(match.Groups["account"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var account)) return null;
            return (match.Groups["name"].Value, account, match.Groups["connected"].Value);
        }

        /// <summary>How many people are on the server, from the line of "status" that counts them.</summary>
        public static int? HumansIn(string line)
        {
            var match = CountLine.Match(line);
            return match.Success && int.TryParse(match.Groups["humans"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var humans) ? humans : (int?)null;
        }

        /// <summary>
        /// A player, with their time on the server as the game prints it ("00:07", "48:58", "3:53:52"). The same player
        /// twice (the answer can come in twice) counts once.
        /// </summary>
        public void Add(string name, string steamId, string connected)
        {
            if (Players.Any(p => p.SteamId == steamId)) return;
            Players.Add(new KsfServerPlayer { Name = name, SteamId = steamId, ConnectedSeconds = Seconds(connected) });
        }

        /// <summary>The players, with their time on the server gone on to <paramref name="now"/>.</summary>
        public List<KsfServerPlayer> PlayersAt(DateTime now) => Players.Select(p => new KsfServerPlayer
        {
            Name = p.Name,
            SteamId = p.SteamId,
            ConnectedSeconds = p.ConnectedSeconds + (int)Math.Max(0, (now - At).TotalSeconds),
        }).ToList();

        /// <summary>"3:53:52" (or "48:58") in seconds; null if it isn't one.</summary>
        internal static int? Seconds(string connected)
        {
            if (string.IsNullOrEmpty(connected)) return null;
            var total = 0;
            foreach (var part in connected.Split(':'))
            {
                if (part.Length == 0 || part.Length > 4 || !int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return null;
                total = total * 60 + n;
            }
            return total;
        }
    }
}
