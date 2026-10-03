using System.Linq;

namespace KsfCompanion
{
    /// <summary>
    /// KSF's map groups. Below a map's top 10, players are in a group by how far down the leaderboard they are, as a
    /// share of everyone below the top 10: group 6 reaches two thirds of the way down, group 5 a third, and each group
    /// above that half as far as the one below it (1/6, 1/12, 1/24, 1/48). Groups 1 to 5 reach at least the 20th,
    /// 35th, 60th, 100th and 150th place, though - unless that's past the end of group 6: nobody further down is in a
    /// group. ksf.surf draws the cutoffs on each map's leaderboard page (GetGroupEndsAsync reads them from there); this
    /// is the rule they follow, for when the page can't be read - it gives the same ranks (surf_bugs: 10, 20, 35, 60,
    /// 100, 161 and 313 of 465; surf_utopia_njv: 10, 494, 978, 1,947, 3,883, 7,757 and 15,506 of 23,254).
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

        /// <summary>
        /// The last rank of the top 10 and of groups 1 to 6 on a leaderboard of <paramref name="total"/> players, the way
        /// ksf.surf lists them ("cutOffs"): [10, 20, 35, 60, 100, 161, 313] for 465. A group that ends where the one
        /// before it does is empty.
        /// </summary>
        public static int[] Ends(int total)
        {
            var ends = new int[Count + 1];
            for (var group = 0; group <= Count; group++) ends[group] = total <= 10 ? System.Math.Max(0, total) : End(group, total);
            return ends;
        }

        /// <summary>The first rank in a group (1-6, or 0 for the top 10); null when nobody can be in it yet.</summary>
        public static int? FirstRank(int group, int[] ends) =>
            LastRank(group, ends) == null ? (int?)null : group <= 0 ? 1 : ends[group - 1] + 1;

        /// <summary>The slowest rank still in a group (1-6, or 0 for the top 10); null when nobody can be in it (a short leaderboard).</summary>
        public static int? LastRank(int group, int[] ends)
        {
            if (ends == null || ends.Length <= Count || group > Count) return null;
            if (group <= 0) return ends[0] > 0 ? ends[0] : (int?)null;
            return ends[group] > ends[group - 1] ? ends[group] : (int?)null;
        }

        /// <summary>The group a rank is in: 0 = the top 10, 1-6, or null for none.</summary>
        public static int? Of(int rank, int[] ends)
        {
            if (rank <= 0) return null;
            for (var group = 0; group <= Count; group++)
                if (LastRank(group, ends) is int last && rank <= last) return group;
            return null;
        }

        public static int? FirstRank(int group, int total) => FirstRank(group, Ends(total));
        public static int? LastRank(int group, int total) => LastRank(group, Ends(total));
        public static int? Of(int rank, int total) => Of(rank, Ends(total));

        /// <summary>
        /// Cutoffs as ksf.surf lists them, if they make sense (no rank lower than the one before), as all 7: it leaves
        /// out the empty groups at the end ([10, 20, 35, 60, 80] - groups 5 and 6 end where group 4 does).
        /// </summary>
        public static int[] Checked(int[] ends)
        {
            if (ends == null || ends.Length == 0 || ends.Length > Count + 1 || ends[0] < 0 || ends.Zip(ends.Skip(1), (a, b) => b < a).Any(down => down)) return null;
            return ends.Concat(Enumerable.Repeat(ends[ends.Length - 1], Count + 1 - ends.Length)).ToArray();
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
        /// puts you (before you've finished: the easiest group there is; group 6 when you're in none). The groups are
        /// KSF's own cutoffs (<paramref name="ends"/>, from ksf.surf) or else worked out for a leaderboard of
        /// <paramref name="total"/>.
        /// </summary>
        public static GroupGoal For(MapReport r, int? picked, int total, int[] ends = null)
        {
            ends = KsfGroups.Checked(ends) ?? (total > 0 ? KsfGroups.Ends(total) : null);
            var me = r.Main;
            // Where ksf.surf has your best (a time you've just set isn't ranked yet: it's compared by time instead).
            int? ranked = me?.Time == null ? null
                : me.Group is int group && group >= 0 && group <= KsfGroups.Count ? group
                : me.Rank is int rank && ends != null ? KsfGroups.Of(rank, ends) : null;
            // Before you've finished it: the easiest group there is on this leaderboard.
            var easiest = Enumerable.Range(0, KsfGroups.Count + 1).Reverse().FirstOrDefault(g => KsfGroups.LastRank(g, ends) != null);
            var synced = me?.Time != null && !me.Unsynced;
            var goal = new GroupGoal
            {
                Group = picked ?? (ranked is int g ? System.Math.Max(0, g - 1) : me?.Time == null && ends != null ? easiest : KsfGroups.Count),
                Total = total,
                YourTime = me?.Time,
                YourRank = synced ? me.Rank : null,
                YourGroup = synced ? ranked : null,
            };
            goal.FirstRank = KsfGroups.FirstRank(goal.Group, ends);
            goal.LastRank = KsfGroups.LastRank(goal.Group, ends);
            return goal;
        }
    }
}
