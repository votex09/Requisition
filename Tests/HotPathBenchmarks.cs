using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using TerraStorage.Common;
using TerraStorage.Helpers.Resolver;

namespace TerraStorage.Tests
{
    // Speed and allocation of the item-movement and panel-refresh paths.
    //
    // StackSelection and CraftingTransaction are linked into this project, so those measurements
    // run the SHIPPED code. The surrounding shells that need Terraria (DiskData's Items list,
    // StorageWorldSystem's disk dictionary, the defragment loop in StorageWorldSystem.Defragment)
    // are replicated here with the same structure, the same allocation sites and the same loop
    // nesting, so the cost being measured is the real one.
    public static class HotPathBenchmarks
    {
        // ---- Replica of Common/StoredItemStack.cs: reference type, same fields ----
        private sealed class Stack
        {
            public int ItemType;
            public int StackCount;
            public int PrefixId;
            public long InsertionOrder;
            public object ModData;

            // Stands in for FullItemTag's "globalData" blob. Every stack in a modded world carries
            // one - issue 24 counted 191 of 191 against a real save - so the merge rule's byte
            // comparison runs for real here rather than being skipped.
            public Dictionary<string, object> ModState;

            public bool Matches(int itemType, int prefixId = -1)
                => ItemType == itemType && (prefixId == -1 || PrefixId == prefixId);
        }

        // ---- Replica of Common/DiskData.cs: the parts the measured paths touch ----
        private sealed class Disk
        {
            public readonly List<Stack> Items = new();
            public int MaxStacks;
            public int UsedStacks => Items.Count;
            public bool IsFull => UsedStacks >= MaxStacks;
        }

        // StackIdentity.IsUnique's rule, not "carries a tag": issue 24 removed the latter, and with
        // a ModState blob on every stack it would make every stack its own item and merge nothing.
        private static bool HasPerInstanceData(Stack s) => s.ModData != null;

        // Replica of DiskData.CanMergeStacks - StoredItemStack.StacksWith and then the mod-state
        // byte comparison. Uniqueness in this fixture always comes from ModData, so StacksWith's
        // last branch (ItemLoader.CanStack, which needs Terraria) is unreachable here.
        private static bool CanMergeStacks(Stack a, Stack b)
            => StacksWith(a, b) && ModStateMatches(a.ModState, b.ModState);

        private static bool StacksWith(Stack a, Stack b)
        {
            if (a.ItemType != b.ItemType || a.PrefixId != b.PrefixId)
                return false;

            if (!HasPerInstanceData(a) && !HasPerInstanceData(b))
                return true;

            return false;
        }

        private const string GlobalDataKey = "globalData";

        // Replica of DiskData.ModStateMatches (Common/DiskData.cs:328). This is the comparison the
        // index does NOT remove - it prunes the pairs that exit on the type check and keeps every
        // pair that reaches here - so modelling it as free would flatter the result.
        private static bool ModStateMatches(Dictionary<string, object> first, Dictionary<string, object> second)
        {
            if (ReferenceEquals(first, second))
                return true;

            bool firstHas = first != null && first.ContainsKey(GlobalDataKey);
            bool secondHas = second != null && second.ContainsKey(GlobalDataKey);
            if (firstHas != secondHas)
                return false;
            if (!firstHas)
                return true;

            return TagValueEquals(first[GlobalDataKey], second[GlobalDataKey]);
        }

        // Replica of DiskData.TagCompoundEquals (Common/DiskData.cs:282).
        private static bool TagCompoundEquals(Dictionary<string, object> a, Dictionary<string, object> b)
        {
            if (a.Count != b.Count) return false;
            foreach (var pair in a)
            {
                if (!b.ContainsKey(pair.Key)) return false;
                if (!TagValueEquals(pair.Value, b[pair.Key])) return false;
            }
            return true;
        }

        // Replica of DiskData.TagValueEquals (Common/DiskData.cs:293).
        private static bool TagValueEquals(object a, object b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            if (a.GetType() != b.GetType()) return false;

            if (a is Dictionary<string, object> nestedA && b is Dictionary<string, object> nestedB)
                return TagCompoundEquals(nestedA, nestedB);

            // The shipped rule uses LINQ SequenceEqual here, whose enumerators allocate.
            if (a is byte[] bytesA && b is byte[] bytesB)
                return bytesA.SequenceEqual(bytesB);

            if (a is int[] intsA && b is int[] intsB)
                return intsA.SequenceEqual(intsB);

            return a.Equals(b);
        }

        private static double MsOf(Action body, int iterations)
        {
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++) body();
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds / iterations;
        }

        public static void Run(Action<bool, string> check)
        {
            ExtractBenchmark(check);
            DefragmentBenchmark(check);
            CraftingTransactionBenchmark(check);

            int recipeCount = ReadDumpRecipeCount();
            SyncScratchDictionaryBenchmark(check, recipeCount);
            SearchKeystrokeBenchmark(check, recipeCount);
        }

        // The real dump's header line is "# numRecipes=14178 itemCount=... storedTypes=...".
        // Falls back to a representative heavy-modpack count when no dump is present.
        private static int ReadDumpRecipeCount()
        {
            string dump = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "My Games", "Terraria", "tModLoader", "ts_recipe_dump.txt");
            if (!System.IO.File.Exists(dump)) return 14178;

            foreach (string line in System.IO.File.ReadLines(dump))
            {
                int at = line.IndexOf("numRecipes=", StringComparison.Ordinal);
                if (at < 0) continue;
                int start = at + "numRecipes=".Length;
                int end = start;
                while (end < line.Length && char.IsDigit(line[end])) end++;
                if (end > start && int.TryParse(line.Substring(start, end - start), out int n)) return n;
                break;
            }
            return 14178;
        }

        // ================================================================================
        // 4. UICraftingPanel.SyncFilteredRecipesIncremental (UICraftingPanel.cs:709)
        //      var desired = new Dictionary<Recipe, bool>(_allRecipes.Count);
        //    Called from the deferred recursive pass every RecursiveSyncThrottleFrames = 12 frames
        //    (~5x/sec) and at completion. Recipe is a reference type, so Dictionary<object, bool>
        //    at the same capacity has the same allocation shape.
        // ================================================================================
        private static void SyncScratchDictionaryBenchmark(Action<bool, string> check, int recipeCount)
        {
            Console.WriteLine("\n-- SyncFilteredRecipesIncremental: pre-sized scratch Dictionary (UICraftingPanel.cs:709)");
            Console.WriteLine("   recipes | fresh B/call | reused+Clear B/call | LOH? | fresh MB/s @5 calls/s | gen2");
            Console.WriteLine("   --------|--------------|---------------------|------|-----------------------|-----");

            int maxCount = Math.Max(recipeCount, 20000);
            var keys = new object[maxCount];
            for (int i = 0; i < maxCount; i++) keys[i] = new object();

            long freshAtRealScale = 0;

            foreach (int count in new[] { 5000, recipeCount, 20000 })
            {
                // Only a fraction of recipes pass the filters and get inserted, but the capacity —
                // and therefore the allocation — is paid in full regardless.
                int inserted = count / 4;

                Action fresh = () =>
                {
                    var desired = new Dictionary<object, bool>(count);
                    for (int i = 0; i < inserted; i++) desired[keys[i]] = true;
                    GC.KeepAlive(desired);
                };

                var reusedScratch = new Dictionary<object, bool>(count);
                Action reused = () =>
                {
                    reusedScratch.Clear();
                    for (int i = 0; i < inserted; i++) reusedScratch[keys[i]] = true;
                };

                fresh(); reused(); // warm + JIT

                const int iters = 50;

                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                int gen2Before = GC.CollectionCount(2);
                long a0 = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < iters; i++) fresh();
                long freshBytes = (GC.GetAllocatedBytesForCurrentThread() - a0) / iters;
                int gen2 = GC.CollectionCount(2) - gen2Before;

                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                long a1 = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < iters; i++) reused();
                long reusedBytes = (GC.GetAllocatedBytesForCurrentThread() - a1) / iters;

                // The CLR allocates anything >= 85,000 bytes on the Large Object Heap, which is not
                // compacted by default and is only reclaimed by a gen2 collection.
                bool loh = freshBytes >= 85000;
                Console.WriteLine($"   {count,7} | {freshBytes,10} B | {reusedBytes,17} B | {(loh ? "YES" : "no"),4} | {freshBytes * 5 / 1048576.0,18:0.00} MB/s | {gen2,4}");

                if (count == recipeCount) freshAtRealScale = freshBytes;
            }

            Console.WriteLine();
            Console.WriteLine($"   At the dump's {recipeCount} recipes the scratch dictionary is {freshAtRealScale / 1024.0:0.0} KB per call, above the");
            Console.WriteLine("   85,000 B LOH threshold. Reusing one instance and calling Clear() keeps the arrays");
            Console.WriteLine("   and drops it to 0 B, turning a gen2-inducing LOH churn into nothing.");

            check(freshAtRealScale >= 85000,
                $"the pre-sized scratch dictionary is an LOH allocation at real scale ({freshAtRealScale} B)");
        }

        // ================================================================================
        // 5. UICraftingPanel.FilterRecipes (UICraftingPanel.cs:641), run on EVERY keystroke via
        //    SetSearchText (:241). Fresh partition List (:648), a Comparison<> lambda rebuilt per
        //    call (:676), two Sorts, and ItemSearchHelper.Matches re-parsing the search per item.
        // ================================================================================
        private enum SearchMode { Name, Tooltip, Mod }
        private enum RecipeSortMode { ID, Name }

        // Faithful copy of ItemSearchHelper.Parse (Content/UI/Elements/ItemSearchHelper.cs:21).
        private static (SearchMode mode, string query) ParseSearch(string search)
        {
            if (search != null && search.StartsWith("#")) return (SearchMode.Tooltip, search.Substring(1));
            if (search != null && search.StartsWith("@")) return (SearchMode.Mod, search.Substring(1));
            return (SearchMode.Name, search ?? "");
        }

        private static void SearchKeystrokeBenchmark(Action<bool, string> check, int recipeCount)
        {
            Console.WriteLine("\n-- FilterRecipes: cost of ONE keystroke in the crafting search box (UICraftingPanel.cs:641)");

            // Stand-in for TerminalUIState._nameCache, pre-warmed as it is in play (a
            // Dictionary<int,string> hit, which is what the real comparison does).
            var rng = new Random(17);
            string[] parts = { "Copper", "Iron", "Gold", "Shadow", "Molten", "Terra", "Spectre", "Chlorophyte",
                               "Sword", "Pickaxe", "Helmet", "Greaves", "Potion", "Bar", "Block", "Wall",
                               "Crimson", "Hallowed", "Solar", "Vortex", "Nebula", "Stardust" };
            var nameCache = new Dictionary<int, string>(recipeCount);
            for (int i = 0; i < recipeCount; i++)
                nameCache[i] = parts[rng.Next(parts.Length)] + " " + parts[rng.Next(parts.Length)] + " " + i;

            var favorited = new HashSet<int>();
            var rngFav = new Random(3);
            for (int i = 0; i < 20; i++) favorited.Add(rngFav.Next(recipeCount));

            var canCraft = new bool[recipeCount];
            for (int i = 0; i < recipeCount; i++) canCraft[i] = i % 3 == 0;

            // _filteredRecipes is a long-lived field, so it is reused in BOTH variants; only
            // `regular` is freshly allocated by the current code.
            var filtered = new List<(int type, bool canCraft)>();
            var reusedRegular = new List<(int type, bool canCraft)>();

            Action<RecipeSortMode, string> current = (sortMode, search) =>
            {
                filtered.Clear();
                var regular = new List<(int type, bool canCraft)>();   // UICraftingPanel.cs:648
                bool hasSearch = !string.IsNullOrEmpty(search);

                for (int i = 0; i < recipeCount; i++)
                {
                    // ItemSearchHelper.Matches re-parses the (constant) search string per item.
                    if (hasSearch)
                    {
                        var (_, query) = ParseSearch(search);
                        if (query.Length > 0 && !nameCache[i].Contains(query, StringComparison.OrdinalIgnoreCase))
                            continue;
                    }
                    if (favorited.Contains(i)) filtered.Add((i, canCraft[i]));
                    else regular.Add((i, canCraft[i]));
                }

                int dir = 1;
                Comparison<(int type, bool canCraft)> sort = (a, b) =>   // UICraftingPanel.cs:676
                {
                    if (a.canCraft != b.canCraft) return a.canCraft ? -1 : 1;
                    int result = sortMode == RecipeSortMode.Name
                        ? string.Compare(nameCache[a.type], nameCache[b.type], StringComparison.OrdinalIgnoreCase)
                        : a.type.CompareTo(b.type);
                    return result * dir;
                };
                filtered.Sort(sort);
                regular.Sort(sort);
                filtered.AddRange(regular);
            };

            Action<RecipeSortMode, string> fixedVersion = (sortMode, search) =>
            {
                filtered.Clear();
                reusedRegular.Clear();
                bool hasSearch = !string.IsNullOrEmpty(search);
                var (_, query) = ParseSearch(search);   // ONCE, not per item

                for (int i = 0; i < recipeCount; i++)
                {
                    if (hasSearch && query.Length > 0
                        && !nameCache[i].Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                    if (favorited.Contains(i)) filtered.Add((i, canCraft[i]));
                    else reusedRegular.Add((i, canCraft[i]));
                }

                int dir = 1;
                Comparison<(int type, bool canCraft)> sort = (a, b) =>
                {
                    if (a.canCraft != b.canCraft) return a.canCraft ? -1 : 1;
                    int result = sortMode == RecipeSortMode.Name
                        ? string.Compare(nameCache[a.type], nameCache[b.type], StringComparison.OrdinalIgnoreCase)
                        : a.type.CompareTo(b.type);
                    return result * dir;
                };
                filtered.Sort(sort);
                reusedRegular.Sort(sort);
                filtered.AddRange(reusedRegular);
            };

            Console.WriteLine("   sort | search    | current ms | current B  | fixed ms | fixed B | gen2 (cur)");
            Console.WriteLine("   -----|-----------|------------|------------|----------|---------|-----------");

            double worstMs = 0;
            long worstBytes = 0;
            string worstLabel = "";

            foreach (var sortMode in new[] { RecipeSortMode.ID, RecipeSortMode.Name })
            {
                foreach (string search in new[] { "", "copper", "#bait", "@terraria" })
                {
                    current(sortMode, search); fixedVersion(sortMode, search); // warm + JIT

                    const int iters = 20;

                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    int g2 = GC.CollectionCount(2);
                    long a0 = GC.GetAllocatedBytesForCurrentThread();
                    var sw = Stopwatch.StartNew();
                    for (int i = 0; i < iters; i++) current(sortMode, search);
                    sw.Stop();
                    long curBytes = (GC.GetAllocatedBytesForCurrentThread() - a0) / iters;
                    double curMs = sw.Elapsed.TotalMilliseconds / iters;
                    int gen2 = GC.CollectionCount(2) - g2;

                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    long a1 = GC.GetAllocatedBytesForCurrentThread();
                    sw.Restart();
                    for (int i = 0; i < iters; i++) fixedVersion(sortMode, search);
                    sw.Stop();
                    long fixBytes = (GC.GetAllocatedBytesForCurrentThread() - a1) / iters;
                    double fixMs = sw.Elapsed.TotalMilliseconds / iters;

                    string label = search.Length == 0 ? "(empty)" : search;
                    Console.WriteLine($"   {sortMode,-4} | {label,-9} | {curMs,7:0.00} ms | {curBytes,8} B | {fixMs,5:0.00} ms | {fixBytes,5} B | {gen2}");

                    if (curMs > worstMs) { worstMs = curMs; worstBytes = curBytes; worstLabel = $"{sortMode}/{label}"; }
                }
            }

            Console.WriteLine();
            Console.WriteLine($"   {recipeCount} recipes. Worst keystroke ({worstLabel}): {worstMs:0.00} ms = {worstMs / 16.67 * 100:0.0}% of a 16.67 ms");
            Console.WriteLine($"   frame, allocating {worstBytes / 1024.0:0.0} KB. Typing at 8 keys/sec sustains {worstBytes * 8 / 1048576.0:0.00} MB/s.");
            Console.WriteLine("   The '#'/'@' rows allocate most: ItemSearchHelper.Parse calls Substring(1) once PER");
            Console.WriteLine($"   RECIPE, so a '#' search makes {recipeCount} throwaway strings per keystroke.");

            check(worstMs > 0, $"search keystroke cost measured ({worstMs:0.00} ms worst, {worstBytes / 1024.0:0.0} KB)");

            StartsWithBenchmark(check, recipeCount);
        }

        // Why hoisting Parse out of the per-item loop is worth so much: ItemSearchHelper.Parse uses
        // string.StartsWith(string), whose DEFAULT is a culture-sensitive comparison that goes
        // through the globalization layer. The ordinal forms are the same test without that cost.
        private static void StartsWithBenchmark(Action<bool, string> check, int recipeCount)
        {
            Console.WriteLine("\n-- Why: string.StartsWith(string) defaults to CULTURE-SENSITIVE comparison");
            Console.WriteLine("   (ItemSearchHelper.Parse:23,25 -- called once per recipe by Matches)");

            const string search = "copper";
            int sink = 0;

            Action culture = () =>
            {
                for (int i = 0; i < recipeCount; i++)
                {
                    if (search.StartsWith("#")) sink++;
                    if (search.StartsWith("@")) sink++;
                }
            };
            Action ordinal = () =>
            {
                for (int i = 0; i < recipeCount; i++)
                {
                    if (search.StartsWith("#", StringComparison.Ordinal)) sink++;
                    if (search.StartsWith("@", StringComparison.Ordinal)) sink++;
                }
            };
            Action charForm = () =>
            {
                for (int i = 0; i < recipeCount; i++)
                {
                    if (search.StartsWith('#')) sink++;
                    if (search.StartsWith('@')) sink++;
                }
            };

            culture(); ordinal(); charForm(); // warm + JIT

            double cultureMs = MsOf(culture, 50);
            double ordinalMs = MsOf(ordinal, 50);
            double charMs = MsOf(charForm, 50);
            GC.KeepAlive(sink);

            Console.WriteLine($"   {recipeCount} recipes x 2 StartsWith calls, per keystroke:");
            Console.WriteLine($"     StartsWith(\"#\")                        = {cultureMs,6:0.00} ms   <-- shipped");
            Console.WriteLine($"     StartsWith(\"#\", StringComparison.Ordinal) = {ordinalMs,6:0.00} ms   (x{cultureMs / Math.Max(ordinalMs, 0.0001):0} faster)");
            Console.WriteLine($"     StartsWith('#')                        = {charMs,6:0.00} ms   (x{cultureMs / Math.Max(charMs, 0.0001):0} faster)");
            Console.WriteLine("   Hoisting Parse out of the loop removes this entirely; making it ordinal fixes");
            Console.WriteLine("   every other caller too (Parse also runs per item in the Terminal's item search).");

            check(cultureMs > ordinalMs,
                $"culture-sensitive StartsWith is the dominant per-item cost ({cultureMs:0.00} vs {ordinalMs:0.00} ms)");
        }

        // ================================================================================
        // 1. DiskData.ExtractItem -> MatchingSlots + StackSelection.PlanWithdrawal
        //    Common/DiskData.cs:114 and :165. Two list allocations plus a full scan of the
        //    disk's stacks, per disk, per extract.
        // ================================================================================
        private static void ExtractBenchmark(Action<bool, string> check)
        {
            Console.WriteLine("\n-- DiskData.ExtractItem: MatchingSlots + PlanWithdrawal per extract (DiskData.cs:114,165)");
            Console.WriteLine("   stacks/disk | ms per extract | B per extract | B per 40-ingredient craft");
            Console.WriteLine("   ------------|----------------|---------------|--------------------------");

            long bytesAtLateGame = 0;

            foreach (int stacksPerDisk in new[] { 64, 512, 2048 })
            {
                var disk = MixedTypeDisk(stacksPerDisk);
                Action extract = () => ExtractOnce(disk, 700);

                extract(); // warm + JIT
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

                const int iters = 2000;
                long a0 = GC.GetAllocatedBytesForCurrentThread();
                double ms = MsOf(extract, iters);
                long bytes = (GC.GetAllocatedBytesForCurrentThread() - a0) / iters;

                Console.WriteLine($"   {stacksPerDisk,11} | {ms,11:0.0000} ms | {bytes,11} B | {bytes * 40,22} B");
                if (stacksPerDisk == 512) bytesAtLateGame = bytes;
            }

            Console.WriteLine();
            Console.WriteLine("   ExtractItem is a per-ACTION path (craft, withdraw), not per-frame. A craft consuming");
            Console.WriteLine($"   40 distinct ingredients across 10 disks costs ~{bytesAtLateGame * 40 * 10 / 1024.0:0.0} KB and one full scan per");
            Console.WriteLine("   (disk x ingredient). The scan, not the two lists, is the part that grows.");

            check(bytesAtLateGame > 0, $"extract path allocation measured ({bytesAtLateGame} B/extract at 512 stacks)");

            // The shape MatchingSlots' state grouping is worst at, and the one the mixed-type rows
            // above never reach: ONE maxStack=1 type filling the disk, so every stack matches and
            // ~1 in 20 opens a new run. Grouping runs rather than interning distinct states is what
            // keeps this one comparison per stack instead of one per stack per state seen so far.
            Console.WriteLine();
            Console.WriteLine("-- ...the same path where every stack on the disk matches (one maxStack=1 type)");
            Console.WriteLine("   stacks/disk | ms per extract | B per extract");
            Console.WriteLine("   ------------|----------------|---------------");

            double msAtFullDisk = 0;

            foreach (int stacksPerDisk in new[] { 64, 512, 2048 })
            {
                var disk = SingleTypeDisk(stacksPerDisk);
                Action extract = () => ExtractOnce(disk, 700);

                extract();
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

                const int iters = 2000;
                long a0 = GC.GetAllocatedBytesForCurrentThread();
                double ms = MsOf(extract, iters);
                long bytes = (GC.GetAllocatedBytesForCurrentThread() - a0) / iters;

                Console.WriteLine($"   {stacksPerDisk,11} | {ms,11:0.0000} ms | {bytes,11} B");
                if (stacksPerDisk == 2048) msAtFullDisk = ms;
            }

            // One press of a withdraw button is one of these per disk. A tenth of a millisecond on a
            // full Terra disk is a per-action cost nobody can feel; interning every distinct state
            // instead of numbering runs would put the disk's stack count into the inner loop.
            const double PerActionBudgetMs = 1.0;
            check(msAtFullDisk < PerActionBudgetMs,
                $"state grouping stays per-action on a full disk of one type ({msAtFullDisk:0.0000} ms < {PerActionBudgetMs} ms)");
        }

        // Exactly what DiskData.ExtractItem does before it touches Terraria types: the scan, the
        // run-index grouping, the shipped selection rule, and the removal list.
        private static void ExtractOnce(Disk disk, int itemType)
        {
            var matching = new List<StackSlot>();                    // DiskData.cs MatchingSlots
            Stack previousPooled = null;
            int runIndex = 0;

            for (int index = 0; index < disk.Items.Count; index++)
            {
                var stored = disk.Items[index];
                if (!stored.Matches(itemType, -1)) continue;

                bool standsForItself = HasPerInstanceData(stored);
                if (!standsForItself)
                {
                    if (previousPooled != null && !CanMergeStacks(previousPooled, stored))
                        runIndex++;

                    previousPooled = stored;
                }

                matching.Add(new StackSlot
                {
                    Index = index,
                    Stack = stored.StackCount,
                    IsUnique = standsForItself,
                    StateGroup = runIndex
                });
            }

            StackSelection.PlanWithdrawal(matching, 50, true, out _);
            var toRemove = new List<Stack>();                        // DiskData.cs ExtractItem
            GC.KeepAlive(toRemove);
        }

        // A drive bay of general stock: 1500 types spread over the disk, so a withdrawal matches a
        // handful of stacks.
        private static Disk MixedTypeDisk(int stacksPerDisk)
        {
            var disk = new Disk { MaxStacks = stacksPerDisk };
            var rng = new Random(9);
            for (int i = 0; i < stacksPerDisk; i++)
            {
                disk.Items.Add(new Stack
                {
                    ItemType = rng.Next(1500),
                    StackCount = rng.Next(1, 999),
                    InsertionOrder = i,
                    ModState = BuildModState(rng)
                });
            }
            return disk;
        }

        // One maxStack=1 type filling the disk - armour, the item class issue 25 was reported
        // against - so every stack matches the withdrawal and every one is grouped.
        private static Disk SingleTypeDisk(int stacksPerDisk)
        {
            var disk = new Disk { MaxStacks = stacksPerDisk };
            var rng = new Random(9);
            for (int i = 0; i < stacksPerDisk; i++)
            {
                disk.Items.Add(new Stack
                {
                    ItemType = 700,
                    StackCount = 1,
                    InsertionOrder = i,
                    ModState = BuildModState(rng)
                });
            }
            return disk;
        }

        // ================================================================================
        // 2. StorageWorldSystem.Defragment (StorageWorldSystem.cs:516)
        //    BuildMergeTargets (:590) allocates List<MergeTarget>(target.Items.Count) for EVERY
        //    donor stack, inside for(target) { for(donor) { for(stack) } }.
        // ================================================================================
        private static void DefragmentBenchmark(Action<bool, string> check)
        {
            Console.WriteLine("\n-- The defragment sweep: DefragmentCore.Sweep against the linear rescan it replaced");
            Console.WriteLine("   per-donor = the shape before 23i's allocation fix. rescan = the linear sweep it replaced.");
            Console.WriteLine("   shipped   = Common/DefragmentCore.cs itself, linked and run - not a transcription of it.");
            Console.WriteLine();
            Console.WriteLine("   disks x stacks | per-donor ms |    MB |  rescan ms |   MB | shipped ms |   MB | speedup");
            Console.WriteLine("   ---------------|--------------|-------|------------|------|------------|------|--------");

            // JIT every sweep before the first row is timed, or the smallest network pays for
            // compiling all three and reads as a slowdown.
            DefragmentCurrent(BuildFragmentedDisks(3, 32));
            DefragmentHoisted(BuildFragmentedDisks(3, 32));
            DefragmentIndexed(BuildFragmentedDisks(3, 32));

            double shippedAtMax = 0;
            double indexedAtMax = 0;
            double shippedMBAtMax = 0;
            double indexedMBAtMax = 0;
            bool everyScaleAgreed = true;

            foreach (var (diskCount, perDisk) in new[] { (4, 64), (8, 128), (10, 256), (10, 512), (20, 1024), (40, 2048) })
            {
                var perDonorDisks = BuildFragmentedDisks(diskCount, perDisk);
                var shippedDisks = BuildFragmentedDisks(diskCount, perDisk);
                var indexedDisks = BuildFragmentedDisks(diskCount, perDisk);

                var (perDonorMs, perDonorBytes) = TimeDefragment(DefragmentCurrent, perDonorDisks);
                var (shippedMs, shippedBytes) = TimeDefragment(DefragmentHoisted, shippedDisks);
                var (indexedMs, indexedBytes) = TimeDefragment(DefragmentIndexed, indexedDisks);

                // The index is only allowed to be faster, never to move a different set of items.
                everyScaleAgreed &= DisksHoldTheSame(shippedDisks, indexedDisks);

                double shippedMB = shippedBytes / 1048576.0;
                double indexedMB = indexedBytes / 1048576.0;
                Console.WriteLine($"   {diskCount,4} x {perDisk,-8} | {perDonorMs,9:0.0} ms | {perDonorBytes / 1048576.0,5:0.0} | {shippedMs,7:0.0} ms | {shippedMB,4:0.0} | {indexedMs,7:0.0} ms | {indexedMB,4:0.0} | x{shippedMs / Math.Max(indexedMs, 0.001):0.0}");

                bool isSupportedMaximum = diskCount == 40 && perDisk == 2048;
                if (isSupportedMaximum)
                {
                    shippedAtMax = shippedMs;
                    indexedAtMax = indexedMs;
                    shippedMBAtMax = shippedMB;
                    indexedMBAtMax = indexedMB;
                }
            }

            Console.WriteLine();
            Console.WriteLine($"   Defragment is a single button press, but it runs on the game thread. At the supported");
            Console.WriteLine($"   maximum of 40 disks x 2048 stacks it blocked for {shippedAtMax:0.0} ms ({shippedAtMax / 16.67:0.0} frames) and now blocks");
            Console.WriteLine($"   for {indexedAtMax:0.0} ms ({indexedAtMax / 16.67:0.0} frames) - x{shippedAtMax / Math.Max(indexedAtMax, 0.001):0.0} - at {indexedMBAtMax:0.0} MB against {shippedMBAtMax:0.0} MB.");
            Console.WriteLine("   The merge rule is unchanged: the index only decides which stacks it is asked about.");

            check(everyScaleAgreed,
                "the shipped sweep moves exactly what the linear rescan moves, at every scale");
            // A floor near the measured ratio rather than the token 5x it replaced: the index is
            // worth roughly 28x here, so a 5x floor left most of the win unguarded and a regression
            // that gave back three quarters of it would still have passed.
            check(indexedAtMax < shippedAtMax / 15,
                $"the index cuts the defrag freeze at the supported maximum by at least 15x ({shippedAtMax:0.0} ms -> {indexedAtMax:0.0} ms)");

            BulkStorageDefragmentBenchmark(check);
        }

        // A (type, prefix) index is only worth anything if the buckets stay short. DiskData.InsertItem
        // bounds the number of PARTIAL stacks per identity, not the total: a drive bay holding half a
        // million Stone keeps hundreds of full stacks under one key. That is the shape most likely to
        // defeat the index, so it gets measured rather than assumed.
        private static void BulkStorageDefragmentBenchmark(Action<bool, string> check)
        {
            Console.WriteLine("\n-- The same sweep over bulk storage: 8 types, 90% of stacks already full");
            Console.WriteLine("   disks x stacks | shipped ms | indexed ms | speedup");
            Console.WriteLine("   ---------------|------------|------------|--------");

            double shippedAtMax = 0;
            double indexedAtMax = 0;
            bool everyScaleAgreed = true;

            foreach (var (diskCount, perDisk) in new[] { (10, 512), (20, 1024), (40, 2048) })
            {
                var shippedDisks = BuildBulkStorageDisks(diskCount, perDisk);
                var indexedDisks = BuildBulkStorageDisks(diskCount, perDisk);

                var (shippedMs, _) = TimeDefragment(DefragmentHoisted, shippedDisks);
                var (indexedMs, _) = TimeDefragment(DefragmentIndexed, indexedDisks);

                everyScaleAgreed &= DisksHoldTheSame(shippedDisks, indexedDisks);

                Console.WriteLine($"   {diskCount,4} x {perDisk,-8} | {shippedMs,7:0.0} ms | {indexedMs,7:0.0} ms | x{shippedMs / Math.Max(indexedMs, 0.001):0.0}");

                if (diskCount == 40 && perDisk == 2048)
                {
                    shippedAtMax = shippedMs;
                    indexedAtMax = indexedMs;
                }
            }

            Console.WriteLine();
            Console.WriteLine("   Every stack of one type shares a bucket here, so the index only pays off because");
            Console.WriteLine("   BuildMergeTargets passes over stacks already at maxStack without asking the merge");
            Console.WriteLine("   rule about them - the comparison the index cannot remove is the expensive one.");

            check(everyScaleAgreed,
                "the shipped sweep moves exactly what the linear rescan moves over bulk storage too");
            // Bulk storage is the shape least suited to a (type, prefix) index, so it is the row a
            // regression shows up in first. Measured around 30x; the floor is set well under that
            // and still an order of magnitude tighter than the bare "wins at all" it replaced.
            check(indexedAtMax < shippedAtMax / 15,
                $"the index still wins by at least 15x on the disk shape least suited to it ({shippedAtMax:0.0} ms -> {indexedAtMax:0.0} ms)");

            ModStateComparisonBenchmark(check);
        }

        // The comparison the index cannot remove, so its own cost decides the floor. DiskData.
        // ModStateMatches used to wrap each blob in a fresh TagCompound purely to hand the pair to
        // TagCompoundEquals - which, for a single key, reduces to comparing the two values.
        private static void ModStateComparisonBenchmark(Action<bool, string> check)
        {
            Console.WriteLine("\n-- DiskData.ModStateMatches: the comparison the index keeps (DiskData.cs:328)");

            var rng = new Random(11);
            var left = BuildModState(rng);
            var right = BuildModState(new Random(11));
            var shared = left;

            Action wrapping = () =>
            {
                var wrappedFirst = new Dictionary<string, object> { ["g"] = left[GlobalDataKey] };
                var wrappedSecond = new Dictionary<string, object> { ["g"] = right[GlobalDataKey] };
                TagCompoundEquals(wrappedFirst, wrappedSecond);
            };
            Action direct = () => ModStateMatches(left, right);
            Action sameTag = () => ModStateMatches(left, shared);

            wrapping(); direct(); sameTag();

            const int iterations = 200000;

            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long before = GC.GetAllocatedBytesForCurrentThread();
            double wrappingMs = MsOf(wrapping, iterations);
            long wrappingBytes = (GC.GetAllocatedBytesForCurrentThread() - before) / iterations;

            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            before = GC.GetAllocatedBytesForCurrentThread();
            double directMs = MsOf(direct, iterations);
            long directBytes = (GC.GetAllocatedBytesForCurrentThread() - before) / iterations;

            double sharedMs = MsOf(sameTag, iterations);

            Console.WriteLine($"   two wrapper TagCompounds  : {wrappingMs * 1000000,7:0} ns/call | {wrappingBytes,3} B/call");
            Console.WriteLine($"   comparing the values      : {directMs * 1000000,7:0} ns/call | {directBytes,3} B/call");
            Console.WriteLine($"   the same tag object       : {sharedMs * 1000000,7:0} ns/call | ReferenceEquals short-circuit");
            Console.WriteLine();
            Console.WriteLine("   A defragment at the supported maximum makes hundreds of thousands of these calls,");
            Console.WriteLine("   and CopyStackWithCount shares the tag object between a stack and its split copies,");
            Console.WriteLine("   so the same-object case is the common one rather than a rare one.");

            check(directBytes < wrappingBytes,
                $"comparing the blobs directly allocates less than wrapping them ({wrappingBytes} B -> {directBytes} B per call)");
            check(sharedMs < directMs,
                "two stacks sharing one tag object settle on reference equality");
        }

        private static (double ms, long bytes) TimeDefragment(Action<List<Disk>> sweep, List<Disk> disks)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long before = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            sweep(disks);
            sw.Stop();
            return (sw.Elapsed.TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        // Same disks, same slots, same counts, same identity - the whole point of the index is that
        // it changes nothing about the outcome.
        private static bool DisksHoldTheSame(List<Disk> first, List<Disk> second)
        {
            if (first.Count != second.Count) return false;

            for (int d = 0; d < first.Count; d++)
            {
                if (first[d].Items.Count != second[d].Items.Count) return false;

                for (int i = 0; i < first[d].Items.Count; i++)
                {
                    var a = first[d].Items[i];
                    var b = second[d].Items[i];
                    if (a.ItemType != b.ItemType || a.PrefixId != b.PrefixId
                        || a.StackCount != b.StackCount
                        || a.InsertionOrder != b.InsertionOrder
                        || (a.ModData == null) != (b.ModData == null))
                        return false;

                    // The mod state is the whole subject of issues 04 and 24, so comparing counts
                    // and calling it "same identity" would prove nothing about the thing at risk.
                    if (!ModStateMatches(a.ModState, b.ModState))
                        return false;
                }
            }

            return true;
        }

        // Issue 24 names CalamityGlobalItem.SaveData writing three keys unconditionally, for an
        // apricot as readily as for a weapon. Most stacks therefore carry the same three defaults;
        // a minority differ, which is what makes the merge rule's comparison decide anything.
        private static Dictionary<string, object> BuildModState(Random rng)
        {
            var globalData = new Dictionary<string, object>
            {
                ["charge"] = rng.Next(20) == 0 ? rng.Next(1, 100) : 0,
                ["enchantment"] = rng.Next(20) == 0 ? "reforged" : "",
                ["rerollCount"] = 0
            };
            return new Dictionary<string, object> { [GlobalDataKey] = globalData };
        }

        // The shape the index has the least to work with: a bulk-storage drive bay holding a handful
        // of types, most stacks already full. Every stack of one type lands in a single bucket, so
        // this is where a (type, prefix) index would degenerate back to a linear scan if
        // BuildMergeTargets did not pass over stacks that are already at capacity.
        private static List<Disk> BuildBulkStorageDisks(int diskCount, int perDisk)
            => BuildFragmentedDisks(diskCount, perDisk, distinctTypes: 8, fullStackShare: 0.9);

        private static List<Disk> BuildFragmentedDisks(int diskCount, int perDisk)
            => BuildFragmentedDisks(diskCount, perDisk, distinctTypes: 120, fullStackShare: 0);

        private static List<Disk> BuildFragmentedDisks(int diskCount, int perDisk,
            int distinctTypes, double fullStackShare)
        {
            var rng = new Random(42);
            var disks = new List<Disk>(diskCount);
            for (int d = 0; d < diskCount; d++)
            {
                var disk = new Disk { MaxStacks = perDisk };
                // Fragmented: many partial stacks of a small type spread, which is exactly the
                // state the Defragment button exists to clean up.
                int fill = (int)(perDisk * 0.8);
                for (int i = 0; i < fill; i++)
                {
                    // A disk's own GUID is the live example of ModItem save data, and a drive bay
                    // full of disks is exactly when a player reaches for Defragment.
                    bool standsForItself = rng.Next(50) == 0;
                    bool alreadyFull = rng.NextDouble() < fullStackShare;
                    disk.Items.Add(new Stack
                    {
                        ItemType = rng.Next(distinctTypes),
                        StackCount = alreadyFull ? MaxStackSize : rng.Next(1, 200),
                        PrefixId = rng.Next(4),
                        InsertionOrder = i,
                        ModData = standsForItself ? new object() : null,
                        ModState = BuildModState(rng)
                    });
                }
                disks.Add(disk);
            }
            return disks;
        }

        private const int MaxStackSize = 999;

        // Faithful transcription of StorageWorldSystem.Defragment's loop nesting and allocation sites.
        private static void DefragmentCurrent(List<Disk> disks)
        {
            for (int ti = 0; ti < disks.Count - 1; ti++)
            {
                var target = disks[ti];
                if (target.IsFull) continue;

                for (int di = ti + 1; di < disks.Count && !target.IsFull; di++)
                {
                    var donor = disks[di];
                    if (donor.Items.Count == 0) continue;

                    for (int si = donor.Items.Count - 1; si >= 0 && !target.IsFull; si--)
                    {
                        var stack = donor.Items[si];
                        bool isUnique = HasPerInstanceData(stack);

                        // StorageWorldSystem.cs:545 -> :590, allocates per donor stack.
                        var mergeTargets = BuildMergeTargets(target, stack);
                        int freeSlots = target.MaxStacks - target.UsedStacks;

                        var plan = StackSelection.PlanDonorMove(mergeTargets, stack.StackCount,
                            MaxStackSize, freeSlots, isUnique);

                        ApplyPlan(target, donor, si, stack, plan);
                    }
                }
            }
        }

        // What master runs today: the buffers and the plan are hoisted out of the loop, so the
        // sweep stays out of the allocator. The rescan of every target stack per donor stack is
        // untouched, which is the half issue 23i left open.
        private static void DefragmentHoisted(List<Disk> disks)
        {
            var scratch = new List<MergeTarget>();
            var plan = new DonorMovePlan();

            for (int ti = 0; ti < disks.Count - 1; ti++)
            {
                var target = disks[ti];
                if (target.IsFull) continue;

                for (int di = ti + 1; di < disks.Count && !target.IsFull; di++)
                {
                    var donor = disks[di];
                    if (donor.Items.Count == 0) continue;

                    for (int si = donor.Items.Count - 1; si >= 0 && !target.IsFull; si--)
                    {
                        var stack = donor.Items[si];
                        bool isUnique = HasPerInstanceData(stack);

                        scratch.Clear();
                        for (int index = 0; index < target.Items.Count; index++)
                        {
                            var existing = target.Items[index];
                            scratch.Add(new MergeTarget
                            {
                                Index = index,
                                Stack = existing.StackCount,
                                Accepts = CanMergeStacks(existing, stack)
                            });
                        }

                        int freeSlots = target.MaxStacks - target.UsedStacks;
                        StackSelection.PlanDonorMove(scratch, stack.StackCount,
                            MaxStackSize, freeSlots, isUnique, plan);

                        ApplyPlan(target, donor, si, stack, plan);
                    }
                }
            }
        }

        // The same sweep, with the target's stacks bucketed by the only thing that can make two
        // stacks mergeable at all. A donor sees its own bucket instead of the whole disk; the
        // merge rule still has the final word on every candidate the bucket returns.
        // No longer a replica: this runs the shipped sweep. DefragmentCore is Terraria-free precisely
        // so it can be linked here, which makes DisksHoldTheSame a differential between the sweep
        // that ships and the linear rescan it replaced, rather than between two transcriptions.
        //
        // The rules below are the same CanMergeStacks and HasPerInstanceData DefragmentHoisted uses,
        // so the two sides of that differential share one identity rule and differ only in sweep.
        private static void DefragmentIndexed(List<Disk> disks)
        {
            var shipped = new List<DefragmentDisk<Stack>>(disks.Count);
            foreach (Disk disk in disks)
                shipped.Add(new DefragmentDisk<Stack>(disk.Items, disk.MaxStacks));

            DefragmentCore.Sweep(shipped, BenchmarkRules);
        }

        private static readonly BenchmarkStackRules BenchmarkRules = new();

        private readonly struct BenchmarkStackRules : IDefragmentRules<Stack>
        {
            public int GetItemType(Stack stack) => stack.ItemType;

            public int GetPrefixId(Stack stack) => stack.PrefixId;

            public int GetCount(Stack stack) => stack.StackCount;

            public void SetCount(Stack stack, int count) => stack.StackCount = count;

            public bool IsUnique(Stack stack) => HasPerInstanceData(stack);

            public int GetMaxStack(Stack stack) => MaxStackSize;

            public bool CanMerge(Stack target, Stack donor) => CanMergeStacks(target, donor);

            public Stack CopyWithCount(Stack source, int count) => new Stack
            {
                ItemType       = source.ItemType,
                StackCount     = count,
                PrefixId       = source.PrefixId,
                InsertionOrder = source.InsertionOrder,
                ModData        = source.ModData,
                ModState       = source.ModState
            };
        }

        private static List<MergeTarget> BuildMergeTargets(Disk target, Stack donorStack)
        {
            var mergeTargets = new List<MergeTarget>(target.Items.Count);
            for (int index = 0; index < target.Items.Count; index++)
            {
                var existing = target.Items[index];
                mergeTargets.Add(new MergeTarget
                {
                    Index = index,
                    Stack = existing.StackCount,
                    Accepts = CanMergeStacks(existing, donorStack)
                });
            }
            return mergeTargets;
        }

        // Only the two historical shapes apply a plan by hand now: the sweep that ships does its own
        // list work inside DefragmentCore, so neither of these keeps a merge index any more.
        private static void ApplyPlan(Disk target, Disk donor, int si, Stack stack, DonorMovePlan plan)
        {
            if (plan.MoveWholeStack)
            {
                target.Items.Add(stack);
                donor.Items.RemoveAt(si);
                return;
            }

            foreach (var merge in plan.Merges)
                target.Items[merge.Index].StackCount += merge.Count;

            foreach (int addAmount in plan.NewSlots)
            {
                target.Items.Add(new Stack
                {
                    ItemType = stack.ItemType,
                    StackCount = addAmount,
                    PrefixId = stack.PrefixId,
                    InsertionOrder = stack.InsertionOrder,
                    ModData = stack.ModData,
                    ModState = stack.ModState
                });
            }

            if (plan.LeftOnDonor == 0) donor.Items.RemoveAt(si);
            else stack.StackCount = plan.LeftOnDonor;
        }

        // ================================================================================
        // 3. Helpers/Resolver/CraftingTransaction.cs - PlanExecutor.Run / MaterialConsumer
        //    Allocates a RefundLedger (with an inner List) and an intermediates List per craft.
        // ================================================================================
        // A reference type, like the Terraria.Item the shipped code actually moves. SplitOff has to
        // leave the caller's handle describing the rest - StoreExcess returns the very handle it
        // passed in and expects the excess to have come off it - which a struct cannot do.
        private sealed class FakeItem
        {
            public int Type;
            public int Count;
        }

        private sealed class FakeCraftStorage : ICraftingStorage<FakeItem>
        {
            private readonly Dictionary<int, int> _counts = new();

            public void Seed(int type, int count) => _counts[type] = count;

            public FakeItem Nothing => new FakeItem { Type = 0, Count = 0 };

            public int CountItem(int itemType) => _counts.TryGetValue(itemType, out int c) ? c : 0;

            // Pooled counts hold no per-instance state, so a draw never crosses a state boundary
            // and the whole amount comes back as one handle - the list is still allocated per draw,
            // the way the shipped sweep allocates it, so the measurement stays honest.
            public List<FakeItem> ExtractStacks(int itemType, int amount)
            {
                var handles = new List<FakeItem>();

                int have = CountItem(itemType);
                int take = Math.Min(have, amount);
                if (take <= 0)
                    return handles;

                _counts[itemType] = have - take;
                handles.Add(new FakeItem { Type = itemType, Count = take });
                return handles;
            }

            // Nothing here carries state to match a stored handle against, so recovery always falls
            // back to the by-type draw - which is correct for interchangeable pooled units.
            public int ExtractStored(FakeItem stored, int count) => 0;

            // Pooled counts carry no state, so two handles of a type are interchangeable.
            public bool SameStoredState(FakeItem first, FakeItem second)
                => first != null && second != null && first.Type == second.Type;

            public int Insert(FakeItem item)
            {
                if (item.Count <= 0) return 0;
                _counts.TryGetValue(item.Type, out int have);
                _counts[item.Type] = have + item.Count;
                return 0;
            }

            public int StackOf(FakeItem item) => item == null ? 0 : item.Count;

            public FakeItem SplitOff(FakeItem item, int count)
            {
                var part = new FakeItem { Type = item.Type, Count = count };
                item.Count -= count;
                return part;
            }
        }

        private sealed class FakeProducer : IStepProducer<FakeItem>
        {
            private readonly IReadOnlyList<ExecutionStep> _steps;
            public FakeProducer(IReadOnlyList<ExecutionStep> steps) => _steps = steps;

            public void PrepareStep(int stepIndex) { }

            public FakeItem ProduceStep(int stepIndex)
                => new FakeItem { Type = _steps[stepIndex].ProducedType, Count = _steps[stepIndex].ProducedCount };
        }

        private static void CraftingTransactionBenchmark(Action<bool, string> check)
        {
            Console.WriteLine("\n-- PlanExecutor.Run: per-CRAFT allocation (CraftingTransaction.cs:175)");
            Console.WriteLine("   steps | ms per craft | B per craft");
            Console.WriteLine("   ------|--------------|------------");

            long bytesAt10 = 0;

            foreach (int stepCount in new[] { 1, 5, 10, 30 })
            {
                var steps = new List<ExecutionStep>();
                for (int s = 0; s < stepCount; s++)
                {
                    var step = new ExecutionStep { ProducedType = 5000 + s, ProducedCount = 10 };
                    for (int ing = 0; ing < 4; ing++)
                        step.Consumed.Add((100 + ing, 5));
                    steps.Add(step);
                }

                var producer = new FakeProducer(steps);

                Action craft = () =>
                {
                    var storage = new FakeCraftStorage();
                    for (int t = 100; t < 110; t++) storage.Seed(t, 100000);
                    var executor = new PlanExecutor<FakeItem>(storage);
                    executor.Run(steps, 10, producer);
                };

                craft(); // warm + JIT
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

                const int iters = 2000;
                long a0 = GC.GetAllocatedBytesForCurrentThread();
                double ms = MsOf(craft, iters);
                long bytes = (GC.GetAllocatedBytesForCurrentThread() - a0) / iters;

                Console.WriteLine($"   {stepCount,5} | {ms,9:0.0000} ms | {bytes,8} B");
                if (stepCount == 10) bytesAt10 = bytes;
            }

            Console.WriteLine();
            Console.WriteLine("   (the fake storage + its dictionary are rebuilt per iteration and are counted here,");
            Console.WriteLine("    so the true PlanExecutor overhead is LOWER than shown.)");
            Console.WriteLine("   A craft is one click. Even held down at 60/sec a 10-step plan is a rounding error.");

            check(bytesAt10 < 100000, $"crafting transaction allocation is small per craft ({bytesAt10} B at 10 steps)");
        }
    }
}
