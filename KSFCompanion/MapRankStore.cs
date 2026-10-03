using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace KsfCompanion
{
    /// <summary>
    /// Your place on the maps you've finished, for the records page. ksf.surf's records page only gives your group below
    /// the top 10, so a map's place is read from its own leaderboard - a request a map - and kept here on disk: read once,
    /// then again when your time on the map changes, or after a few days (others pass you).
    /// </summary>
    sealed class MapRankStore
    {
        public sealed class Entry
        {
            public DateTime At;
            /// <summary>Your time when the place was read: another time on the map means another place.</summary>
            public double Time;
            public int Rank;
            /// <summary>How many players have a time on the map.</summary>
            public int? Players;
        }

        static readonly TimeSpan KeepOnDisk = TimeSpan.FromDays(60);
        readonly string path;
        readonly object gate = new object();
        // key: player|game|style (FinishedMaps.Key), tab, map
        readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        public MapRankStore(string path)
        {
            this.path = path;
            try
            {
                if (!File.Exists(path)) return;
                foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    // player|game|style, map, saved (UTC ticks), time, rank, players
                    var f = line.Split('\t');
                    if (f.Length < 6 || !MapNames.IsValid(f[1])
                        || !long.TryParse(f[2], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) || ticks <= 0 || ticks > DateTime.MaxValue.Ticks
                        || !double.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var time)
                        || !int.TryParse(f[4], NumberStyles.None, CultureInfo.InvariantCulture, out var rank) || rank < 1) continue;
                    var at = new DateTime(ticks, DateTimeKind.Utc).ToLocalTime();
                    if (DateTime.Now - at > KeepOnDisk) continue;
                    entries[f[0] + "\t" + f[1]] = new Entry
                    {
                        At = at,
                        Time = time,
                        Rank = rank,
                        Players = int.TryParse(f[5], NumberStyles.None, CultureInfo.InvariantCulture, out var players) ? players : (int?)null,
                    };
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public Entry Get(string key, string map)
        {
            lock (gate) return key != null && map != null && entries.TryGetValue(key + "\t" + map, out var e) ? e : null;
        }

        public void Put(string key, string map, double time, int rank, int? players)
        {
            if (key == null || !MapNames.IsValid(map)) return;
            lock (gate) entries[key + "\t" + map] = new Entry { At = DateTime.Now, Time = time, Rank = rank, Players = players };
        }

        /// <summary>
        /// The same run: the records page and a map's leaderboard can give its time a little differently (6 decimals, or
        /// the float it's stored as). A new best is a different run - far more than this apart.
        /// </summary>
        public static bool SameTime(double a, double b) => Math.Abs(a - b) <= Math.Max(0.001, Math.Max(Math.Abs(a), Math.Abs(b)) * 1e-6);

        public void Save()
        {
            string[] lines;
            lock (gate)
                lines = entries.Where(e => DateTime.Now - e.Value.At < KeepOnDisk).Select(e => string.Join("\t",
                    e.Key,
                    e.Value.At.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),
                    e.Value.Time.ToString("R", CultureInfo.InvariantCulture),
                    e.Value.Rank.ToString(CultureInfo.InvariantCulture),
                    e.Value.Players?.ToString(CultureInfo.InvariantCulture) ?? "")).ToArray();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var temp = path + ".tmp";
                File.WriteAllLines(temp, lines, new UTF8Encoding(false));
                File.Move(temp, path, overwrite: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
