namespace KsfCompanion
{
    /// <summary>
    /// KSF's map groups. Below a map's top 10, players are in a group by how far down the leaderboard they are, as a
    /// share of everyone below the top 10: group 6 reaches two thirds of the way down, group 5 a third, and each group
    /// above that half as far as the one below it (1/6, 1/12, 1/24, 1/48). Groups 1 to 5 reach at least the 20th,
    /// 35th, 60th, 100th and 150th place, though - unless that's past the end of group 6: nobody further down is in a
    /// group. Worked out from the groups ksf.surf gives players on maps 159 to 23,254 players have finished (October
    /// 2026); KSF doesn't publish the rule anywhere but the in-game !mi.
    /// </summary>
    static class KsfGroups
    {
        public const int Count = 6;

        /// <summary>
        /// How far each group reaches below the top 10, as a share (numerator / denominator) of the players there; and
        /// whether a player right on it is in (on surf_utopia_njv, with 23,244 below the top 10, the 1,937th of them - a
        /// twelfth exactly - is in group 3, and the 15,496th - two thirds - in group 6, but the 3,874th and the 7,748th,
        /// a sixth and a third, are in the next group).
        /// </summary>
        static readonly (int Num, int Den, bool Inclusive)[] Shares =
            { (1, 48, false), (1, 24, false), (1, 12, true), (1, 6, false), (1, 3, false), (2, 3, true) };

        /// <summary>The place groups 1 to 5 reach at least, however few have finished the map.</summary>
        static readonly int[] AtLeast = { 20, 35, 60, 100, 150 };

        /// <summary>The first rank in a group (1-6, or 0 for the top 10); null when nobody can be in it yet.</summary>
        public static int? FirstRank(int group, int total) =>
            LastRank(group, total) == null ? (int?)null : group <= 0 ? 1 : End(group - 1, total) + 1;

        /// <summary>
        /// The slowest rank still in a group (1-6, or 0 for the top 10) on a leaderboard of <paramref name="total"/>
        /// players; null when nobody can be in it (a short leaderboard).
        /// </summary>
        public static int? LastRank(int group, int total)
        {
            if (group <= 0) return total > 0 ? System.Math.Min(10, total) : (int?)null;
            if (group > Count || total <= 10) return null;
            int end = End(group, total), before = End(group - 1, total);
            return end > before ? end : (int?)null;
        }

        /// <summary>The group a rank is in on a leaderboard of <paramref name="total"/>: 0 = the top 10, 1-6, or null for none.</summary>
        public static int? Of(int rank, int total)
        {
            if (rank <= 0) return null;
            if (rank <= 10) return 0;
            for (var group = 1; group <= Count; group++)
                if (LastRank(group, total) is int last && rank <= last) return group;
            return null;
        }

        /// <summary>The last rank in groups 1 to <paramref name="group"/> (10 for none of them: the top 10).</summary>
        static int End(int group, int total)
        {
            if (group <= 0 || total <= 10) return 10;
            var below = total - 10;
            // Group 6 ends at two thirds of the way down, and nobody past that is in a group.
            var last = 10 + Share(Count, below);
            if (group >= Count) return last;
            return System.Math.Max(End(group - 1, total), System.Math.Min(last, System.Math.Max(10 + Share(group, below), AtLeast[group - 1])));
        }

        /// <summary>How many of the <paramref name="below"/> players under the top 10 a group's share reaches - in whole numbers, so it's exact.</summary>
        static int Share(int group, int below)
        {
            var (num, den, inclusive) = Shares[group - 1];
            return (int)(((long)num * below - (inclusive ? 0 : 1)) / den);
        }
    }

    /// <summary>What the group tile shows: a group (or the top 10) on the map you're on, and what it takes to get in.</summary>
    sealed class GroupGoal
    {
        /// <summary>0 = the top 10, 1-6 = KSF's groups.</summary>
        public int Group;
        /// <summary>The ranks in it (null: nobody can be in it yet).</summary>
        public int? FirstRank, LastRank;
        /// <summary>Players on the map's leaderboard (0 = not known).</summary>
        public int Total;
        /// <summary>The time at LastRank: beat it and you're in (null until it's looked up).</summary>
        public double? Cutoff;
        public double? YourTime;
        public int? YourRank;
        /// <summary>Your group now: 0 = the top 10, 1-6, null = none.</summary>
        public int? YourGroup;
        public bool Loading;

        /// <summary>The time at the end of the group is needed: there is an end, and your best isn't in it already.</summary>
        public bool NeedsCutoff => LastRank is int last && !(YourRank <= last);

        /// <summary>The group_goal setting: 0 for the top 10, 1-6, or null for auto (the next one up from yours).</summary>
        public static int? Picked(string setting)
        {
            if (string.Equals(setting, "top10", System.StringComparison.OrdinalIgnoreCase) || setting == "0") return 0;
            return int.TryParse(setting, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var group)
                && group >= 1 && group <= KsfGroups.Count ? group : (int?)null;
        }

        /// <summary>
        /// The group tile for a map: the group <paramref name="picked"/>, or else the next one up from where your best
        /// puts you (group 6 before you've finished, or when you're in none), on a leaderboard of <paramref name="total"/>.
        /// </summary>
        public static GroupGoal For(MapReport r, int? picked, int total)
        {
            var me = r.Main;
            // Where ksf.surf has your best (a time you've just set isn't ranked yet: it's compared by time instead).
            int? ranked = me?.Time == null ? null
                : me.Group is int group && group >= 0 && group <= KsfGroups.Count ? group
                : me.Rank is int rank && total > 0 ? KsfGroups.Of(rank, total) : null;
            var goal = new GroupGoal
            {
                Group = picked ?? (ranked is int g ? System.Math.Max(0, g - 1) : KsfGroups.Count),
                Total = total,
                YourTime = me?.Time,
                YourRank = me?.Unsynced == true ? null : me?.Rank,
                YourGroup = me?.Unsynced == true ? null : ranked,
            };
            goal.FirstRank = KsfGroups.FirstRank(goal.Group, total);
            goal.LastRank = KsfGroups.LastRank(goal.Group, total);
            return goal;
        }
    }
}
