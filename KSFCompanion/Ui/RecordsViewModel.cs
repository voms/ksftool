using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;

namespace KsfCompanion.Ui
{
    /// <summary>Map pictures already loaded (the last few hundred), so searching and filtering don't load them again.</summary>
    sealed class MapThumbs
    {
        const int Kept = 300;
        readonly Dictionary<string, Bitmap> thumbs = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        readonly Queue<string> order = new Queue<string>();

        public Bitmap Get(string map) => map != null && thumbs.TryGetValue(map, out var thumb) ? thumb : null;

        public void Put(string map, Bitmap thumb)
        {
            if (map == null || thumb == null) return;
            if (!thumbs.ContainsKey(map))
            {
                order.Enqueue(map);
                while (order.Count > Kept) thumbs.Remove(order.Dequeue());
            }
            thumbs[map] = thumb;
        }
    }

    /// <summary>One map on the records page: its picture and your record on it, as ksf.surf's records page has it.</summary>
    sealed class RecordRow : Observable
    {
        Bitmap thumb;
        bool isSaved;

        public string Map { get; set; }
        public string TierText { get; set; }
        public IBrush TierBrush { get; set; }
        public IBrush TierSoftBrush { get; set; }
        /// <summary>"staged  ·  6 stages  ·  2 bonuses"</summary>
        public string Kind { get; set; }
        /// <summary>"staged" or "linear", the list's map type column.</summary>
        public string Type { get; set; }
        public bool IsDone { get; set; }
        /// <summary>The pictures of maps you haven't finished yet are dimmed, like a collection with gaps in it.</summary>
        public double ThumbOpacity => IsDone ? 1 : 0.45;
        /// <summary>Your time ("1:02.937"); "" when you haven't finished it.</summary>
        public string Time { get; set; }
        /// <summary>"+3.297", or "WR" when the record is yours.</summary>
        public string WrDiff { get; set; }
        /// <summary>"WR", "#3", "G2" (group 2) or "#1,234"; "" when not done.</summary>
        public string Rank { get; set; } = "";
        public bool HasRank => !string.IsNullOrEmpty(Rank);
        public IBrush RankBrush { get; set; }
        public IBrush RankSoftBrush { get; set; }
        public string RankTip { get; set; }
        /// <summary>"1,580"</summary>
        public string Points { get; set; }
        public string Completions { get; set; }
        /// <summary>"1 day ago", "Aug 6, 2026"</summary>
        public string Date { get; set; }
        /// <summary>A tile's line under the name: "1,580 pts  ·  11 completions  ·  1 day ago", or what's done of an unfinished map.</summary>
        public string Summary { get; set; }
        /// <summary>The zone bars: the stages (on a linear map, the map itself), then the bonuses - '1' done, '0' not yet.</summary>
        public string StagePattern { get; set; }
        public string BonusPattern { get; set; }
        public bool HasBonuses => !string.IsNullOrEmpty(BonusPattern);
        public string ZonesTip { get; set; }
        /// <summary>The map you're playing right now.</summary>
        public bool IsCurrent { get; set; }
        public Bitmap Thumb { get => thumb; set => Set(ref thumb, value); }
        /// <summary>In your play-later list.</summary>
        public bool IsSaved
        {
            get => isSaved;
            set
            {
                if (Set(ref isSaved, value)) Raise(nameof(SaveTip));
            }
        }
        public string SaveTip => isSaved ? "In your play-later list (click to take it off)" : "Save it to your play-later list";
    }

    /// <summary>
    /// The records page: every KSF map with your record on it - your time, how far off the record, your rank or group,
    /// points, completions, when, and which stages and bonuses you've done - like your records page on ksf.surf, with the
    /// maps' pictures. Searched, filtered and sorted here.
    /// </summary>
    sealed class RecordsViewModel : Observable
    {
        const int PerPage = 60;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly IBrush WrBrush = Frozen("#F5C542"), WrSoftBrush = Frozen("#2EF5C542");
        static readonly IBrush TopBrush = Frozen("#FF9A45"), TopSoftBrush = Frozen("#26FF7A1A");
        static readonly IBrush GroupBrush = Frozen("#3DDC97"), GroupSoftBrush = Frozen("#223DDC97");
        static readonly IBrush PlaceBrush = Frozen("#B3B3BE"), PlaceSoftBrush = Frozen("#1CB3B3BE");

        readonly MapThumbs thumbs;
        List<MapRecord> records = new List<MapRecord>();
        HashSet<string> saved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string playing, search = "", sort = "points", show = "all", kind = "all", view = "tiles", status = "", heading = "", notice = "", updated = "";
        int tier, shown = PerPage, matching;
        bool loading, hasMore, hasRows, sortReversed;

        public RecordsViewModel(MapThumbs thumbs)
        {
            this.thumbs = thumbs;
            SetSortCommand = new RelayCommand(p => PickSort(p as string ?? "points"));
            FlipSortCommand = new RelayCommand(_ => SortReversed = !sortReversed);
            SetShowCommand = new RelayCommand(p => Show = p as string ?? "all");
            SetTierCommand = new RelayCommand(p => Tier = int.TryParse(p as string, NumberStyles.Integer, Inv, out var t) ? t : 0);
            SetKindCommand = new RelayCommand(p => Kind = p as string ?? "all");
            SetViewCommand = new RelayCommand(p => View = p as string == "list" ? "list" : "tiles");
            ShowMoreCommand = new RelayCommand(_ =>
            {
                shown += PerPage;
                Filter();
            });
            ClearSearchCommand = new RelayCommand(_ => Search = "");
        }

        public ObservableCollection<RecordRow> Rows { get; } = new ObservableCollection<RecordRow>();
        /// <summary>A map's mappers (from KSF's map list), so a search finds maps by who made them too.</summary>
        public Func<string, string> MappersOf { get; set; }
        /// <summary>Rows on show that have no picture yet: Companion loads them.</summary>
        public event Action<List<RecordRow>> ThumbsNeeded;

        /// <summary>Parameter: the order. The one that's on again turns it round.</summary>
        public ICommand SetSortCommand { get; }
        public ICommand FlipSortCommand { get; }
        public ICommand SetShowCommand { get; }
        public ICommand SetTierCommand { get; }
        public ICommand SetKindCommand { get; }
        public ICommand SetViewCommand { get; }
        public ICommand ShowMoreCommand { get; }
        public ICommand ClearSearchCommand { get; }
        /// <summary>Read the records from ksf.surf again (Companion's).</summary>
        public ICommand RefreshCommand { get; set; }

        public string Search
        {
            get => search;
            set
            {
                if (!Set(ref search, value ?? "")) return;
                Raise(nameof(HasSearch));
                Filter(fromStart: true);
            }
        }
        public bool HasSearch => search.Length > 0;
        /// <summary>"points" (the default, like ksf.surf), "rank", "time", "wrdiff", "completions", "date", "tier" or "name".</summary>
        public string Sort
        {
            get => sort;
            set
            {
                if (!Set(ref sort, value)) return;
                Raise(nameof(SortDirection));
                Filter(fromStart: true);
            }
        }
        /// <summary>The order the other way round: the worst first (fewest points, lowest rank, ...), Z-A.</summary>
        public bool SortReversed
        {
            get => sortReversed;
            set
            {
                if (!Set(ref sortReversed, value)) return;
                Raise(nameof(SortDirection));
                Filter(fromStart: true);
            }
        }
        /// <summary>Which way round the order is, in its own words: "Most first", "Worst first", "Z-A"...</summary>
        public string SortDirection => Direction(sort, sortReversed);

        internal static string Direction(string sort, bool reversed) => sort switch
        {
            "rank" => reversed ? "Worst first" : "Best first",
            "time" => reversed ? "Longest first" : "Shortest first",
            "wrdiff" => reversed ? "Furthest first" : "Closest first",
            "completions" => reversed ? "Fewest first" : "Most first",
            "date" => reversed ? "Oldest first" : "Newest first",
            "tier" => reversed ? "Hardest first" : "Easiest first",
            "name" => reversed ? "Z-A" : "A-Z",
            _ => reversed ? "Fewest first" : "Most first",
        };

        /// <summary>An order picked: the one that's on again turns it round; another starts the right way round (the best first).</summary>
        void PickSort(string value)
        {
            if (value == sort)
            {
                SortReversed = !sortReversed;
                return;
            }
            if (sortReversed)
            {
                sortReversed = false;
                Raise(nameof(SortReversed));
            }
            Sort = value;
        }
        /// <summary>"all", "done", "todo" (not finished) or "zones" (finished, with stages or bonuses still to do).</summary>
        public string Show { get => show; set { if (Set(ref show, value)) Filter(fromStart: true); } }
        public int Tier { get => tier; set { if (Set(ref tier, value)) Filter(fromStart: true); } }
        /// <summary>"all", "linear" or "staged".</summary>
        public string Kind { get => kind; set { if (Set(ref kind, value)) Filter(fromStart: true); } }
        public string View
        {
            get => view;
            set
            {
                if (!Set(ref view, value)) return;
                Raise(nameof(IsTileView));
                Raise(nameof(IsListView));
            }
        }
        public bool IsTileView => view != "list";
        public bool IsListView => view == "list";
        public string Status { get => status; set => Set(ref status, value); }
        /// <summary>Whose records, on which tick: "voms  ·  66T".</summary>
        public string Heading { get => heading; set => Set(ref heading, value); }
        /// <summary>When the records were read from ksf.surf: "updated 15:42".</summary>
        public string Updated { get => updated; set => Set(ref updated, value); }
        /// <summary>Instead of the maps, when there are none to show: still loading, no Steam account, ksf.surf not reached.</summary>
        public string Notice
        {
            get => notice;
            set
            {
                if (!Set(ref notice, value ?? "")) return;
                Raise(nameof(HasNotice));
            }
        }
        public bool HasNotice => notice.Length > 0;
        public bool IsLoading { get => loading; set => Set(ref loading, value); }
        public bool HasMore { get => hasMore; set => Set(ref hasMore, value); }
        public bool HasRows { get => hasRows; set => Set(ref hasRows, value); }

        /// <summary>The records (every KSF map); <paramref name="loading"/> while a newer list is on its way.</summary>
        public void SetRecords(List<MapRecord> list, string heading, DateTime? readAt, bool loading)
        {
            var changed = list != null && !ReferenceEquals(list, records);
            if (list != null) records = list;
            Heading = heading ?? "";
            Updated = readAt is DateTime at ? "updated " + at.ToString("HH:mm", Inv) : "";
            IsLoading = loading;
            if (changed) Filter();
            else UpdateStatus();
        }

        /// <summary>Your play-later maps (a filled star) and the map you're on.</summary>
        public void SetContext(ICollection<string> savedMaps, string currentMap)
        {
            saved = new HashSet<string>(savedMaps, StringComparer.OrdinalIgnoreCase);
            if (!string.Equals(playing, currentMap, StringComparison.OrdinalIgnoreCase))
            {
                playing = currentMap;
                Filter();
                return;
            }
            foreach (var row in Rows) row.IsSaved = saved.Contains(row.Map);
        }

        public void SetThumb(RecordRow row, Bitmap thumb)
        {
            if (thumb == null) return;
            thumbs.Put(row.Map, thumb);
            row.Thumb = thumb;
            // The list may have been rebuilt (search, filter) while the picture loaded.
            foreach (var other in Rows.Where(r => r != row && r.Thumb == null && string.Equals(r.Map, row.Map, StringComparison.OrdinalIgnoreCase)))
                other.Thumb = thumb;
        }

        void Filter(bool fromStart = false)
        {
            if (fromStart) shown = PerPage;
            var words = MapMatch.Words(search);
            IEnumerable<MapRecord> list = records;
            if (tier > 0) list = list.Where(r => r.Tier == tier);
            if (kind == "linear") list = list.Where(r => r.IsLinear);
            else if (kind == "staged") list = list.Where(r => !r.IsLinear);
            list = show switch
            {
                "done" => list.Where(r => r.IsDone),
                "todo" => list.Where(r => !r.IsDone),
                "zones" => list.Where(r => r.IsDone && (r.Stages.Contains(false) || r.Bonuses.Contains(false))),
                _ => list,
            };
            Dictionary<MapRecord, int> scores = null;
            if (words.Length > 0)
            {
                scores = new Dictionary<MapRecord, int>();
                foreach (var r in list)
                    if (MapMatch.Score(r.Map, MappersOf?.Invoke(r.Map), words) is int score) scores[r] = score;
                // Maps that match exactly hide the ones that are only close (a letter off).
                if (scores.Values.Any(s => s < MapMatch.Close))
                    foreach (var close in scores.Where(s => s.Value >= MapMatch.Close).Select(s => s.Key).ToList()) scores.Remove(close);
                list = list.Where(scores.ContainsKey);
            }
            var ordered = Sorted(list, sort, sortReversed);
            // Searching: the best matches first, in the order picked among themselves.
            var sorted = (scores == null ? ordered : ordered.OrderBy(r => scores[r])).ToList();

            Rows.Clear();
            foreach (var record in sorted.Take(shown)) Rows.Add(ToRow(record));
            matching = sorted.Count;
            HasMore = sorted.Count > shown;
            HasRows = Rows.Count > 0;
            UpdateStatus();
            var missing = Rows.Where(r => r.Thumb == null).ToList();
            if (missing.Count > 0) ThumbsNeeded?.Invoke(missing);
        }

        /// <summary>
        /// The maps in an order, the best first (or, <paramref name="reversed"/>, the worst first). In the orders by your
        /// record the maps you haven't finished come after the rest, either way round.
        /// </summary>
        internal static IEnumerable<MapRecord> Sorted(IEnumerable<MapRecord> list, string sort, bool reversed = false)
        {
            var byName = StringComparer.OrdinalIgnoreCase;
            switch (sort)
            {
                case "name": return reversed ? list.OrderByDescending(r => r.Map, byName) : list.OrderBy(r => r.Map, byName);
                case "tier": return (reversed ? list.OrderByDescending(r => r.Tier) : list.OrderBy(r => r.Tier)).ThenBy(r => r.Map, byName);
            }
            var done = list.Where(r => r.IsDone);
            var notDone = list.Where(r => !r.IsDone).OrderBy(r => r.Map, byName);
            // The best first: low for some orders (a rank, a time), high for others (points).
            IOrderedEnumerable<MapRecord> By<T>(Func<MapRecord, T> key, bool lowIsBest) => lowIsBest != reversed ? done.OrderBy(key) : done.OrderByDescending(key);
            var ordered = sort switch
            {
                // Within a group, the most points first (the fewest, the other way round).
                "rank" => reversed ? By(Standing, true).ThenBy(r => r.Points ?? 0) : By(Standing, true).ThenByDescending(r => r.Points ?? 0),
                "time" => By(r => r.Time ?? 0, true),
                "wrdiff" => By(r => r.WrDiff ?? double.MaxValue, true),
                "completions" => By(r => r.Completions ?? 0, false),
                "date" => By(r => r.Date ?? DateTime.MinValue, false),
                _ => By(r => r.Points ?? 0, false),
            };
            return ordered.ThenBy(r => r.Map, byName).Concat(notDone);
        }

        /// <summary>How high you are on a map: the top 10 by place, then the groups, then places below them.</summary>
        static int Standing(MapRecord r) => r.Rank is int rank && rank <= 10 ? rank : r.Group is int group ? 100 + group : r.Rank is int place ? 1000 + place : int.MaxValue;

        void UpdateStatus()
        {
            if (records.Count == 0)
            {
                Status = loading ? "Getting your records from ksf.surf..." : "";
                return;
            }
            var filtered = search.Trim().Length > 0 || tier > 0 || kind != "all" || show != "all";
            var done = records.Count(r => r.IsDone);
            var wrs = records.Count(r => r.Rank == 1);
            var top10 = records.Count(r => r.Rank <= 10);
            Status = (filtered ? $"{matching:N0} of {records.Count:N0} maps" : $"{records.Count:N0} maps")
                     + $"  ·  {done:N0} done"
                     + (wrs > 0 ? $"  ·  {wrs:N0} {(wrs == 1 ? "WR" : "WRs")}" : "")
                     + (top10 > 0 ? $"  ·  {top10:N0} in the top 10" : "")
                     + (loading ? "  ·  updating..." : "");
        }

        RecordRow ToRow(MapRecord r)
        {
            var stages = r.IsLinear || r.Stages.Length == 0 ? (r.IsDone ? "1" : "0") : Pattern(r.Stages);
            var bonuses = Pattern(r.Bonuses);
            var row = new RecordRow
            {
                Map = r.Map,
                TierText = r.Tier > 0 ? "T" + r.Tier.ToString(Inv) : "T?",
                TierBrush = DashboardViewModel.TierColor(r.Tier),
                TierSoftBrush = DashboardViewModel.TierSoft(r.Tier),
                Type = r.IsLinear ? "linear" : "staged",
                Kind = (r.IsLinear ? "linear" : r.StageCount == 1 ? "1 stage" : $"{r.StageCount} stages") + "  ·  "
                       + (r.BonusCount == 0 ? "no bonuses" : r.BonusCount == 1 ? "1 bonus" : $"{r.BonusCount} bonuses"),
                IsDone = r.IsDone,
                Time = r.Time is double time ? Format.Time(time) : "",
                WrDiff = !r.IsDone ? "" : r.Rank == 1 || r.WrDiff is double zero && zero <= 0 ? "WR" : r.WrDiff is double diff ? "+" + Format.Short(diff) : "",
                Points = r.IsDone && r.Points is double points ? Math.Round(points, MidpointRounding.AwayFromZero).ToString("N0", Inv) : "",
                Completions = r.IsDone && r.Completions is int count ? count.ToString("N0", Inv) : "",
                Date = r.IsDone && r.Date is DateTime date ? When(date) : "",
                StagePattern = stages,
                BonusPattern = bonuses,
                ZonesTip = ZonesTip(r),
                IsCurrent = string.Equals(r.Map, playing, StringComparison.OrdinalIgnoreCase),
                IsSaved = saved.Contains(r.Map),
                Thumb = thumbs.Get(r.Map),
            };
            if (r.Rank == 1) SetRank(row, "WR", WrBrush, WrSoftBrush, "The world record is yours");
            else if (r.Rank is int rank && rank <= 10) SetRank(row, "#" + rank.ToString(Inv), TopBrush, TopSoftBrush, $"{Ordinal(rank)} on the leaderboard (the top 10)");
            else if (r.Group is int group) SetRank(row, "G" + group.ToString(Inv), GroupBrush, GroupSoftBrush, $"Group {group}");
            else if (r.Rank is int place) SetRank(row, "#" + place.ToString("N0", Inv), PlaceBrush, PlaceSoftBrush, $"{Ordinal(place)} on the leaderboard");
            row.Summary = r.IsDone
                ? string.Join("  ·  ", new[]
                  {
                      row.Points.Length > 0 ? row.Points + " pts" : null,
                      r.Completions is int c ? (c == 1 ? "1 completion" : $"{c:N0} completions") : null,
                      row.Date.Length > 0 ? row.Date : null,
                  }.Where(x => x != null))
                : r.Stages.Contains(true) || r.Bonuses.Contains(true) ? "not finished  ·  " + ZonesDone(r) : "not finished yet";
            return row;
        }

        static void SetRank(RecordRow row, string text, IBrush brush, IBrush soft, string tip)
        {
            row.Rank = text;
            row.RankBrush = brush;
            row.RankSoftBrush = soft;
            row.RankTip = tip;
        }

        static string Pattern(bool[] done) => new string(done.Select(d => d ? '1' : '0').ToArray());

        /// <summary>"2 of 5 stages, 1 of 3 bonuses done"</summary>
        static string ZonesDone(MapRecord r)
        {
            var parts = new List<string>();
            if (!r.IsLinear && r.Stages.Length > 0) parts.Add($"{r.Stages.Count(d => d)} of {r.Stages.Length} stages");
            if (r.Bonuses.Length > 0) parts.Add($"{r.Bonuses.Count(d => d)} of {r.Bonuses.Length} {(r.Bonuses.Length == 1 ? "bonus" : "bonuses")}");
            return parts.Count == 0 ? "" : string.Join(", ", parts) + " done";
        }

        static string ZonesTip(MapRecord r)
        {
            var map = r.IsLinear ? (r.IsDone ? "Map done" : "Map not done yet") : null;
            var zones = ZonesDone(r);
            return string.Join("  ·  ", new[] { map, zones.Length > 0 ? zones : null }.Where(x => x != null));
        }

        /// <summary>Like ksf.surf: "today", "1 day ago", "12 days ago", then the date ("Aug 6, 2026").</summary>
        internal static string When(DateTime date)
        {
            var days = (DateTime.Now.Date - date.Date).Days;
            return days <= 0 ? "today" : days == 1 ? "1 day ago" : days < 30 ? $"{days} days ago" : date.ToString("MMM d, yyyy", Inv);
        }

        static string Ordinal(int n) =>
            n.ToString("N0", Inv) + (n % 100 is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });

        static IBrush Frozen(string hex) => new ImmutableSolidColorBrush(Color.Parse(hex));
    }
}
