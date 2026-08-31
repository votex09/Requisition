using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using TerraStorage.Common;
using TerraStorage.Content.UI;
using TerraStorage.Helpers.Resolver;

namespace TerraStorage.Tests
{
    // Zero-dependency console test runner for the crafting resolver core. Run with:
    //   dotnet run --project Tests
    // Exit code 0 = all green, 1 = a failure (so it can gate a build).
    //
    // Each scenario contrasts the resolver's authoritative feasibility, the list-flag craftability,
    // and BOTH the OLD per-ingredient preview (BuggyPreview — independent checks that double-count
    // shared stock) and the NEW shared-pool preview (CoreResolver.ComputeIngredientPreview). The
    // bug the user reported is that the preview shows materials as available when the recipe is not
    // actually craftable; the BuggyPreview asserts reproduce that, the ComputeIngredientPreview
    // asserts prove the fix.
    public static class Program
    {
        private static int _pass;
        private static int _fail;
        private static readonly List<string> _failures = new();

        public static int Main()
        {
            Console.WriteLine("=== Crafting resolver core tests ===\n");

            SharedBaseContention();
            DemoniteCrimtaneTwoWay();
            RecipeGroupContention();
            StationShadowingSubCraft();
            StationFallbackOrderIndependent();
            DirectAndSimpleSanity();
            NoOpRecipesAreUncraftable();
            BlockingIngredientIsIdentified();
            RepeatedIngredientSlotsAccumulate();
            DuplicateSlotsAreSummedInPreview();
            FeasibilityHonoursMaxDepth();
            PreviewAppliesForceCraftSemantics();
            SatisfiableAgreesWithThePlan();
            SubCraftClaimsSharedBaseMaterial();
            ListFlagAgreesWithCraftButton();
            IngredientCacheIsScopedByGroup();
            RecipeGroupSlotsMixMembers();
            DepthLimitsCraftsNotLookups();
            FailedResolveRestoresPool();
            MaterialTransactionIsAtomic();
            PlanExecutionIsAtomic();
            WithdrawalNeverMixesUniqueStacks();
            DefragmentRespectsStackIdentity();
            PanelRefreshCacheInvalidates();
            ClippedRowsRegisterNoHitRect();
            PartialDepositIsReported();
            PreviewExcludesTheOutputFromDirectDraw();
            NestedGroupSlotsMixMembers();
            IngredientCacheIsScopedByOutput();
            AbortRefundSurvivesAFullNetwork();
            ReachabilityEquivalence();
            ReachabilityScaleBenchmark();
            ReachabilityRealisticScaleBenchmark();
            MaxAmountCountsSubCrafts();
            MaxAmountMatchesTheCraftButton();
            MaxAmountSurvivesARoundingDip();
            MaxAmountNeverOffersAnUnplannableAmount();
            MaxAmountAppliesForceCraftAndConditions();
            MaxAmountSurvivesDeepStackMultiplication();
            MaxAmountOnScopedDump();
            RealDumpBenchmark();
            MergeCandidateIndexAgreesWithTheMergeRule();
            HotPathBenchmarks.Run(Check);

            WindowStackTests();
            DepositGateTests();
            ClickBlockerTests();
            FavoritesRowCacheTests();
            VanillaMouseBlockingStaysInTheUIPhase();
            ScrollBoundsComeFromTheDrawGeometry();
            TrackingSnapshotIsScopedAndFailsSafe();
            StackIdentityTests();
            DenialReasonsSurviveTheWire();
            WireCountBoundsTests();
            DiskClaimTests();
            TerminalReachIsOneRule();
            DiskAccessRules();
            AuthorizationIsWiredIntoTheHandlers();
            BandOfDoorIsPayableFromStacksThatStandAlone();
            SeparateStacksKeepTheirStateThroughARefund();
            BandOfDoorFixtureBuildsTheReportedPlan();
            NetworkDrainsInOneSweep();
            TakeBackRecoversTheRunsOwnStack();
            RefundKeepsThePlayersStackNotTheLastOne();
            AStateBoundaryEndsTheDrawWithinOneDisk();
            DefragmentSweepMovesStacksWithoutLosingAny();

            Console.WriteLine($"\n=== {_pass} passed, {_fail} failed ===");
            if (_fail > 0)
            {
                Console.WriteLine("\nFAILURES:");
                foreach (var f in _failures) Console.WriteLine("  - " + f);
            }
            return _fail == 0 ? 0 : 1;
        }

        // ---- Scenario: the MAX button must count what sub-crafting unlocks ----
        // The reported shape, straight off a real dump: thousands of wooden arrows, ZERO torches,
        // but wood and gel to make torches from. The panel's old formula read the empty torch shelf
        // as a hard zero and offered one craft; nine are actually possible.
        private static void MaxAmountCountsSubCrafts()
        {
            Section("MAX amount counts sub-crafts (flaming arrows)");
            const int FLAMING_ARROW = 41, WOODEN_ARROW = 40, TORCH = 8, WOOD = 9, GEL = 23;

            var env = new FakeEnvironment();
            var flamingArrow = env.AddRecipe(FLAMING_ARROW, 10, new[] { (WOODEN_ARROW, 10), (TORCH, 1) });
            env.AddRecipe(TORCH, 3, new[] { (GEL, 1), (WOOD, 1) });
            var r = new CoreResolver(env);

            // 3 wood -> 3 torch crafts -> 9 torches -> 9 flaming-arrow crafts (90 arrows of 3580).
            var noTorches = new Dictionary<int, int> { [WOODEN_ARROW] = 3580, [GEL] = 114, [WOOD] = 3 };
            Eq(PanelDirectOnlyMax(env, flamingArrow, noTorches), 0, "OLD formula: torch shelf empty -> 0");
            Eq(r.ComputeMaxCraftAmount(flamingArrow, noTorches, 9999), 9, "NEW: 9 crafts via sub-crafted torches");
            IsTrue(r.CanCraftAmount(flamingArrow, noTorches, 9), "9 crafts feasible");
            IsFalse(r.CanCraftAmount(flamingArrow, noTorches, 10), "10 crafts needs a 4th wood");
            Eq(noTorches[WOOD], 3, "the snapshot is restored, not drained");

            // Enough wood for torches: the arrow stock becomes the ceiling (3580 / 10).
            var plentyOfWood = new Dictionary<int, int> { [WOODEN_ARROW] = 3580, [GEL] = 1000, [WOOD] = 1000 };
            Eq(r.ComputeMaxCraftAmount(flamingArrow, plentyOfWood, 9999), 358, "arrow stock binds at 358 crafts");

            // Torches in stock and nothing to craft more from: the direct answer still holds.
            var torchesInStock = new Dictionary<int, int> { [WOODEN_ARROW] = 3580, [TORCH] = 5 };
            Eq(PanelDirectOnlyMax(env, flamingArrow, torchesInStock), 5, "OLD formula: 5 torches -> 5");
            Eq(r.ComputeMaxCraftAmount(flamingArrow, torchesInStock, 9999), 5, "NEW agrees when nothing is sub-crafted");

            // The cap the panel passes is honoured even when materials would allow more.
            Eq(r.ComputeMaxCraftAmount(flamingArrow, plentyOfWood, 100), 100, "cap is respected");

            // Nothing to work with at all: zero, and the panel's Math.Max(1, ...) shows 1.
            var empty = new Dictionary<int, int>();
            Eq(r.ComputeMaxCraftAmount(flamingArrow, empty, 9999), 0, "no materials -> 0");
        }

        // ---- Scenario: MAX may never offer more crafts than the craft button can plan ----
        // Sub-crafts share one pool, so a base material two slots both want must not be counted
        // twice — the failure mode that would turn an inflated MAX into a half-finished craft.
        private static void MaxAmountMatchesTheCraftButton()
        {
            Section("MAX amount agrees with the resolver");
            const int ORE_X = 10, A = 11, B = 12, TARGET = 13, FURNACE = 100;

            var env = new FakeEnvironment().WithStations(FURNACE);
            env.AddRecipe(A, 1, new[] { (ORE_X, 1) }, tiles: new[] { FURNACE });
            env.AddRecipe(B, 1, new[] { (ORE_X, 1) }, tiles: new[] { FURNACE });
            var target = env.AddRecipe(TARGET, 1, new[] { (A, 1), (B, 1) }, tiles: new[] { FURNACE });
            var r = new CoreResolver(env);

            // 7 ore covers 3 targets (2 ore each), not 4 — A and B draw on the same ore.
            var sevenOre = new Dictionary<int, int> { [ORE_X] = 7 };
            Eq(r.ComputeMaxCraftAmount(target, sevenOre, 9999), 3, "shared base is not double-counted");
            IsTrue(PlannerCanCraftAmount(r, target, sevenOre, 3), "the craft button plans the 3 MAX offers");
            IsFalse(PlannerCanCraftAmount(r, target, sevenOre, 4), "the craft button refuses one more");

            // One ore short of a single craft.
            var oneOre = new Dictionary<int, int> { [ORE_X] = 1 };
            Eq(r.ComputeMaxCraftAmount(target, oneOre, 9999), 0, "not even one craft");

            // Depth is the panel's recursion-depth slider: at depth 0 no sub-craft is allowed, so
            // MAX falls back to what is directly in stock.
            var shallow = new CoreResolver(env) { MaxDepth = 0 };
            Eq(shallow.ComputeMaxCraftAmount(target, sevenOre, 9999), 0, "depth 0: ore alone crafts nothing");
            var barsInStock = new Dictionary<int, int> { [A] = 4, [B] = 6 };
            Eq(shallow.ComputeMaxCraftAmount(target, barsInStock, 9999), 4, "depth 0: direct stock still counts");

            // A station the network does not have blocks the sub-craft, so MAX must not count it.
            var stationless = new FakeEnvironment();
            stationless.AddRecipe(A, 1, new[] { (ORE_X, 1) }, tiles: new[] { FURNACE });
            stationless.AddRecipe(B, 1, new[] { (ORE_X, 1) }, tiles: new[] { FURNACE });
            var stationlessTarget = stationless.AddRecipe(TARGET, 1, new[] { (A, 1), (B, 1) });
            Eq(new CoreResolver(stationless).ComputeMaxCraftAmount(stationlessTarget, sevenOre, 9999), 0,
                "no furnace: the sub-craft does not count");

            // Nor may MAX ignore the station the SELECTED recipe itself needs, even with every
            // ingredient on the shelf — it is a public answer to "how many can I make here".
            var needsMissingStation = new FakeEnvironment();
            var gated = needsMissingStation.AddRecipe(TARGET, 1, new[] { (A, 1) }, tiles: new[] { FURNACE });
            var barsOnly = new Dictionary<int, int> { [A] = 50 };
            IsFalse(new CoreResolver(needsMissingStation).CanCraftAmount(gated, barsOnly, 1), "missing station: not one craft");
            Eq(new CoreResolver(needsMissingStation).ComputeMaxCraftAmount(gated, barsOnly, 9999), 0, "missing station: MAX 0");
        }

        // ---- Scenario: a craft count that fails must not end the search ----
        // A sub-craft is planned ceil(deficit / OutputStack) times, so a larger demand can round onto
        // a different sub-recipe that fits where the first one did not — plannability dips and comes
        // back. A bisect that trusts monotonicity stops at the dip and reports the same "1" the
        // reported bug did. No recipe groups and no stations here: rounding alone is enough.
        private static void MaxAmountSurvivesARoundingDip()
        {
            Section("MAX amount survives a rounding dip");
            const int TARGET = 60, PART = 61, FILLER = 62, SCARCE = 63;

            var env = new FakeEnvironment();
            var target = env.AddRecipe(TARGET, 1, new[] { (PART, 1), (PART, 3) });
            env.AddRecipe(PART, 4, new[] { (SCARCE, 2) });
            env.AddRecipe(PART, 3, new[] { (FILLER, 1) });
            env.AddRecipe(FILLER, 3, new[] { (SCARCE, 1) });
            var r = new CoreResolver(env);

            var stock = new Dictionary<int, int> { [SCARCE] = 3 };
            IsTrue(r.CanCraftAmount(target, stock, 3), "3 crafts plannable");
            IsFalse(r.CanCraftAmount(target, stock, 4), "4 rounds onto the wasteful sub-recipe");
            IsTrue(r.CanCraftAmount(target, stock, 5), "5 rounds back onto the one that fits");
            IsTrue(r.CanCraftAmount(target, stock, 6), "and 6 too");

            Eq(r.ComputeMaxCraftAmount(target, stock, 9999), 6, "the search looks past the dip");
        }

        // ---- Scenario: MAX may never offer an amount the craft button then refuses ----
        // The allocation-free feasibility mirror and the planner disagree about stations: the mirror
        // refuses a station-gated recipe and tries the next group candidate, while the planner takes
        // the first candidate that resolves at all and keeps a station-incomplete plan — which the
        // panel reports as "Missing Stations". MAX asks the planner, so it cannot inherit that gap.
        private static void MaxAmountNeverOffersAnUnplannableAmount()
        {
            Section("MAX amount never offers what the button refuses");
            const int TARGET = 80, MID = 81, GATED = 82, SUBSTITUTE = 83, BASE = 84, ANY = 8, MISSING_STATION = 999;

            var env = new FakeEnvironment().WithGroup(ANY, GATED, SUBSTITUTE);
            env.AddRecipe(GATED, 1, new[] { (BASE, 1) }, tiles: new[] { MISSING_STATION });
            env.AddRecipe(SUBSTITUTE, 1, new[] { (BASE, 1) });
            env.AddRecipe(MID, 1, new[] { (GATED, 1) }, groups: new[] { ANY });
            var target = env.AddRecipe(TARGET, 1, new[] { (MID, 1) });
            var r = new CoreResolver(env);

            var stock = new Dictionary<int, int> { [BASE] = 4 };
            int max = r.ComputeMaxCraftAmount(target, stock, 9999);
            for (int amount = 1; amount <= max; amount++)
                IsTrue(PlannerCanCraftAmount(r, target, stock, amount), $"x{amount} is plannable");

            // The station-gated route is the only one the planner takes, so nothing here is craftable.
            Eq(max, 0, "a station-incomplete plan is not an offer");
        }

        // ---- Scenario: force-craft semantics and live recipe conditions ----
        private static void MaxAmountAppliesForceCraftAndConditions()
        {
            Section("MAX amount: force-craft semantics and conditions");
            const int LOOP = 90, OTHER = 91, GATED = 92, BASE = 93;

            // A no-op loop: the only way to "make" LOOP is to unmake OTHER, which is made from LOOP.
            // Existing stock of the output is not a material, so no amount is craftable — the same
            // rule the craft button applies, and without it MAX would offer the shelf back to you.
            var loopEnv = new FakeEnvironment();
            var loop = loopEnv.AddRecipe(LOOP, 1, new[] { (OTHER, 1) });
            loopEnv.AddRecipe(OTHER, 1, new[] { (LOOP, 1) });
            var loopResolver = new CoreResolver(loopEnv);

            var tenInStorage = new Dictionary<int, int> { [LOOP] = 10 };
            IsFalse(loopResolver.CanCraftAmount(loop, tenInStorage, 5), "no-op loop: output stock is not a material");
            Eq(loopResolver.ComputeMaxCraftAmount(loop, tenInStorage, 9999), 0, "no-op loop: MAX 0");
            IsFalse(PlannerCanCraftAmount(loopResolver, loop, tenInStorage, 1), "and the craft button agrees");

            // A recipe whose condition is not met (night, biome, a boss not downed) crafts nothing,
            // however full the shelves are.
            var closedEnv = new FakeEnvironment().WithConditions(_ => false);
            var closed = closedEnv.AddRecipe(GATED, 1, new[] { (BASE, 1) });
            var plenty = new Dictionary<int, int> { [BASE] = 50 };
            Eq(new CoreResolver(closedEnv).ComputeMaxCraftAmount(closed, plenty, 9999), 0, "condition unmet: MAX 0");

            var openEnv = new FakeEnvironment().WithConditions(_ => true);
            var open = openEnv.AddRecipe(GATED, 1, new[] { (BASE, 1) });
            Eq(new CoreResolver(openEnv).ComputeMaxCraftAmount(open, plenty, 9999), 50, "condition met: MAX 50");
        }

        // ---- Scenario: a deep x100 chain asked for thousands of crafts ----
        // Every level multiplies the demand again. In int arithmetic that wraps negative, and an
        // empty pool "satisfies" a negative demand — so an impossible craft reads as possible. The
        // coin chain is the shipped example: copper -> silver -> gold -> platinum, x100 each.
        private static void MaxAmountSurvivesDeepStackMultiplication()
        {
            Section("MAX amount survives deep stack multiplication");
            const int COPPER = 71, SILVER = 72, GOLD = 73, PLATINUM = 74, SPENDER = 75;

            var env = new FakeEnvironment();
            env.AddRecipe(SILVER, 1, new[] { (COPPER, 100) });
            env.AddRecipe(GOLD, 1, new[] { (SILVER, 100) });
            env.AddRecipe(PLATINUM, 1, new[] { (GOLD, 100) });
            var spender = env.AddRecipe(SPENDER, 1, new[] { (PLATINUM, 3996) });
            var r = new CoreResolver(env);

            var empty = new Dictionary<int, int>();
            IsFalse(r.CanCraftAmount(spender, empty, 7), "nothing in storage crafts nothing, at any amount");
            Eq(r.ComputeMaxCraftAmount(spender, empty, 9999), 0, "empty storage -> MAX 0");

            // The guard must refuse the overflow, not the craft: one platinum on the shelf is one craft.
            var oneCraftsWorth = new Dictionary<int, int> { [PLATINUM] = 3996 };
            Eq(r.ComputeMaxCraftAmount(spender, oneCraftsWorth, 9999), 1, "stock for exactly one craft -> 1");
        }

        // ---- The reported bug, on the real graphs it was reported against ----
        // The fixtures are minimised slices of real /tsdumps: every line load-bearing, the recipe
        // groups and station tiles a hand-built fixture forgets still intact. MAX is checked against
        // the PLANNER — TryResolveRecipe, what the craft button actually runs — not against the
        // feasibility mirror the search itself uses, so the two cannot agree by construction.
        private static void MaxAmountOnScopedDump()
        {
            Section("MAX amount on the scoped flaming-arrow dumps");
            const int FLAMING_ARROW = 41;

            // The reported world: no torches at all, and the 4th wood for the torches exists only as
            // 3 group substitutes plus a sub-craft from acorns. MAX offered 1.
            AssertScopedDump("flaming-arrow-group-slot.tsdump.txt", FLAMING_ARROW,
                expectedRecipes: 3, expectedStoredTypes: 4, expectedMax: 12, expectedDirectOnlyMax: 0);

            // The same world later: 2 torches in stock, and 107 gel that makes 321 more.
            AssertScopedDump("flaming-arrow-gel-ceiling.tsdump.txt", FLAMING_ARROW,
                expectedRecipes: 3, expectedStoredTypes: 5, expectedMax: 323, expectedDirectOnlyMax: 2);
        }

        private static void AssertScopedDump(string fixture, int targetItem,
            int expectedRecipes, int expectedStoredTypes, int expectedMax, int expectedDirectOnlyMax)
        {
            var env = new FakeEnvironment();
            var available = new Dictionary<int, int>();
            var stations = new HashSet<int>();
            ParseDump(FixturePath(fixture), env, available, stations);

            // ParseDump skips malformed lines silently, which would quietly shrink a committed
            // fixture into a different world that still asserts cleanly.
            Eq(env.AllRecipes.Count, expectedRecipes, $"{fixture}: recipe count");
            Eq(available.Count, expectedStoredTypes, $"{fixture}: stored types");

            var core = new CoreResolver(env) { MaxDepth = 10 };
            var recipe = env.RecipesProducing(targetItem)[0];
            int max = core.ComputeMaxCraftAmount(recipe, available, 9999);

            Eq(PanelDirectOnlyMax(env, recipe, available), expectedDirectOnlyMax, $"{fixture}: OLD direct-stock formula");
            Eq(max, expectedMax, $"{fixture}: MAX offers {expectedMax} crafts");
            IsTrue(PlannerCanCraftAmount(core, recipe, available, max), $"{fixture}: the craft button can plan {max}");
            IsFalse(PlannerCanCraftAmount(core, recipe, available, max + 1), $"{fixture}: it cannot plan {max + 1}");

            // Exhaustive, not just the boundary: every amount the planner can build, up to well past
            // the answer. This is the assertion the binary search cannot satisfy by construction.
            Eq(PlannerMaxCraftAmount(core, recipe, available, expectedMax + 40), expectedMax,
                $"{fixture}: an exhaustive planner scan finds the same ceiling");

            // MAX must never promise more than the planner delivers, for any recipe in the slice.
            foreach (var other in env.AllRecipes)
            {
                int otherMax = core.ComputeMaxCraftAmount(other, available, 9999);
                if (otherMax == 0) continue;

                IsTrue(PlannerCanCraftAmount(core, other, available, otherMax),
                    $"{fixture}: {other.OutputType} x{otherMax} is plannable");
            }
        }

        // Test fixtures are copied next to the test assembly by Tests.csproj.
        private static string FixturePath(string fileName)
            => Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);

        private static bool PlannerCanCraftAmount(CoreResolver core, CoreRecipe recipe, Dictionary<int, int> available, int amount)
            => core.PlannerCanCraftAmount(recipe, available, amount);

        private static int PlannerMaxCraftAmount(CoreResolver core, CoreRecipe recipe, Dictionary<int, int> available, int cap)
        {
            int max = 0;
            for (int amount = 1; amount <= cap; amount++)
                if (PlannerCanCraftAmount(core, recipe, available, amount))
                    max = amount;
            return max;
        }

        // The panel's pre-fix MAX formula: direct stock only, own type plus recipe-group substitutes.
        // Kept as the contrast the fixed number is measured against, the way BuggyPreview is.
        private static int PanelDirectOnlyMax(IRecipeEnvironment env, CoreRecipe recipe, Dictionary<int, int> available)
        {
            int max = 9999;
            foreach (var ingredient in recipe.Ingredients)
            {
                if (ingredient.Stack <= 0) continue;
                available.TryGetValue(ingredient.Type, out int have);
                foreach (int groupId in recipe.AcceptedGroups)
                {
                    if (!env.GroupContains(groupId, ingredient.Type)) continue;
                    foreach (int validItem in env.GroupValidItems(groupId))
                        if (validItem != ingredient.Type && available.TryGetValue(validItem, out int substitute))
                            have += substitute;
                    break;
                }
                max = Math.Min(max, have / ingredient.Stack);
            }
            return max;
        }

        // ---- Scenario 1: two ingredients made from the SAME base ore, only enough ore for one ----
        // This is the canonical "magically get more items" bug: each ingredient checked alone sees
        // the full ore stock, so both report available; the recipe as a whole cannot be crafted.
        private static void SharedBaseContention()
        {
            Section("Shared-base sibling contention (A + B both from ORE_X)");
            const int ORE_X = 10, A = 11, B = 12, TARGET = 13, FURNACE = 100;

            var env = new FakeEnvironment().WithStations(FURNACE);
            env.AddRecipe(A, 1, new[] { (ORE_X, 1) }, tiles: new[] { FURNACE });
            env.AddRecipe(B, 1, new[] { (ORE_X, 1) }, tiles: new[] { FURNACE });
            var target = env.AddRecipe(TARGET, 1, new[] { (A, 1), (B, 1) }, tiles: new[] { FURNACE });
            var r = new CoreResolver(env);

            // Only 1 ore: cannot make both A and B.
            var one = new Dictionary<int, int> { [ORE_X] = 1 };
            IsFalse(r.IsFeasibleFromSnapshot(TARGET, 1, one), "S1 resolver: TARGET NOT craftable with 1 ORE_X");
            IsFalse(ListCraftable(r, target, one), "S1 list-flag: TARGET NOT craftable with 1 ORE_X");

            var buggy = BuggyPreview(r, env, target, one, 1);
            Eq(View(buggy, A).TotalHave, 1, "S1 OLD preview LIES: A shows 1/1 (inflated)");
            Eq(View(buggy, B).TotalHave, 1, "S1 OLD preview LIES: B shows 1/1 (inflated)");

            var fixedView = r.ComputeIngredientPreview(target, one, 1);
            Eq(View(fixedView, A).TotalHave, 0, "S1 NEW preview HONEST: A shows 0/1");
            Eq(View(fixedView, B).TotalHave, 0, "S1 NEW preview HONEST: B shows 0/1");

            // Two ore: genuinely craftable.
            var two = new Dictionary<int, int> { [ORE_X] = 2 };
            IsTrue(r.IsFeasibleFromSnapshot(TARGET, 1, two), "S1 resolver: TARGET craftable with 2 ORE_X");
            IsTrue(ListCraftable(r, target, two), "S1 list-flag: TARGET craftable with 2 ORE_X");
        }

        // ---- Scenario 2: demonite <-> crimtane two-way conversion (single ingredient, cycle) ----
        private static void DemoniteCrimtaneTwoWay()
        {
            Section("Demonite/Crimtane two-way conversion");
            const int DEMO_ORE = 20, DEMO = 21, CRIM = 22, FURNACE = 100;

            var env = new FakeEnvironment().WithStations(FURNACE);
            env.AddRecipe(DEMO, 1, new[] { (DEMO_ORE, 3) }, tiles: new[] { FURNACE });
            var crimRecipe = env.AddRecipe(CRIM, 1, new[] { (DEMO, 1) }, tiles: new[] { FURNACE });
            env.AddRecipe(DEMO, 1, new[] { (CRIM, 1) }, tiles: new[] { FURNACE }); // reverse two-way
            var r = new CoreResolver(env);

            // 2 demonite bars, want 3 crimtane: cannot make the 3rd demonite (no ore; reverse is a cycle).
            var twoBars = new Dictionary<int, int> { [DEMO] = 2 };
            IsFalse(r.IsFeasibleFromSnapshot(CRIM, 3, twoBars), "S2 resolver: 3 CRIM NOT craftable from 2 DEMO (cycle blocked)");

            var buggy = BuggyPreview(r, env, crimRecipe, twoBars, 3);
            Eq(View(buggy, DEMO).TotalHave, 2, "S2 OLD preview: DEMO 2/3 (two-way correctly not inflated)");
            var fixedView = r.ComputeIngredientPreview(crimRecipe, twoBars, 3);
            Eq(View(fixedView, DEMO).TotalHave, 2, "S2 NEW preview: DEMO 2/3 (honest)");

            // 6 ore -> 2 demonite -> 2 crimtane: feasible.
            var sixOre = new Dictionary<int, int> { [DEMO_ORE] = 6 };
            IsTrue(r.IsFeasibleFromSnapshot(CRIM, 2, sixOre), "S2 resolver: 2 CRIM craftable from 6 DEMO_ORE");
            // 5 ore is not enough for 2 demonite (needs 6).
            var fiveOre = new Dictionary<int, int> { [DEMO_ORE] = 5 };
            IsFalse(r.IsFeasibleFromSnapshot(CRIM, 2, fiveOre), "S2 resolver: 2 CRIM NOT craftable from 5 DEMO_ORE");
        }

        // ---- Scenario: "Nothing to Craft" recipes must not show as craftable in the list ----
        // The craft button force-crafts (ResolveForceCraft ignores existing stock of the output), so a
        // recipe whose ingredients are only reachable by looping back through its own output does
        // nothing when clicked. The list flag used to count that stock and advertise it as craftable,
        // which is the mismatch the user reported: green in the list, "Nothing to Craft" on the button.
        private static void NoOpRecipesAreUncraftable()
        {
            Section("No-op recipes are uncraftable in the list");
            const int DEMO_ORE = 20, DEMO = 21, CRIM = 22, FURNACE = 100;

            var env = new FakeEnvironment().WithStations(FURNACE);
            env.AddRecipe(DEMO, 1, new[] { (DEMO_ORE, 3) }, tiles: new[] { FURNACE });
            var crimRecipe = env.AddRecipe(CRIM, 1, new[] { (DEMO, 1) }, tiles: new[] { FURNACE });
            env.AddRecipe(DEMO, 1, new[] { (CRIM, 1) }, tiles: new[] { FURNACE }); // reverse: CRIM -> DEMO
            var r = new CoreResolver(env);

            // Hold ONLY crimtane. CRIM's recipe needs DEMO, and the only way to get DEMO is to convert
            // the crimtane we already hold — force-crafting removes it, so the craft is a pure no-op.
            var onlyCrim = new Dictionary<int, int> { [CRIM] = 5 };
            IsFalse(ListCraftable(r, crimRecipe, onlyCrim),
                "NC-001 CRIM recipe NOT craftable when only its own output is in stock (no-op loop)");

            // Same stock, but the ore for a real demonite bar is present -> a genuine craft exists.
            var crimAndOre = new Dictionary<int, int> { [CRIM] = 5, [DEMO_ORE] = 3 };
            IsTrue(ListCraftable(r, crimRecipe, crimAndOre),
                "NC-002 CRIM recipe craftable when DEMO is reachable without consuming CRIM");

            // Holding the output must not suppress an otherwise-real craft.
            var crimAndDemo = new Dictionary<int, int> { [CRIM] = 5, [DEMO] = 1 };
            IsTrue(ListCraftable(r, crimRecipe, crimAndDemo),
                "NC-003 holding the output does not block a recipe whose ingredients are really in stock");

            // A recipe that consumes its own output is a no-op under force-craft, even with stock.
            var env2 = new FakeEnvironment();
            var selfRecipe = env2.AddRecipe(CRIM, 2, new[] { (CRIM, 1), (DEMO_ORE, 1) });
            var r2 = new CoreResolver(env2);
            var selfStock = new Dictionary<int, int> { [CRIM] = 5, [DEMO_ORE] = 5 };
            IsFalse(ListCraftable(r2, selfRecipe, selfStock),
                "NC-004 recipe consuming its own output is NOT craftable (force-craft drops the stock)");

            // Every recipe for an item being a no-op => the item is uncraftable outright.
            var env3 = new FakeEnvironment();
            var a = env3.AddRecipe(CRIM, 1, new[] { (DEMO, 1) });
            var b = env3.AddRecipe(CRIM, 1, new[] { (DEMO_ORE, 1) });
            env3.AddRecipe(DEMO, 1, new[] { (CRIM, 1) });
            env3.AddRecipe(DEMO_ORE, 1, new[] { (CRIM, 1) });
            var r3 = new CoreResolver(env3);
            var loopOnly = new Dictionary<int, int> { [CRIM] = 5 };
            IsFalse(ListCraftable(r3, a, loopOnly), "NC-005 variant A is a no-op loop");
            IsFalse(ListCraftable(r3, b, loopOnly), "NC-006 variant B is a no-op loop");
        }

        // ---- Scenario: the preview must name the ingredient that actually blocks the recipe ----
        // Reproduces a real save (/tsdump): 7 gold bars of the 10 an AnyGoldBar slot needs, no gold
        // ore and no platinum to substitute, but plenty of sand for the glass. The recipe is
        // genuinely uncraftable — because of the bars. The panel used to gate its "will be
        // sub-crafted" state on the WHOLE plan being feasible, so the gold bar (partial stock) looked
        // healthier than the glass (zero stock but freely craftable from sand), and the player was
        // told they were short of glass.
        private static void BlockingIngredientIsIdentified()
        {
            Section("Preview identifies the blocking ingredient, not the sub-craftable one");
            const int GOLD_BAR = 19, PLAT_BAR = 706, GOLD_ORE = 13;
            const int GLASS = 170, SAND = 169, LENS = 38, DISK = 20042;
            const int ANVIL = 16, FURNACE = 17, BAR_GROUP = 325;

            var env = new FakeEnvironment().WithStations(ANVIL, FURNACE).WithGroup(BAR_GROUP, GOLD_BAR, PLAT_BAR);
            env.AddRecipe(GLASS, 1, new[] { (SAND, 2) }, tiles: new[] { FURNACE });
            env.AddRecipe(GOLD_BAR, 1, new[] { (GOLD_ORE, 4) }, tiles: new[] { FURNACE });
            env.AddRecipe(GOLD_BAR, 1, new[] { (PLAT_BAR, 1) }, tiles: new[] { FURNACE });
            var disk = env.AddRecipe(DISK, 1, new[] { (GOLD_BAR, 10), (GLASS, 3), (LENS, 1) },
                tiles: new[] { ANVIL }, groups: new[] { BAR_GROUP });
            var r = new CoreResolver(env);

            var realSave = new Dictionary<int, int> { [GOLD_BAR] = 7, [SAND] = 1211, [LENS] = 5 };
            IsFalse(ListCraftable(r, disk, realSave), "BI-001 disk is genuinely uncraftable (3 bars short)");

            var views = r.ComputeIngredientPreview(disk, new Dictionary<int, int>(realSave), 1);
            IsFalse(View(views, GOLD_BAR).Satisfiable, "BI-002 gold bar is the blocker (7/10, no ore, no platinum)");
            IsTrue(View(views, GLASS).Satisfiable, "BI-003 glass is NOT the blocker (0/3, sub-craftable from sand)");
            IsTrue(View(views, LENS).Satisfiable, "BI-004 lens is satisfied directly");

            // TotalHave stays the honest directly-held count; sub-craftability is a separate flag.
            Eq(View(views, GOLD_BAR).TotalHave, 7, "BI-005 gold bar still reports its real stock");
            Eq(View(views, GLASS).TotalHave, 0, "BI-006 glass stock is not inflated by sub-craftability");

            // Add the missing bars and every slot becomes satisfiable.
            var enoughBars = new Dictionary<int, int> { [GOLD_BAR] = 7, [PLAT_BAR] = 3, [SAND] = 1211, [LENS] = 5 };
            var okViews = r.ComputeIngredientPreview(disk, new Dictionary<int, int>(enoughBars), 1);
            IsTrue(View(okViews, GOLD_BAR).Satisfiable, "BI-007 gold bar satisfiable once platinum covers the rest");
            IsTrue(View(okViews, GLASS).Satisfiable, "BI-008 glass still satisfiable");

            // Sand for only two glass: the glass slot itself becomes the blocker.
            var thinSand = new Dictionary<int, int> { [GOLD_BAR] = 10, [SAND] = 4, [LENS] = 5 };
            var thinViews = r.ComputeIngredientPreview(disk, new Dictionary<int, int>(thinSand), 1);
            IsTrue(View(thinViews, GOLD_BAR).Satisfiable, "BI-009 gold bar fine when fully stocked");
            IsFalse(View(thinViews, GLASS).Satisfiable, "BI-010 glass IS the blocker when sand runs out");

            // The shared pool still governs: two slots drawing on one ore cannot both be satisfiable.
            const int ORE = 30, A = 31, B = 32, TARGET = 33;
            var env2 = new FakeEnvironment().WithStations(FURNACE);
            env2.AddRecipe(A, 1, new[] { (ORE, 1) }, tiles: new[] { FURNACE });
            env2.AddRecipe(B, 1, new[] { (ORE, 1) }, tiles: new[] { FURNACE });
            var contended = env2.AddRecipe(TARGET, 1, new[] { (A, 1), (B, 1) }, tiles: new[] { FURNACE });
            var r2 = new CoreResolver(env2);
            var oneOre = new Dictionary<int, int> { [ORE] = 1 };
            var shared = r2.ComputeIngredientPreview(contended, oneOre, 1);
            IsTrue(View(shared, A).Satisfiable, "BI-011 first slot claims the only ore");
            IsFalse(View(shared, B).Satisfiable, "BI-012 second slot cannot re-spend it");
        }

        // ---- Scenario: a recipe listing the same item in two slots must consume both amounts ----
        // The step's Consumed map drives ExecutePlan's extraction. Assigning instead of accumulating
        // recorded only the last slot, so the craft extracted less than it used and handed the player
        // the output anyway — a duplication.
        private static void RepeatedIngredientSlotsAccumulate()
        {
            Section("Repeated ingredient slots accumulate into Consumed");
            const int WOOD = 9, TABLE = 40, BENCH = 101;

            var env = new FakeEnvironment().WithStations(BENCH);
            env.AddRecipe(TABLE, 1, new[] { (WOOD, 4), (WOOD, 6) }, tiles: new[] { BENCH });
            var r = new CoreResolver(env);

            var stock = new Dictionary<int, int> { [WOOD] = 100 };
            var steps = new List<CoreStep>();
            IsTrue(r.ResolveRecursive(TABLE, 1, stock, steps, new HashSet<int>(), 0), "RS-001 table resolves");
            Eq(steps.Count, 1, "RS-002 one step");
            Eq(steps[0].Consumed[WOOD], 10, "RS-003 both wood slots recorded (4 + 6)");
            Eq(stock[WOOD], 90, "RS-004 pool deducted the full 10");
        }

        // ---- Scenario: the preview must sum duplicate slots of the same item ----
        // A recipe may name one item twice. Reading only the first slot's stack understates the need,
        // so every slot reads satisfied while the recipe cannot be crafted — no red square anywhere,
        // which is the very failure the Satisfiable flag exists to prevent.
        private static void DuplicateSlotsAreSummedInPreview()
        {
            Section("Preview sums duplicate ingredient slots");
            const int WOOD = 9, TABLE = 40, BENCH = 101;

            var env = new FakeEnvironment().WithStations(BENCH);
            var table = env.AddRecipe(TABLE, 1, new[] { (WOOD, 4), (WOOD, 6) }, tiles: new[] { BENCH });
            var r = new CoreResolver(env);

            var views = r.ComputeIngredientPreview(table, new Dictionary<int, int> { [WOOD] = 6 }, 1);
            Eq(views.Count, 1, "DS-001 one view per distinct item type");
            Eq(View(views, WOOD).Needed, 10, "DS-002 need is the SUM of both slots");
            Eq(View(views, WOOD).TotalHave, 6, "DS-003 stock is not inflated");
            IsFalse(View(views, WOOD).Satisfiable, "DS-004 6 wood cannot cover 10");

            var enough = r.ComputeIngredientPreview(table, new Dictionary<int, int> { [WOOD] = 10 }, 1);
            IsTrue(View(enough, WOOD).Satisfiable, "DS-005 10 wood covers 10");

            // craftAmount multiplies the summed need, not just the first slot's.
            var doubled = r.ComputeIngredientPreview(table, new Dictionary<int, int> { [WOOD] = 19 }, 2);
            Eq(View(doubled, WOOD).Needed, 20, "DS-006 craftAmount applies to the sum");
            IsFalse(View(doubled, WOOD).Satisfiable, "DS-007 19 wood cannot cover 20");
        }

        // ---- Scenario: the preview and the list flag must honour MaxDepth ----
        // The craft button plans through ResolveRecursive, which stops at MaxDepth. The feasibility
        // mirror had no depth limit, so a chain longer than the recursion-depth slider read as
        // craftable everywhere and did nothing when clicked.
        private static void FeasibilityHonoursMaxDepth()
        {
            Section("Feasibility mirrors ResolveRecursive's depth limit");
            const int CHAIN_TOP = 1000, CHAIN_LEN = 12;

            var env = new FakeEnvironment();
            for (int i = CHAIN_TOP; i < CHAIN_TOP + CHAIN_LEN; i++)
                env.AddRecipe(i, 1, new[] { (i + 1, 1) });
            var topRecipe = env.AllRecipes[0];
            var stock = new Dictionary<int, int> { [CHAIN_TOP + CHAIN_LEN] = 5 };

            // Contiguous, not a sample: the two sides last diverged at exactly one value
            // (MaxDepth == chain length - 1), which a sparse sweep walked straight past.
            for (int depth = 1; depth <= CHAIN_LEN + 3; depth++)
            {
                var r = new CoreResolver(env) { MaxDepth = depth };

                var planSteps = new List<CoreStep>();
                bool button = r.ResolveRecursive(CHAIN_TOP, 1, new Dictionary<int, int>(stock),
                    planSteps, new HashSet<int>(), 0);

                bool listFlag = r.RecheckRecipeCraftable(topRecipe, new Dictionary<int, int>(stock),
                    new Dictionary<(int ctx, int group, int type, int stack), bool>());
                bool feasible = r.IsFeasibleFromSnapshot(CHAIN_TOP, 1, new Dictionary<int, int>(stock));
                var views = r.ComputeIngredientPreview(topRecipe, new Dictionary<int, int>(stock), 1);

                Check(listFlag == button, $"MD-{depth:00}a list flag agrees with the craft button at depth {depth}");
                Check(feasible == button, $"MD-{depth:00}b feasibility agrees with the craft button at depth {depth}");
                Check(views[0].Satisfiable == button, $"MD-{depth:00}c preview agrees with the craft button at depth {depth}");
            }

            // A chain that fits must still be craftable — the limit must not reject everything.
            var shallow = new FakeEnvironment();
            shallow.AddRecipe(1, 1, new[] { (2, 1) });
            var deep = new CoreResolver(shallow) { MaxDepth = 10 };
            IsTrue(deep.IsFeasibleFromSnapshot(1, 1, new Dictionary<int, int> { [2] = 3 }),
                "MD-100 a one-link chain within the limit stays feasible");
        }

        // ---- Scenario: a slot must not read satisfiable off the item being crafted ----
        // The craft button force-crafts: existing stock of the OUTPUT is not a material. The preview
        // seeded its cycle guard but left the output stock in the pool, so a recipe that loops back
        // through its own output showed an orange "will be sub-crafted" slot on a dead button.
        private static void PreviewAppliesForceCraftSemantics()
        {
            Section("Preview excludes the output own stock");
            const int DEMO = 21, CRIM = 22, FURNACE = 100;

            var env = new FakeEnvironment().WithStations(FURNACE);
            var crimRecipe = env.AddRecipe(CRIM, 1, new[] { (DEMO, 1) }, tiles: new[] { FURNACE });
            env.AddRecipe(DEMO, 1, new[] { (CRIM, 1) }, tiles: new[] { FURNACE });
            var r = new CoreResolver(env);

            // Only the output in stock: the sole route to DEMO converts the CRIM force-craft drops.
            var onlyCrim = new Dictionary<int, int> { [CRIM] = 5 };
            IsFalse(ListCraftable(r, crimRecipe, onlyCrim), "FC-001 recipe is a no-op loop");
            var views = r.ComputeIngredientPreview(crimRecipe, onlyCrim, 1);
            IsFalse(View(views, DEMO).Satisfiable, "FC-002 ingredient NOT satisfiable from the output stock");

            // A genuine route exists once the ingredient is really obtainable.
            var withDemo = new Dictionary<int, int> { [CRIM] = 5, [DEMO] = 2 };
            var okViews = r.ComputeIngredientPreview(crimRecipe, withDemo, 1);
            IsTrue(View(okViews, DEMO).Satisfiable, "FC-003 satisfiable when the ingredient is really in stock");
        }

        // ---- Scenario: every slot satisfiable must mean the craft button works ----
        // The invariant the whole preview rests on. If the preview says nothing is blocking, the
        // resolver must be able to produce a plan; otherwise the player sees no red square and a
        // "Missing Materials" button, which is the original complaint.
        private static void SatisfiableAgreesWithThePlan()
        {
            Section("All-satisfiable implies a resolvable plan");
            const int ORE = 30, BAR = 31, GEM = 32, TOOL = 33, ANVIL = 16, FURNACE = 17;

            var shapes = new List<(string name, Dictionary<int, int> stock)>
            {
                ("no stock",            new Dictionary<int, int>()),
                ("ore only",            new Dictionary<int, int> { [ORE] = 100 }),
                ("ore and gem",         new Dictionary<int, int> { [ORE] = 100, [GEM] = 5 }),
                ("one ore short",       new Dictionary<int, int> { [ORE] = 19, [GEM] = 5 }),
                ("exact ore",           new Dictionary<int, int> { [ORE] = 20, [GEM] = 5 }),
                ("bars held",           new Dictionary<int, int> { [BAR] = 5, [GEM] = 5 }),
                ("bars short",          new Dictionary<int, int> { [BAR] = 3, [GEM] = 5 }),
                ("bars short, ore too", new Dictionary<int, int> { [BAR] = 3, [ORE] = 4, [GEM] = 5 }),
            };

            foreach (var (name, stock) in shapes)
            {
                var env = new FakeEnvironment().WithStations(ANVIL, FURNACE);
                env.AddRecipe(BAR, 1, new[] { (ORE, 4) }, tiles: new[] { FURNACE });
                var tool = env.AddRecipe(TOOL, 1, new[] { (BAR, 5), (GEM, 1) }, tiles: new[] { ANVIL });
                var r = new CoreResolver(env) { MaxDepth = 10 };

                var views = r.ComputeIngredientPreview(tool, new Dictionary<int, int>(stock), 1);
                bool allSatisfiable = views.TrueForAll(v => v.Satisfiable);

                var steps = new List<CoreStep>();
                bool planned = r.TryResolveRecipe(tool, TOOL, 1, new Dictionary<int, int>(stock),
                    steps, new HashSet<int> { TOOL }, 0);

                Check(allSatisfiable == planned, $"SA-{name}: preview ({allSatisfiable}) agrees with the plan ({planned})");
            }
        }

        // ---- Scenario: an earlier slot sub-craft really does claim shared base material ----
        // Pins the ordering semantics: sand burned by the glass slot is no longer free for the sand
        // slot, so the later slot reports what is still claimable rather than the raw stack count.
        private static void SubCraftClaimsSharedBaseMaterial()
        {
            Section("Sub-craft deductions carry into later slots");
            const int SAND = 169, GLASS = 170, TARGET = 41, FURNACE = 17;

            var env = new FakeEnvironment().WithStations(FURNACE);
            env.AddRecipe(GLASS, 1, new[] { (SAND, 2) }, tiles: new[] { FURNACE });
            var target = env.AddRecipe(TARGET, 1, new[] { (GLASS, 1), (SAND, 1) }, tiles: new[] { FURNACE });
            var r = new CoreResolver(env);

            // 3 sand: 2 become the glass, 1 is left for the sand slot.
            var three = r.ComputeIngredientPreview(target, new Dictionary<int, int> { [SAND] = 3 }, 1);
            IsTrue(View(three, GLASS).Satisfiable, "SC-001 glass sub-crafts from 2 of the 3 sand");
            Eq(View(three, SAND).TotalHave, 1, "SC-002 sand slot sees the 1 sand still free");
            IsTrue(View(three, SAND).Satisfiable, "SC-003 sand slot is covered");

            // 2 sand: the glass consumes both, nothing is left — the sand slot is the blocker.
            var two = r.ComputeIngredientPreview(target, new Dictionary<int, int> { [SAND] = 2 }, 1);
            IsTrue(View(two, GLASS).Satisfiable, "SC-004 glass still sub-craftable");
            Eq(View(two, SAND).TotalHave, 0, "SC-005 sand slot sees nothing free (glass claimed it)");
            IsFalse(View(two, SAND).Satisfiable, "SC-006 sand slot blocks the recipe");

            var steps = new List<CoreStep>();
            IsFalse(r.TryResolveRecipe(target, TARGET, 1, new Dictionary<int, int> { [SAND] = 2 },
                steps, new HashSet<int> { TARGET }, 0), "SC-007 the plan agrees: 2 sand is not enough");
        }

        // ---- Scenario: the list flag must agree with the craft button ----
        // The grid paints from RecheckRecipeCraftable; the button plans through TryResolveRecipe.
        // Every disagreement is a recipe that looks craftable and does nothing when clicked, or a
        // craftable recipe the player never sees. These three shapes each produced one.
        private static void ListFlagAgreesWithCraftButton()
        {
            Section("List flag agrees with the craft button");
            const int WOOD = 9, TABLE = 40, BENCH = 101;

            // Duplicate slots: the per-slot checks both measure against the full stock, so only the
            // shared confirm catches the contention — and it used to be skipped when all slots were
            // individually satisfied.
            foreach (int wood in new[] { 5, 6, 9, 10, 20 })
            {
                var env = new FakeEnvironment().WithStations(BENCH);
                var table = env.AddRecipe(TABLE, 1, new[] { (WOOD, 4), (WOOD, 6) }, tiles: new[] { BENCH });
                var r = new CoreResolver(env);
                var stock = new Dictionary<int, int> { [WOOD] = wood };

                bool listFlag = ListCraftableNoPrefilter(r, table, stock);
                var steps = new List<CoreStep>();
                bool button = r.TryResolveRecipe(table, TABLE, 1, new Dictionary<int, int>(stock),
                    steps, new HashSet<int> { TABLE }, 0);
                Check(listFlag == button, $"LF-dup{wood:00} wood={wood}: list ({listFlag}) agrees with button ({button})");
            }

            // Self-loop: the prefilter must not satisfy a slot by producing the very item being
            // crafted — ResolveRecursive holds the output in `resolving` and forbids exactly that.
            {
                const int A = 700, B = 701;
                var env = new FakeEnvironment();
                var selfRecipe = env.AddRecipe(A, 3, new[] { (A, 3) });
                env.AddRecipe(A, 1, new[] { (B, 2) });
                var r = new CoreResolver(env);
                var stock = new Dictionary<int, int> { [B] = 20 };

                bool listFlag = ListCraftableNoPrefilter(r, selfRecipe, stock);
                var steps = new List<CoreStep>();
                bool button = r.TryResolveRecipe(selfRecipe, A, 3, new Dictionary<int, int>(stock),
                    steps, new HashSet<int> { A }, 0);
                IsFalse(listFlag, "LF-loop1 a recipe that only loops through its own output is not craftable");
                Check(listFlag == button, $"LF-loop2 list ({listFlag}) agrees with button ({button})");
            }

            // Recipe groups: the prefilter must consider substitutes when sub-crafting, not just the
            // named item, or a craftable recipe never appears in the grid.
            {
                const int GOLD = 19, PLAT = 706, PLAT_ORE = 702, LENS = 38, CROWN = 20;
                const int ANVIL = 16, FURNACE = 17, GROUP = 325;

                var env = new FakeEnvironment().WithStations(ANVIL, FURNACE).WithGroup(GROUP, GOLD, PLAT);
                env.AddRecipe(PLAT, 1, new[] { (PLAT_ORE, 4) }, tiles: new[] { FURNACE });
                var crown = env.AddRecipe(CROWN, 1, new[] { (GOLD, 10), (LENS, 1) },
                    tiles: new[] { ANVIL }, groups: new[] { GROUP });
                var r = new CoreResolver(env);

                var withOre = new Dictionary<int, int> { [PLAT] = 2, [PLAT_ORE] = 40, [LENS] = 5 };
                var steps = new List<CoreStep>();
                bool button = r.TryResolveRecipe(crown, CROWN, 1, new Dictionary<int, int>(withOre),
                    steps, new HashSet<int> { CROWN }, 0);
                IsTrue(button, "LF-grp1 the plan sub-crafts the substitute");
                IsTrue(ListCraftableNoPrefilter(r, crown, withOre),
                    "LF-grp2 the list flag sees it too (prefilter is group-aware)");

                var noOre = new Dictionary<int, int> { [PLAT] = 2, [LENS] = 5 };
                IsFalse(ListCraftableNoPrefilter(r, crown, noOre),
                    "LF-grp3 not craftable when the substitute cannot be made either");
            }
        }

        // ---- Scenario: an ingredient verdict cached for one recipe must not leak to another ----
        // The prefilter's answer depends on which recipe group may fill the slot, so two recipes
        // naming the same item with different accepted groups must not share a cache entry.
        private static void IngredientCacheIsScopedByGroup()
        {
            Section("Ingredient cache is scoped by accepted group");
            const int GOLD = 19, PLAT = 706, PLAT_ORE = 702, ANVIL = 16, FURNACE = 17, GROUP = 325;
            const int WITH_GROUP = 50, WITHOUT_GROUP = 51;

            var env = new FakeEnvironment().WithStations(ANVIL, FURNACE).WithGroup(GROUP, GOLD, PLAT);
            env.AddRecipe(PLAT, 1, new[] { (PLAT_ORE, 4) }, tiles: new[] { FURNACE });
            var grouped = env.AddRecipe(WITH_GROUP, 1, new[] { (GOLD, 10) },
                tiles: new[] { ANVIL }, groups: new[] { GROUP });
            var ungrouped = env.AddRecipe(WITHOUT_GROUP, 1, new[] { (GOLD, 10) }, tiles: new[] { ANVIL });
            var r = new CoreResolver(env);

            // Only platinum ore: the grouped recipe can be made, the ungrouped one cannot.
            var stock = new Dictionary<int, int> { [PLAT_ORE] = 40 };
            var sharedCache = new Dictionary<(int ctx, int group, int type, int stack), bool>();

            bool groupedFirst = r.RecheckRecipeCraftable(grouped, new Dictionary<int, int>(stock), sharedCache);
            bool ungroupedAfter = r.RecheckRecipeCraftable(ungrouped, new Dictionary<int, int>(stock), sharedCache);
            IsTrue(groupedFirst, "IC-001 grouped recipe is craftable via the substitute");
            IsFalse(ungroupedAfter, "IC-002 ungrouped recipe is NOT craftable, despite the shared cache");

            // Same pair, evaluated in the other order — the cache must not poison either direction.
            var reverseCache = new Dictionary<(int ctx, int group, int type, int stack), bool>();
            bool ungroupedFirst = r.RecheckRecipeCraftable(ungrouped, new Dictionary<int, int>(stock), reverseCache);
            bool groupedAfter = r.RecheckRecipeCraftable(grouped, new Dictionary<int, int>(stock), reverseCache);
            IsFalse(ungroupedFirst, "IC-003 ungrouped recipe still not craftable when checked first");
            IsTrue(groupedAfter, "IC-004 grouped recipe still craftable when checked second");
        }

        // ---- Scenario: a recipe-group slot draws from every accepted member ----
        // Vanilla counts a group in aggregate: 3 gold bars plus 7 platinum bars fill a 10-bar slot.
        // Committing the slot to one concrete type meant holding a few of the NAMED item turned a
        // craftable recipe uncraftable — picking up one gold bar broke a recipe that worked on
        // platinum alone.
        private static void RecipeGroupSlotsMixMembers()
        {
            Section("Recipe-group slot draws from every accepted member");
            const int GOLD = 19, PLAT = 706, GOLD_ORE = 13, LENS = 38, DISK = 20042;
            const int ANVIL = 16, FURNACE = 17, GROUP = 325;

            FakeEnvironment Build(out CoreRecipe disk)
            {
                var e = new FakeEnvironment().WithStations(ANVIL, FURNACE).WithGroup(GROUP, GOLD, PLAT);
                e.AddRecipe(GOLD, 1, new[] { (GOLD_ORE, 4) }, tiles: new[] { FURNACE });
                disk = e.AddRecipe(DISK, 1, new[] { (GOLD, 10), (LENS, 1) },
                    tiles: new[] { ANVIL }, groups: new[] { GROUP });
                return e;
            }

            void Case(string label, Dictionary<int, int> stock, bool expected)
            {
                var env = Build(out var disk);
                var r = new CoreResolver(env);
                var steps = new List<CoreStep>();
                bool planned = r.TryResolveRecipe(disk, DISK, 1, new Dictionary<int, int>(stock),
                    steps, new HashSet<int> { DISK }, 0);
                Check(planned == expected, $"GM-{label}: plan {planned}, expected {expected}");
                Check(ListCraftableNoPrefilter(r, disk, stock) == expected,
                    $"GM-{label}-list: list flag agrees");
                var views = r.ComputeIngredientPreview(disk, new Dictionary<int, int>(stock), 1);
                Check(views.TrueForAll(v => v.Satisfiable) == expected, $"GM-{label}-view: preview agrees");
            }

            Case("all-plat ", new Dictionary<int, int> { [PLAT] = 99, [LENS] = 5 }, true);
            Case("all-gold ", new Dictionary<int, int> { [GOLD] = 99, [LENS] = 5 }, true);
            Case("mixed    ", new Dictionary<int, int> { [GOLD] = 3, [PLAT] = 7, [LENS] = 5 }, true);
            Case("mixed-big", new Dictionary<int, int> { [GOLD] = 3, [PLAT] = 99, [LENS] = 5 }, true);
            Case("one-short", new Dictionary<int, int> { [GOLD] = 3, [PLAT] = 6, [LENS] = 5 }, false);
            Case("gold+ore ", new Dictionary<int, int> { [GOLD] = 3, [GOLD_ORE] = 28, [LENS] = 5 }, true);
            Case("plat+ore ", new Dictionary<int, int> { [PLAT] = 3, [GOLD_ORE] = 28, [LENS] = 5 }, true);
            Case("no-lens  ", new Dictionary<int, int> { [GOLD] = 99 }, false);

            // The consumed map must name both members, or ExecutePlan extracts the wrong items.
            var mixEnv = Build(out var mixDisk);
            var mixR = new CoreResolver(mixEnv);
            var mixSteps = new List<CoreStep>();
            mixR.TryResolveRecipe(mixDisk, DISK, 1,
                new Dictionary<int, int> { [GOLD] = 3, [PLAT] = 7, [LENS] = 5 },
                mixSteps, new HashSet<int> { DISK }, 0);
            var final = mixSteps[mixSteps.Count - 1];
            Eq(final.Consumed[GOLD], 3, "GM-consume-gold both members recorded");
            Eq(final.Consumed[PLAT], 7, "GM-consume-plat both members recorded");
            Eq(final.Consumed[LENS], 1, "GM-consume-lens");
        }

        // ---- Scenario: taking something out of storage is not recursion ----
        // The depth limit bounds how far the resolver may CHAIN crafts. An ingredient sitting in
        // stock costs no depth, so a one-craft recipe works even at the lowest slider setting.
        private static void DepthLimitsCraftsNotLookups()
        {
            Section("Depth limit bounds crafts, not stock lookups");
            const int PLANK = 500, LOG = 501, BENCH = 101;

            var env = new FakeEnvironment().WithStations(BENCH);
            var plank = env.AddRecipe(PLANK, 1, new[] { (LOG, 2) }, tiles: new[] { BENCH });
            var stock = new Dictionary<int, int> { [LOG] = 5 };

            foreach (int depth in new[] { 0, 1, 2, 10 })
            {
                var r = new CoreResolver(env) { MaxDepth = depth };
                var steps = new List<CoreStep>();
                bool planned = r.ResolveRecursive(PLANK, 1, new Dictionary<int, int>(stock),
                    steps, new HashSet<int>(), 0);
                Check(planned, $"DL-{depth:00} a single craft off in-stock material works at MaxDepth {depth}");
                Check(ListCraftableNoPrefilter(r, plank, stock), $"DL-{depth:00}-list list flag agrees");
            }
        }

        // ---- Scenario: a failed resolve must not spend the caller's pool ----
        // Partial stock is deducted before the resolver knows a plan exists. Every false path has to
        // hand it back, or the next caller reads a pool that was quietly drained by a failed attempt.
        private static void FailedResolveRestoresPool()
        {
            Section("Failed resolve leaves the pool untouched");
            const int BAR = 700, ORE = 701, A = 800, B = 801;

            // Not enough ore to cover the deficit: the 3 bars already held must survive.
            var env = new FakeEnvironment();
            env.AddRecipe(BAR, 1, new[] { (ORE, 5) });
            var r = new CoreResolver(env);
            var pool = new Dictionary<int, int> { [BAR] = 3, [ORE] = 2 };
            var steps = new List<CoreStep>();

            IsFalse(r.ResolveRecursive(BAR, 10, pool, steps, new HashSet<int>(), 0),
                "PR-001 10 bars cannot be made from 3 bars and 2 ore");
            Eq(pool[BAR], 3, "PR-002 the 3 held bars are still there");
            Eq(pool[ORE], 2, "PR-003 the ore is still there");

            // Same via the cycle guard: A and B convert into each other and nothing else.
            var cyclic = new FakeEnvironment();
            cyclic.AddRecipe(A, 1, new[] { (B, 1) });
            cyclic.AddRecipe(B, 1, new[] { (A, 1) });
            var r2 = new CoreResolver(cyclic);
            var cyclePool = new Dictionary<int, int> { [A] = 1, [B] = 1 };
            var steps2 = new List<CoreStep>();

            IsFalse(r2.ResolveRecursive(A, 5, cyclePool, steps2, new HashSet<int>(), 0),
                "PR-004 a two-way loop cannot conjure the shortfall");
            Eq(cyclePool[A], 1, "PR-005 A survives the failed attempt");
            Eq(cyclePool[B], 1, "PR-006 B survives the failed attempt");

            // A successful resolve still consumes, of course.
            var okPool = new Dictionary<int, int> { [BAR] = 3, [ORE] = 40 };
            var steps3 = new List<CoreStep>();
            IsTrue(r.ResolveRecursive(BAR, 10, okPool, steps3, new HashSet<int>(), 0),
                "PR-007 10 bars from 3 bars and 40 ore");
            Eq(okPool[BAR], 0, "PR-008 the held bars were spent");
            Eq(okPool[ORE], 5, "PR-009 35 ore became the other 7 bars");
        }

        // ---- Scenario 3: recipe-group contention (copper/tin bars share a group) ----
        // The preview's group-sum double-counts a single shared bar across two slots even without
        // the recursive inflation — the deducting pool is what fixes it.
        private static void RecipeGroupContention()
        {
            Section("Recipe-group contention (copper/tin shared group)");
            const int COPPER = 31, TIN = 32, WIDGET = 34, BAR_GROUP = 1000;

            var env = new FakeEnvironment().WithGroup(BAR_GROUP, COPPER, TIN);
            // Both slots accept the group, so either bar can fill either slot.
            var widget = env.AddRecipe(WIDGET, 1, new[] { (COPPER, 1), (TIN, 1) }, groups: new[] { BAR_GROUP });
            var r = new CoreResolver(env);

            var oneCopper = new Dictionary<int, int> { [COPPER] = 1 };
            IsFalse(r.IsFeasibleFromSnapshot(WIDGET, 1, oneCopper), "S3 resolver: WIDGET NOT craftable from 1 bar");
            IsFalse(ListCraftable(r, widget, oneCopper), "S3 list-flag: WIDGET NOT craftable from 1 bar");

            var buggy = BuggyPreview(r, env, widget, oneCopper, 1);
            Eq(View(buggy, TIN).TotalHave, 1, "S3 OLD preview LIES: TIN slot shows 1/1 (counts the copper twice)");

            var fixedView = r.ComputeIngredientPreview(widget, oneCopper, 1);
            Eq(View(fixedView, COPPER).TotalHave, 1, "S3 NEW preview: COPPER slot 1/1 (claims the one bar)");
            Eq(View(fixedView, TIN).TotalHave, 0, "S3 NEW preview HONEST: TIN slot 0/1 (bar already claimed)");

            var copperAndTin = new Dictionary<int, int> { [COPPER] = 1, [TIN] = 1 };
            IsTrue(r.IsFeasibleFromSnapshot(WIDGET, 1, copperAndTin), "S3 resolver: WIDGET craftable from 1 copper + 1 tin");
            var bothView = r.ComputeIngredientPreview(widget, copperAndTin, 1);
            Eq(View(bothView, COPPER).TotalHave, 1, "S3 NEW preview: COPPER 1/1 with both bars");
            Eq(View(bothView, TIN).TotalHave, 1, "S3 NEW preview: TIN 1/1 with both bars");
        }

        // ---- Scenario 4: a recipe whose alternative path sub-crafts at a missing station ----
        // The station-feasible conversion must win over a path that needs a station you don't have,
        // in DEFAULT mode (no lock). Regression guard for the "missing stations" fix.
        private static void StationShadowingSubCraft()
        {
            Section("Station shadowing: pick the fully-station-feasible recipe");
            const int DEMO = 21, CRIM = 22, HELL_BAR = 40, HELL_ORE = 41, FURNACE = 100, HELLFORGE = 200;

            var env = new FakeEnvironment().WithStations(FURNACE); // no hellforge
            // Path 1: CRIM from DEMO at a furnace (we have the furnace).
            env.AddRecipe(CRIM, 1, new[] { (DEMO, 1) }, tiles: new[] { FURNACE });
            // Path 2: CRIM from a hell bar, which is sub-crafted at a hellforge we DON'T have.
            env.AddRecipe(CRIM, 1, new[] { (HELL_BAR, 1) }, tiles: new[] { FURNACE });
            env.AddRecipe(HELL_BAR, 1, new[] { (HELL_ORE, 1) }, tiles: new[] { HELLFORGE });
            var r = new CoreResolver(env);

            // Have demonite AND hell ore, but no hellforge: must resolve via the demonite path.
            var stock = new Dictionary<int, int> { [DEMO] = 5, [HELL_ORE] = 5 };
            IsTrue(r.IsFeasibleFromSnapshot(CRIM, 1, stock), "S4 resolver: CRIM craftable via furnace path (default mode)");

            // Sanity: with neither furnace path material nor hellforge, it is genuinely infeasible.
            var onlyHellOre = new Dictionary<int, int> { [HELL_ORE] = 5 };
            IsFalse(r.IsFeasibleFromSnapshot(CRIM, 1, onlyHellOre), "S4 resolver: CRIM infeasible when only the hellforge path exists");
        }

        // ---- Scenario 4b: the same, but the station-BLOCKED recipe is registered FIRST ----
        // Exercises the fallback/rollback path itself (not just the top-level ordering heuristic):
        // the resolver must reject the hellforge sub-craft, roll back, and resolve via the furnace path.
        private static void StationFallbackOrderIndependent()
        {
            Section("Station fallback triggers even when the blocked recipe is first");
            const int DEMO = 21, CRIM = 22, HELL_BAR = 40, HELL_ORE = 41, FURNACE = 100, HELLFORGE = 200;

            var env = new FakeEnvironment().WithStations(FURNACE); // no hellforge
            // Blocked path FIRST: CRIM <- HELL_BAR, and HELL_BAR needs a hellforge we don't have.
            env.AddRecipe(CRIM, 1, new[] { (HELL_BAR, 1) }, tiles: new[] { FURNACE });
            env.AddRecipe(HELL_BAR, 1, new[] { (HELL_ORE, 1) }, tiles: new[] { HELLFORGE });
            // Good path registered LAST: CRIM <- DEMO at a furnace.
            env.AddRecipe(CRIM, 1, new[] { (DEMO, 1) }, tiles: new[] { FURNACE });
            var r = new CoreResolver(env);

            var stock = new Dictionary<int, int> { [DEMO] = 5, [HELL_ORE] = 5 };
            IsTrue(r.IsFeasibleFromSnapshot(CRIM, 1, stock),
                "S4b resolver: CRIM resolves via furnace path even though the hellforge path is registered first");
        }

        // ---- Scenario 5: plain sanity (direct have, simple shortfall) ----
        private static void DirectAndSimpleSanity()
        {
            Section("Direct/simple sanity");
            const int ORE = 50, BAR = 51, FURNACE = 100;

            var env = new FakeEnvironment().WithStations(FURNACE);
            var barRecipe = env.AddRecipe(BAR, 1, new[] { (ORE, 3) }, tiles: new[] { FURNACE });
            var r = new CoreResolver(env);

            var threeOre = new Dictionary<int, int> { [ORE] = 3 };
            IsTrue(r.IsFeasibleFromSnapshot(BAR, 1, threeOre), "S5 resolver: 1 BAR craftable from 3 ORE");
            Eq(View(r.ComputeIngredientPreview(barRecipe, threeOre, 1), ORE).TotalHave, 3, "S5 preview: ORE 3/3");

            var twoOre = new Dictionary<int, int> { [ORE] = 2 };
            IsFalse(r.IsFeasibleFromSnapshot(BAR, 1, twoOre), "S5 resolver: 1 BAR NOT craftable from 2 ORE");
            Eq(View(r.ComputeIngredientPreview(barRecipe, twoOre, 1), ORE).TotalHave, 2, "S5 preview: ORE 2/3 (honest shortfall)");

            // craftAmount scales need.
            var sixOre = new Dictionary<int, int> { [ORE] = 6 };
            IsTrue(r.IsFeasibleFromSnapshot(BAR, 2, sixOre), "S5 resolver: 2 BAR craftable from 6 ORE");
            Eq(View(r.ComputeIngredientPreview(barRecipe, sixOre, 2), ORE).Needed, 6, "S5 preview: need scales with craftAmount (6)");
        }

        // ---- Reachability: the optimized worklist must equal the old naive fixpoint exactly ----
        // ComputeReachableTypes was rewritten from a re-scan-every-recipe-every-pass fixpoint
        // (quadratic on dependency chains — the open-terminal freeze) to worklist propagation.
        // ReachableNaive below is the OLD algorithm verbatim, used as the oracle.
        private static void ReachabilityEquivalence()
        {
            Section("Reachability: worklist == naive fixpoint");

            // Shaped case 1: linear chain 0->1->...->K from seed {0} (the pathological O(R^2) shape).
            {
                var env = new FakeEnvironment();
                for (int i = 0; i < 50; i++) env.AddRecipe(i + 1, 1, new[] { (i, 1) });
                AssertReachableEquiv(env, new Dictionary<int, int> { [0] = 1 }, "linear chain K=50");
            }

            // Shaped case 2: a cycle, a recipe-group ingredient, and a station we lack (never reachable).
            {
                var env = new FakeEnvironment().WithStations(100).WithGroup(1000, 200, 201);
                env.AddRecipe(2, 1, new[] { (0, 1) }, tiles: new[] { 100 });
                env.AddRecipe(0, 1, new[] { (2, 1) }, tiles: new[] { 100 });        // cycle 0<->2
                env.AddRecipe(3, 1, new[] { (200, 1) }, groups: new[] { 1000 });    // satisfiable via 201
                env.AddRecipe(4, 1, new[] { (2, 1), (3, 1) }, tiles: new[] { 100 });
                env.AddRecipe(5, 1, new[] { (4, 1) }, tiles: new[] { 999 });        // station 999 missing
                AssertReachableEquiv(env, new Dictionary<int, int> { [0] = 5, [201] = 1 }, "cycle+group+missing-station");
            }

            // Randomized worlds: cycles, self-loops, groups, unsatisfiable stations, multi-ingredient.
            var rng = new Random(0xC0FFEE);
            bool allEq = true;
            int firstFail = -1;
            for (int trial = 0; trial < 400 && allEq; trial++)
            {
                var env = BuildRandomEnv(rng, out var available);
                var core = new CoreResolver(env);
                if (!core.ComputeReachableTypes(available).SetEquals(ReachableNaive(env, available)))
                {
                    allEq = false;
                    firstFail = trial;
                }
            }
            Check(allEq, $"reachable equivalence over 400 random worlds (first mismatch: {firstFail})");
        }

        // Proves the rewrite is not just correct but materially faster on the shape that caused the
        // freeze: a long dependency chain, where the naive fixpoint does one pass per link.
        private static void ReachabilityScaleBenchmark()
        {
            Section("Reachability scale benchmark (long dependency chain)");
            const int K = 5000;
            var env = new FakeEnvironment();
            // Register in REVERSE dependency order (produces K first, ..., produces 1 last). This is the
            // naive fixpoint's worst case — one new item per full pass, K passes over K recipes = O(K^2) —
            // and it mirrors reality: mods register recipes in load order, not dependency order.
            for (int i = K - 1; i >= 0; i--) env.AddRecipe(i + 1, 1, new[] { (i, 1) });
            var available = new Dictionary<int, int> { [0] = 1 };
            var core = new CoreResolver(env);

            // Warm + measure the worklist.
            var fast = core.ComputeReachableTypes(available);
            var sw = Stopwatch.StartNew();
            fast = core.ComputeReachableTypes(available);
            sw.Stop();
            long fastMs = sw.ElapsedMilliseconds;

            sw.Restart();
            var naive = ReachableNaive(env, available);
            sw.Stop();
            long naiveMs = sw.ElapsedMilliseconds;

            Check(fast.SetEquals(naive), $"scale: worklist == naive on K={K} chain");
            Check(fast.Count == K + 1, $"scale: all {K + 1} types reachable (got {fast.Count})");
            string ratio = fastMs > 0 ? $"x{(double)naiveMs / fastMs:0.0}" : ">>";
            Console.WriteLine($"   TIME  worklist={fastMs}ms  naive={naiveMs}ms  speedup {ratio}  (K={K} recipes)");
            // Generous bound (the real gap is orders of magnitude); guards against a regression to quadratic.
            Check(fastMs * 4 < naiveMs || naiveMs < 4, $"scale: worklist materially faster (fast={fastMs}ms, naive={naiveMs}ms)");
        }

        // Wall-time of ComputeReachableTypes on a realistic heavy-modpack-scale branching graph,
        // to decide whether the (currently single-frame) call needs to be spread across frames.
        private static void ReachabilityRealisticScaleBenchmark()
        {
            Section("Reachability realistic-scale wall time (branching DAG)");
            const int Recipes = 20000;
            const int BaseItems = 400;
            var env = new FakeEnvironment().WithStations(100).WithGroup(1000, 0, 1, 2, 3, 4);
            var rng = new Random(99);
            for (int i = 0; i < Recipes; i++)
            {
                int outType = BaseItems + i;
                int ingCount = rng.Next(1, 5);
                var ings = new (int, int)[ingCount];
                for (int j = 0; j < ingCount; j++) ings[j] = (rng.Next(BaseItems + i), rng.Next(1, 4)); // depends on earlier items
                int[] tiles = rng.Next(4) == 0 ? null : new[] { 100 };
                int[] groups = rng.Next(6) == 0 ? new[] { 1000 } : null;
                env.AddRecipe(outType, rng.Next(1, 3), ings, tiles, groups);
            }
            var available = new Dictionary<int, int>();
            for (int i = 0; i < 100; i++) available[rng.Next(BaseItems)] = rng.Next(1, 50);

            var core = new CoreResolver(env);
            core.ComputeReachableTypes(available); // warm

            long best = long.MaxValue;
            int count = 0;
            for (int run = 0; run < 5; run++)
            {
                var sw = Stopwatch.StartNew();
                var reachable = core.ComputeReachableTypes(available);
                sw.Stop();
                count = reachable.Count;
                best = Math.Min(best, sw.ElapsedMilliseconds);
            }
            Console.WriteLine($"   TIME  ComputeReachableTypes = {best}ms  ({Recipes} recipes, {count} reachable types, best of 5)");
            // A single frame at 60fps is ~16.7ms. Flag if one call eats more than a third of a frame.
            Check(best <= 6, $"reachability stays well under one frame at {Recipes} recipes ({best}ms)");
        }

        // Real-game data: compares the current FULL revalidation (recompute reachable + re-check
        // every recipe — what commit 506eda8 runs on each storage change) against a TARGETED update
        // (only recipes that use a changed item type as an ingredient, plus the result recipes).
        // Skips silently if no /tsdump file is present, so it never breaks the suite elsewhere.
        private static void RealDumpBenchmark()
        {
            Section("Real-game dump: full vs targeted craftability revalidation");
            string dump = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "My Games", "Terraria", "tModLoader", "ts_recipe_dump.txt");
            if (!File.Exists(dump)) { Console.WriteLine($"   SKIP  no dump at {dump}"); return; }

            var env = new FakeEnvironment();
            var available = new Dictionary<int, int>();
            var stations = new HashSet<int>();
            ParseDump(dump, env, available, stations);
            var recipes = env.AllRecipes;
            Console.WriteLine($"   loaded {recipes.Count} recipes, {available.Count} stored types, {stations.Count} stations");

            var core = new CoreResolver(env) { MaxDepth = 10 };

            // Reverse indices, mirroring the panel's RebuildIngredientIndex (incl. group substitutes).
            var ingIndex = new Dictionary<int, List<int>>();
            var outIndex = new Dictionary<int, List<int>>();
            for (int i = 0; i < recipes.Count; i++)
            {
                var r = recipes[i];
                Add(outIndex, r.OutputType, i);
                foreach (var ing in r.Ingredients)
                {
                    Add(ingIndex, ing.Type, i);
                    foreach (int gid in r.AcceptedGroups)
                        if (env.GroupContains(gid, ing.Type))
                            foreach (int v in env.GroupValidItems(gid))
                                if (v != ing.Type) Add(ingIndex, v, i);
                }
            }

            // FULL revalidation (current per-storage-change behavior).
            var sw = Stopwatch.StartNew();
            var reachable = core.ComputeReachableTypes(available);
            long reachMs = sw.ElapsedMilliseconds;
            var canCraft = new bool[recipes.Count];
            var ingCacheFull = new Dictionary<(int ctx, int group, int type, int stack), bool>();
            for (int i = 0; i < recipes.Count; i++)
                canCraft[i] = core.IsRecipeCraftable(recipes[i], reachable, available, ingCacheFull);
            sw.Stop();
            long fullMs = sw.ElapsedMilliseconds;
            Console.WriteLine($"   FULL revalidation: {fullMs}ms (of which reachable {reachMs}ms), {canCraft.Count(b => b)} craftable");

            // Simulate crafting the first craftable recipe: consume its ingredients, produce its output.
            int craftIdx = -1;
            for (int i = 0; i < recipes.Count; i++)
                if (canCraft[i] && recipes[i].Ingredients.Count >= 1) { craftIdx = i; break; }
            if (craftIdx < 0) { Console.WriteLine("   no craftable recipe to simulate; skipping"); return; }

            var crafted = recipes[craftIdx];
            var after = new Dictionary<int, int>(available);
            var changed = new HashSet<int>();
            foreach (var ing in crafted.Ingredients)
                if (after.TryGetValue(ing.Type, out int h)) { after[ing.Type] = Math.Max(0, h - ing.Stack); changed.Add(ing.Type); }
            after.TryGetValue(crafted.OutputType, out int oh);
            after[crafted.OutputType] = oh + crafted.OutputStack;
            changed.Add(crafted.OutputType);

            // Oracle: a FULL recompute after the craft (the authoritative result every variant is checked against).
            var reachableAfter = core.ComputeReachableTypes(after);
            var fullAfter = new bool[recipes.Count];
            var ingCacheO = new Dictionary<(int ctx, int group, int type, int stack), bool>();
            for (int i = 0; i < recipes.Count; i++)
                fullAfter[i] = core.IsRecipeCraftable(recipes[i], reachableAfter, after, ingCacheO);

            // Split the change into consumed (decreased) and produced (increased) types.
            var decreased = new HashSet<int>();
            var increased = new HashSet<int>();
            foreach (int t in changed)
            {
                available.TryGetValue(t, out int b4);
                after.TryGetValue(t, out int af);
                if (af < b4) decreased.Add(t);
                else if (af > b4) increased.Add(t);
            }

            // Variant A — every recipe touching a changed type (the broad "contains changed ingredient" set).
            var affectedA = new HashSet<int>();
            foreach (int t in changed)
            {
                if (ingIndex.TryGetValue(t, out var a)) affectedA.UnionWith(a);
                if (outIndex.TryGetValue(t, out var b)) affectedA.UnionWith(b);
            }

            // Variant B — direction-aware: a consumed item can only DEMOTE a currently-craftable recipe;
            // a produced item can only PROMOTE a currently-uncraftable one. Plus the result recipe(s).
            var affectedB = new HashSet<int>();
            foreach (int t in decreased)
                if (ingIndex.TryGetValue(t, out var a)) foreach (int i in a) if (canCraft[i]) affectedB.Add(i);
            foreach (int t in increased)
                if (ingIndex.TryGetValue(t, out var a)) foreach (int i in a) if (!canCraft[i]) affectedB.Add(i);
            foreach (int t in increased)
                if (outIndex.TryGetValue(t, out var b)) affectedB.UnionWith(b);

            long timeReachAfter = MeasureMs(() => core.ComputeReachableTypes(after));
            var (msA, matchA, missA) = MeasureTargeted(core, recipes, affectedA, reachableAfter, after, fullAfter, canCraft, useGate: true);
            var (msB, matchB, missB) = MeasureTargeted(core, recipes, affectedB, reachableAfter, after, fullAfter, canCraft, useGate: true);

            Check(matchA, "real dump: broad targeted (A) matches full recompute for every recipe it re-checks");
            Check(matchB, "real dump: direction-aware targeted (B) matches full recompute for every recipe it re-checks");
            Console.WriteLine($"   crafted recipe #{craftIdx} (output {crafted.OutputType}); changed types: {changed.Count} (consumed {decreased.Count}, produced {increased.Count})");
            Console.WriteLine($"   reachable recompute after craft: {timeReachAfter}ms");
            Console.WriteLine($"   A broad      : {affectedA.Count,5} recipes, {msA,4}ms, {missA} flips missed");
            Console.WriteLine($"   B direction  : {affectedB.Count,5} recipes, {msB,4}ms, {missB} flips missed");
            Console.WriteLine($"   >>> full {fullMs}ms  ->  A {msA + timeReachAfter}ms  ->  B {msB + timeReachAfter}ms (incl. reachable recompute)");

            // --- Memory-vs-compute classification: bytes allocated + GC pressure per pass ---
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long aF = GC.GetAllocatedBytesForCurrentThread();
            int gF = GC.CollectionCount(0);
            var rchF = core.ComputeReachableTypes(available);
            var icF = new Dictionary<(int ctx, int group, int type, int stack), bool>();
            for (int i = 0; i < recipes.Count; i++) core.IsRecipeCraftable(recipes[i], rchF, available, icF);
            double fullMB = (GC.GetAllocatedBytesForCurrentThread() - aF) / 1048576.0;
            int fullGen0 = GC.CollectionCount(0) - gF;

            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long aT = GC.GetAllocatedBytesForCurrentThread();
            int gT = GC.CollectionCount(0);
            var rchT = core.ComputeReachableTypes(after);
            var icT = new Dictionary<(int ctx, int group, int type, int stack), bool>();
            foreach (int i in affectedB) core.IsRecipeCraftable(recipes[i], rchT, after, icT);
            double targMB = (GC.GetAllocatedBytesForCurrentThread() - aT) / 1048576.0;
            int targGen0 = GC.CollectionCount(0) - gT;

            Console.WriteLine($"   MEM  full     : {fullMB,7:0.0} MB allocated, {fullGen0} gen0 GCs  ({fullMB / Math.Max(1, recipes.Count) * 1024:0.0} KB/recipe)");
            Console.WriteLine($"   MEM  targeted : {targMB,7:0.0} MB allocated, {targGen0} gen0 GCs  ({targMB / Math.Max(1, affectedB.Count) * 1024:0.0} KB/recipe)");
        }

        private static long MeasureMs(Action a)
        {
            var sw = Stopwatch.StartNew();
            a();
            sw.Stop();
            return sw.ElapsedMilliseconds;
        }

        private static (long ms, bool match, int miss) MeasureTargeted(
            CoreResolver core, IReadOnlyList<CoreRecipe> recipes, HashSet<int> affected,
            HashSet<int> reachableAfter, Dictionary<int, int> after, bool[] fullAfter, bool[] canCraft, bool useGate)
        {
            var ingCache = new Dictionary<(int ctx, int group, int type, int stack), bool>();
            var result = new Dictionary<int, bool>();
            var sw = Stopwatch.StartNew();
            foreach (int i in affected)
                result[i] = useGate
                    ? core.IsRecipeCraftable(recipes[i], reachableAfter, after, ingCache)
                    : core.RecheckRecipeCraftable(recipes[i], after, ingCache);
            sw.Stop();

            bool match = affected.All(i => result[i] == fullAfter[i]);
            int miss = 0;
            for (int i = 0; i < recipes.Count; i++)
                if (!affected.Contains(i) && fullAfter[i] != canCraft[i]) miss++;
            return (sw.ElapsedMilliseconds, match, miss);
        }

        private static void Add(Dictionary<int, List<int>> map, int key, int value)
        {
            if (!map.TryGetValue(key, out var list)) { list = new List<int>(); map[key] = list; }
            list.Add(value);
        }

        private static void ParseDump(string path, FakeEnvironment env, Dictionary<int, int> available, HashSet<int> stations)
        {
            int section = 0; // 1=storage 2=groups 3=recipes
            foreach (var line in File.ReadLines(path))
            {
                // Everything from the first '#' is the dump's human-readable half - item names on
                // storage and recipe lines - and never part of the data.
                int comment = line.IndexOf('#');
                string data = (comment >= 0 ? line.Substring(0, comment) : line).TrimEnd();
                if (data.Length == 0) continue;
                if (data.StartsWith("STATIONS:")) { foreach (var s in data.Substring(9).Split(' ', StringSplitOptions.RemoveEmptyEntries)) stations.Add(int.Parse(s)); continue; }
                if (data.StartsWith("CONDITIONS:")) continue; // FakeEnvironment treats conditions as always met
                if (data == "STORAGE:") { section = 1; continue; }
                if (data == "GROUPS:") { section = 2; continue; }
                if (data == "RECIPES:") { section = 3; continue; }

                try
                {
                    if (section == 1)
                    {
                        var p = data.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        available[int.Parse(p[0])] = int.Parse(p[1]);
                    }
                    else if (section == 2)
                    {
                        var p = data.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        env.WithGroup(int.Parse(p[0]), p.Skip(1).Select(int.Parse).ToArray());
                    }
                    else if (section == 3)
                    {
                        var parts = data.Split('|');
                        var outp = parts[0].Trim().Split(':');
                        int outType = int.Parse(outp[0]), outStack = int.Parse(outp[1]);
                        var ings = parts[1].Trim().Split(',', StringSplitOptions.RemoveEmptyEntries)
                            .Select(s => { var ip = s.Split(':'); return (int.Parse(ip[0]), int.Parse(ip[1])); }).ToArray();
                        var tiles = parts[2].Replace("tiles:", "").Trim().Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
                        var groups = parts[3].Replace("groups:", "").Trim().Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
                        env.AddRecipe(outType, outStack, ings, tiles.Length > 0 ? tiles : null, groups.Length > 0 ? groups : null);
                    }
                }
                catch { /* skip malformed line */ }
            }
            env.WithStations(stations.ToArray());
        }

        private static void AssertReachableEquiv(IRecipeEnvironment env, Dictionary<int, int> available, string label)
        {
            var fast = new CoreResolver(env).ComputeReachableTypes(available);
            var naive = ReachableNaive(env, available);
            Check(fast.SetEquals(naive), $"reachable equivalence: {label} (worklist={fast.Count}, naive={naive.Count})");
        }

        // A random recipe world: cycles, self-loops, recipe groups, sometimes-unsatisfiable stations.
        private static FakeEnvironment BuildRandomEnv(Random rng, out Dictionary<int, int> available)
        {
            var env = new FakeEnvironment();
            int itemSpace = rng.Next(8, 40);
            bool hasStation = rng.Next(2) == 0;
            if (hasStation) env.WithStations(100);
            bool useGroup = rng.Next(3) == 0;
            const int groupId = 1000;
            if (useGroup) env.WithGroup(groupId, 0, 1);

            int recipeCount = rng.Next(5, 60);
            for (int i = 0; i < recipeCount; i++)
            {
                int outType = rng.Next(itemSpace);
                int ingCount = rng.Next(1, 4);
                var ings = new (int, int)[ingCount];
                for (int j = 0; j < ingCount; j++) ings[j] = (rng.Next(itemSpace), rng.Next(1, 4));

                int[] tiles = null;
                int tileRoll = rng.Next(4);
                if (tileRoll == 0) tiles = new[] { 999 };               // station we never have
                else if (hasStation && tileRoll == 1) tiles = new[] { 100 };

                int[] groups = (useGroup && rng.Next(3) == 0) ? new[] { groupId } : null;
                env.AddRecipe(outType, rng.Next(1, 3), ings, tiles, groups);
            }

            available = new Dictionary<int, int>();
            int seedCount = rng.Next(1, 6);
            for (int i = 0; i < seedCount; i++) available[rng.Next(itemSpace)] = rng.Next(1, 10);
            return env;
        }

        // The OLD ComputeReachableTypes algorithm, verbatim, as the equivalence oracle.
        private static HashSet<int> ReachableNaive(IRecipeEnvironment env, Dictionary<int, int> available)
        {
            var eligible = new List<CoreRecipe>();
            foreach (var r in env.AllRecipes)
            {
                bool ok = true;
                foreach (int t in r.RequiredTiles)
                    if (!env.IsStationSatisfied(t)) { ok = false; break; }
                if (!ok) continue;
                if (!env.ConditionsMet(r)) continue;
                eligible.Add(r);
            }

            var reachable = new HashSet<int>();
            foreach (var kvp in available)
                if (kvp.Value > 0) reachable.Add(kvp.Key);

            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (var r in eligible)
                {
                    if (reachable.Contains(r.OutputType)) continue;

                    bool met = true;
                    foreach (var ing in r.Ingredients)
                    {
                        if (reachable.Contains(ing.Type)) continue;
                        bool grp = false;
                        foreach (int gid in r.AcceptedGroups)
                        {
                            if (!env.GroupContains(gid, ing.Type)) continue;
                            foreach (int v in env.GroupValidItems(gid))
                                if (reachable.Contains(v)) { grp = true; break; }
                            if (grp) break;
                        }
                        if (!grp) { met = false; break; }
                    }
                    if (met) { reachable.Add(r.OutputType); changed = true; }
                }
            }
            return reachable;
        }

        // ---- The OLD preview logic, reproduced verbatim, to prove what was wrong ----
        // Each ingredient is checked INDEPENDENTLY against the full stock: group members are summed
        // and a recursive sub-plan is resolved against an un-deducted snapshot, so two ingredients
        // sharing a base both report it as theirs.
        private static List<IngredientView> BuggyPreview(CoreResolver r, IRecipeEnvironment env,
            CoreRecipe recipe, Dictionary<int, int> available, int craftAmount)
        {
            var views = new List<IngredientView>();
            var seen = new HashSet<int>();
            foreach (var ing in recipe.Ingredients)
            {
                if (!seen.Add(ing.Type)) continue;

                available.TryGetValue(ing.Type, out int directHave);
                bool hasRecipe = env.RecipesProducing(ing.Type).Count > 0;

                bool isGroup = false;
                int totalHave = directHave;
                foreach (int gid in recipe.AcceptedGroups)
                {
                    if (!env.GroupContains(gid, ing.Type)) continue;
                    isGroup = true;
                    foreach (int v in env.GroupValidItems(gid))
                        if (v != ing.Type && available.TryGetValue(v, out int vh))
                            totalHave += vh;
                    break;
                }

                int needed = ing.Stack * craftAmount;
                if (hasRecipe && totalHave < needed)
                {
                    if (r.IsFeasibleFromSnapshot(ing.Type, needed, available))
                        totalHave = needed; // the inflation
                }

                views.Add(new IngredientView { Type = ing.Type, TotalHave = totalHave, Needed = needed, HasRecipe = hasRecipe, IsGroup = isGroup });
            }
            return views;
        }

        // Craftability without the reachable fast-reject, so a scenario can compare the flag against
        // the craft button without also building a reachable set for a one-recipe fixture.
        private static bool ListCraftableNoPrefilter(CoreResolver r, CoreRecipe recipe, Dictionary<int, int> available)
            => r.RecheckRecipeCraftable(recipe, new Dictionary<int, int>(available),
                new Dictionary<(int ctx, int group, int type, int stack), bool>());

        private static bool ListCraftable(CoreResolver r, CoreRecipe recipe, Dictionary<int, int> available)
        {
            var reachable = r.ComputeReachableTypes(available);
            var ingCache = new Dictionary<(int ctx, int group, int type, int stack), bool>();
            return r.IsRecipeCraftable(recipe, reachable, available, ingCache);
        }

        private static IngredientView View(List<IngredientView> views, int type) => views.First(v => v.Type == type);

        // The window a click belongs to is the topmost one under the cursor. Before this, the
        // window that consumed a click was whichever ModSystem happened to update first, which had
        // nothing to do with what the player saw on top.
        private static void WindowStackTests()
        {
            Section("WindowStackCore - z-order arbitration");

            var stack = new WindowStackCore();
            int a = stack.Register();
            int b = stack.Register();           // registered last, so it sits on top
            Eq(stack.TopMatching(new[] { true, true }), b, "TC-001 both hovered -> topmost wins");

            stack.Raise(a);
            Eq(stack.TopMatching(new[] { true, true }), a, "TC-002 raise(A) -> A now wins");

            var fallThrough = new WindowStackCore();
            int belowA = fallThrough.Register();
            fallThrough.Register();
            Eq(fallThrough.TopMatching(new[] { true, false }), belowA,
                "TC-003 cursor outside the top window -> falls through to the one below");
            Eq(fallThrough.TopMatching(new[] { false, false }), -1, "TC-004 nothing hovered -> -1");

            var withClosed = new WindowStackCore();
            withClosed.Register();
            withClosed.Register();
            int top = withClosed.Register();
            Eq(withClosed.TopMatching(new[] { true, false, true }), top,
                "TC-005 a closed window is never an arbitration target");

            var reordered = new WindowStackCore();
            int first = reordered.Register();
            int second = reordered.Register();
            int third = reordered.Register();
            reordered.Raise(first);
            IsTrue(reordered.ZOrder.SequenceEqual(new[] { second, third, first }),
                "TC-006 raise is move-to-top and preserves the order of the rest");

            var alreadyTop = new WindowStackCore();
            int low = alreadyTop.Register();
            int high = alreadyTop.Register();
            alreadyTop.Raise(high);
            IsTrue(alreadyTop.ZOrder.SequenceEqual(new[] { low, high }),
                "TC-007 raising the top window is a no-op");

            var empty = new WindowStackCore();
            Eq(empty.TopMatching(Array.Empty<bool>()), -1, "TC-008 empty stack -> -1");
            empty.Raise(999);
            Eq(empty.ZOrder.Count, 0, "TC-008 raising an unknown handle is a silent no-op");

            var cleared = new WindowStackCore();
            cleared.Register();
            cleared.Register();
            cleared.Clear();
            Eq(cleared.ZOrder.Count, 0, "TC-009 Clear empties the stack");
            Eq(cleared.TopMatching(new[] { true, true }), -1, "TC-009 Clear -> nothing to arbitrate");

            // A child dialog (Disk Recovery) must not be buried by the panel that opened it
            // (the Drive Bay), even though clicking that panel raises it.
            var parented = new WindowStackCore();
            int other = parented.Register();
            int driveBay = parented.Register();
            int recovery = parented.Register(keepAbove: driveBay);
            parented.Raise(driveBay);
            IsTrue(parented.ZOrder.SequenceEqual(new[] { other, driveBay, recovery }),
                "TC-010 raising a parent lifts its child back above it");
            Eq(parented.TopMatching(new[] { false, true, true }), recovery,
                "TC-010 the child still wins the click where they overlap");
        }

        // A held item is deposited only over the item grid. It used to deposit anywhere on the
        // window, so clicking the search bar or a tab silently swallowed whatever you were holding.
        private static void DepositGateTests()
        {
            Section("DepositGate - deposit only over the item grid");

            // Baseline: a fresh press over an empty grid cell, Storage tab, item on the cursor.
            const bool PressEdge = true, Armed = true, StorageTab = true, HasItem = true;
            const bool NoAnimation = false, InsideGrid = true, OverOccupiedSlot = false;

            IsTrue(
                DepositGate.ShouldDeposit(PressEdge, Armed, StorageTab, HasItem, NoAnimation, InsideGrid, OverOccupiedSlot),
                "TC-101 press on an empty grid cell -> deposit");

            IsFalse(
                DepositGate.ShouldDeposit(PressEdge, Armed, StorageTab, HasItem, NoAnimation, false, OverOccupiedSlot),
                "TC-102 press outside the grid (search bar, tabs, title bar, scrollbar) -> no deposit");

            IsFalse(
                DepositGate.ShouldDeposit(PressEdge, Armed, StorageTab, HasItem, NoAnimation, InsideGrid, true),
                "TC-103 press on an occupied slot -> no deposit, OnItemClicked handles it");

            IsFalse(
                DepositGate.ShouldDeposit(false, Armed, StorageTab, HasItem, NoAnimation, InsideGrid, OverOccupiedSlot),
                "TC-104 button held rather than newly pressed -> no deposit mid-drag");

            IsFalse(
                DepositGate.ShouldDeposit(PressEdge, Armed, StorageTab, false, NoAnimation, InsideGrid, OverOccupiedSlot),
                "TC-105 empty cursor -> no deposit");

            IsFalse(
                DepositGate.ShouldDeposit(PressEdge, Armed, false, HasItem, NoAnimation, InsideGrid, OverOccupiedSlot),
                "TC-106 not the Storage tab -> no deposit");

            IsFalse(
                DepositGate.ShouldDeposit(PressEdge, Armed, StorageTab, HasItem, true, InsideGrid, OverOccupiedSlot),
                "TC-107 item use animation active -> no deposit");

            IsFalse(
                DepositGate.ShouldDeposit(PressEdge, false, StorageTab, HasItem, NoAnimation, InsideGrid, OverOccupiedSlot),
                "TC-108 the very click that opened the Terminal -> no deposit");

            // Empty storage is TC-101's input exactly (no slot is occupied, so none can be hovered);
            // that it still deposits is a wiring property of TerminalUIState, covered by IT-004.
        }

        // The frame clock used to be Main.uCount, which vanilla resets to zero every second. A
        // consume at count K therefore came back to life every time the counter wrapped onto K
        // again -- one frame per second where every click in the mod was silently dropped.
        private static void ClickBlockerTests()
        {
            Section("UIClickBlocker - consumption must not outlive its frame");

            UIClickBlocker.ResetForTests();
            UIClickBlocker.Consume();
            IsFalse(UIClickBlocker.IsConsumed,
                "TC-200 a consume before the first frame cannot latch (it would kill every click forever)");

            UIClickBlocker.ResetForTests();
            UIClickBlocker.BeginFrame(mouseLeft: false, mouseRight: false, mouseMiddle: false);
            IsFalse(UIClickBlocker.IsConsumed, "TC-201 a fresh frame starts unconsumed");

            UIClickBlocker.Consume();
            IsTrue(UIClickBlocker.IsConsumed, "TC-202 Consume() holds for the rest of the frame");

            UIClickBlocker.BeginFrame(mouseLeft: false, mouseRight: false, mouseMiddle: false);
            IsFalse(UIClickBlocker.IsConsumed, "TC-203 the next frame starts unconsumed again");

            // The regression: consume once, then run well past a second's worth of frames and
            // assert the stale consume never resurfaces on any of them.
            UIClickBlocker.ResetForTests();
            UIClickBlocker.BeginFrame(mouseLeft: false, mouseRight: false, mouseMiddle: false);
            UIClickBlocker.BeginFrame(mouseLeft: false, mouseRight: false, mouseMiddle: false);
            UIClickBlocker.BeginFrame(mouseLeft: false, mouseRight: false, mouseMiddle: false);
            UIClickBlocker.BeginFrame(mouseLeft: false, mouseRight: false, mouseMiddle: false);
            UIClickBlocker.BeginFrame(mouseLeft: false, mouseRight: false, mouseMiddle: false);
            UIClickBlocker.Consume();

            bool resurfaced = false;
            for (int frame = 0; frame < 300; frame++)
            {
                UIClickBlocker.BeginFrame(mouseLeft: false, mouseRight: false, mouseMiddle: false);
                if (UIClickBlocker.IsConsumed)
                    resurfaced = true;
            }
            IsFalse(resurfaced, "TC-204 a consume never resurfaces on a later frame (uCount wrap bug)");

            // Suppressing a window zeroes Main.mouseLeft around its update. A window that latches
            // its previous-button state from Main.mouseLeft therefore records "released" for a
            // button still held, and fires a phantom press on the first unsuppressed frame. The
            // real button state is captured at BeginFrame, where suppression cannot reach it.
            UIClickBlocker.ResetForTests();
            UIClickBlocker.BeginFrame(mouseLeft: true, mouseRight: true, mouseMiddle: true);
            bool prevLeft = UIClickBlocker.RealMouseLeft;
            bool prevRight = UIClickBlocker.RealMouseRight;
            bool prevMiddle = UIClickBlocker.RealMouseMiddle;

            IsTrue(prevLeft && prevRight && prevMiddle,
                "TC-205 every held button survives a suppressed frame");

            // Every button, not just left: a phantom right-press in the Terminal withdraws a stack
            // from storage, and a phantom middle-press starts a drag.
            UIClickBlocker.BeginFrame(mouseLeft: true, mouseRight: true, mouseMiddle: true);
            IsFalse(UIClickBlocker.RealMouseLeft && !prevLeft,
                "TC-206 a held LEFT button never looks like a fresh press");
            IsFalse(UIClickBlocker.RealMouseRight && !prevRight,
                "TC-207 a held RIGHT button never looks like a fresh press (phantom withdraw)");
            IsFalse(UIClickBlocker.RealMouseMiddle && !prevMiddle,
                "TC-208 a held MIDDLE button never looks like a fresh press");

            // A window must claim the frame's click whatever the button. Claiming only left-clicks
            // is what let a right-click be acted on by every window under the cursor at once.
            UIClickBlocker.ResetForTests();
            UIClickBlocker.BeginFrame(mouseLeft: false, mouseRight: true, mouseMiddle: false);
            UIClickBlocker.ClaimIfPressed(hovered: true, left: false, right: true, middle: false);
            IsTrue(UIClickBlocker.IsConsumed, "TC-209 a RIGHT-click is claimed, not just a left one");

            UIClickBlocker.ResetForTests();
            UIClickBlocker.BeginFrame(mouseLeft: false, mouseRight: false, mouseMiddle: true);
            UIClickBlocker.ClaimIfPressed(hovered: true, left: false, right: false, middle: true);
            IsTrue(UIClickBlocker.IsConsumed, "TC-210 a MIDDLE-click is claimed");

            UIClickBlocker.ResetForTests();
            UIClickBlocker.BeginFrame(mouseLeft: true, mouseRight: false, mouseMiddle: false);
            UIClickBlocker.ClaimIfPressed(hovered: false, left: true, right: false, middle: false);
            IsFalse(UIClickBlocker.IsConsumed, "TC-211 a window the cursor is not over claims nothing");

            UIClickBlocker.ResetForTests();
            UIClickBlocker.BeginFrame(mouseLeft: false, mouseRight: false, mouseMiddle: false);
            UIClickBlocker.ClaimIfPressed(hovered: true, left: false, right: false, middle: false);
            IsFalse(UIClickBlocker.IsConsumed, "TC-212 hovering with no button pressed claims nothing");

            // The z-order must not be reshuffled under the player's hand mid-drag.
            UIClickBlocker.ResetForTests();
            UIClickBlocker.BeginFrame(mouseLeft: true, mouseRight: false, mouseMiddle: false);
            IsFalse(UIClickBlocker.GestureActive, "TC-213 no gesture -> the press may raise a window");

            UIClickBlocker.MarkGesture();
            UIClickBlocker.BeginFrame(mouseLeft: true, mouseRight: false, mouseMiddle: false);
            IsTrue(UIClickBlocker.GestureActive, "TC-214 a gesture in progress blocks the raise");

            UIClickBlocker.BeginFrame(mouseLeft: false, mouseRight: false, mouseMiddle: false);
            UIClickBlocker.BeginFrame(mouseLeft: false, mouseRight: false, mouseMiddle: false);
            IsFalse(UIClickBlocker.GestureActive, "TC-215 the gesture grace expires once it ends");
        }

        // ---- tiny assert framework ----
        // The Favorited Recipes panel draws every frame while pinned during normal gameplay;
        // the 5-8 fps defect was uncached per-frame storage scans + string churn there. These
        // tests encode the cache's contract: rebuild only on version change, re-allocate a
        // slot string only on count change, and allocate NOTHING in the steady state.
        private static void FavoritesRowCacheTests()
        {
            Section("FavoritesRowCache - version gating, change-gated strings, zero-alloc steady state");

            var cache = new FavoritesRowCache();

            IsTrue(cache.NeedsRowRebuild(1), "FC-01 initial state needs a row rebuild");
            cache.Rows.Add(new FavoritesRowCache.Row { RecipeIndex = 7, OutputType = 42 });
            cache.MarkRowsRebuilt(1);
            IsFalse(cache.NeedsRowRebuild(1), "FC-01 same favorites version -> no rebuild");
            IsTrue(cache.NeedsRowRebuild(2), "FC-02 favorites version bump -> rebuild");

            IsTrue(cache.NeedsStorageRefresh(5, 1), "FC-02 initial storage state needs a refresh");
            cache.MarkStorageRefreshed(5, 1);
            IsFalse(cache.NeedsStorageRefresh(5, 1), "FC-02 unchanged storage+disks -> no refresh");
            IsTrue(cache.NeedsStorageRefresh(6, 1), "FC-02 storage version bump -> refresh");
            IsTrue(cache.NeedsStorageRefresh(5, 2), "FC-02 disk-set token bump -> refresh");

            var slot = new FavoritesRowCache.Slot { ItemType = 42, Needed = 5 };
            IsTrue(FavoritesRowCache.UpdateSlotCount(slot, 3), "FC-04 first count set builds the text");
            Check(slot.Text == "3/5", "FC-05 FormatCount format is have/needed");
            string before = slot.Text;
            IsFalse(FavoritesRowCache.UpdateSlotCount(slot, 3), "FC-03 unchanged count -> no text change");
            IsTrue(ReferenceEquals(before, slot.Text), "FC-03 unchanged count keeps the same string instance");
            IsTrue(FavoritesRowCache.UpdateSlotCount(slot, 4), "FC-04 changed count -> text rebuilt");
            Check(slot.Text == "4/5", "FC-04 rebuilt text reflects the new count");
            Check(FavoritesRowCache.FormatCount(0, 1) == "0/1", "FC-05 zero have formats as 0/1");

            // FC-08: world/character switch reset. A new session can legitimately present the
            // SAME stamps (FavoritesVersion restarts per player; StorageVersion is not reset on
            // world load), so the explicit reset must force staleness even for identical stamps.
            cache.MarkRowsRebuilt(2);
            cache.MarkStorageRefreshed(6, 2);
            cache.ResetVersionStamps();
            IsTrue(cache.NeedsRowRebuild(2), "FC-08 reset forces a row rebuild even at the previously marked version");
            IsTrue(cache.NeedsStorageRefresh(6, 2), "FC-08 reset forces a storage refresh even at the previously marked stamps");

            var heightCache = new FavoritesRowCache();
            for (int i = 0; i < 6; i++) heightCache.Rows.Add(new FavoritesRowCache.Row());
            heightCache.MarkRowsRebuilt(1);
            Check(Math.Abs(heightCache.BodyHeight - (FavoritesRowCache.TopPad + 6 * FavoritesRowCache.RowHeight)) < 0.001f,
                "FC-07 BodyHeight = pad + rows * rowHeight");

            // FC-06: the per-frame path (version checks + slot updates with unchanged counts +
            // BodyHeight read) must allocate zero bytes once warmed up.
            var steady = new FavoritesRowCache();
            var row = new FavoritesRowCache.Row { RecipeIndex = 1, OutputType = 10 };
            row.Slots.Add(new FavoritesRowCache.Slot { ItemType = 11, Needed = 2 });
            row.Slots.Add(new FavoritesRowCache.Slot { ItemType = 12, Needed = 3 });
            steady.Rows.Add(row);
            steady.MarkRowsRebuilt(1);
            steady.MarkStorageRefreshed(1, 1);
            FavoritesRowCache.UpdateSlotCount(row.Slots[0], 1);
            FavoritesRowCache.UpdateSlotCount(row.Slots[1], 3);

            SimulateSteadyFrames(steady, 100); // warmup: JITs the loop

            int gen0Before = GC.CollectionCount(0);
            long allocBefore = GC.GetAllocatedBytesForCurrentThread();
            SimulateSteadyFrames(steady, 10_000);
            long allocDelta = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
            int gen0Delta = GC.CollectionCount(0) - gen0Before;
            Check(allocDelta == 0, $"FC-06 steady state allocates nothing over 10k frames (delta {allocDelta} B)");
            Check(gen0Delta == 0, $"FC-06 no gen0 collections in steady state (delta {gen0Delta})");
        }

        private static void SimulateSteadyFrames(FavoritesRowCache cache, int frames)
        {
            for (int f = 0; f < frames; f++)
            {
                if (cache.NeedsRowRebuild(1) || cache.NeedsStorageRefresh(1, 1))
                    throw new InvalidOperationException("steady state must not invalidate");
                if (cache.BodyHeight < 0)
                    throw new InvalidOperationException("unreachable");
                for (int r = 0; r < cache.Rows.Count; r++)
                {
                    var row = cache.Rows[r];
                    for (int s = 0; s < row.Slots.Count; s++)
                        FavoritesRowCache.UpdateSlotCount(row.Slots[s], row.Slots[s].Have);
                }
            }
        }

        // ---- The disk-upgrade transaction: all of it, or none of it ----
        // Issues 01 and 02 both shipped an upgrade that was never paid for. The rule under test is
        // that TryConsume either takes the whole material list or leaves storage exactly as it was.
        private static void MaterialTransactionIsAtomic()
        {
            Section("Material consumption is all-or-nothing");
            const int IRON = 1, GOLD = 2, GLASS = 3, SAND = 4;

            var stocked = new FakeStorage().With(IRON, 10).With(GOLD, 5);
            var neverCrafts = new MaterialConsumer<FakeItem>(stocked, (type, need) => null);
            IsTrue(neverCrafts.TryConsume(new[] { (IRON, 10), (GOLD, 5) }), "TX-01 a fully stocked list is consumed");
            Eq(stocked.CountItem(IRON), 0, "TX-01a iron consumed exactly");
            Eq(stocked.CountItem(GOLD), 0, "TX-01b gold consumed exactly");

            var shortByOne = new FakeStorage().With(IRON, 9);
            var noCraft = new MaterialConsumer<FakeItem>(shortByOne, (type, need) => null);
            IsFalse(noCraft.TryConsume(new[] { (IRON, 10) }), "TX-02 one unit short is refused");
            Eq(shortByOne.CountItem(IRON), 9, "TX-02a nothing was consumed");

            // The blame case behind issue 01: the first material is taken before the second is
            // known to be missing, so it has to come back.
            var secondMissing = new FakeStorage().With(IRON, 10).With(GOLD, 4);
            var partial = new MaterialConsumer<FakeItem>(secondMissing, (type, need) => null);
            IsFalse(partial.TryConsume(new[] { (IRON, 10), (GOLD, 5) }), "TX-03 a later shortfall refuses the whole list");
            Eq(secondMissing.CountItem(IRON), 10, "TX-03a the first material was refunded");
            Eq(secondMissing.CountItem(GOLD), 4, "TX-03b the second material is untouched");

            // A shortfall the network can craft. The resolver must be asked for the FULL need:
            // asking for need - have makes it see the stock the caller already subtracted and
            // report a free direct extract.
            var craftable = new FakeStorage().With(GLASS, 4).With(SAND, 20);
            var craftRequests = new List<(int type, int need)>();
            var crafting = new MaterialConsumer<FakeItem>(craftable, (type, need) =>
            {
                craftRequests.Add((type, need));
                craftable.ExtractStacks(SAND, 12);
                return new FakeItem { Type = GLASS, Stack = 6 };
            });
            IsTrue(crafting.TryConsume(new[] { (GLASS, 10) }), "TX-04 a craftable shortfall is crafted and consumed");
            Eq(craftRequests.Count, 1, "TX-04a the shortfall was crafted exactly once");
            Eq(craftRequests.Count > 0 ? craftRequests[0].need : -1, 10, "TX-04b the craft was asked for the full need");
            Eq(craftable.CountItem(GLASS), 0, "TX-04c all ten glass were consumed");

            var uncraftable = new FakeStorage().With(IRON, 10).With(GLASS, 0);
            var craftFails = new MaterialConsumer<FakeItem>(uncraftable, (type, need) => null);
            IsFalse(craftFails.TryConsume(new[] { (IRON, 10), (GLASS, 1) }), "TX-05 an impossible craft refuses the list");
            Eq(uncraftable.CountItem(IRON), 10, "TX-05a the paid material was refunded");

            // Storage too full to hold everything the craft produced. The extract that follows
            // would come up short, so fail here - and take back the part that did land, or the
            // refund of the earlier material has nowhere to go.
            var full = new FakeStorage().With(IRON, 10);
            full.Capacity = 10;
            var craftsIntoFullStorage = new MaterialConsumer<FakeItem>(full,
                (type, need) => new FakeItem { Type = GLASS, Stack = need });
            IsFalse(craftsIntoFullStorage.TryConsume(new[] { (IRON, 10), (GLASS, 20) }), "TX-06 an unstorable craft refuses the list");
            Eq(full.CountItem(IRON), 10, "TX-06a the paid material was refunded");
            Eq(full.CountItem(GLASS), 0, "TX-06b the partial insert was taken back");

            var skipping = new FakeStorage().With(IRON, 10);
            var skipper = new MaterialConsumer<FakeItem>(skipping, (type, need) => null);
            IsTrue(skipper.TryConsume(new[] { (IRON, 0), (GOLD, -3) }), "TX-07 zero and negative requirements cost nothing");
            Eq(skipping.CountItem(IRON), 10, "TX-07a storage untouched");
        }

        // ---- A plan that cannot finish must leave storage exactly as it found it ----
        // Issue 03: a step that could not be paid for still produced its output. The subtler half
        // is that a refund alone is not enough once an earlier step has already made something.
        private static void PlanExecutionIsAtomic()
        {
            Section("A failed plan refunds materials and keeps nothing it made");
            const int WOOD = 1, PLANK = 2, IRON = 3, TARGET = 4;

            var single = new FakeStorage().With(WOOD, 5);
            var oneStep = Steps((new[] { (WOOD, 5) }, PLANK, 1));
            var product = new PlanExecutor<FakeItem>(single).Run(oneStep, 1, new FakeStepProducer(oneStep));
            Eq(single.StackOf(product), 1, "PX-01 a payable step hands back its product");
            Eq(single.CountItem(WOOD), 0, "PX-01a its materials were consumed");
            Eq(single.CountItem(PLANK), 0, "PX-01b the final product never routes through storage");

            var chained = new FakeStorage().With(WOOD, 5).With(IRON, 3);
            var twoSteps = Steps(
                (new[] { (WOOD, 5) }, PLANK, 1),
                (new[] { (PLANK, 1), (IRON, 3) }, TARGET, 1));
            var chainProduct = new PlanExecutor<FakeItem>(chained).Run(twoSteps, 1, new FakeStepProducer(twoSteps));
            Eq(chained.StackOf(chainProduct), 1, "PX-02 an intermediate is stored and then consumed");
            Eq(chained.CountItem(PLANK), 0, "PX-02a no intermediate is left behind");

            // The duplication this seam exposed: step 2 cannot be paid for, so the wood comes back
            // - and without discarding the plank the player keeps both the ingredients and the
            // thing made from them.
            var aborts = new FakeStorage().With(WOOD, 5).With(IRON, 2);
            var abortSteps = Steps(
                (new[] { (WOOD, 5) }, PLANK, 1),
                (new[] { (PLANK, 1), (IRON, 3) }, TARGET, 1));
            var abortProducer = new FakeStepProducer(abortSteps);
            var aborted = new PlanExecutor<FakeItem>(aborts).Run(abortSteps, 1, abortProducer);
            Eq(aborts.StackOf(aborted), 0, "PX-03 a mid-plan shortfall produces nothing");
            Eq(aborts.CountItem(WOOD), 5, "PX-03a the first step's materials were refunded");
            Eq(aborts.CountItem(IRON), 2, "PX-03b the partial payment was refunded");
            Eq(aborts.CountItem(PLANK), 0, "PX-03c the intermediate was NOT left in storage");
            Eq(abortProducer.Prepared.Count, 2, "PX-03d each step is prepared before it is paid for");

            var unaffordable = new FakeStorage().With(WOOD, 4);
            var firstStepFails = Steps((new[] { (WOOD, 5) }, PLANK, 1));
            var nothing = new PlanExecutor<FakeItem>(unaffordable).Run(firstStepFails, 1, new FakeStepProducer(firstStepFails));
            Eq(unaffordable.StackOf(nothing), 0, "PX-04 an unaffordable first step produces nothing");
            Eq(unaffordable.CountItem(WOOD), 4, "PX-04a the partial extraction was refunded");

            // Storage too full to hold the intermediate: the part that did land must come back out,
            // or refunding the materials would be blocked by the very product they were spent on.
            var cramped = new FakeStorage().With(WOOD, 5);
            cramped.Capacity = 5;
            var bulkySteps = Steps(
                (new[] { (WOOD, 5) }, PLANK, 10),
                (new[] { (PLANK, 10) }, TARGET, 1));
            var cramping = new PlanExecutor<FakeItem>(cramped).Run(bulkySteps, 1, new FakeStepProducer(bulkySteps));
            Eq(cramped.StackOf(cramping), 0, "PX-05 an unstorable intermediate aborts the plan");
            Eq(cramped.CountItem(WOOD), 5, "PX-05a the materials were refunded");
            Eq(cramped.CountItem(PLANK), 0, "PX-05b the partial insert was taken back");

            var batched = new FakeStorage().With(WOOD, 5);
            var batchStep = Steps((new[] { (WOOD, 5) }, PLANK, 10));
            var trimmed = new PlanExecutor<FakeItem>(batched).Run(batchStep, 3, new FakeStepProducer(batchStep));
            Eq(batched.StackOf(trimmed), 3, "PX-06 batch rounding hands back only what was asked for");
            Eq(batched.CountItem(PLANK), 7, "PX-06a the excess was stored");

            // The drain that ends an abort asks for everything the run made in one Extract call.
            // A material held as stacks that each stand for themselves hands back one stack per
            // call, so the rest stayed in storage: the ingredients came back AND the player kept
            // what was made from them.
            var leaky = new FakeStorage().WithUniqueType(PLANK).With(WOOD, 5);
            var leakySteps = Steps(
                (new[] { (WOOD, 5) }, PLANK, 3),
                (new[] { (IRON, 1) }, TARGET, 1));
            var leaked = new PlanExecutor<FakeItem>(leaky).Run(leakySteps, 1, new FakeStepProducer(leakySteps));
            Eq(leaky.StackOf(leaked), 0, "PX-07 an unpayable final step produces nothing");
            Eq(leaky.CountItem(WOOD), 5, "PX-07a the materials came back");
            Eq(leaky.CountItem(PLANK), 0, "PX-07b and none of what the run made was kept");
        }

        // ---- A partial deposit is still a deposit ----
        // Issue 13: the count that went in was read after the item's stack had been overwritten
        // with the leftover, so a partial deposit reported failure and its delta never went out.
        private static void PartialDepositIsReported()
        {
            Section("Deposit outcome arithmetic");

            var full = new DepositOutcome(50, 0);
            Eq(full.Deposited, 50, "DP-01 a full deposit banks everything");
            IsTrue(full.AnyDeposited, "DP-01a and reports success");
            IsFalse(full.NeedsReturn, "DP-01b with nothing to hand back");

            // The bug: 30 of 50 landed, so the network changed and other clients must be told.
            var partial = new DepositOutcome(50, 20);
            Eq(partial.Deposited, 30, "DP-02 a partial deposit banks the difference");
            IsTrue(partial.AnyDeposited, "DP-02a and still counts as a deposit");
            IsTrue(partial.NeedsReturn, "DP-02b with the remainder going back");

            var rejected = new DepositOutcome(50, 50);
            Eq(rejected.Deposited, 0, "DP-03 a rejected deposit banks nothing");
            IsFalse(rejected.AnyDeposited, "DP-03a and reports no change");
            IsTrue(rejected.NeedsReturn, "DP-03b handing the whole stack back");

            var empty = new DepositOutcome(0, 0);
            IsFalse(empty.AnyDeposited, "DP-04 an empty deposit is not a change");
            IsFalse(empty.NeedsReturn, "DP-04a and returns nothing");

            // Storage cannot bounce more than it was offered, and a negative leftover would make
            // the deposit look larger than the stack.
            var overshoot = new DepositOutcome(10, 40);
            Eq(overshoot.Deposited, 0, "DP-05 a leftover beyond the offer cannot bank a negative");
            var undershoot = new DepositOutcome(10, -5);
            Eq(undershoot.Deposited, 10, "DP-06 a negative leftover cannot bank more than offered");
        }

        // ---- A row the scissor hides must not be clickable ----
        // Issue 16: alt-click is the vanilla favorite gesture, so a hit rect left registered
        // outside the clipped body turns an ordinary inventory alt-click into unfavoriting a row
        // the player cannot even see.
        private static void ClippedRowsRegisterNoHitRect()
        {
            Section("Favorites hit rects stop at the clip");

            const float bodyTop = 100f, slotSize = 36f, maxBodyH = 200f;

            float shortBody = FavoritesRowCache.GetBodyBottom(bodyTop, 120f, maxBodyH);
            Check(Math.Abs(shortBody - 220f) < 0.001f, "HR-01 a body shorter than the maximum ends where it ends");

            float clippedBody = FavoritesRowCache.GetBodyBottom(bodyTop, 900f, maxBodyH);
            Check(Math.Abs(clippedBody - 300f) < 0.001f, "HR-02 a long body is clipped to the maximum");

            IsTrue(FavoritesRowCache.IsHitRectVisible(bodyTop + slotSize, bodyTop, clippedBody), "HR-03 a row inside the body is clickable");
            IsTrue(FavoritesRowCache.IsHitRectVisible(clippedBody, bodyTop, clippedBody), "HR-04 a row ending exactly on the clip is clickable");
            IsFalse(FavoritesRowCache.IsHitRectVisible(clippedBody + 1f, bodyTop, clippedBody), "HR-05 one pixel past the clip is not");

            // The case that bit: many more rows than fit, drawn anyway, scissored away.
            var overflowing = new FavoritesRowCache();
            for (int i = 0; i < 40; i++) overflowing.Rows.Add(new FavoritesRowCache.Row());
            overflowing.MarkRowsRebuilt(1);
            float overflowBottom = FavoritesRowCache.GetBodyBottom(bodyTop, overflowing.BodyHeight, maxBodyH);

            int clickable = 0;
            for (int i = 0; i < overflowing.Rows.Count; i++)
            {
                float rectBottom = bodyTop + FavoritesRowCache.TopPad + i * FavoritesRowCache.RowHeight + slotSize;
                if (FavoritesRowCache.IsHitRectVisible(rectBottom, bodyTop, overflowBottom))
                    clickable++;
            }
            Eq(clickable, 5, "HR-06 only the rows the clip actually shows register a rect");

            IsFalse(FavoritesRowCache.IsHitRectVisible(bodyTop - 1f, bodyTop, clippedBody), "HR-07 a row scrolled off the top registers nothing");
            IsTrue(FavoritesRowCache.IsHitRectVisible(bodyTop, bodyTop, clippedBody), "HR-08 a row scrolling in at the top is clickable as soon as it shows");
        }

        // ---- A terminal left open must not keep showing numbers that stopped being true ----
        // Issues 09, 14 and 15: stock stamped against storage alone survived a walk to another
        // terminal, conditions were snapshotted once, and favorites toggled elsewhere never
        // re-filtered the grid.
        private static void PanelRefreshCacheInvalidates()
        {
            Section("Panel refresh gating");

            var cache = new PanelRefreshCache();
            IsTrue(cache.NeedsOutputStockRecount(1, 10), "RC-01 an uncounted output slot needs a count");
            cache.MarkOutputStockCounted(1, 10);
            IsFalse(cache.NeedsOutputStockRecount(1, 10), "RC-01a same storage and same output -> no recount");
            IsTrue(cache.NeedsOutputStockRecount(2, 10), "RC-02 a storage change forces a recount");
            cache.MarkOutputStockCounted(2, 10);
            IsTrue(cache.NeedsOutputStockRecount(2, 11), "RC-03 selecting another output forces a recount");

            // The disk-set case: storage itself did not change, so nothing above catches it.
            cache.MarkOutputStockCounted(2, 11);
            IsFalse(cache.NeedsOutputStockRecount(2, 11), "RC-04 a counted slot is stable");
            cache.InvalidateOutputStock();
            IsTrue(cache.NeedsOutputStockRecount(2, 11), "RC-04a walking to another terminal forces a recount");

            IsTrue(cache.NeedsFavoritesRefilter(0), "RC-05 favorites start unfiltered");
            cache.MarkFavoritesFiltered(0);
            IsFalse(cache.NeedsFavoritesRefilter(0), "RC-05a an unchanged favorites version re-filters nothing");
            IsTrue(cache.NeedsFavoritesRefilter(1), "RC-06 a favorite toggled elsewhere re-filters the grid");

            IsTrue(cache.NeedsStorageReact(5), "RC-07 an unseen storage version needs reacting to");
            cache.MarkStorageReacted(5);
            IsFalse(cache.NeedsStorageReact(5), "RC-07a the same version is not reacted to twice");

            // Conditions are live world state: the first check is always due, then once a second.
            var clock = new PanelRefreshCache();
            IsTrue(clock.NeedsConditionRecheck(0), "RC-08 the first condition check is always due");
            clock.MarkConditionsChecked(0);
            IsFalse(clock.NeedsConditionRecheck(59), "RC-08a and not repeated within the interval");
            IsTrue(clock.NeedsConditionRecheck(60), "RC-09 a check falls due once the interval passes");
            clock.MarkConditionsChecked(60);
            IsFalse(clock.NeedsConditionRecheck(61), "RC-09a and the interval restarts from there");

            // uint tick counters wrap. The window must survive it, or a terminal open across the
            // wrap freezes its condition flags for the rest of the session.
            var wrapping = new PanelRefreshCache();
            wrapping.MarkConditionsChecked(uint.MaxValue - 10);
            IsFalse(wrapping.NeedsConditionRecheck(uint.MaxValue), "RC-10 no early re-check across the tick wrap");
            IsTrue(wrapping.NeedsConditionRecheck(50), "RC-10a and the check still falls due after it");

            // Flags only matter when one actually flips - re-filtering the whole list otherwise
            // is what made this a once-per-refresh snapshot in the first place.
            var flags = new[] { true, true, false };
            IsFalse(PanelRefreshCache.ApplyFlags(flags, 3, i => flags[i]), "RC-11 unchanged flags report no change");
            IsTrue(PanelRefreshCache.ApplyFlags(flags, 3, i => i == 2), "RC-12 a flipped flag is reported");
            IsTrue(flags[2], "RC-12a and written through");
            IsFalse(flags[0], "RC-12b including the ones that flipped off");

            // A stale array belongs to a recipe list that has since been rebuilt; applying it would
            // blame the wrong recipes.
            var stale = new[] { true, true };
            IsFalse(PanelRefreshCache.ApplyFlags(stale, 3, i => false), "RC-13 a length mismatch applies nothing");
            IsTrue(stale[0], "RC-13a leaving the old flags untouched");
            IsFalse(PanelRefreshCache.ApplyFlags(null, 3, i => false), "RC-14 no flags yet applies nothing");
        }

        // ---- A unique stack stands for itself ----
        // Issue 05: a bulk withdrawal that picked up one enchanted stack stamped its state onto
        // every unit returned. The rule is that plain stacks drain first and a unique stack is
        // only ever taken alone.
        private static void WithdrawalNeverMixesUniqueStacks()
        {
            Section("A withdrawal drains plain stacks and takes a unique one only alone");

            // The reported shape: a unique stack sorted first, 300 plain units behind it.
            var uniqueFirst = new List<StackSlot>
            {
                new StackSlot { Index = 0, Stack = 1, IsUnique = true },
                new StackSlot { Index = 1, Stack = 300, IsUnique = false }
            };
            var bulk = StackSelection.PlanWithdrawal(uniqueFirst, 300, true, out bool bulkUnique);
            Eq(bulk.Sum(d => d.Count), 300, "SL-01 a bulk withdrawal takes the full amount");
            IsFalse(bulkUnique, "SL-01a it is not reported as a unique stack");
            IsFalse(bulk.Any(d => d.Index == 0), "SL-01b the unique stack was left alone");

            var spread = new List<StackSlot>
            {
                new StackSlot { Index = 0, Stack = 40, IsUnique = false },
                new StackSlot { Index = 1, Stack = 40, IsUnique = false },
                new StackSlot { Index = 2, Stack = 40, IsUnique = false }
            };
            var partial = StackSelection.PlanWithdrawal(spread, 50, true, out _);
            Eq(partial.Sum(d => d.Count), 50, "SL-02 a withdrawal spans as many stacks as it needs");
            Eq(partial.Count, 2, "SL-02a and stops at the first stack that covers the rest");
            Eq(partial.Count > 1 ? partial[1].Count : -1, 10, "SL-02b taking only the remainder from the last");

            var overdraw = StackSelection.PlanWithdrawal(spread, 500, true, out _);
            Eq(overdraw.Sum(d => d.Count), 120, "SL-03 asking for more than exists takes everything plain");

            // Nothing plain matched, so the fallback applies - this is how a disk, always unique,
            // still comes out of storage.
            var onlyUnique = new List<StackSlot>
            {
                new StackSlot { Index = 0, Stack = 1, IsUnique = true },
                new StackSlot { Index = 1, Stack = 1, IsUnique = true }
            };
            var fallback = StackSelection.PlanWithdrawal(onlyUnique, 5, true, out bool fellBack);
            IsTrue(fellBack, "SL-04 with nothing plain to take, the unique fallback applies");
            Eq(fallback.Count, 1, "SL-04a exactly one unique stack is taken");
            Eq(fallback.Count > 0 ? fallback[0].Count : -1, 1, "SL-04b and only what that stack holds");

            // A caller already carrying plain items from another disk must not pull a unique stack
            // it would then have to fold into a count that does not describe it.
            var refused = StackSelection.PlanWithdrawal(onlyUnique, 5, false, out bool refusedUnique);
            Eq(refused.Count, 0, "SL-05 a caller mid-withdrawal refuses the unique fallback");
            IsFalse(refusedUnique, "SL-05a and is not told it got one");

            var nothingWanted = StackSelection.PlanWithdrawal(spread, 0, true, out _);
            Eq(nothingWanted.Count, 0, "SL-06 asking for nothing draws nothing");
        }

        // ---- Defragmenting must not invent, merge away or strip identity ----
        // Issue 04: unique stacks were merged on type and prefix alone.
        private static void DefragmentRespectsStackIdentity()
        {
            Section("Defragment merges only what shares an identity");

            var partials = new List<MergeTarget>
            {
                new MergeTarget { Index = 0, Stack = 90, Accepts = true },
                new MergeTarget { Index = 1, Stack = 95, Accepts = false },
                new MergeTarget { Index = 2, Stack = 50, Accepts = true }
            };

            var merged = StackSelection.PlanDonorMove(partials, 40, 99, 5, false);
            Eq(merged.Merges.Sum(m => m.Count), 40, "DF-01 a plain donor fills partial stacks first");
            Eq(merged.NewSlots.Count, 0, "DF-01a and needs no new slot");
            Eq(merged.LeftOnDonor, 0, "DF-01b leaving nothing on the donor");
            IsFalse(merged.Merges.Any(m => m.Index == 1), "DF-01c a stack of another identity is skipped");

            var spills = StackSelection.PlanDonorMove(partials, 300, 99, 5, false);
            Eq(spills.Merges.Sum(m => m.Count), 58, "DF-02 partials are topped up to maxStack");
            Eq(spills.NewSlots.Sum(), 242, "DF-02a the rest takes fresh slots");
            IsTrue(spills.NewSlots.All(c => c <= 99), "DF-02b no new slot exceeds maxStack");
            Eq(spills.LeftOnDonor, 0, "DF-02c the donor is emptied");

            var cramped = StackSelection.PlanDonorMove(partials, 300, 99, 1, false);
            Eq(cramped.NewSlots.Count, 1, "DF-03 only the free slots available are taken");
            Eq(cramped.LeftOnDonor, 143, "DF-03a what does not fit stays on the donor");

            // The identity bug: a unique stack must never merge and never split, whatever the
            // target holds. Every target here would accept it on type and prefix.
            var willingTargets = new List<MergeTarget>
            {
                new MergeTarget { Index = 0, Stack = 1, Accepts = true }
            };
            var unique = StackSelection.PlanDonorMove(willingTargets, 1, 99, 3, true);
            IsTrue(unique.MoveWholeStack, "DF-04 a unique donor moves whole into a free slot");
            Eq(unique.Merges.Count, 0, "DF-04a it never merges, however willing the target");
            Eq(unique.NewSlots.Count, 0, "DF-04b and is never split into counted copies");
            Eq(unique.LeftOnDonor, 0, "DF-04c it leaves the donor entirely");

            var noRoom = StackSelection.PlanDonorMove(willingTargets, 1, 99, 0, true);
            IsFalse(noRoom.MoveWholeStack, "DF-05 with no free slot a unique stack stays put");
            Eq(noRoom.LeftOnDonor, 1, "DF-05a and is not partially moved");

            var full = StackSelection.PlanDonorMove(new List<MergeTarget>(), 10, 99, 0, false);
            Eq(full.LeftOnDonor, 10, "DF-06 a full target moves nothing");

            // Defragment skips building merge targets at all for a unique donor, which is only safe
            // while a unique donor's plan does not depend on them. That is an unwritten coupling
            // between two files, so it gets a test rather than a comment.
            var withTargets = StackSelection.PlanDonorMove(partials, 40, 99, 5, true);
            var withoutTargets = StackSelection.PlanDonorMove(new List<MergeTarget>(), 40, 99, 5, true);
            IsTrue(withTargets.MoveWholeStack == withoutTargets.MoveWholeStack
                   && withTargets.LeftOnDonor == withoutTargets.LeftOnDonor
                   && withTargets.Merges.Count == withoutTargets.Merges.Count
                   && withTargets.NewSlots.Count == withoutTargets.NewSlots.Count,
                "DF-07 a unique donor plans the same move whether or not it is offered targets");
        }

        // ---- Force-craft semantics apply to the preview's direct draw too ----
        // The output's own stock is not a material. RecheckRecipeCraftable and ResolveForceCraft
        // both exclude it; the preview's direct draw did not, so a slot painted green off the very
        // item being crafted. Reachable via a recipe group whose members include the output.
        private static void PreviewExcludesTheOutputFromDirectDraw()
        {
            Section("Preview will not fill a slot from the item being crafted");
            const int IRON = 1, LEAD = 2, CHAIN = 3, ANVIL = 90, ANY_IRON = 500;

            var env = new FakeEnvironment().WithStations(ANVIL).WithGroup(ANY_IRON, IRON, LEAD);
            var lead = env.AddRecipe(LEAD, 1, new[] { (IRON, 3), (CHAIN, 1) },
                tiles: new[] { ANVIL }, groups: new[] { ANY_IRON });
            var r = new CoreResolver(env);
            var stock = new Dictionary<int, int> { [LEAD] = 40, [CHAIN] = 5 };

            // The button path: ResolveForceCraft removes the target from the pool first.
            var forceStock = new Dictionary<int, int>(stock);
            forceStock.Remove(LEAD);
            var steps = new List<CoreStep>();
            bool button = r.ResolveRecursive(LEAD, 1, forceStock, steps, new HashSet<int>(), 0);
            bool listFlag = ListCraftableNoPrefilter(r, lead, stock);
            var views = r.ComputeIngredientPreview(lead, stock, 1);
            var ironView = views.Find(v => v.Type == IRON);

            IsFalse(button, "FD-01 the button refuses: force-craft will not spend the output");
            Check(listFlag == button, "FD-02 the list flag agrees with the button");
            Eq(ironView.TotalHave, 0, "FD-03 the slot is not filled from the item being crafted");
            Check(ironView.Satisfiable == button, "FD-04 the preview agrees with the button");

            // A recipe naming its own output as an ingredient: same rule, simpler shape.
            const int GEAR = 10, PLATE = 11, BENCH = 91;
            var env2 = new FakeEnvironment().WithStations(BENCH);
            var selfFed = env2.AddRecipe(GEAR, 2, new[] { (GEAR, 1), (PLATE, 1) }, tiles: new[] { BENCH });
            var r2 = new CoreResolver(env2);
            var stock2 = new Dictionary<int, int> { [GEAR] = 50, [PLATE] = 50 };
            var views2 = r2.ComputeIngredientPreview(selfFed, stock2, 1);
            Eq(views2.Find(v => v.Type == GEAR).TotalHave, 0, "FD-05 an output named as its own ingredient draws nothing");
        }

        // ---- A recipe-group slot mixes members at EVERY level, not just the top ----
        // ResolveIngredientSlot mixes; CanProduce used to commit to one concrete type, so
        // feasibility disagreed with the plan for any group slot below the top level.
        private static void NestedGroupSlotsMixMembers()
        {
            Section("Group slots mix members below the top level");
            const int GOLD = 10, PLAT = 11, WIDGET = 12, DISK = 13, ANVIL = 91, GRP = 501;

            var env = new FakeEnvironment().WithStations(ANVIL).WithGroup(GRP, GOLD, PLAT);
            env.AddRecipe(WIDGET, 1, new[] { (GOLD, 10) }, tiles: new[] { ANVIL }, groups: new[] { GRP });
            var disk = env.AddRecipe(DISK, 1, new[] { (WIDGET, 1) }, tiles: new[] { ANVIL });
            var r = new CoreResolver(env);

            // Neither metal alone covers the slot; together they do.
            var stock = new Dictionary<int, int> { [GOLD] = 3, [PLAT] = 7 };
            var steps = new List<CoreStep>();
            bool button = r.TryResolveRecipe(disk, DISK, 1, new Dictionary<int, int>(stock),
                steps, new HashSet<int> { DISK }, 0);
            var views = r.ComputeIngredientPreview(disk, stock, 1);

            IsTrue(button, "NG-01 the plan mixes 3 gold + 7 platinum one level down");
            Check(ListCraftableNoPrefilter(r, disk, stock) == button, "NG-02 the list flag agrees");
            Check(views.TrueForAll(v => v.Satisfiable) == button, "NG-03 the preview agrees");
            Check(r.IsFeasibleFromSnapshot(DISK, 1, new Dictionary<int, int>(stock)) == button,
                "NG-04 snapshot feasibility agrees");

            // One short: all three must still agree, now on false.
            var scarce = new Dictionary<int, int> { [GOLD] = 3, [PLAT] = 6 };
            var steps2 = new List<CoreStep>();
            bool button2 = r.TryResolveRecipe(disk, DISK, 1, new Dictionary<int, int>(scarce),
                steps2, new HashSet<int> { DISK }, 0);
            IsFalse(button2, "NG-05 nine units cannot fill a ten-unit slot");
            Check(ListCraftableNoPrefilter(r, disk, scarce) == button2, "NG-06 the list flag agrees when short");
        }

        // ---- One recipe's ingredient verdict must not decide another's ----
        // IsIngredientFeasible seeds the cycle guard with the recipe's output, so a cached verdict
        // is only valid for that output. Keying it only when the output happened to be in stock
        // let whichever recipe was evaluated first decide for both.
        private static void IngredientCacheIsScopedByOutput()
        {
            Section("Ingredient cache is scoped by the recipe's output");
            const int OUT1 = 20, OUT2 = 21, MID = 22, BASE = 23;

            var env = new FakeEnvironment();
            var r1 = env.AddRecipe(OUT1, 1, new[] { (MID, 1) });
            var r2 = env.AddRecipe(OUT2, 1, new[] { (MID, 1) });
            env.AddRecipe(MID, 1, new[] { (OUT1, 1) });   // MID routes back through OUT1
            env.AddRecipe(OUT1, 1, new[] { (BASE, 1) });  // the real route to OUT1
            var r = new CoreResolver(env);
            var stock = new Dictionary<int, int> { [BASE] = 10 };

            bool alone1 = ListCraftableNoPrefilter(r, r1, stock);
            bool alone2 = ListCraftableNoPrefilter(r, r2, stock);
            IsFalse(alone1, "IO-01 OUT1 cannot be made: its only ingredient routes back through itself");
            IsTrue(alone2, "IO-02 OUT2 can be made: MID may route through OUT1 freely");

            // Both orders, one shared cache: the verdicts must not move.
            var forward = new Dictionary<(int ctx, int group, int type, int stack), bool>();
            bool f1 = r.RecheckRecipeCraftable(r1, new Dictionary<int, int>(stock), forward);
            bool f2 = r.RecheckRecipeCraftable(r2, new Dictionary<int, int>(stock), forward);
            Check(f1 == alone1, "IO-03 sharing a cache does not change OUT1's verdict");
            Check(f2 == alone2, "IO-04 sharing a cache does not change OUT2's verdict");

            var reverse = new Dictionary<(int ctx, int group, int type, int stack), bool>();
            bool b2 = r.RecheckRecipeCraftable(r2, new Dictionary<int, int>(stock), reverse);
            bool b1 = r.RecheckRecipeCraftable(r1, new Dictionary<int, int>(stock), reverse);
            Check(b1 == alone1, "IO-05 evaluation order does not change OUT1's verdict");
            Check(b2 == alone2, "IO-06 evaluation order does not change OUT2's verdict");
        }

        // ---- An abort on a full network must return storage exactly as it was ----
        // Refunding everything and then discarding the conjured intermediates overflows by exactly
        // the conjured amount, and real materials drop as leftover.
        private static void AbortRefundSurvivesAFullNetwork()
        {
            Section("An aborted plan refunds fully even with no spare room");
            const int WOOD = 1, PLANK = 2, IRON = 3, TARGET = 4;

            // 5 wood + 2 iron + 2 planks the player already owned = 9 units; capacity is exactly 9.
            var storage = new FakeStorage().With(WOOD, 5).With(IRON, 2).With(PLANK, 2);
            storage.Capacity = 9;
            var steps = Steps(
                (new[] { (WOOD, 5) }, PLANK, 1),
                (new[] { (PLANK, 3), (IRON, 3) }, TARGET, 1));   // only 2 iron, so it aborts

            var result = new PlanExecutor<FakeItem>(storage).Run(steps, 1, new FakeStepProducer(steps));

            Eq(storage.StackOf(result), 0, "AF-01 the plan aborts and produces nothing");
            Eq(storage.CountItem(WOOD), 5, "AF-02 the wood came back");
            Eq(storage.CountItem(IRON), 2, "AF-03 the iron came back, not dropped as leftover");
            Eq(storage.CountItem(PLANK), 2, "AF-04 the player's own planks survived, the conjured one did not");
            Eq(storage.TotalUnits, 9, "AF-05 storage is exactly as it started");

            // An intermediate no later step consumed is still in storage and must also go.
            var untouched = new FakeStorage().With(WOOD, 5).With(IRON, 1);
            var threeSteps = Steps(
                (new[] { (WOOD, 5) }, PLANK, 2),
                (new[] { (PLANK, 1) }, TARGET, 1),
                (new[] { (IRON, 5) }, TARGET, 1));
            var aborted = new PlanExecutor<FakeItem>(untouched).Run(threeSteps, 1, new FakeStepProducer(threeSteps));
            Eq(untouched.StackOf(aborted), 0, "AF-06 the later step's shortfall aborts the plan");
            Eq(untouched.CountItem(WOOD), 5, "AF-07 materials refunded");
            Eq(untouched.CountItem(PLANK), 0, "AF-08 no conjured intermediate is left behind");
            Eq(untouched.CountItem(TARGET), 0, "AF-09 including one produced by a middle step");
        }

        // Builds the execution steps for a plan: each entry is (what it costs, what it makes).
        // ---- A withdrawal must be able to pay for what a recipe lists ----
        // Band of Door = 1 Shackle + 20 Door Pants at a Work Bench. Door Pants are armour, so 18 of
        // them are 18 stacks, and in the build this was reported from every stack stood for itself.
        // Extract hands back one such stack per call, so the step that asked for twenty got one:
        // the plan aborted and ExecuteCraft returned without a sound, a message or a change.
        private static void BandOfDoorIsPayableFromStacksThatStandAlone()
        {
            Section("Band of Door: twenty stacks that each stand alone pay for one recipe");
            const int BAND = 18120, SHACKLE = 216, DOOR_PANTS = 18121, WOODEN_DOOR = 25, WOOD = 9;

            var storage = new FakeStorage()
                .WithUniqueType(DOOR_PANTS)
                .With(WOOD, 4472)
                .With(SHACKLE, 1)
                .With(DOOR_PANTS, 18);

            Eq(storage.CountItem(DOOR_PANTS), 18, "BD-01 eighteen Door Pants, each its own stack");

            var steps = Steps(
                (new[] { (WOOD, 24) }, WOODEN_DOOR, 4),
                (new[] { (WOODEN_DOOR, 4) }, DOOR_PANTS, 2),
                (new[] { (SHACKLE, 1), (DOOR_PANTS, 20) }, BAND, 1));
            var band = new PlanExecutor<FakeItem>(storage).Run(steps, 1, new FakeStepProducer(steps));

            Eq(storage.StackOf(band), 1, "BD-02 the craft goes through");
            Eq(storage.CountItem(DOOR_PANTS), 0, "BD-02a spending all twenty");
            Eq(storage.CountItem(SHACKLE), 0, "BD-02b and the shackle");
            Eq(storage.CountItem(WOOD), 4448, "BD-02c leaving the wood the chain actually cost");

            // One short is still one short: the step must not be part-paid into a product.
            var short1 = new FakeStorage()
                .WithUniqueType(DOOR_PANTS)
                .With(SHACKLE, 1)
                .With(DOOR_PANTS, 19);
            var lastStep = Steps((new[] { (SHACKLE, 1), (DOOR_PANTS, 20) }, BAND, 1));
            var nothing = new PlanExecutor<FakeItem>(short1).Run(lastStep, 1, new FakeStepProducer(lastStep));
            Eq(short1.StackOf(nothing), 0, "BD-03 nineteen cannot pay for twenty");
            Eq(short1.CountItem(DOOR_PANTS), 19, "BD-03a and every stack came back");
            Eq(short1.CountItem(SHACKLE), 1, "BD-03b along with the shackle");
        }

        // ---- Drawing several stacks must not blur them into one ----
        // Issue 05: the reason a withdrawal only ever took one stack that stands for itself was
        // that ONE item handle cannot carry two stacks' mod state. Taking them as separate handles
        // is what makes drawing twenty safe - each goes back as the stack it came from.
        private static void SeparateStacksKeepTheirStateThroughARefund()
        {
            Section("A multi-stack draw refunds each stack with the state it came with");
            const int CHARM = 7, IRON = 3, TARGET = 4, GOLD = 8;

            var storage = new FakeStorage()
                .WithUniqueType(CHARM)
                .WithUniqueStack(CHARM, 1, "enchanted")
                .WithUniqueStack(CHARM, 1, "charged")
                .WithUniqueStack(CHARM, 1, "plain");

            // The iron is absent, so the step cannot be paid for and everything drawn goes back.
            var steps = Steps((new[] { (CHARM, 3), (IRON, 1) }, TARGET, 1));
            var made = new PlanExecutor<FakeItem>(storage).Run(steps, 1, new FakeStepProducer(steps));

            Eq(storage.StackOf(made), 0, "ID-01 the unpayable step produces nothing");
            Eq(storage.CountItem(CHARM), 3, "ID-01a all three charms came back");
            string marks = string.Join(",", storage.MarksOf(CHARM));
            Check(marks == "charged,enchanted,plain",
                $"ID-02 each with the state it went in with, none copied over another  [got {marks}]");

            // The case that decides which handles a refund keeps: the run makes two charms of its
            // own, a later step cannot be paid for, and the three the player owned must be the
            // three that come back. Dropping conjured units from the front of the ledger instead
            // of the end put the right COUNT back and the wrong items - two of the player's
            // stacks destroyed, two stateless copies in their place.
            var withConjured = new FakeStorage()
                .WithUniqueType(CHARM)
                .WithUniqueStack(CHARM, 1, "own-a")
                .WithUniqueStack(CHARM, 1, "own-b")
                .WithUniqueStack(CHARM, 1, "own-c")
                .With(IRON, 2);

            var chain = Steps(
                (new[] { (IRON, 2) }, CHARM, 2),
                (new[] { (CHARM, 5), (GOLD, 1) }, TARGET, 1));
            var never = new PlanExecutor<FakeItem>(withConjured).Run(chain, 1, new FakeStepProducer(chain));

            Eq(withConjured.StackOf(never), 0, "ID-03 the chain cannot be paid for");
            Eq(withConjured.CountItem(CHARM), 3, "ID-03a three charms are back by count");
            string kept = string.Join(",", withConjured.MarksOf(CHARM));
            Check(kept == "own-a,own-b,own-c",
                $"ID-04 and they are the player's own three, not the run's copies  [got {kept}]");
            Eq(withConjured.CountItem(IRON), 2, "ID-04a with the iron refunded");
        }


        // ---- The reported recipe, against the real recipe graph ----
        // A three-hop slice of the /tsdump the bug was reported from, so the plan under test is the
        // one the game actually built rather than a hand-written approximation of it.
        private static void BandOfDoorFixtureBuildsTheReportedPlan()
        {
            Section("Band of Door: the real dump slice resolves to the reported plan");
            const int BAND = 18120, SHACKLE = 216, DOOR_PANTS = 18121, WOODEN_DOOR = 25, WOOD = 9;

            string fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "band-of-door.tsdump.txt");
            if (!File.Exists(fixture)) { Check(false, $"FX-00 fixture missing at {fixture}"); return; }

            var env = new FakeEnvironment();
            var counted = new Dictionary<int, int>();
            var stations = new HashSet<int>();
            ParseDump(fixture, env, counted, stations);

            Eq(env.AllRecipes.Count, 6, "FX-01 the slice carries the recipes the chain needs");
            Eq(counted[DOOR_PANTS], 18, "FX-02 the dump counted eighteen Door Pants");

            var steps = new List<CoreStep>();
            var core = new CoreResolver(env) { MaxDepth = 10 };
            IsTrue(core.ResolveRecursive(BAND, 1, new Dictionary<int, int>(counted), steps, new HashSet<int>(), 0),
                "FX-03 which made the panel offer the craft");
            Eq(steps.Count, 3, "FX-03a through the same three steps the game planned");

            var finalStep = steps[steps.Count - 1];
            Eq(finalStep.ProducedType, BAND, "FX-04 the last step makes the band");
            Eq(finalStep.Consumed[DOOR_PANTS], 20, "FX-04a and asks storage for twenty Door Pants at once");
            Eq(finalStep.Consumed[SHACKLE], 1, "FX-04b plus the shackle");
            Eq(steps[0].ProducedType, WOODEN_DOOR, "FX-05 the chain starts at wooden doors");
            Eq(steps[0].Consumed[WOOD], 24, "FX-05a costing the wood the dump had");

            // Held as stacks that each stand for themselves - which is what the build the bug was
            // Executed against storage laid out the way the report's build laid it out - every
            // stack standing for itself - the plan the dump produced now goes through.
            var asStoredThen = new FakeStorage()
                .WithUniqueType(DOOR_PANTS)
                .With(WOOD, counted[WOOD])
                .With(SHACKLE, counted[SHACKLE])
                .With(DOOR_PANTS, counted[DOOR_PANTS]);

            var executable = Steps(
                (new[] { (WOOD, steps[0].Consumed[WOOD]) }, WOODEN_DOOR, steps[0].ProducedCount),
                (new[] { (WOODEN_DOOR, steps[1].Consumed[WOODEN_DOOR]) }, DOOR_PANTS, steps[1].ProducedCount),
                (new[] { (SHACKLE, 1), (DOOR_PANTS, 20) }, BAND, 1));
            var band = new PlanExecutor<FakeItem>(asStoredThen).Run(executable, 1, new FakeStepProducer(executable));

            Eq(asStoredThen.StackOf(band), 1, "FX-06 and the craft the player asked for goes through");
            Eq(asStoredThen.CountItem(DOOR_PANTS), 0, "FX-06a spending every Door Pant");
        }

        private static List<StackSlot> UniqueOnes(int count)
        {
            var sizes = new int[count];
            for (int index = 0; index < count; index++)
                sizes[index] = 1;
            return UniqueStacks(sizes);
        }

        private static List<StackSlot> UniqueStacks(params int[] sizes)
        {
            var slots = new List<StackSlot>();
            for (int index = 0; index < sizes.Length; index++)
                slots.Add(new StackSlot { Index = index, Stack = sizes[index], IsUnique = true });
            return slots;
        }

        private static List<StackSlot> PlainStacks(params int[] sizes)
        {
            var slots = new List<StackSlot>();
            for (int index = 0; index < sizes.Length; index++)
                slots.Add(new StackSlot { Index = index, Stack = sizes[index] });
            return slots;
        }
        private static List<ExecutionStep> Steps(params ((int type, int count)[] consumed, int producedType, int producedCount)[] steps)
        {
            var built = new List<ExecutionStep>();

            foreach (var (consumed, producedType, producedCount) in steps)
            {
                var step = new ExecutionStep { ProducedType = producedType, ProducedCount = producedCount };
                step.Consumed.AddRange(consumed.Select(c => (c.type, c.count)));
                built.Add(step);
            }

            return built;
        }

        private static void VanillaMouseBlockingStaysInTheUIPhase()
        {
            Section("Vanilla mouse blocking is decided in the UI phase only");

            string repoRoot = FindRepoRoot();
            IsTrue(repoRoot != null, "PU-00 repo root located from " + AppContext.BaseDirectory);
            if (repoRoot == null) return;

            var uiPhaseOwners = new SortedSet<string>(StringComparer.Ordinal)
            {
                "Content/UI/CraftingCoreUIState.cs",
                "Content/UI/CraftingTree/CraftingTreeState.cs",
                "Content/UI/DiskRecoveryUIState.cs",
                "Content/UI/DriveBayUIState.cs",
                "Content/UI/Elements/TSButton.cs",
                "Content/UI/Elements/TSCloseButton.cs",
                "Content/UI/Elements/TSTab.cs",
                "Content/UI/Elements/TSWindowElement.cs",
                "Content/UI/Elements/UICategoryFilterBar.cs",
                "Content/UI/Elements/UICraftingPanel.cs",
                "Content/UI/Elements/UIDiskPanel.cs",
                "Content/UI/Elements/UISortBar.cs",
                "Content/UI/Encyclopedia/EncyclopediaState.cs",
                "Content/UI/FavoritedRecipesPanelSystem.cs",
                "Content/UI/TerminalUIState.cs",
                "Content/UI/UIFavoritedRecipesPanel.cs"
            };

            var blockers = new SortedSet<string>(FindFilesAssigningMouseInterface(repoRoot), StringComparer.Ordinal);

            string outsideTheUIPhase = string.Join(", ", blockers.Except(uiPhaseOwners));
            string stoppedBlocking = string.Join(", ", uiPhaseOwners.Except(blockers));

            IsTrue(outsideTheUIPhase.Length == 0,
                "PU-01 mouseInterface is assigned only from UI-phase code [" + outsideTheUIPhase + "]");
            IsTrue(stoppedBlocking.Length == 0,
                "PU-02 every Requisition window still blocks the vanilla mouse [" + stoppedBlocking + "]");
        }

        private static IEnumerable<string> FindFilesAssigningMouseInterface(string repoRoot)
        {
            foreach (string file in Directory.EnumerateFiles(repoRoot, "*.cs", SearchOption.AllDirectories))
            {
                if (IsOutsideModSource(repoRoot, file)) continue;
                if (!AssignsMouseInterface(StripLineComments(File.ReadAllText(file)))) continue;

                yield return file.Substring(repoRoot.Length).TrimStart(Path.DirectorySeparatorChar)
                                 .Replace(Path.DirectorySeparatorChar, '/');
            }
        }

        private static bool AssignsMouseInterface(string source)
        {
            const string field = "mouseInterface";

            int search = 0;
            while (true)
            {
                int mention = source.IndexOf(field, search, StringComparison.Ordinal);
                if (mention < 0) return false;
                search = mention + field.Length;

                int after = search;
                while (after < source.Length && char.IsWhiteSpace(source[after])) after++;
                if (after < source.Length && source[after] == '=' && source[after + 1] != '=') return true;
            }
        }

        // The scroll bound and the draw path each computed the grid's height from their own copy of
        // the layout numbers, drifted apart, and the grid scrolled into blank space. Both must read
        // the shared helper, and the scroll input must not do geometry at all.
        // Snapshotting every disk in the world on every storage operation made registry size a cost
        // multiplier on each of them (issue 27, item 4). The snapshot is now scoped to the
        // operation's own network, which is only safe because a disk modified outside that scope is
        // sent whole rather than handed an empty before-state - which would have told the client
        // every stack on a disk it already knows had just appeared.
        //
        // StorageWorldSystem and DriveBayUIState both bind Terraria.Item, so like MX-12 and SG-07
        // this is a source scan. It pins the structure, not the behaviour: it cannot prove the scope
        // covers what an operation touches, only that under-scoping degrades to a full sync.
        private static void TrackingSnapshotIsScopedAndFailsSafe()
        {
            Section("Tracking snapshots the operation's network, and under-scoping fails safe");

            string repoRoot = FindRepoRoot();
            IsTrue(repoRoot != null, "TS-00 repo root located");
            if (repoRoot == null) return;

            string worldSystem = ReadModSource(repoRoot, "Systems/StorageWorldSystem.cs");
            string beginBody = ExtractMethodBody(worldSystem, "public void BeginModificationTracking");

            IsTrue(beginBody.Contains("_preModificationSnapshot"),
                "TS-01 the BeginModificationTracking body was located");
            IsTrue(!beginBody.Contains("foreach (var kvp in _allDiskData)"),
                "TS-02 it no longer snapshots every disk registered in the world");
            IsTrue(beginBody.Contains("operationDiskIds"),
                "TS-03 it snapshots the disks the operation was issued against");

            string endBody = ExtractMethodBody(worldSystem, "EndModificationTrackingWithDeltas");

            IsTrue(endBody.Contains("needsFullSync"),
                "TS-04 a modified disk with no snapshot is reported for a full sync");
            IsTrue(!endBody.Contains("snap : new List<StoredItemStack>()"),
                "TS-05 and never given an empty before-state, which reads as an all-new disk");

            string handler = ReadModSource(repoRoot, "Systems/NetworkHandler.cs");

            IsTrue(!handler.Contains("BeginModificationTracking()"),
                "TS-06 no handler starts tracking without naming its network");
            IsTrue(handler.Contains("BroadcastFullSyncFor"),
                "TS-07 and the un-describable disks are actually sent");

            // The prune lived only in the server branch of a packet handler, and singleplayer takes
            // a disk out of a bay without sending one - so empty entries accumulated until the next
            // world load purged them.
            string bayPanel = ReadModSource(repoRoot, "Content/UI/DriveBayUIState.cs");

            IsTrue(CountOccurrences(bayPanel, "DropOrphanedDiskData") >= 2,
                "TS-08 singleplayer drops an emptied disk's entry on both removal paths too");
        }

        private static void ScrollBoundsComeFromTheDrawGeometry()
        {
            Section("Scroll bounds are derived from the same geometry the draw path uses");

            string repoRoot = FindRepoRoot();
            IsTrue(repoRoot != null, "SG-00 repo root located from " + AppContext.BaseDirectory);
            if (repoRoot == null) return;

            string diskPanel = ReadModSource(repoRoot, "Content/UI/Elements/UIDiskPanel.cs");
            string scrollWheelBody = ExtractMethodBody(diskPanel, "public override void ScrollWheel");

            IsTrue(scrollWheelBody.Contains("GetGridScrollRows"),
                "SG-01 the ScrollWheel body was located, so the absence checked below is real");
            IsTrue(!scrollWheelBody.Contains("UpgradeSectionHeight") && !scrollWheelBody.Contains("ItemCellSize"),
                "SG-02 UIDiskPanel.ScrollWheel computes no grid geometry, it only accumulates");
            IsTrue(CountOccurrences(diskPanel, "GetContentsGridHeight(") >= 3,
                "SG-03 the contents-grid height has one definition and both the clamp and the draw call it");
            IsTrue(CountOccurrences(diskPanel, "GetDiskListHeight(") >= 4,
                "SG-04 the disk-list height is shared by the clamp, the draw and the hit test");

            string craftingPanel = ReadModSource(repoRoot, "Content/UI/Elements/UICraftingPanel.cs");

            IsTrue(CountOccurrences(craftingPanel, "GetGridVisibleRows(") >= 3,
                "SG-05 the recipe grid's visible-row count has one definition, used by the view and the draw");
            IsTrue(!craftingPanel.Contains("- 25) / CellSize"),
                "SG-06 no open-coded header subtraction survives beside the shared getter");

            // The craft button is gated on the plan, not on the recipe, so deselecting has to drop
            // the plan with it. Clicking the selected recipe again and then CRAFT otherwise reached
            // ExecuteCraft with _selectedRecipe null. UICraftingPanel needs Terraria, so this is a
            // source scan for the same reason SG-01..SG-06 are.
            string deselectBody = ExtractMethodBody(craftingPanel, "private void DeselectRecipe");

            IsTrue(deselectBody.Contains("_selectedRecipe = null"),
                "SG-07 the DeselectRecipe body was located, so the presence checked below is real");
            IsTrue(deselectBody.Contains("_currentPlan = null"),
                "SG-07a deselecting clears the plan the craft button is gated on");
            IsTrue(deselectBody.Contains("_craftIsNoOp = false"),
                "SG-07b and the no-op verdict derived from that plan");
        }

        // ---- The defragment merge-candidate index must never hide a mergeable stack ----
        // Issue 23i: BuildMergeTargets asked the merge rule about every stack on the target disk for
        // every donor stack, which at the supported maximum froze the game thread for about a third
        // of a second.
        // The index narrows the field to stacks sharing the donor's type and prefix - but a key that
        // disagreed with DiskData.CanMergeStacks would be issue 04 again, as silent duplication.
        private static void MergeCandidateIndexAgreesWithTheMergeRule()
        {
            Section("Defragment's merge-candidate index agrees with the merge rule");

            const int StackCount = 400, DistinctTypes = 37;
            int[] prefixes = { -1, 0, 1 };

            var rng = new Random(23);
            var types = new int[StackCount];
            var stackPrefixes = new int[StackCount];
            var index = new MergeCandidateIndex();

            for (int i = 0; i < StackCount; i++)
            {
                types[i] = rng.Next(DistinctTypes);
                stackPrefixes[i] = prefixes[rng.Next(prefixes.Length)];
                index.Add(types[i], stackPrefixes[i], i);
            }

            // The index may only ever withhold a stack the merge rule would have refused anyway, so
            // its candidate set has to be exactly what the linear rescan it replaces would find.
            bool everyKeyAgreed = true;
            bool everyBucketAscends = true;
            int keysChecked = 0;

            foreach (int itemType in Enumerable.Range(0, DistinctTypes))
            {
                foreach (int prefixId in prefixes)
                {
                    var expected = new List<int>();
                    for (int i = 0; i < StackCount; i++)
                        if (types[i] == itemType && stackPrefixes[i] == prefixId)
                            expected.Add(i);

                    var candidates = index.GetCandidates(itemType, prefixId);
                    everyKeyAgreed &= expected.Count == candidates.Count
                        && expected.SequenceEqual(candidates);

                    for (int c = 1; c < candidates.Count; c++)
                        everyBucketAscends &= candidates[c] > candidates[c - 1];

                    keysChecked++;
                }
            }

            IsTrue(everyKeyAgreed,
                $"MX-01 every stack sharing a donor's type and prefix is a candidate ({keysChecked} keys)");
            IsTrue(everyBucketAscends,
                "MX-02 candidates come back in ascending slot order, so the earliest partial fills first");

            var missing = index.GetCandidates(DistinctTypes + 5, 0);
            Eq(missing.Count, 0, "MX-03 an identity no stack carries has no candidates");
            IsTrue(ReferenceEquals(missing, index.GetCandidates(DistinctTypes + 6, 3)),
                "MX-03a and an identity never seen at all hands back one shared empty list, not a new one");

            // The sweep appends to the target disk as it moves stacks in - a whole stack relocated,
            // or a fresh slot taken - and a later donor of the same identity has to find them.
            index.Add(types[0], stackPrefixes[0], StackCount);
            var grown = index.GetCandidates(types[0], stackPrefixes[0]);
            Eq(grown[grown.Count - 1], StackCount, "MX-04 a slot appended mid-sweep becomes a candidate, still last");

            // A disk that donated to an earlier target arrives as a target with its stacks moved, so
            // nothing recorded against the previous disk may survive.
            index.Clear();
            int survivors = 0;
            foreach (int itemType in Enumerable.Range(0, DistinctTypes))
                foreach (int prefixId in prefixes)
                    survivors += index.GetCandidates(itemType, prefixId).Count;
            Eq(survivors, 0, "MX-05 Clear leaves no slot from the previous target disk");

            index.Add(700, 0, 4);
            Eq(index.GetCandidates(700, 1).Count, 0, "MX-06 two identities differing only in prefix never share a bucket");
            Eq(index.GetCandidates(700, 0).Count, 1, "MX-06a while the prefix that was added still resolves");

            MergeCandidateIndexKeyMatchesTheMergeRule();
        }

        // The link from "the index returns every stack sharing a type and prefix" to "the index
        // never hides a mergeable stack". The merge rule needs Terraria and cannot be linked here,
        // so the two halves that make the key safe are asserted against the source itself.
        private static void MergeCandidateIndexKeyMatchesTheMergeRule()
        {
            string repoRoot = FindRepoRoot();
            IsTrue(repoRoot != null, "MX-07 repo root located from " + AppContext.BaseDirectory);
            if (repoRoot == null) return;

            string storedStack = ReadModSource(repoRoot, "Common/StoredItemStack.cs");
            string stacksWithBody = ExtractMethodBody(storedStack, "public bool StacksWith(StoredItemStack other)");

            IsTrue(stacksWithBody.Length > 0, "MX-07a the merge rule's identity test was located");

            // The key is only safe while a mismatched type or prefix is an outright REFUSAL, and
            // while nothing can say yes ahead of it. Asserting the guard's text comes first is not
            // enough: a guard that fell through to GameAllowsStacking instead of returning false
            // would read the same and would make the key hide real merges. Pin the whole statement.
            string identityGuard = "if (ItemType != other.ItemType || PrefixId != other.PrefixId)";
            int guardAt = stacksWithBody.IndexOf(identityGuard, StringComparison.Ordinal);
            string afterGuard = guardAt < 0
                ? string.Empty
                : stacksWithBody.Substring(guardAt + identityGuard.Length).TrimStart();

            IsTrue(guardAt >= 0 && afterGuard.StartsWith("return false;", StringComparison.Ordinal),
                "MX-08 StacksWith refuses a mismatched type or prefix outright, so both halves of the key are necessary");

            string beforeGuard = guardAt < 0 ? stacksWithBody : stacksWithBody.Substring(0, guardAt);
            IsTrue(!beforeGuard.Contains("return"),
                "MX-08a and nothing can say yes ahead of it - the guard is the first thing the rule does");

            // The guard above lives on the StoredItemStack overload. The Item overload of StacksWith
            // has no type or prefix test at all, so the key would be unsafe the moment the merge
            // rule reached it - pin which one CanMergeStacks actually calls.
            string diskData = ReadModSource(repoRoot, "Common/DiskData.cs");
            IsTrue(diskData.Contains("public static bool CanMergeStacks(StoredItemStack a, StoredItemStack b)")
                   && diskData.Contains("=> a.StacksWith(b) &&"),
                "MX-08b the merge rule reaches StacksWith through the overload that carries the guard");

            // The sweep moved into Common/DefragmentCore.cs, where DG-* executes it. What text still
            // has to pin is the part behaviour cannot see: that the candidates come from the index
            // at all. A linear rescan of the target produces IDENTICAL results, so no assertion
            // about what the sweep MOVES can tell the two apart - only the source can.
            string core = ReadModSource(repoRoot, "Common/DefragmentCore.cs");
            string buildBody = ExtractMethodBody(core, "private static void BuildMergeTargets");
            string sweepBody = ExtractMethodBody(core, "public static List<int> Sweep");

            IsTrue(buildBody.Length > 0 && sweepBody.Length > 0,
                "MX-09a the sweep and its candidate filter were located in DefragmentCore");

            IsTrue(buildBody.Contains("GetCandidates("),
                "MX-10 and the candidates come from the index rather than a rescan of the disk");
            // Pin the absence of the linear WALK, not of the token: the bounds check that keeps a
            // stale slot from crediting the wrong stack has to read target.Items.Count too.
            IsTrue(!buildBody.Contains("< target.Items.Count"),
                "MX-10a with no linear walk of the target left in it");
            IsTrue(buildBody.Contains("index >= target.Items.Count"),
                "MX-10b and a slot past the end of the disk is passed over rather than credited");

            // A disk list naming one disk twice makes it its own donor; removing a stack from the
            // donor then shifts every slot the index recorded for the target. Comparing the Items
            // lists catches an alias that comparing disk indices cannot.
            IsTrue(sweepBody.Contains("ReferenceEquals(target.Items, donor.Items)"),
                "MX-11 a disk repeated in the request never donates to itself");

            // Everything below is the half that still cannot be linked, because it binds Terraria.
            // A source match is the only mechanism available for it, and it is the residual gap.
            string worldSystem = ReadModSource(repoRoot, "Systems/StorageWorldSystem.cs");

            // The rule the sweep asks is the rule DiskData defines, in that argument order. A
            // transposed pair, or a fallback to comparing type and prefix, is issue 24 verbatim -
            // and every DG-* case would stay green through it.
            // Matched against the whole file rather than a method body: ExtractMethodBody walks to
            // the next brace, which an expression-bodied member does not have.
            IsTrue(worldSystem.Contains("=> DiskData.CanMergeStacks(target, donor)"),
                "MX-09 the merge rule, not the index, still decides whether a candidate accepts");

            // Without this the sweep could be extracted and then quietly re-implemented in place:
            // every DG-*, DF-* and MX-* assertion would stay green while the shipped path diverged.
            string defragBody = ExtractMethodBody(worldSystem, "public List<Guid> Defragment");
            IsTrue(defragBody.Contains("DefragmentCore.Sweep("),
                "MX-12 Defragment delegates to the sweep that DG-* executes");
            // The property is "does no stack surgery of its own", not "contains no for-loop": a
            // private copy of the sweep written with foreach or while would pass a token match.
            IsTrue(!defragBody.Contains("Items.Add(") && !defragBody.Contains("Items.RemoveAt("),
                "MX-12a and moves no stacks itself");

            IsTrue(worldSystem.Contains("=> DiskData.HasPerInstanceData(stack)"),
                "MX-13 the sweep's uniqueness verdict is DiskData's");
            IsTrue(worldSystem.Contains("=> CopyStackWithCount(source, count)"),
                "MX-13a and a stack it splits is built by CopyStackWithCount");

            // Issue 04's third fix bullet - carry ModData and FullItemTag onto the new stack - is
            // this method, and nothing asserted it at all until now.
            // Match the ASSIGNMENT, not the token: "ModData" alone is satisfied by ModData = null,
            // which is precisely the regression this exists to catch.
            string copyBody = ExtractMethodBody(worldSystem,
                "private static StoredItemStack CopyStackWithCount");
            IsTrue(copyBody.Contains("= source.ModData") && copyBody.Contains("= source.FullItemTag"),
                "MX-14 a split stack carries the per-instance data that makes it what it is");
            IsTrue(copyBody.Contains("= source.InsertionOrder") && copyBody.Contains("CopyIdentityVerdictFrom(source)"),
                "MX-14a along with its insertion order and its settled identity verdict");
        }

        // The UI closes its panel at a tile distance from the block's stored position; the server
        // used to measure pixels to the block's 3x3 centre, which sits 1.5 tiles down-right of it.
        // A player up-and-left could therefore be inside the range the panel enforced and outside
        // the one the server did - the panel stays open while every packet it sends is refused.
        // These sweep the boundary rather than sampling it, which is issue 20's whole lesson.
        private static void TerminalReachIsOneRule()
        {
            Section("Terminal reach: one origin for the panel and the packet");

            Eq(TerminalReach.GetRangeInTiles(), 15,
                "TR-01 the range is the 15 tiles both UI panels close at");
            Eq(TerminalReach.GetTilePixelSize(), 16,
                "TR-02 a tile is the 16 pixels both UI panels divide by");

            const int blockTileX = 100;
            const int blockTileY = 200;
            float tileSize = TerminalReach.GetTilePixelSize();

            IsTrue(TerminalReach.IsWithinRange(blockTileX * tileSize, blockTileY * tileSize,
                    blockTileX, blockTileY),
                "TR-03 standing on the block is in range");
            IsTrue(TerminalReach.IsWithinRange((blockTileX + 15f) * tileSize, blockTileY * tileSize,
                    blockTileX, blockTileY),
                "TR-04 exactly 15 tiles away is still in range");
            IsFalse(TerminalReach.IsWithinRange((blockTileX + 15.01f) * tileSize, blockTileY * tileSize,
                    blockTileX, blockTileY),
                "TR-05 a hundredth of a tile past 15 is not");
            IsTrue(TerminalReach.IsWithinRange((blockTileX - 15f) * tileSize, blockTileY * tileSize,
                    blockTileX, blockTileY)
                && TerminalReach.IsWithinRange(blockTileX * tileSize, (blockTileY - 15f) * tileSize,
                    blockTileX, blockTileY),
                "TR-06 the rule does not care which side of the block the player stands on");

            // Eight directions, 0 to 20 tiles in twentieths. A rule with one boundary flips once per
            // direction; a rule measured from a different origin flips somewhere else, or twice.
            int directionsWithOneFlip = 0;
            int stepsDisagreeingWithThePanel = 0;
            for (int direction = 0; direction < 8; direction++)
            {
                double angle = direction * Math.PI / 4d;
                double unitX = Math.Cos(angle);
                double unitY = Math.Sin(angle);

                bool previousVerdict = true;
                int flips = 0;
                double flippedAt = -1d;
                for (int step = 0; step <= 400; step++)
                {
                    double distanceInTiles = step * 0.05d;
                    float playerX = (float)((blockTileX + unitX * distanceInTiles) * tileSize);
                    float playerY = (float)((blockTileY + unitY * distanceInTiles) * tileSize);

                    bool inRange = TerminalReach.IsWithinRange(playerX, playerY, blockTileX, blockTileY);
                    if (inRange != PanelWouldStayOpen(playerX, playerY, blockTileX, blockTileY))
                        stepsDisagreeingWithThePanel++;

                    if (step > 0 && inRange != previousVerdict)
                    {
                        flips++;
                        flippedAt = distanceInTiles;
                    }
                    previousVerdict = inRange;
                }

                // Where it flips, not just that it flips once: a rule with the wrong range still has
                // exactly one boundary. One sweep step of tolerance, because the flip is recorded at
                // the first step past it.
                bool flippedAtTheStatedRange = flips == 1
                    && Math.Abs(flippedAt - TerminalReach.GetRangeInTiles()) <= 0.05d + 1e-9d;
                if (flippedAtTheStatedRange) directionsWithOneFlip++;
            }

            Eq(directionsWithOneFlip, 8,
                "TR-07 the verdict flips exactly once in every direction, and at the stated range");
            Eq(stepsDisagreeingWithThePanel, 0,
                "TR-08 the packet rule agrees with the panel's own formula at every swept point");
        }

        // TerminalUISystem.UpdateUI and DriveBayUISystem.UpdateUI, verbatim: the player's centre in
        // tiles against the entity's stored Position, closed when the distance exceeds 15.
        private static bool PanelWouldStayOpen(float playerCenterXPixels, float playerCenterYPixels,
            int blockTileX, int blockTileY)
        {
            float playerTileX = playerCenterXPixels / 16f;
            float playerTileY = playerCenterYPixels / 16f;
            double distance = Math.Sqrt((playerTileX - blockTileX) * (playerTileX - blockTileX)
                + (playerTileY - blockTileY) * (playerTileY - blockTileY));

            return !(distance > 15f);
        }

        private static void DiskAccessRules()
        {
            Section("Disk access: who may operate a Terminal, and when an entry may be dropped");

            IsTrue(DiskAccess.MayOperateTerminal(true, false),
                "DA-01 standing at the Terminal is enough");

            // The regression this pins is real and shipped: Defragment was the one handler scoped
            // by range, with no second arm, so it refused every Remote Terminal user outright. An
            // AND here would not be a stricter rule, it would be that bug restored - and this is
            // the row that fails when someone writes one.
            IsTrue(DiskAccess.MayOperateTerminal(false, true),
                "DA-02 a Remote Terminal reaches a Terminal the player is nowhere near");

            IsFalse(DiskAccess.MayOperateTerminal(false, false),
                "DA-03 neither arm means no");
            IsTrue(DiskAccess.MayOperateTerminal(true, true),
                "DA-04 both arms is still yes");

            IsTrue(DiskAccess.MayPruneDiskData(0, false),
                "DA-06 an empty entry no bay references may be dropped");
            IsFalse(DiskAccess.MayPruneDiskData(3, false),
                "DA-07 an entry still holding items is never dropped, wherever its disk went");
            IsFalse(DiskAccess.MayPruneDiskData(0, true),
                "DA-08 an entry another bay still holds is never dropped");
            IsFalse(DiskAccess.MayPruneDiskData(3, true),
                "DA-09 neither arm alone is enough");

            // Sweep rather than sample: the destructive mistake here is turning the AND into an OR,
            // which shows up only on the stacks-held rows.
            int prunable = 0;
            for (int usedStacks = 0; usedStacks <= 8; usedStacks++)
            {
                foreach (bool anotherBayHoldsDisk in new[] { false, true })
                {
                    if (DiskAccess.MayPruneDiskData(usedStacks, anotherBayHoldsDisk))
                        prunable++;
                }
            }
            Eq(prunable, 1,
                "DA-10 exactly one of the eighteen states may be pruned: empty and unreferenced");
        }

        // The handlers themselves cannot be compiled outside the game, so these read the source the
        // way DN-06/DN-07/DN-08 already do. They pin the wiring the predicates above are useless
        // without: that the rule is actually called, that the GUID list really left the wire, and
        // that a refused deposit hands the item back instead of eating it.
        private static void AuthorizationIsWiredIntoTheHandlers()
        {
            Section("Authorization wiring: the rule reaches every handler that needs it");

            string repoRoot = FindRepoRoot();
            IsTrue(repoRoot != null, "DA-11 repo root located from " + AppContext.BaseDirectory);
            if (repoRoot == null) return;

            string network = ReadModSource(repoRoot, "Systems/NetworkHandler.cs");

            var handlersNeedingATerminal = new[]
            {
                "HandleWithdrawItem(Mod mod",
                "HandleWithdrawItemByModData(Mod mod",
                "HandleWithdrawItemByFullItemTag(Mod mod",
                "HandleDepositItem(Mod mod",
                "HandleCraftRequest(Mod mod",
                "HandleDefragRequest(Mod mod",
                "HandleUpgradeDiskRequest(Mod mod",
            };

            int gated = 0;
            foreach (string signature in handlersNeedingATerminal)
            {
                string body = ExtractMethodBody(network, signature);
                if (body.Contains("TryResolveOperableTerminal", StringComparison.Ordinal)) gated++;
                else Check(false, $"DA-12 {signature} acts without resolving an operable Terminal");
            }
            Eq(gated, handlersNeedingATerminal.Length,
                "DA-12a every handler that spends a network resolves it from the named Terminal");

            // The signature change doc 25 recorded as the blocker for naming this handler's refusals.
            IsTrue(network.Contains("HandleUpgradeDiskRequest(Mod mod, BinaryReader reader, int whoAmI)",
                    StringComparison.Ordinal)
                && network.Contains("HandleUpgradeDiskRequest(mod, reader, whoAmI)", StringComparison.Ordinal),
                "DA-13 the upgrade handler takes the sender, and the dispatcher passes it");

            // A GUID list left on a client-to-server packet is the hole itself, so its absence is
            // asserted rather than assumed. Server-to-client broadcasts may still take a list -
            // those the server built itself.
            var clientToServerSenders = new[]
            {
                "SendWithdrawItem(Mod mod",
                "SendWithdrawItemByModData(Mod mod",
                "SendWithdrawItemByFullItemTag(Mod mod",
                "SendDepositItem(Mod mod",
                "SendCraftRequest(Mod mod",
                "SendDefragRequest(Mod mod",
                "SendUpgradeDiskRequest(Mod mod",
                "SendRequestDiskData(Mod mod",
            };

            int sendersNamingABlock = 0;
            foreach (string signature in clientToServerSenders)
            {
                int at = network.IndexOf(signature, StringComparison.Ordinal);
                if (at < 0) { Check(false, $"DA-14 {signature} not found"); continue; }

                string parameters = network.Substring(at, network.IndexOf(')', at) - at);
                bool carriesADiskList = parameters.Contains("List<Guid>", StringComparison.Ordinal);
                bool namesABlock = parameters.Contains("EntityId", StringComparison.Ordinal);

                if (!carriesADiskList && namesABlock) sendersNamingABlock++;
                else Check(false, $"DA-14 {signature} still carries a client-supplied disk list");
            }
            Eq(sendersNamingABlock, clientToServerSenders.Length,
                "DA-14a every client-to-server storage packet names a block, not a disk list");

            Eq(CountOccurrences(network, "ReadGuidList"), 0,
                "DA-14b the reader that pre-sized a list from a wire count is gone with it");

            // One origin for the range rule: the literals this replaced were the disagreement.
            Eq(CountOccurrences(network, "240f"), 0,
                "DA-15 no hand-rolled pixel range survives in the handlers");
            Eq(CountOccurrences(network, "* 16f + 24f"), 0,
                "DA-15a nor the centre offset that disagreed with the panel");

            // The panels are the other half of that rule, and the half a player sees. A panel that
            // keeps its own copy is free to drift back into the band where it stays open and every
            // packet it sends is refused - which is the defect, not the literal.
            var filesThatMustNotOwnARange = new[]
            {
                "Content/UI/TerminalUISystem.cs",
                "Content/UI/DriveBayUISystem.cs",
                "Systems/QuickStackSystem.cs",
                "Systems/AndroLibCompat.cs",
            };

            int filesDeferringToTheSharedRule = 0;
            foreach (string relativePath in filesThatMustNotOwnARange)
            {
                string source = ReadModSource(repoRoot, relativePath);
                bool ownsARange = source.Contains("MaxInteractDistance", StringComparison.Ordinal)
                    || source.Contains("240f", StringComparison.Ordinal)
                    || source.Contains("/ 16f", StringComparison.Ordinal);

                if (!ownsARange && source.Contains("TerminalReach.", StringComparison.Ordinal))
                    filesDeferringToTheSharedRule++;
                else
                    Check(false, $"DA-17 {relativePath} still keeps its own copy of the range rule");
            }
            Eq(filesDeferringToTheSharedRule, filesThatMustNotOwnARange.Length,
                "DA-17a every panel and helper asks the same rule the packet handlers ask");

            // The one ordering in this change that can destroy a player's stack: the client empties
            // the slot before sending, so a refusal that keeps the item deletes it.
            string depositBody = ExtractMethodBody(network, "HandleDepositItem(Mod mod");
            int guardAt = depositBody.IndexOf("if (Main.netMode != NetmodeID.Server)", StringComparison.Ordinal);
            int trackingAt = depositBody.IndexOf("BeginModificationTracking", StringComparison.Ordinal);
            IsTrue(guardAt >= 0 && trackingAt > guardAt, "DA-16 the deposit handler has a refusal window to check");

            string refusalWindow = depositBody.Substring(guardAt, trackingAt - guardAt);
            int earlyReturns = CountOccurrences(refusalWindow, "return;") - 1; // the netMode guard's own
            Eq(CountOccurrences(refusalWindow, "SendReturnItemToClient("), earlyReturns,
                "DA-16a every refused deposit hands the item back before it gives up on it");
        }

        private static string ReadModSource(string repoRoot, string relativePath)
            => StripLineComments(File.ReadAllText(Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar))));

        private static int CountOccurrences(string source, string needle)
        {
            int count = 0;
            int at = source.IndexOf(needle, StringComparison.Ordinal);
            while (at >= 0)
            {
                count++;
                at = source.IndexOf(needle, at + needle.Length, StringComparison.Ordinal);
            }
            return count;
        }

        // Returns the text between the method's opening brace and its matching close.
        private static string ExtractMethodBody(string source, string signature)
        {
            int start = source.IndexOf(signature, StringComparison.Ordinal);
            if (start < 0) return string.Empty;

            int open = source.IndexOf('{', start);
            if (open < 0) return string.Empty;

            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0)
                    return source.Substring(open, i - open);
            }
            return string.Empty;
        }

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "build.txt")))
                dir = dir.Parent;
            return dir?.FullName;
        }

        private static bool IsOutsideModSource(string repoRoot, string file)
        {
            string relative = file.Substring(repoRoot.Length).Replace(Path.DirectorySeparatorChar, '/');
            return relative.Contains("/obj/") || relative.Contains("/bin/")
                || relative.Contains("/.claude/") || relative.Contains("/Tests/");
        }

        private static string StripLineComments(string source)
        {
            var kept = new List<string>();
            foreach (string line in source.Split('\n'))
            {
                int comment = line.IndexOf("//", StringComparison.Ordinal);
                kept.Add(comment >= 0 ? line.Substring(0, comment) : line);
            }
            return string.Join("\n", kept);
        }

        // Two 1-stacks of the same fruit deposited into storage have to become one 2-stack, the way
        // they do in the player inventory and in a chest. They did not, because every item in a
        // modded world carries bytes some GlobalItem wrote, and carrying those was read as "this
        // stack is its own item".
        private static void StackIdentityTests()
        {
            Section("Stack identity: what makes a stack its own item");

            IsFalse(StackIdentity.IsUnique(hasModItemData: false, gameRefusesToStackWithPlainItem: false),
                "SI-01 a plain fruit carrying another mod's GlobalItem bytes is NOT its own item");

            IsTrue(StackIdentity.IsUnique(hasModItemData: true, gameRefusesToStackWithPlainItem: false),
                "SI-02 a storage disk, whose GUID is its own ModItem save data, IS its own item");

            IsTrue(StackIdentity.IsUnique(hasModItemData: false, gameRefusesToStackWithPlainItem: true),
                "SI-03 an item the game itself refuses to stack IS its own item");

            IsTrue(StackIdentity.MustPreserveFullTag(hasModItemData: false, carriesModWrittenData: true),
                "SI-04 mod-written bytes are still preserved for extraction");

            IsFalse(StackIdentity.MustPreserveFullTag(hasModItemData: false, carriesModWrittenData: false),
                "SI-05 a stack carrying nothing needs no full tag");

            // The reported bug, as the grid decides it: the terminal gives a stack its own cell
            // exactly when the stack is its own item, so two 1-stacks of the same fruit are one
            // cell and a storage disk is not folded into its neighbours.
            var fruit = new StackSlot { Index = 0, Stack = 1, IsUnique = false };
            var alsoFruit = new StackSlot { Index = 1, Stack = 1, IsUnique = false };
            var disk = new StackSlot { Index = 2, Stack = 1, IsUnique = true };

            var wholeFruitStack = StackSelection.PlanWithdrawal(
                new[] { fruit, alsoFruit }, 2, allowUniqueFallback: true, out bool tookUnique);
            Eq(wholeFruitStack.Sum(d => d.Count), 2, "SI-06 two 1-stacks of the same fruit come out as 2");
            IsFalse(tookUnique, "SI-07 pooling two plain stacks is not a unique withdrawal");

            var pastTheDisk = StackSelection.PlanWithdrawal(
                new[] { disk, fruit }, 2, allowUniqueFallback: true, out _);
            Eq(pastTheDisk.Sum(d => d.Count), 1, "SI-08 a stack that stands for itself is not drained into a count");
        }

        // A server that refuses a storage operation used to send a bare success flag the client
        // logged and threw away, so every denied craft was "click, nothing happens" in multiplayer.
        // The byte codes below are the wire format and the decisions behind them are shared by the
        // panel and the packet handler, neither of which can be compiled outside the game — so
        // everything that CAN be checked here is checked here.
        private static void DenialReasonsSurviveTheWire()
        {
            Section("Denial reasons: wire codes, the decisions behind them, and the burst throttle");

            var denied = StorageOperationFailures.GetDeniedFailures();

            // DN-14 first: these numbers ARE the wire format. Every other assertion in this
            // section passes under any numbering, so without this one a reorder ships silently.
            Eq((byte)StorageOperationFailure.None, 0, "DN-14 None is 0");
            Eq((byte)StorageOperationFailure.Unspecified, 1, "DN-14a Unspecified is 1");
            Eq((byte)StorageOperationFailure.RecipeNotFeasible, 2, "DN-14b RecipeNotFeasible is 2");
            Eq((byte)StorageOperationFailure.NoRoomInInventory, 3, "DN-14c NoRoomInInventory is 3");
            Eq((byte)StorageOperationFailure.NoRoomInStorageOrInventory, 4, "DN-14d NoRoomInStorageOrInventory is 4");
            Eq((byte)StorageOperationFailure.CraftCostingNoLongerHolds, 5, "DN-14e CraftCostingNoLongerHolds is 5");
            Eq((byte)StorageOperationFailure.NothingWithdrawn, 6, "DN-14f NothingWithdrawn is 6");
            Eq((byte)StorageOperationFailure.NothingDeposited, 7, "DN-14g NothingDeposited is 7");
            Eq((byte)StorageOperationFailure.NothingQuickStacked, 8, "DN-14h NothingQuickStacked is 8");
            Eq((byte)StorageOperationFailure.NoStorageInRange, 9, "DN-14i NoStorageInRange is 9");
            Eq((byte)StorageOperationFailure.NoStorageConnected, 10, "DN-14j NoStorageConnected is 10");
            Eq((byte)StorageOperationFailure.NoTerminalFound, 11, "DN-14k NoTerminalFound is 11");
            Eq((byte)StorageOperationFailure.NotAtDriveBay, 12, "DN-14n NotAtDriveBay is 12");
            Eq((byte)StorageOperationFailure.DiskNotInSlot, 13, "DN-14o DiskNotInSlot is 13");
            Eq((byte)StorageOperationFailure.UpgradeUnavailable, 14, "DN-14p UpgradeUnavailable is 14");
            Eq((byte)StorageOperationFailure.MaterialsNoLongerAvailable, 15, "DN-14q MaterialsNoLongerAvailable is 15");
            Eq((byte)StorageOperationFailure.DiskNotFound, 16, "DN-14r DiskNotFound is 16");
            Eq((byte)StorageOperationFailure.DiskRecoveryRefused, 17, "DN-14s DiskRecoveryRefused is 17");
            Eq((byte)StorageOperationFailure.NothingToDefragment, 18, "DN-14t NothingToDefragment is 18");
            Eq((byte)StorageOperationFailure.DiskClaimRefused, 19, "DN-14u DiskClaimRefused is 19");
            Eq((byte)StorageOperationFailure.DriveBaySlotUnavailable, 20, "DN-14v DriveBaySlotUnavailable is 20");
            Eq((byte)StorageOperationFailure.DriveBayNotOnNetwork, 21, "DN-14w DriveBayNotOnNetwork is 21");
            Eq(Enum.GetValues<StorageOperationFailure>().Length, 22,
                "DN-14l a new member was appended without pinning its value");
            Eq(denied.Count, Enum.GetValues<StorageOperationFailure>().Length - 1,
                "DN-14m every member but None is reportable");

            // DN-05: success is derived from the cause in one place, so the two cannot disagree.
            IsTrue(StorageOperationFailures.IsSuccess(StorageOperationFailure.None),
                "DN-05 None is the only success");
            foreach (var failure in denied)
            {
                IsFalse(StorageOperationFailures.IsSuccess(failure),
                    $"DN-05a {failure} is a denial");
            }

            // DN-01 / DN-02: a member added without a mapping arm collides with Unspecified.
            var keys = new List<string>();
            foreach (var failure in denied)
            {
                string key = StorageOperationFailures.GetLocalizationKey(failure);
                IsTrue(key.EndsWith("." + failure, StringComparison.Ordinal),
                    $"DN-02 {failure} maps to a key named after it  [{key}]");
                keys.Add(key);
            }
            Eq(new HashSet<string>(keys, StringComparer.Ordinal).Count, denied.Count,
                "DN-01 every reportable cause has its own key");

            // None never travels, but if a patched peer spells it the generic line is what shows.
            IsTrue(StorageOperationFailures.GetLocalizationKey(StorageOperationFailure.None)
                == StorageOperationFailures.GetLocalizationKey(StorageOperationFailure.Unspecified),
                "DN-01a None falls back to the generic line rather than a key of its own");

            // DN-03: sweep every byte a peer could send, not a sample of them. Each byte is
            // compared against what it must map to, so a defined value quietly rewritten to
            // Unspecified fails here rather than being waved through as "still in the enum".
            int wrongMappings = 0;
            for (int wireValue = 0; wireValue <= 255; wireValue++)
            {
                var mapped = StorageOperationFailures.GetFailureFromWireValue((byte)wireValue);

                var required = wireValue < 22
                    ? (StorageOperationFailure)wireValue
                    : StorageOperationFailure.Unspecified;

                if (mapped == required) continue;

                wrongMappings++;
                Check(false, $"DN-03 byte {wireValue} must map to {required}, mapped to {mapped}");
                if (wrongMappings >= 3) break;
            }
            Eq(wrongMappings, 0, "DN-03a all 256 bytes map exactly as the wire format requires");

            // DN-04: round-trip, which also catches a member added without a mapping arm.
            foreach (var failure in Enum.GetValues<StorageOperationFailure>())
            {
                Eq((int)StorageOperationFailures.GetFailureFromWireValue((byte)failure), (int)failure,
                    $"DN-04 {failure} round-trips");
            }

            // DN-09: the four craft guards the panel and the server BOTH encode. Full truth table,
            // because this is the decision neither of those two files can be compiled to check.
            Eq((int)StorageOperationFailures.GetCraftFailure(false, false, true, true),
                (int)StorageOperationFailure.RecipeNotFeasible, "DN-09 an infeasible plan outranks room");
            Eq((int)StorageOperationFailures.GetCraftFailure(false, true, true, true),
                (int)StorageOperationFailure.RecipeNotFeasible, "DN-09a even when crafting to inventory");
            Eq((int)StorageOperationFailures.GetCraftFailure(true, true, false, true),
                (int)StorageOperationFailure.NoRoomInInventory,
                "DN-09b craft-to-inventory ignores storage room");
            Eq((int)StorageOperationFailures.GetCraftFailure(true, true, true, false),
                (int)StorageOperationFailure.None, "DN-09c and needs only the inventory");
            Eq((int)StorageOperationFailures.GetCraftFailure(true, false, false, false),
                (int)StorageOperationFailure.NoRoomInStorageOrInventory, "DN-09d neither has room");
            Eq((int)StorageOperationFailures.GetCraftFailure(true, false, true, false),
                (int)StorageOperationFailure.None, "DN-09e the inventory alone is enough");
            Eq((int)StorageOperationFailures.GetCraftFailure(true, false, false, true),
                (int)StorageOperationFailure.None, "DN-09f as is storage alone");

            // Every combination is checked against the CODE it must reproduce, not merely against
            // "did it succeed" — returning NoRoomInInventory where NoRoomInStorageOrInventory
            // belongs is a wrong message on a right verdict, and would otherwise pass.
            int wrongCraftVerdicts = 0;
            for (int bits = 0; bits < 16; bits++)
            {
                bool feasible = (bits & 1) != 0;
                bool toInventory = (bits & 2) != 0;
                bool playerRoom = (bits & 4) != 0;
                bool storageRoom = (bits & 8) != 0;

                StorageOperationFailure required;
                if (!feasible) required = StorageOperationFailure.RecipeNotFeasible;
                else if (toInventory)
                    required = playerRoom ? StorageOperationFailure.None : StorageOperationFailure.NoRoomInInventory;
                else if (storageRoom || playerRoom) required = StorageOperationFailure.None;
                else required = StorageOperationFailure.NoRoomInStorageOrInventory;

                var verdict = StorageOperationFailures.GetCraftFailure(feasible, toInventory, playerRoom, storageRoom);
                if (verdict == required) continue;

                wrongCraftVerdicts++;
                Check(false, $"DN-09g feasible={feasible} toInventory={toInventory} playerRoom={playerRoom} "
                    + $"storageRoom={storageRoom} must give {required}, gave {verdict}");
            }
            Eq(wrongCraftVerdicts, 0, "DN-09h all sixteen guard combinations name the right cause");

            // DN-13: nothing matched and nothing fitted are different refusals with different fixes.
            Eq((int)StorageOperationFailures.GetQuickStackFailure(false, false),
                (int)StorageOperationFailure.NothingQuickStacked, "DN-13 nothing matched");
            Eq((int)StorageOperationFailures.GetQuickStackFailure(true, false),
                (int)StorageOperationFailure.NothingDeposited, "DN-13a matched, but a full network");
            Eq((int)StorageOperationFailures.GetQuickStackFailure(true, true),
                (int)StorageOperationFailure.None, "DN-13b something landed");

            // DN-10 / DN-11 / DN-12: deposit-all sends one packet per inventory slot, so a full
            // network denies forty times for one click.
            var throttle = new StorageOperationFailureThrottle();
            int reported = 0;
            for (int slot = 0; slot < 40; slot++)
            {
                if (throttle.ShouldReport(StorageOperationFailure.NothingDeposited, 1000)) reported++;
            }
            Eq(reported, 1, "DN-10 forty denials from one click are one line");

            IsTrue(throttle.ShouldReport(StorageOperationFailure.NothingWithdrawn, 1000),
                "DN-11 a different cause is never suppressed");

            var window = new StorageOperationFailureThrottle();
            IsTrue(window.ShouldReport(StorageOperationFailure.NothingDeposited, 100), "DN-12 the first is heard");
            IsFalse(window.ShouldReport(StorageOperationFailure.NothingDeposited, 159),
                "DN-12a a repeat 59 ticks later is the same refusal");
            IsTrue(window.ShouldReport(StorageOperationFailure.NothingDeposited, 160),
                "DN-12b a repeat a full second later is heard again");

            var wrapping = new StorageOperationFailureThrottle();
            IsTrue(wrapping.ShouldReport(StorageOperationFailure.NothingDeposited, uint.MaxValue - 10),
                "DN-12c the first before the tick counter wraps");
            IsFalse(wrapping.ShouldReport(StorageOperationFailure.NothingDeposited, 20),
                "DN-12d and the wrap does not turn 30 ticks into four billion");

            DenialReasonsAreTranslated();
            DenialReasonsAreNamedAtEverySite();
        }

        // The real failure mode of a reason code is a member nobody translated: the game then
        // prints the raw key into chat. Both catalogs are checked, not just the one being edited.
        private static void DenialReasonsAreTranslated()
        {
            string repoRoot = FindRepoRoot();
            IsTrue(repoRoot != null, "DN-06 repo root located from " + AppContext.BaseDirectory);
            if (repoRoot == null) return;

            var catalogs = new[] { "en-US_Mods.TerraStorage.hjson", "ru-RU_Mods.TerraStorage.hjson" };
            foreach (string catalogName in catalogs)
            {
                string path = Path.Combine(repoRoot, "Localization", catalogName);
                if (!File.Exists(path)) { Check(false, $"DN-06 missing catalog {catalogName}"); continue; }

                string block = ExtractCatalogGroup(File.ReadAllText(path), "OperationFailed");
                IsTrue(block.Length > 0, $"DN-06a {catalogName} carries an OperationFailed group");

                IsTrue(!string.IsNullOrWhiteSpace(GetCatalogValue(block, "Prefix")),
                    $"DN-06b {catalogName} carries the shared Prefix");

                int translated = 0;
                foreach (var failure in StorageOperationFailures.GetDeniedFailures())
                {
                    string value = GetCatalogValue(block, failure.ToString());
                    if (!string.IsNullOrWhiteSpace(value)) translated++;
                    else Check(false, $"DN-06c {catalogName} has no line for {failure}");
                }
                Eq(translated, StorageOperationFailures.GetDeniedFailures().Count,
                    $"DN-06d {catalogName} translates every reportable cause");
            }

            // DN-15: the four craft lines moved out of C# literals into the catalog. Paraphrasing
            // one is a silent wording regression that presence-checking alone would pass.
            string english = ExtractCatalogGroup(
                File.ReadAllText(Path.Combine(repoRoot, "Localization", "en-US_Mods.TerraStorage.hjson")),
                "OperationFailed");

            AssertCatalogValue(english, "RecipeNotFeasible",
                "this recipe cannot be crafted from what the network can hand over.",
                "DN-15 the infeasible-plan line is the one singleplayer already printed");
            AssertCatalogValue(english, "NoRoomInInventory",
                "no room in your inventory for the result.",
                "DN-15a the inventory-full line is unchanged");
            AssertCatalogValue(english, "NoRoomInStorageOrInventory",
                "no room in storage or your inventory for the result.",
                "DN-15b the nowhere-to-put-it line is unchanged");
            AssertCatalogValue(english, "CraftCostingNoLongerHolds",
                "the craft was cancelled — storage no longer holds what the plan was costed against.",
                "DN-15c the costed-against line keeps its wording and its em dash");
        }

        // The packet handler and the crafting panel cannot be compiled outside the game, so a
        // mistyped or invented member would ship unnoticed. This is the compiler that is missing.
        private static void DenialReasonsAreNamedAtEverySite()
        {
            string repoRoot = FindRepoRoot();
            if (repoRoot == null) return;

            var memberNames = new HashSet<string>(Enum.GetNames<StorageOperationFailure>(), StringComparer.Ordinal);
            var sources = new[]
            {
                "Systems/NetworkHandler.cs",
                "Content/UI/Elements/UICraftingPanel.cs",
                "Systems/StorageOperationReporter.cs"
            };

            int invented = 0;
            int vague = 0;
            int referenced = 0;
            foreach (string relativePath in sources)
            {
                string source = ReadModSource(repoRoot, relativePath);
                foreach (string member in FindFailureMemberReferences(source))
                {
                    referenced++;
                    if (!memberNames.Contains(member)) invented++;
                    if (member == nameof(StorageOperationFailure.Unspecified)) vague++;
                }
            }

            IsTrue(referenced > 0, "DN-08 the reason vocabulary is actually used by the mod sources");
            Eq(invented, 0, "DN-08a every named cause is a real enum member");
            Eq(vague, 0, "DN-08b no site settles for Unspecified when it could name its cause");

            // DN-07: the four English literals are gone from the panel, moved rather than copied.
            string panel = ReadModSource(repoRoot, "Content/UI/Elements/UICraftingPanel.cs");
            IsFalse(panel.Contains("Main.NewText(\"Requisition:", StringComparison.Ordinal),
                "DN-07 no hardcoded denial text survives in the crafting panel");

            string network = ReadModSource(repoRoot, "Systems/NetworkHandler.cs");
            IsTrue(!network.Contains("EndTrackingAndRespond(mod, whoAmI, !", StringComparison.Ordinal)
                && !network.Contains("EndTrackingAndRespond(mod, whoAmI, outcome", StringComparison.Ordinal)
                && !network.Contains("EndTrackingAndRespond(mod, whoAmI, results", StringComparison.Ordinal),
                "DN-08c no response site still reports a bare boolean");
        }

        private static IEnumerable<string> FindFailureMemberReferences(string source)
        {
            const string marker = "StorageOperationFailure.";

            int at = source.IndexOf(marker, StringComparison.Ordinal);
            while (at >= 0)
            {
                int start = at + marker.Length;
                int end = start;
                while (end < source.Length && (char.IsLetterOrDigit(source[end]) || source[end] == '_'))
                    end++;

                if (end > start) yield return source.Substring(start, end - start);
                at = source.IndexOf(marker, end, StringComparison.Ordinal);
            }
        }

        // A wording regression is the thing DN-15 exists to catch, so a failure has to show the two
        // strings rather than "expected 1, got 0".
        private static void AssertCatalogValue(string group, string key, string expected, string name)
        {
            string actual = GetCatalogValue(group, key);
            Check(actual == expected, $"{name}  [expected \"{expected}\", got \"{actual}\"]");
        }

        // Returns the body of a named hjson group, from its opening brace to its close.
        private static string ExtractCatalogGroup(string catalog, string groupName)
        {
            int start = catalog.IndexOf(groupName + ": {", StringComparison.Ordinal);
            if (start < 0) return string.Empty;

            int open = catalog.IndexOf('{', start);
            if (open < 0) return string.Empty;

            int depth = 0;
            for (int i = open; i < catalog.Length; i++)
            {
                if (catalog[i] == '{') depth++;
                else if (catalog[i] == '}' && --depth == 0)
                    return catalog.Substring(open, i - open);
            }
            return string.Empty;
        }

        private static string GetCatalogValue(string group, string key)
        {
            foreach (string line in group.Split('\n'))
            {
                string trimmed = line.Trim();
                if (!trimmed.StartsWith(key + ":", StringComparison.Ordinal)) continue;

                string value = trimmed.Substring(key.Length + 1).Trim();
                if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                    value = value.Substring(1, value.Length - 2);
                return value;
            }
            return null;
        }

        // ---- Refunding what the player owned, not what happened to be last ----
        // Doc 25's remaining defect: RefundLedger.Refund identified conjured units by POSITION,
        // withholding from the end of the ledger. A step's product goes into the first disk with
        // room, which is AHEAD of stock the player holds on a later disk - so the trailing handle is
        // the player's, and the abort dropped it and re-inserted the run's copy. Same defect class
        // as the TakeBack one, at the site that runs on every abort.
        private static void RefundKeepsThePlayersStackNotTheLastOne()
        {
            Section("Refund withholds the handle the run made, wherever it landed");
            const int CHARM = 7, IRON = 3, TARGET = 4, GOLD = 8, COPPER = 9;

            // Disk 0 holds own-a and has a free slot; disk 1 holds own-b; disk 2 holds the iron. The
            // conjured charm fills disk 0's free slot, so the ledger draws own-a, made, own-b - the
            // player's stack is the trailing one, and withholding from the end destroys it.
            var storage = new FakeStorage()
                .WithDiskSlots(2, 1, 1)
                .WithUniqueType(CHARM)
                .WithUniqueStackOn(0, CHARM, 1, "own-a")
                .WithUniqueStackOn(1, CHARM, 1, "own-b")
                .WithOn(2, IRON, 1);

            var chain = Steps(
                (new[] { (IRON, 1) }, CHARM, 1),
                (new[] { (CHARM, 3), (GOLD, 1) }, TARGET, 1));
            var never = new PlanExecutor<FakeItem>(storage).Run(chain, 1, new FakeStepProducer(chain, "made"));

            Eq(storage.StackOf(never), 0, "RF-01 the chain cannot be paid for");
            Eq(storage.CountItem(CHARM), 2, "RF-01a the player's two charms are back by count");

            string kept = string.Join(",", storage.MarksOf(CHARM));
            Check(kept == "own-a,own-b",
                $"RF-02 and they are the player's own two, not the run's copy  [got {kept}]");

            // The layout the old rule got right, so the fix cannot be "always withhold from the
            // front" - both orders have to work.
            var trailing = new FakeStorage()
                .WithUniqueType(CHARM)
                .WithUniqueStack(CHARM, 1, "own-a")
                .WithUniqueStack(CHARM, 1, "own-b")
                .With(IRON, 1);

            var alsoNever = new PlanExecutor<FakeItem>(trailing).Run(chain, 1, new FakeStepProducer(chain, "made"));

            Eq(trailing.StackOf(alsoNever), 0, "RF-03 the same chain on one disk cannot be paid for");
            string stillKept = string.Join(",", trailing.MarksOf(CHARM));
            Check(stillKept == "own-a,own-b",
                $"RF-04 and the conjured charm is still the one withheld  [got {stillKept}]");

            // Plain units have no state to tell apart, so the count is the whole of the guarantee -
            // the handle match must not refuse to withhold just because nothing distinguishes them.
            var plain = new FakeStorage().With(IRON, 4);
            var plainChain = Steps(
                (new[] { (IRON, 2) }, COPPER, 2),
                (new[] { (COPPER, 2), (GOLD, 1) }, TARGET, 1));
            new PlanExecutor<FakeItem>(plain).Run(plainChain, 1, new FakeStepProducer(plainChain));

            Eq(plain.CountItem(IRON), 4, "RF-05 plain materials come back whole");
            Eq(plain.CountItem(COPPER), 0, "RF-05a and the conjured plain units are not handed back");
        }

        // ---- Taking back what the run made, not what the player owned ----
        // Issue 25-C: recovering a conjured product by TYPE draws in storage order, and for a type
        // whose stacks each stand for themselves that is whichever sorts first - the player's own as
        // readily as the one this run conjured. The units balanced; the identity did not.
        private static void TakeBackRecoversTheRunsOwnStack()
        {
            Section("Recovering a conjured product takes the run's stack, not the player's");
            const int CHARM = 7, IRON = 3, GOLD = 8, TARGET = 4, PLANK = 9;

            var abandoned = new FakeStorage()
                .WithUniqueType(CHARM)
                .WithUniqueStack(CHARM, 1, "own")
                .With(IRON, 2);

            // The run makes a charm, then cannot pay for the step that would have consumed it.
            var chain = Steps(
                (new[] { (IRON, 2) }, CHARM, 1),
                (new[] { (GOLD, 1) }, TARGET, 1));
            new PlanExecutor<FakeItem>(abandoned).Run(chain, 1, new FakeStepProducer(chain, "made"));

            string survivor = string.Join(",", abandoned.MarksOf(CHARM));
            Check(survivor == "own",
                $"HB-01 the charm left standing is the player's, not the run's copy  [got {survivor}]");
            Eq(abandoned.CountItem(CHARM), 1, "HB-02 with the conjured one taken back");
            Eq(abandoned.CountItem(IRON), 2, "HB-02a and the iron it was made from refunded");

            // The same recovery reached through a full store: only part of the intermediate lands,
            // so what did land has to come back before the materials can be refunded.
            var full = new FakeStorage()
                .WithUniqueType(CHARM)
                .WithUniqueStack(CHARM, 1, "own")
                .With(IRON, 1);
            full.Capacity = 3;

            var overflowing = Steps(
                (new[] { (IRON, 1) }, CHARM, 3),
                (new[] { (GOLD, 1) }, TARGET, 1));
            new PlanExecutor<FakeItem>(full).Run(overflowing, 1, new FakeStepProducer(overflowing, "made"));

            string keptThroughOverflow = string.Join(",", full.MarksOf(CHARM));
            Check(keptThroughOverflow == "own",
                $"HB-03 a part-stored intermediate is recovered by handle too  [got {keptThroughOverflow}]");
            Eq(full.CountItem(IRON), 1, "HB-03a with its materials put back");

            // And through the material consumer, the second place the same recovery runs.
            var consumed = new FakeStorage()
                .WithUniqueType(CHARM)
                .WithUniqueStack(CHARM, 1, "own");
            consumed.Capacity = 3;

            var consumer = new MaterialConsumer<FakeItem>(consumed,
                (type, need) => new FakeItem { Type = CHARM, Stack = 3, Mark = "made" });
            IsFalse(consumer.TryConsume(new[] { (CHARM, 4) }), "HB-04 a shortfall that will not fit fails the consume");

            string keptThroughConsumer = string.Join(",", consumed.MarksOf(CHARM));
            Check(keptThroughConsumer == "own",
                $"HB-04a and the player's charm is still the one in storage  [got {keptThroughConsumer}]");

            // Plain units are interchangeable, so there is no handle to match and nothing changes.
            var plain = new FakeStorage().With(PLANK, 3).With(IRON, 2);
            var plainChain = Steps(
                (new[] { (IRON, 2) }, PLANK, 2),
                (new[] { (GOLD, 1) }, TARGET, 1));
            new PlanExecutor<FakeItem>(plain).Run(plainChain, 1, new FakeStepProducer(plainChain));

            Eq(plain.CountItem(PLANK), 3, "HB-05 a plain conjured stack is still taken back by type");
            Eq(plain.CountItem(IRON), 2, "HB-05a with its materials refunded");

            // A stack carrying the same state but holding more than this run stored also holds units
            // the player owned. Taking it whole to recover one unit would destroy the rest.
            var shared = new FakeStorage()
                .WithUniqueType(CHARM)
                .WithUniqueStack(CHARM, 3, "shared")
                .With(IRON, 2);

            var sharedChain = Steps(
                (new[] { (IRON, 2) }, CHARM, 1),
                (new[] { (GOLD, 1) }, TARGET, 1));
            new PlanExecutor<FakeItem>(shared).Run(sharedChain, 1, new FakeStepProducer(sharedChain, "shared"));

            Eq(shared.CountItem(CHARM), 3, "HB-06 a matching stack bigger than the run stored is left whole");
            Eq(shared.MarksOf(CHARM).Count, 1, "HB-06a with only the conjured stack taken back");
        }

        // ---- One sweep of the network, one handle per group of units that share state ----
        // Issue 25-A: a step needing N units called Extract N times, and every call walked every
        // disk. The sweep now drains up to `count` in one pass and hands back a handle per state
        // boundary, so nothing has to ask again - and a caller that can hold several items gets both
        // disks' units instead of stopping at the first boundary.
        private static void NetworkDrainsInOneSweep()
        {
            Section("A withdrawal drains the whole network in one sweep");
            const int unlimited = int.MaxValue;

            var standaloneOnly = new FakeDiskNetwork().WithDisk().WithDisk().WithDisk();
            standaloneOnly.WithStandalone(0, 1).WithStandalone(1, 1).WithStandalone(2, 1);
            var drawn = NetworkWithdrawal.Drain(standaloneOnly, 3, unlimited);
            Eq(drawn.Count, 3, "NW-01 three stacks that each stand for themselves come back as three handles");
            Eq(TotalUnits(drawn), 3, "NW-01a paying for all three units");

            var mixed = new FakeDiskNetwork().WithDisk().WithDisk();
            mixed.WithPooled(0, 4, "A").WithStandalone(1, 6);
            var mixedDraw = NetworkWithdrawal.Drain(mixed, 10, unlimited);
            // A disk is asked for pooled stock until it stops yielding, because one draw carries one
            // mod state: the draw off disk 0, the probe that finds it empty, and the probe that finds
            // disk 1 holding nothing pooled.
            Eq(mixed.PooledDraws, 3, "NW-02 the network is swept for pooled stock once per disk, plus the probe that ends it");
            // Three pooled probes, then one draw per stack that stands for itself.
            Eq(mixed.TotalDraws, 10, "NW-02a and asked ten times in total, one per stack, not one per unit");
            Eq(TotalUnits(mixedDraw), 10, "NW-02b for all ten units");

            // The falsifiable form of "not once per unit", immune to the constants above: a hundred
            // times the pooled stock costs the same number of pooled draws.
            var deepPool = new FakeDiskNetwork().WithDisk().WithDisk();
            deepPool.WithPooled(0, 400, "A").WithStandalone(1, 6);
            NetworkWithdrawal.Drain(deepPool, 406, unlimited);
            Eq(deepPool.PooledDraws, mixed.PooledDraws, "NW-02c a hundredfold deeper pool costs the same pooled draws");

            var samePool = new FakeDiskNetwork().WithDisk().WithDisk();
            samePool.WithPooled(0, 7, "A").WithPooled(1, 9, "A");
            Eq(NetworkWithdrawal.Drain(samePool, 12, unlimited).Count, 1,
                "NW-03 pooled stock sharing one state folds into a single handle");

            var samePoolCapped = new FakeDiskNetwork().WithDisk().WithDisk();
            samePoolCapped.WithPooled(0, 7, "A").WithPooled(1, 9, "A");
            var capped = NetworkWithdrawal.Drain(samePoolCapped, 12, 1);
            Eq(capped.Count, 1, "NW-04 and still one handle for a caller that can only hold one");
            Eq(TotalUnits(capped), 12, "NW-04a carrying all twelve units");

            // The reported cross-disk case: one disk's state must not be stamped over the other's,
            // but a caller holding two items loses nothing by taking both.
            var twoStates = new FakeDiskNetwork().WithDisk().WithDisk();
            twoStates.WithPooled(0, 7, "A").WithPooled(1, 9, "B");
            var split = NetworkWithdrawal.Drain(twoStates, 12, unlimited);
            Eq(split.Count, 2, "NW-05 a disk whose state differs opens a second handle");
            Eq(TotalUnits(split), 12, "NW-05a so both disks pay into the same withdrawal");

            var twoStatesCapped = new FakeDiskNetwork().WithDisk().WithDisk();
            twoStatesCapped.WithPooled(0, 7, "A").WithPooled(1, 9, "B");
            var stopped = NetworkWithdrawal.Drain(twoStatesCapped, 12, 1);
            Eq(TotalUnits(stopped), 7, "NW-06 one item handle still stops at the state boundary");
            Eq(twoStatesCapped.UnitsOn(1), 9, "NW-06a with the second disk's draw put back");
            Eq(twoStatesCapped.SlotsOn(1), 1, "NW-06b into the slot it came from");

            var pooledThenStandalone = new FakeDiskNetwork().WithDisk().WithDisk();
            pooledThenStandalone.WithPooled(0, 4, "A").WithStandalone(1, 6);
            var followed = NetworkWithdrawal.Drain(pooledThenStandalone, 10, unlimited);
            Eq(TotalUnits(followed), 10, "NW-07 stacks that stand alone are drawn after pooled stock");
            Eq(followed.Count, 7, "NW-07a each as its own handle");

            var pooledThenStandaloneCapped = new FakeDiskNetwork().WithDisk().WithDisk();
            pooledThenStandaloneCapped.WithPooled(0, 4, "A").WithStandalone(1, 6);
            var suppressed = NetworkWithdrawal.Drain(pooledThenStandaloneCapped, 10, 1);
            Eq(TotalUnits(suppressed), 4, "NW-08 a caller already holding an item refuses the fallback");
            Eq(pooledThenStandaloneCapped.UnitsOn(1), 6, "NW-08a leaving every standalone stack alone");

            // Fold into the most recent handle only, so a handle is a run of CONSECUTIVE draws
            // sharing state. That is what lets handleLimit mean "how many separate items the caller
            // can hold", and it keeps _taken in draw order. (Until RF-* this also propped up
            // RefundLedger.Refund withholding from the end; the refund now matches handles by
            // state, so it no longer depends on this - the rule stands on its own terms.)
            var alternating = new FakeDiskNetwork().WithDisk().WithDisk().WithDisk();
            alternating.WithPooled(0, 10, "A").WithPooled(1, 10, "B").WithPooled(2, 10, "A");
            Eq(NetworkWithdrawal.Drain(alternating, 30, unlimited).Count, 3,
                "NW-09 a state that comes back later opens a new handle rather than rejoining the first");

            // Against a network that still holds stock, so these are not true by exhaustion. Note
            // Drain's own two guards are redundant with downstream ones - PlanWithdrawal refuses
            // count <= 0, and a zero handle budget puts the draw back - so removing either leaves
            // these green. NW-10b1 is the load-bearing one: it goes red on a broken PutBack.
            var untouched = new FakeDiskNetwork().WithDisk();
            untouched.WithPooled(0, 8, "A");
            Eq(NetworkWithdrawal.Drain(untouched, 0, unlimited).Count, 0, "NW-10a nothing asked for, nothing drawn");
            Eq(untouched.TotalUnits, 8, "NW-10a1 and the network is untouched");
            Eq(NetworkWithdrawal.Drain(untouched, 5, 0).Count, 0, "NW-10b no handle to hold it, nothing drawn");
            Eq(untouched.TotalUnits, 8, "NW-10b1 and the network is still untouched");
            Eq(NetworkWithdrawal.Drain(new FakeDiskNetwork(), 5, unlimited).Count, 0, "NW-10c an empty network draws nothing");

            var short1 = new FakeDiskNetwork().WithDisk();
            short1.WithPooled(0, 12, "A");
            Eq(TotalUnits(NetworkWithdrawal.Drain(short1, 50, unlimited)), 12, "NW-10d a short network never over-draws");

            // Sweep the handle budget contiguously rather than sampling it: issue 20 shipped a
            // passing test over a defect because the only divergent value was never tried.
            bool everyBudgetHolds = true;
            for (int budget = 0; budget <= 13; budget++)
            {
                var swept = new FakeDiskNetwork().WithDisk();
                swept.WithStandalone(0, 12);
                var handles = NetworkWithdrawal.Drain(swept, 12, budget);
                int expected = budget < 12 ? budget : 12;
                if (handles.Count != expected || TotalUnits(handles) != expected)
                    everyBudgetHolds = false;
            }
            IsTrue(everyBudgetHolds, "NW-11 every handle budget from 0 to 13 draws exactly what it can hold");

            var legacyComparison = CompareSingleHandleDrainsWithLegacy();
            IsTrue(legacyComparison.EveryAgreementLayoutAgrees,
                "NW-12 one-handle drains still agree with the pre-change rule on every layout that shares it");
            IsTrue(legacyComparison.EveryDivergenceLayoutDivergesAsDeclared,
                "SB-15 and diverge exactly as declared on the layouts whose rule deliberately changed");
            IsTrue(legacyComparison.NoDrainLosesStateOrOverdraws,
                "SB-16 and across the whole matrix no item comes back stateless and no drain overdraws");

            // A put-back leaves the disk for the next draw to find. This is the one shape where the
            // one-sweep drain and the old per-call loop could have diverged.
            var reDrawn = new FakeDiskNetwork().WithDisk().WithDisk();
            reDrawn.WithPooled(0, 5, "A").WithPooled(1, 6, "B");
            NetworkWithdrawal.Drain(reDrawn, 11, 1);
            Eq(reDrawn.TotalUnits, 6, "NW-13 what a put-back restored is still there for the next draw");
        }

        private static int TotalUnits(List<WithdrawalHandle> handles)
        {
            int total = 0;
            foreach (WithdrawalHandle handle in handles)
                total += handle.Units;
            return total;
        }

        // What one layout is expected to do when the rewritten sweep is held against the rule it
        // replaced. Every layout declares one, so a layout cannot be added to the matrix without
        // someone deciding which it is - the omission issue 20 is about.
        private sealed class LegacyExpectation
        {
            public Func<FakeDiskNetwork> Build;

            // The legacy rule got this layout right, so the rewritten sweep must still agree with it
            // on handles, units, per-disk units and per-disk slots.
            public bool AgreesWithLegacy;

            // The legacy rule folded across a state boundary WITHIN one disk, which is the defect.
            // Agreement is impossible here by construction, so the divergence itself is pinned: the
            // rewritten sweep draws exactly this much, and the legacy rule draws more.
            public int DivergentUnits;
            public int DivergentHandles;
        }

        private sealed class LegacyComparison
        {
            public bool EveryAgreementLayoutAgrees = true;
            public bool EveryDivergenceLayoutDivergesAsDeclared = true;

            // Swept over both arms: whatever a layout is declared to do, no item may come back
            // without the state of the stack it was drawn from, and no drain may exceed what was
            // asked for. Handing every unit back with no state is the defect this change closes.
            public bool NoDrainLosesStateOrOverdraws = true;
        }

        // Holds the rewritten sweep against the rule it replaced across a matrix of layouts, rather
        // than at the handful of points a reader would think to pick. The layouts whose rule
        // deliberately changed stay IN the matrix with their new answer written down, because a
        // divergent case quietly left out of the list is how issue 20 shipped over a passing test.
        private static LegacyComparison CompareSingleHandleDrainsWithLegacy()
        {
            var layouts = new List<LegacyExpectation>
            {
                Agrees(net => net.WithPooled(0, 7, "A").WithPooled(1, 9, "A"), 2),
                Agrees(net => net.WithPooled(0, 7, "A").WithPooled(1, 9, "B"), 2),
                Agrees(net => net.WithPooled(0, 7, "B").WithPooled(1, 9, "A"), 2),
                Agrees(net => net.WithStandalone(0, 3).WithStandalone(1, 4), 2),
                Agrees(net => net.WithPooled(0, 4, "A").WithStandalone(1, 6), 2),
                Agrees(net => net.WithStandalone(0, 6).WithPooled(1, 4, "A"), 2),
                Agrees(net => net.WithPooled(1, 9, "A"), 2),
                Agrees(net => net.WithPooled(0, 10, "A").WithPooled(2, 10, "B"), 3),
                Agrees(net => net.WithPooled(0, 3, "A").WithPooled(1, 3, "A").WithPooled(2, 3, "B"), 3),
                Agrees(net => net.WithStandalone(0, 2, 3).WithPooled(1, 5, "A"), 2),
                // Pooled and standalone stock on the SAME disk: the one shape where a disk's own
                // "unique only when nothing plain matched" rule meets the network-wide pooled pass.
                Agrees(net => net.WithPooled(0, 4, "A").WithStandalone(0, 3), 2),
                Agrees(net => net.WithStandalone(0, 3).WithPooled(0, 4, "A"), 2),
                // Two pooled stacks sharing a state on one disk: the sweep re-asks the disk, so it
                // still folds them, and the legacy rule still agrees.
                Agrees(net => net.WithPooled(0, 4, "A").WithPooled(0, 5, "A"), 2),
                // Two pooled STATES on one disk, all of the stock on that disk. Both rules hand back
                // the first run and stop - the legacy rule because it never asks a disk twice, the
                // rewritten one because a one-item caller puts the second run back. They agree on
                // the units AND, now, on the state, which is the half the legacy rule got wrong.
                Agrees(net => net.WithPooled(0, 7, "A").WithPooled(0, 5, "B"), 2),
                Agrees(net => net.WithPooled(0, 1, "A").WithPooled(0, 9, "B"), 2),
                Agrees(net => net.WithPooled(0, 4, "A").WithPooled(0, 4, "B").WithPooled(0, 4, "A"), 2),
                Agrees(net => net, 2),

                // An in-disk boundary with the state the sweep opened with waiting on a LATER disk.
                // This is the one shape where the two rules part: the legacy rule, blind to the
                // boundary it left behind on disk 0, walks on and folds the later disk's units into
                // the handle it is holding. The rewritten sweep stops at the boundary instead,
                // because a caller that can hold one item may not skip past a state to reach more
                // of an earlier one (NW-06 refuses the same thing across disks).
                Diverges(net => net.WithPooled(0, 7, "A").WithPooled(0, 5, "B").WithPooled(1, 9, "A"), 2,
                    units: 7, handles: 1),
                Diverges(net => net.WithPooled(0, 4, "A").WithPooled(0, 4, "B").WithPooled(2, 6, "A"), 3,
                    units: 4, handles: 1)
            };

            var comparison = new LegacyComparison();

            foreach (LegacyExpectation layout in layouts)
            {
                bool divergedSomewhere = false;

                // Contiguous rather than sampled, for the reason NW-11 is: a boundary a sweep never
                // tries is a boundary the sweep cannot find.
                for (int count = 0; count <= 20; count++)
                {
                    var rewrittenNetwork = layout.Build();
                    var legacyNetwork = layout.Build();
                    var rewritten = NetworkWithdrawal.Drain(rewrittenNetwork, count, 1);
                    var legacy = LegacySingleHandleDrain.Drain(legacyNetwork, count);

                    if (!EveryHandleKeepsItsState(rewritten, rewrittenNetwork) || TotalUnits(rewritten) > count)
                        comparison.NoDrainLosesStateOrOverdraws = false;

                    if (layout.AgreesWithLegacy)
                    {
                        if (!DrainsAgree(rewritten, legacy, rewrittenNetwork, legacyNetwork))
                            comparison.EveryAgreementLayoutAgrees = false;
                        continue;
                    }

                    if (!DivergesAsDeclared(layout, count, rewritten))
                        comparison.EveryDivergenceLayoutDivergesAsDeclared = false;

                    if (TotalUnits(legacy) > TotalUnits(rewritten))
                        divergedSomewhere = true;
                }

                // A layout parked in the divergent list that quietly agrees everywhere guards
                // nothing, and would let the real divergent shape drop out of the matrix unnoticed.
                if (!layout.AgreesWithLegacy && !divergedSomewhere)
                    comparison.EveryDivergenceLayoutDivergesAsDeclared = false;
            }

            return comparison;
        }

        private static bool EveryHandleKeepsItsState(List<WithdrawalHandle> handles, FakeDiskNetwork network)
        {
            foreach (WithdrawalHandle handle in handles)
            {
                if (network.StateOfHandle(handle) == null)
                    return false;
            }

            return true;
        }

        private static bool DrainsAgree(List<WithdrawalHandle> rewritten, List<WithdrawalHandle> legacy,
            FakeDiskNetwork rewrittenNetwork, FakeDiskNetwork legacyNetwork)
        {
            if (rewritten.Count != legacy.Count || TotalUnits(rewritten) != TotalUnits(legacy))
                return false;

            for (int handle = 0; handle < rewritten.Count; handle++)
            {
                if (rewritten[handle].Units != legacy[handle].Units
                    || rewritten[handle].Draws.Count != legacy[handle].Draws.Count)
                    return false;
            }

            // Equal totals drawn from the wrong disks, or a put-back left unrestored, would pass
            // every check above. Conservation lives in what the network holds after - slots as well
            // as units, so a put-back that restores the right count into a different stack
            // arrangement is caught too.
            for (int disk = 0; disk < rewrittenNetwork.DiskCount; disk++)
            {
                if (rewrittenNetwork.UnitsOn(disk) != legacyNetwork.UnitsOn(disk))
                    return false;

                if (rewrittenNetwork.SlotsOn(disk) != legacyNetwork.SlotsOn(disk))
                    return false;
            }

            return true;
        }

        // A one-item caller stops at the first state boundary, so it draws the opening run and no
        // more, however much the request asked for.
        private static bool DivergesAsDeclared(LegacyExpectation layout, int count,
            List<WithdrawalHandle> rewritten)
        {
            int expectedUnits = Math.Min(count, layout.DivergentUnits);
            int expectedHandles = expectedUnits == 0 ? 0 : layout.DivergentHandles;

            return TotalUnits(rewritten) == expectedUnits && rewritten.Count == expectedHandles;
        }

        private static LegacyExpectation Agrees(Func<FakeDiskNetwork, FakeDiskNetwork> stock, int diskCount)
            => new LegacyExpectation { Build = () => Layout(stock, diskCount), AgreesWithLegacy = true };

        private static LegacyExpectation Diverges(Func<FakeDiskNetwork, FakeDiskNetwork> stock, int diskCount,
            int units, int handles)
            => new LegacyExpectation
            {
                Build = () => Layout(stock, diskCount),
                AgreesWithLegacy = false,
                DivergentUnits = units,
                DivergentHandles = handles
            };

        private static FakeDiskNetwork Layout(Func<FakeDiskNetwork, FakeDiskNetwork> stock, int diskCount)
        {
            var network = new FakeDiskNetwork();
            for (int disk = 0; disk < diskCount; disk++)
                network.WithDisk();
            return stock(network);
        }

        // ---- Scenario: a count a packet supplied must not size an allocation ----
        // List<T>(capacity) commits the whole backing array before the first element is read, so a
        // ~20-byte packet claiming int.MaxValue elements allocates gigabytes. The read loop cannot
        // catch it: Terraria reuses one buffer, so reading past the packet returns stale bytes
        // instead of throwing.
        private static void WireCountBoundsTests()
        {
            Section("WireCount - a count off the wire may not size an allocation");

            const int GuidBytes = WireCount.GuidBytes;
            // 131070 / 16, the most GUIDs the largest possible packet could carry.
            const int LargestHonestGuidCount = 8191;

            IsTrue(WireCount.FitsInOnePacket(0, GuidBytes),
                "WB-01 an empty list is an honest count");
            IsTrue(WireCount.FitsInOnePacket(LargestHonestGuidCount, GuidBytes),
                "WB-02 a list filling the whole buffer is still honest");
            IsFalse(WireCount.FitsInOnePacket(LargestHonestGuidCount + 1, GuidBytes),
                "WB-03 one element past the buffer cannot have been sent");
            IsFalse(WireCount.FitsInOnePacket(int.MaxValue, GuidBytes),
                "WB-04 the attack value is refused before it sizes anything");
            IsFalse(WireCount.FitsInOnePacket(-1, GuidBytes),
                "WB-05 a negative count is refused, not handed to List<T>");
            IsFalse(WireCount.FitsInOnePacket(int.MinValue, GuidBytes),
                "WB-06 the extreme negative is refused by the same sign check");
            IsFalse(WireCount.FitsInOnePacket(1, 0),
                "WB-07 an element size of zero never divides");

            // A disk's own tier bounds its stacks far more tightly than the packet does.
            const int Tier6Capacity = 2048, Tier1Capacity = 64;

            IsTrue(WireCount.FitsDiskCapacity(Tier6Capacity, Tier6Capacity),
                "WB-08 a full Terra disk is an honest count");
            IsFalse(WireCount.FitsDiskCapacity(Tier6Capacity + 1, Tier6Capacity),
                "WB-09 one stack past a full disk cannot be real");
            IsFalse(WireCount.FitsDiskCapacity(int.MaxValue, Tier1Capacity),
                "WB-10 the attack value is refused against the smallest disk");
            IsFalse(WireCount.FitsDiskCapacity(-1, Tier6Capacity),
                "WB-11 a negative stack count is refused");
            IsTrue(WireCount.FitsDiskCapacity(0, 0),
                "WB-12 an empty disk of no capacity holds nothing, honestly");
        }

        // ---- Scenario: naming a disk GUID proves nothing ----
        // Every client receives every disk's GUID, so the server has to establish separately that
        // the sender has any claim on the disk it named.
        private static void DiskClaimTests()
        {
            Section("DiskClaim - who may name a disk GUID over the wire");

            var Unassigned = Guid.Empty;
            var SomeDisk = Guid.NewGuid();
            const bool InUse = true, NotInUse = false;
            const bool Held = true, NotHeld = false;

            IsTrue(DiskClaim.SenderMayClaim(Unassigned, InUse, NotHeld),
                "DC-01 a fresh uninitialised disk is always allowed");
            IsTrue(DiskClaim.SenderMayClaim(SomeDisk, NotInUse, NotHeld),
                "DC-02 a GUID no physical disk carries is allowed (unarchive mints one client-side)");
            IsTrue(DiskClaim.SenderMayClaim(SomeDisk, InUse, Held),
                "DC-03 the sender's own disk is allowed");
            IsFalse(DiskClaim.SenderMayClaim(SomeDisk, InUse, NotHeld),
                "DC-04 someone else's live disk is refused");

            // The scans the caller passes in answer meaninglessly for an empty GUID: "is it in use"
            // says true so that recovery refuses it, and "does the sender hold one" finds any other
            // blank disk in their inventory. The empty arm has to win over both.
            IsTrue(DiskClaim.SenderMayClaim(Unassigned, InUse, Held),
                "DC-05 empty wins over an in-use scan that answers true for empty");
            IsTrue(DiskClaim.SenderMayClaim(Unassigned, NotInUse, NotHeld),
                "DC-05a and over both scans answering false");

            IsTrue(DiskClaim.MayRestoreArchivedItems(worldAlreadyHasDisk: false),
                "DC-06 archived items restore onto a GUID the world does not know");
            IsFalse(DiskClaim.MayRestoreArchivedItems(worldAlreadyHasDisk: true),
                "DC-07 restoring never overwrites a disk that already exists");
        }

        // ---- Within one disk, a state boundary ends the draw ----
        // Issue 25's last "Not fixed" bullet. DiskData.ExtractItem set the returned tag only when
        // every stack drawn from happened to carry the same state, so a bulk withdrawal spanning two
        // plain stacks with different globalData handed all of it back with NONE - issue 05's harm
        // inverted, one level down from where it was fixed, and reachable since 24 stopped treating
        // globalData as identity. A plan now ends at the boundary instead, and the caller's handle
        // budget decides whether that opens another item or ends the sweep - the same rule NW-*
        // already applied across disks, now applied within one.
        private static void AStateBoundaryEndsTheDrawWithinOneDisk()
        {
            Section("Within one disk, a withdrawal stops where the mod state changes");
            const int unlimited = int.MaxValue;
            const int PLANK = 9;

            var twoStates = Grouped((7, 0), (5, 1));
            var stopped = StackSelection.PlanWithdrawal(twoStates, 12, true, out _);
            Eq(stopped.Count, 1, "SB-01 a plan ends at the first stack it cannot merge with");
            Eq(stopped.Sum(d => d.Count), 7, "SB-01a taking only the run it opened");

            Eq(StackSelection.PlanWithdrawal(twoStates, 5, true, out _).Sum(d => d.Count), 5,
                "SB-02 a count inside the first run never reaches the boundary");

            Eq(StackSelection.PlanWithdrawal(Grouped((7, 0), (5, 0)), 12, true, out _).Sum(d => d.Count), 12,
                "SB-03 stacks that do merge still pool across slots");

            // A stack that stands for itself is skipped rather than drawn from, so it does not
            // separate the pooled stacks either side of it.
            var uniqueBetween = new List<StackSlot>
            {
                new StackSlot { Index = 0, Stack = 7, StateGroup = 0 },
                new StackSlot { Index = 1, Stack = 1, IsUnique = true, StateGroup = 1 },
                new StackSlot { Index = 2, Stack = 5, StateGroup = 0 }
            };
            var past = StackSelection.PlanWithdrawal(uniqueBetween, 12, true, out _);
            Eq(past.Sum(d => d.Count), 12, "SB-04 a stack standing for itself is skipped, not a boundary");
            IsFalse(past.Any(d => d.Index == 1), "SB-04a and is still left alone");

            // Nothing is drawn from an empty slot, so reading its group would end the pass over a
            // stack the withdrawal never touched.
            Eq(StackSelection.PlanWithdrawal(Grouped((7, 0), (0, 1), (5, 0)), 12, true, out _).Sum(d => d.Count), 12,
                "SB-05 an empty slot is not a boundary");

            var onlyUnique = new List<StackSlot>
            {
                new StackSlot { Index = 0, Stack = 1, IsUnique = true, StateGroup = 0 },
                new StackSlot { Index = 1, Stack = 1, IsUnique = true, StateGroup = 1 }
            };
            var fellBack = StackSelection.PlanWithdrawal(onlyUnique, 5, true, out bool reportedUnique);
            IsTrue(reportedUnique, "SB-06 the unique fallback is untouched by grouping");
            Eq(fellBack.Count, 1, "SB-06a still exactly one stack, taken alone");

            // The harm stated positively: the state has to survive the withdrawal.
            var mixedDisk = OneDisk(net => net.WithPooled(0, 7, "A").WithPooled(0, 5, "B"));
            var split = NetworkWithdrawal.Drain(mixedDisk, 12, unlimited);
            string splitStates = mixedDisk.StatesOf(split);
            Check(splitStates == "A,B",
                $"SB-07 two states on one disk come back as two items, each with its own  [expected A,B, got {splitStates}]");
            Eq(TotalUnits(split), 12, "SB-07a for all twelve units");

            var mixedCapped = OneDisk(net => net.WithPooled(0, 7, "A").WithPooled(0, 5, "B"));
            var oneItem = NetworkWithdrawal.Drain(mixedCapped, 12, 1);
            Eq(TotalUnits(oneItem), 7, "SB-08 one item handle stops at the boundary inside the disk");
            string cappedState = mixedCapped.StatesOf(oneItem);
            Check(cappedState == "A", $"SB-08a carrying the state it opened with  [expected A, got {cappedState}]");
            Eq(mixedCapped.UnitsOn(0), 5, "SB-08b with the rest put back");
            Eq(mixedCapped.SlotsOn(0), 1, "SB-08c into the slot it came from");

            // NW-09's rule, now reachable within one disk: a state that comes back opens a new item
            // rather than rejoining the first.
            var alternatingWithin = OneDisk(net => net.WithPooled(0, 4, "A").WithPooled(0, 4, "B").WithPooled(0, 4, "A"));
            var runs = NetworkWithdrawal.Drain(alternatingWithin, 12, unlimited);
            string runStates = alternatingWithin.StatesOf(runs);
            Check(runStates == "A,B,A",
                $"SB-09 a state that comes back opens a new item within one disk too  [expected A,B,A, got {runStates}]");
            Eq(TotalUnits(runs), 12, "SB-09a for all twelve units");

            // A later disk holding the state the sweep opened with is NOT reached past a boundary:
            // NW-06 refuses that across disks, and this refuses it within one.
            var boundaryThenMatch = TwoDisks(net =>
                net.WithPooled(0, 7, "A").WithPooled(0, 5, "B").WithPooled(1, 9, "A"));
            Eq(TotalUnits(NetworkWithdrawal.Drain(boundaryThenMatch, 20, 1)), 7,
                "SB-10 one item handle never skips a boundary to reach a later matching disk");

            var ledger = TwoDisks(net => net.WithPooled(0, 7, "A").WithPooled(0, 5, "B").WithPooled(1, 9, "A"));
            var paid = NetworkWithdrawal.Drain(ledger, 20, unlimited);
            Eq(TotalUnits(paid), 20, "SB-11 a ledger that can hold three items is paid in full");
            string ledgerStates = ledger.StatesOf(paid);
            Check(ledgerStates == "A,B,A", $"SB-11a from all three runs  [expected A,B,A, got {ledgerStates}]");

            // The worst case the rule admits, pinned rather than left emergent: a withdrawal yields
            // the FIRST run's size, not the largest. Reachable because Defragment declines to merge
            // stacks whose state differs, so a short leading run can sit in front of a long one.
            var smallRunFirst = OneDisk(net => net.WithPooled(0, 1, "A").WithPooled(0, 999, "B"));
            Eq(TotalUnits(NetworkWithdrawal.Drain(smallRunFirst, 1000, 1)), 1,
                "SB-12 a one-unit leading run answers a thousand-unit request with one");
            Eq(smallRunFirst.UnitsOn(0), 999, "SB-12a leaving the thousand behind it untouched");
            Eq(smallRunFirst.SlotsOn(0), 1, "SB-12b in the slot it was already in");

            // Through FakeStorage, which BD-*, ID-*, FX-*, HB-* and RF-* all run on: the player's
            // plain stock and a run's own product are two handles, not one blurred item.
            var storage = new FakeStorage().With(PLANK, 4);
            storage.Insert(new FakeItem { Type = PLANK, Stack = 4, Mark = "made" });
            var drawnStacks = storage.ExtractStacks(PLANK, 8);
            string drawnMarks = string.Join(",", drawnStacks.ConvertAll(item => item.Mark ?? "none"));
            Check(drawnMarks == "none,made",
                $"SB-13 the player's stock and the run's product stay apart  [expected none,made, got {drawnMarks}]");
            Eq(drawnStacks.Sum(item => item.Stack), 8, "SB-13a with all eight units drawn");

            SourceScanTheExtractionPath();
        }

        // DiskData.cs cannot be linked into the runner, so the rule it carries out is asserted
        // through the fakes and its SHAPE is asserted here - the compiler this change does not
        // otherwise get, the same device DN-14 and SG-01..SG-07 use.
        private static void SourceScanTheExtractionPath()
        {
            string repoRoot = FindRepoRoot();
            IsTrue(repoRoot != null, "SB-14 repo root located from " + AppContext.BaseDirectory);
            if (repoRoot == null) return;

            string diskData = ReadModSource(repoRoot, "Common/DiskData.cs");
            string extractBody = ExtractMethodBody(diskData, "public Item ExtractItem(int itemType, int count, int prefixId, bool allowUniqueFallback,");

            IsTrue(extractBody.Contains("StackSelection.PlanWithdrawal"),
                "SB-14a the ExtractItem body was located, so the checks below are real");
            IsTrue(!diskData.Contains("AllDrawsShareModState"),
                "SB-14b the after-the-fact share check is gone, not merely bypassed");
            IsTrue(extractBody.Contains("runOpener"),
                "SB-14c the returned item's state comes from the stack that opened the run");
            IsTrue(!extractBody.Contains("result.Prefix(prefixId)"),
                "SB-14d and its prefix too, not the prefix the request asked by");

            string matchingBody = ExtractMethodBody(diskData, "private List<StackSlot> MatchingSlots");
            IsTrue(matchingBody.Contains("StateGroup"),
                "SB-14e MatchingSlots tells the planner which stacks merge");
            IsTrue(matchingBody.Contains("CanMergeStacks"),
                "SB-14f on the same rule defragmenting asks, so prefix counts as well as mod state");
        }

        private static List<StackSlot> Grouped(params (int stack, int stateGroup)[] slots)
        {
            var built = new List<StackSlot>();
            for (int index = 0; index < slots.Length; index++)
                built.Add(new StackSlot { Index = index, Stack = slots[index].stack, StateGroup = slots[index].stateGroup });
            return built;
        }

        private static FakeDiskNetwork OneDisk(Func<FakeDiskNetwork, FakeDiskNetwork> stock) => Layout(stock, 1);

        private static FakeDiskNetwork TwoDisks(Func<FakeDiskNetwork, FakeDiskNetwork> stock) => Layout(stock, 2);

        // ---- The defragment sweep itself, not a copy of it ----
        // Until DefragmentCore was extracted, DF-* pinned PlanDonorMove's rules and MX-* matched the
        // sweep's source text, but nothing executed the sweep: it lived in StorageWorldSystem, which
        // binds Terraria.Item. Issue 04 - defragment destroying and duplicating per-instance mod
        // data - happened in this function, and issue 24 one level under it.
        private static void DefragmentSweepMovesStacksWithoutLosingAny()
        {
            Section("The defragment sweep, executed rather than transcribed");

            var rules = new FakeDefragmentRules();

            var emptyTarget = FakeDisks.Disk(10);
            var singleDonor = FakeDisks.Disk(10, FakeDisks.Stack(7, 30));
            DefragmentCore.Sweep(new[] { emptyTarget, singleDonor }, rules);
            Eq(FakeDisks.Layout(emptyTarget), "t7x30", "DG-01 a plain stack moves into free space");
            Eq(singleDonor.Items.Count, 0, "DG-01a leaving the donor empty");

            // Two donors of one identity have to end as ONE stack. The sweep records each slot it
            // appends into the merge index before appending it; a sweep that skipped that would give
            // the second donor no candidates and open a second slot.
            var poolTarget = FakeDisks.Disk(10);
            var firstDonor = FakeDisks.Disk(10, FakeDisks.Stack(7, 30));
            var secondDonor = FakeDisks.Disk(10, FakeDisks.Stack(7, 40));
            DefragmentCore.Sweep(new[] { poolTarget, firstDonor, secondDonor }, rules);
            Eq(poolTarget.Items.Count, 1, "DG-02 a second donor of one identity tops up the slot the first opened");
            Eq(poolTarget.Items[0].Count, 70, "DG-02a rather than opening a second slot");

            var partialTarget = FakeDisks.Disk(10, FakeDisks.Stack(7, 90));
            var spillDonor = FakeDisks.Disk(10, FakeDisks.Stack(7, 30));
            DefragmentCore.Sweep(new[] { partialTarget, spillDonor }, rules);
            Eq(FakeDisks.Layout(partialTarget), "t7x99,t7x21", "DG-03 a partial stack is filled to maxStack before a fresh slot opens");
            Eq(spillDonor.Items.Count, 0, "DG-03a and the donor is emptied");

            // Issue 04: a stack that stands for itself may never be folded into a plain one, however
            // willing the target looks. The sweep does not even offer the rule the chance to say yes.
            rules.AskedPairs.Clear();
            var willingTarget = FakeDisks.Disk(10, FakeDisks.Stack(7, 10));
            var uniqueDonor = FakeDisks.Disk(10, FakeDisks.Unique(7, 5));
            DefragmentCore.Sweep(new[] { willingTarget, uniqueDonor }, rules);
            Eq(willingTarget.Items[0].Count, 10, "DG-04 a unique donor never merges into a stack of its own type");
            Eq(willingTarget.Items.Count, 2, "DG-04a it takes a slot of its own");
            Eq(rules.AskedPairs.Count, 0, "DG-04b and the merge rule is never asked about it at all");

            // The target fills on the first unique stack and the sweep stops, so the second is left
            // untouched. PlanDonorMove's own "no free slot" branch is unreachable from here - the
            // sweep re-checks IsFull before every donor stack - so DF-05 is what pins that; this
            // pins that a unique stack the sweep never reached is left whole rather than part-moved.
            var crampedRules = new FakeDefragmentRules();
            var crampedTarget = FakeDisks.Disk(2, FakeDisks.Stack(7, 10));
            var twoUniques = FakeDisks.Disk(10, FakeDisks.Unique(7, 5), FakeDisks.Unique(8, 5));
            DefragmentCore.Sweep(new[] { crampedTarget, twoUniques }, crampedRules);
            Eq(twoUniques.Items.Count, 1, "DG-05 a unique stack the filled target never reached stays where it is");
            Eq(twoUniques.Items[0].Count, 5, "DG-05a whole, never partially moved");
            Eq(crampedRules.CopyWithCountCalls, 0, "DG-05b and never copied into counted pieces");

            // Issue 24: sharing a type and prefix is what the merge INDEX keys on, and it is never
            // sufficient. Two stacks carrying different mod state are the same item and still must
            // not be folded, because folding discards one state.
            rules.AskedPairs.Clear();
            var stateTarget = FakeDisks.Disk(10, FakeDisks.Stack(7, 10, "A"), FakeDisks.Stack(8, 50));
            var stateDonor = FakeDisks.Disk(10, FakeDisks.Stack(7, 20, "B"));
            DefragmentCore.Sweep(new[] { stateTarget, stateDonor }, rules);
            Eq(FakeDisks.Layout(stateTarget), "t7x10,t8x50,t7x20", "DG-06 stacks sharing a type and prefix but not their mod state do not merge");
            Eq(rules.AskedPairs.Count, 1, "DG-06a the rule was asked about exactly the one stack sharing the key");
            IsTrue(rules.AskedPairs.TrueForAll(p => p.Target.ItemType == 7),
                "DG-06b and never about a stack of another identity");

            // A disk list naming one disk twice used to make it its own donor, and removing a stack
            // then shifted every slot the index had recorded for the target.
            var repeated = FakeDisks.Disk(10, FakeDisks.Stack(7, 5), FakeDisks.Stack(7, 3));
            var alias = FakeDisks.Alias(repeated);
            IsTrue(ReferenceEquals(repeated.Items, alias.Items), "DG-07 the fixture really is one disk named twice");
            var selfMoved = DefragmentCore.Sweep(new[] { repeated, alias }, rules);
            Eq(selfMoved.Count, 0, "DG-07a a disk repeated in the request never donates to itself");
            Eq(FakeDisks.Layout(repeated), "t7x5,t7x3", "DG-07b so its stacks are left exactly as they were");

            // Moving a stack removes it from the donor, which shifts every slot above it down. The
            // walk runs downwards so that shifting cannot make it skip a stack.
            var orderTarget = FakeDisks.Disk(10);
            var threeStacks = FakeDisks.Disk(10, FakeDisks.Stack(1, 5), FakeDisks.Stack(2, 5), FakeDisks.Stack(3, 5));
            DefragmentCore.Sweep(new[] { orderTarget, threeStacks }, rules);
            Eq(orderTarget.Items.Count, 3, "DG-08 every stack on the donor is moved, none skipped");
            Eq(threeStacks.Items.Count, 0, "DG-08a and the donor is emptied");
            Eq(FakeDisks.Layout(orderTarget), "t3x5,t2x5,t1x5", "DG-08b the donor is walked from its last slot down");

            var oneSlotTarget = FakeDisks.Disk(1);
            var bigDonor = FakeDisks.Disk(10, FakeDisks.Stack(7, 3), FakeDisks.Stack(7, 99), FakeDisks.Stack(7, 99));
            DefragmentCore.Sweep(new[] { oneSlotTarget, bigDonor }, rules);
            Eq(FakeDisks.Layout(oneSlotTarget), "t7x99", "DG-09 only as much moves as there are free slots");
            Eq(FakeDisks.Layout(bigDonor), "t7x3,t7x99", "DG-09a and the rest stays on the donor");

            var fullTarget = FakeDisks.Disk(1, FakeDisks.Stack(9, 1));
            var blockedDonor = FakeDisks.Disk(10, FakeDisks.Stack(7, 5));
            var nothingMoved = DefragmentCore.Sweep(new[] { fullTarget, blockedDonor }, rules);
            Eq(nothingMoved.Count, 0, "DG-10 a full target moves nothing");
            Eq(FakeDisks.Layout(blockedDonor), "t7x5", "DG-10a and the donor keeps its stack");

            var reportTarget = FakeDisks.Disk(10);
            var reportDonor = FakeDisks.Disk(10, FakeDisks.Stack(7, 5));
            var untouched = FakeDisks.Disk(10);
            var moved = DefragmentCore.Sweep(new[] { reportTarget, reportDonor, untouched }, rules);
            Eq(string.Join(",", moved), "0,1", "DG-11 only the disks whose contents changed are reported, ascending");

            var idleTarget = FakeDisks.Disk(10, FakeDisks.Stack(7, 5));
            var emptyDonor = FakeDisks.Disk(10);
            Eq(DefragmentCore.Sweep(new[] { idleTarget, emptyDonor }, rules).Count, 0,
                "DG-11a a sweep that moves nothing reports nothing");

            DefragmentSweepNeverCreatesOrDestroysAUnit();
            DefragmentSweepSurvivesASlotVanishingMidSweep();
            DefragmentSweepConservesUnitsAndModState();
        }

        // The net under everything above. Each case asserts the layout it expects, but a layout
        // assertion only checks what it names; this walks whole disks before and after and insists
        // the totals match. Defragmenting moves items - the failure this guards is the one that
        // costs a player their save rather than their patience.
        private static void DefragmentSweepNeverCreatesOrDestroysAUnit()
        {
            var layouts = new List<DefragmentDisk<FakeStack>[]>
            {
                new[] { FakeDisks.Disk(10), FakeDisks.Disk(10, FakeDisks.Stack(7, 30)) },
                new[] { FakeDisks.Disk(10), FakeDisks.Disk(10, FakeDisks.Stack(7, 30)),
                        FakeDisks.Disk(10, FakeDisks.Stack(7, 40)) },
                new[] { FakeDisks.Disk(10, FakeDisks.Stack(7, 90)), FakeDisks.Disk(10, FakeDisks.Stack(7, 30)) },
                new[] { FakeDisks.Disk(10, FakeDisks.Stack(7, 10)), FakeDisks.Disk(10, FakeDisks.Unique(7, 5)) },
                new[] { FakeDisks.Disk(2, FakeDisks.Stack(7, 10)),
                        FakeDisks.Disk(10, FakeDisks.Unique(7, 5), FakeDisks.Unique(8, 5)) },
                new[] { FakeDisks.Disk(10, FakeDisks.Stack(7, 10, "A")), FakeDisks.Disk(10, FakeDisks.Stack(7, 20, "B")) },
                new[] { FakeDisks.Disk(10), FakeDisks.Disk(10, FakeDisks.Stack(1, 5),
                        FakeDisks.Stack(2, 5), FakeDisks.Stack(3, 5)) },
                new[] { FakeDisks.Disk(1), FakeDisks.Disk(10, FakeDisks.Stack(7, 3),
                        FakeDisks.Stack(7, 99), FakeDisks.Stack(7, 99)) },
                new[] { FakeDisks.Disk(1, FakeDisks.Stack(9, 1)), FakeDisks.Disk(10, FakeDisks.Stack(7, 5)) },
                new[] { FakeDisks.Disk(10, FakeDisks.Stack(7, 99)), FakeDisks.Disk(10, FakeDisks.Stack(7, 20)) },
                new[] { FakeDisks.Disk(10, FakeDisks.Stack(7, 50, "plain", maxStack: 60)),
                        FakeDisks.Disk(10, FakeDisks.Stack(7, 30, "plain", maxStack: 99)) },
                new[] { FakeDisks.Disk(3), FakeDisks.Disk(10, FakeDisks.Stack(1, 5),
                        FakeDisks.Stack(2, 300), FakeDisks.Stack(3, 5)) }
            };

            int conserved = 0;
            foreach (DefragmentDisk<FakeStack>[] layout in layouts)
            {
                int before = FakeDisks.TotalUnits(layout);
                DefragmentCore.Sweep(layout, new FakeDefragmentRules());
                if (FakeDisks.TotalUnits(layout) == before)
                    conserved++;
            }

            Eq(conserved, layouts.Count, "DG-14 no layout gains or loses a single unit to the sweep");
        }

        // Nothing removes from a target disk while the sweep is running today. The merge index holds
        // slot positions for a whole target, so if anything ever did, a recorded slot could point
        // past the end of the disk or at a stack of an entirely different item. Both have to cost a
        // missed merge and never a count credited to the wrong stack.
        private static void DefragmentSweepSurvivesASlotVanishingMidSweep()
        {
            var rules = new FakeDefragmentRules();

            var shrinking = FakeDisks.Disk(10, FakeDisks.Stack(7, 10), FakeDisks.Stack(7, 20));
            var donor = FakeDisks.Disk(10, FakeDisks.Stack(7, 6));
            rules.RemoveSlotWhenStackIsWeighed(shrinking.Items, 1, shrinking.Items[0]);
            DefragmentCore.Sweep(new[] { shrinking, donor }, rules);
            Eq(shrinking.Items.Count, 1, "DG-12 a slot recorded by the index but gone from the disk is passed over");
            Eq(shrinking.Items[0].Count, 16, "DG-12a and the count lands on the stack that is still there");

            // The dangerous case is not the slot past the end - it is the live slot of another item
            // that shifted down into a recorded position. The bounds check cannot see that one; the
            // merge rule being re-asked for every candidate is what refuses it.
            //
            // The donor has to be big enough to spill PAST the first candidate: PlanDonorMove stops
            // as soon as the donor is placed, so a small donor never reaches the shifted slot and
            // the assertion would pass whatever the rule answered.
            var shifting = FakeDisks.Disk(10, FakeDisks.Stack(7, 10), FakeDisks.Stack(7, 20), FakeDisks.Stack(8, 50));
            var shiftDonor = FakeDisks.Disk(10, FakeDisks.Stack(7, 200));
            rules.RemoveSlotWhenStackIsWeighed(shifting.Items, 0, shifting.Items[0]);
            DefragmentCore.Sweep(new[] { shifting, shiftDonor }, rules);
            var strandedOtherItem = shifting.Items.Find(s => s.ItemType == 8);
            Eq(strandedOtherItem.Count, 50, "DG-12b a stack of another item shifted into a recorded slot is never credited");

            // What the guards do NOT cover, asserted so the limit is written down rather than
            // assumed: the count recorded alongside the slot goes stale too, so the stack that
            // shifted into the recorded position is filled using the departed stack's room and
            // ends above maxStack. Nothing outside the sweep removes from a disk mid-sweep today,
            // which is the only reason this is a curiosity rather than a defect.
            var overfilled = shifting.Items.Find(s => s.ItemType == 7);
            Eq(overfilled.Count, 109, "DG-12c a stale recorded count can overfill the stack that took its place");

            // A stack already at maxStack has no room, so the sweep passes over it without paying
            // for the identity comparison - which is the expensive one on a bulk-storage disk.
            rules.AskedPairs.Clear();
            var atCapacity = FakeDisks.Disk(10, FakeDisks.Stack(7, 99));
            var toppingUp = FakeDisks.Disk(10, FakeDisks.Stack(7, 20));
            DefragmentCore.Sweep(new[] { atCapacity, toppingUp }, rules);
            Eq(rules.AskedPairs.Count, 0, "DG-13 a stack already at maxStack is skipped before the merge rule is asked");
            Eq(FakeDisks.Layout(atCapacity), "t7x99,t7x20", "DG-13a and the donor takes a fresh slot instead");
        }

        private static void DefragmentSweepConservesUnitsAndModState()
        {
            var rules = new FakeDefragmentRules();

            // A relocated unique stack must be the SAME stack, not a rebuilt copy: everything that
            // makes it unique rides on the object, and issue 04 is what happens when it does not.
            var receiving = FakeDisks.Disk(10);
            var uniqueStack = FakeDisks.Unique(7, 5);
            var giving = FakeDisks.Disk(10, uniqueStack);
            DefragmentCore.Sweep(new[] { receiving, giving }, rules);
            IsTrue(ReferenceEquals(receiving.Items[0], uniqueStack),
                "DG-15 a stack that moves whole is the same stack, not a copy of it");

            var splitTarget = FakeDisks.Disk(3);
            var mixedDonor = FakeDisks.Disk(10, FakeDisks.Stack(1, 5),
                FakeDisks.Stack(2, 300), FakeDisks.Stack(3, 5));
            int beforeSplit = FakeDisks.TotalUnits(splitTarget, mixedDonor);
            DefragmentCore.Sweep(new[] { splitTarget, mixedDonor }, rules);
            Eq(FakeDisks.Layout(mixedDonor), "t1x5,t2x102", "DG-16 a partly drained stack keeps its remainder and its neighbours keep their slots");
            Eq(FakeDisks.TotalUnits(splitTarget, mixedDonor), beforeSplit, "DG-16a with every unit accounted for");

            // maxStack belongs to the item being moved, so the donor's is what bounds the merge.
            var lowMaxTarget = FakeDisks.Disk(10, FakeDisks.Stack(7, 50, "plain", maxStack: 60));
            var highMaxDonor = FakeDisks.Disk(10, FakeDisks.Stack(7, 30, "plain", maxStack: 99));
            DefragmentCore.Sweep(new[] { lowMaxTarget, highMaxDonor }, rules);
            Eq(lowMaxTarget.Items[0].Count, 80, "DG-17 the moving stack's own maxStack bounds the merge");

            // Issue 04 in the shape the sweep can express: two states go in, two states come out,
            // neither stamped onto the other's units.
            var stateTarget = FakeDisks.Disk(10, FakeDisks.Stack(7, 10, "A"));
            var stateDonor = FakeDisks.Disk(10, FakeDisks.Stack(7, 10, "A"), FakeDisks.Stack(7, 10, "B"));
            int beforeState = FakeDisks.TotalUnits(stateTarget, stateDonor);
            DefragmentCore.Sweep(new[] { stateTarget, stateDonor }, rules);
            var pooledA = stateTarget.Items.Find(s => s.ModState == "A");
            var keptB = stateTarget.Items.Find(s => s.ModState == "B");
            Eq(pooledA.Count, 20, "DG-18 stacks sharing their mod state pool together");
            Eq(keptB.Count, 10, "DG-18a a stack carrying different state keeps its own slot and its units");
            Eq(FakeDisks.TotalUnits(stateTarget, stateDonor), beforeState, "DG-18b and no unit is created or destroyed");
            Eq(stateTarget.Items.Count, 2, "DG-18c neither state is stamped onto the other's stack");
        }

        private static void Section(string title) => Console.WriteLine($"-- {title}");

        private static void Check(bool cond, string name)
        {
            if (cond) { _pass++; Console.WriteLine($"   PASS  {name}"); }
            else { _fail++; _failures.Add(name); Console.WriteLine($"   FAIL  {name}"); }
        }

        private static void Eq(int actual, int expected, string name) => Check(actual == expected, $"{name}  [expected {expected}, got {actual}]");
        private static void Eq(string actual, string expected, string name) => Check(actual == expected, $"{name}  [expected {expected}, got {actual}]");
        private static void IsTrue(bool c, string n) => Check(c, n);
        private static void IsFalse(bool c, string n) => Check(!c, n);
    }
}
