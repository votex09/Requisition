using System.Collections.Generic;

namespace TerraStorage.Common
{
    // Why the server refused a storage operation.
    //
    // A refusal used to travel as a bare success flag that the client wrote to a debug file and
    // discarded, so every denied craft, withdrawal and deposit was "click, nothing happens".
    //
    // These byte values ARE the wire format. A peer reads one and maps it back to a localization
    // key, so members may only ever be APPENDED - renumbering one silently mistranslates every
    // refusal the other side reports. StorageOperationFailureTests pins each value so a reorder
    // fails the suite instead of the session.
    public enum StorageOperationFailure : byte
    {
        None = 0,
        Unspecified = 1,
        RecipeNotFeasible = 2,
        NoRoomInInventory = 3,
        NoRoomInStorageOrInventory = 4,
        CraftCostingNoLongerHolds = 5,
        NothingWithdrawn = 6,
        NothingDeposited = 7,
        NothingQuickStacked = 8,
        NoStorageInRange = 9,
        NoStorageConnected = 10,
        NoTerminalFound = 11,
        NotAtDriveBay = 12,
        DiskNotInSlot = 13,
        UpgradeUnavailable = 14,
        MaterialsNoLongerAvailable = 15,
        DiskNotFound = 16,
        DiskRecoveryRefused = 17,
        NothingToDefragment = 18,
        DiskClaimRefused = 19,
        DriveBaySlotUnavailable = 20,
        DriveBayNotOnNetwork = 21,
    }

    public static class StorageOperationFailures
    {
        private static readonly StorageOperationFailure[] Denied =
        {
            StorageOperationFailure.Unspecified,
            StorageOperationFailure.RecipeNotFeasible,
            StorageOperationFailure.NoRoomInInventory,
            StorageOperationFailure.NoRoomInStorageOrInventory,
            StorageOperationFailure.CraftCostingNoLongerHolds,
            StorageOperationFailure.NothingWithdrawn,
            StorageOperationFailure.NothingDeposited,
            StorageOperationFailure.NothingQuickStacked,
            StorageOperationFailure.NoStorageInRange,
            StorageOperationFailure.NoStorageConnected,
            StorageOperationFailure.NoTerminalFound,
            StorageOperationFailure.NotAtDriveBay,
            StorageOperationFailure.DiskNotInSlot,
            StorageOperationFailure.UpgradeUnavailable,
            StorageOperationFailure.MaterialsNoLongerAvailable,
            StorageOperationFailure.DiskNotFound,
            StorageOperationFailure.DiskRecoveryRefused,
            StorageOperationFailure.NothingToDefragment,
            StorageOperationFailure.DiskClaimRefused,
            StorageOperationFailure.DriveBaySlotUnavailable,
            StorageOperationFailure.DriveBayNotOnNetwork,
        };

        public static IReadOnlyList<StorageOperationFailure> GetDeniedFailures() => Denied;

        // The one derivation of "did it work" from "what went wrong". Holding both as separate
        // values is how the two drift apart, so nothing outside here decides success.
        public static bool IsSuccess(StorageOperationFailure failure)
            => failure == StorageOperationFailure.None;

        public static string GetLocalizationKey(StorageOperationFailure failure)
        {
            StorageOperationFailure named = GetReportableFailure(failure);
            return GetLocalizationKeyPrefix() + named;
        }

        // None means nothing went wrong and never reaches a message; a value this build does not
        // define can only come from a peer it does not understand. Both fall back to one line.
        public static StorageOperationFailure GetReportableFailure(StorageOperationFailure failure)
        {
            if (failure == StorageOperationFailure.None)
                return StorageOperationFailure.Unspecified;

            return GetFailureFromWireValue((byte)failure);
        }

        // Total by construction: the discard arm is what stops an undefined byte from falling
        // through into a state nothing handles.
        public static StorageOperationFailure GetFailureFromWireValue(byte wireValue)
        {
            var candidate = (StorageOperationFailure)wireValue;

            return candidate switch
            {
                StorageOperationFailure.None
                    or StorageOperationFailure.Unspecified
                    or StorageOperationFailure.RecipeNotFeasible
                    or StorageOperationFailure.NoRoomInInventory
                    or StorageOperationFailure.NoRoomInStorageOrInventory
                    or StorageOperationFailure.CraftCostingNoLongerHolds
                    or StorageOperationFailure.NothingWithdrawn
                    or StorageOperationFailure.NothingDeposited
                    or StorageOperationFailure.NothingQuickStacked
                    or StorageOperationFailure.NoStorageInRange
                    or StorageOperationFailure.NoStorageConnected
                    or StorageOperationFailure.NoTerminalFound
                    or StorageOperationFailure.NotAtDriveBay
                    or StorageOperationFailure.DiskNotInSlot
                    or StorageOperationFailure.UpgradeUnavailable
                    or StorageOperationFailure.MaterialsNoLongerAvailable
                    or StorageOperationFailure.DiskNotFound
                    or StorageOperationFailure.DiskRecoveryRefused
                    or StorageOperationFailure.NothingToDefragment
                    or StorageOperationFailure.DiskClaimRefused
                    or StorageOperationFailure.DriveBaySlotUnavailable
                    or StorageOperationFailure.DriveBayNotOnNetwork => candidate,
                _ => StorageOperationFailure.Unspecified,
            };
        }

        // The crafting panel and the server's craft handler guard the same four conditions. Two
        // hand-written copies of one rule is how 23a, 23b and 23c each survived their own fix, and
        // neither of these two sites can be compiled outside the game - so the decision lives here,
        // where the test runner really executes it.
        public static StorageOperationFailure GetCraftFailure(bool planIsFeasible,
            bool craftToInventory, bool playerHasRoomForResult, bool storageHasRoomForResult)
        {
            if (!planIsFeasible)
                return StorageOperationFailure.RecipeNotFeasible;

            if (craftToInventory)
                return playerHasRoomForResult
                    ? StorageOperationFailure.None
                    : StorageOperationFailure.NoRoomInInventory;

            if (storageHasRoomForResult || playerHasRoomForResult)
                return StorageOperationFailure.None;

            return StorageOperationFailure.NoRoomInStorageOrInventory;
        }

        // Nothing matching and nothing fitting are different refusals with different remedies, and
        // the length of a results list cannot tell them apart.
        public static StorageOperationFailure GetQuickStackFailure(bool matchedAnySlot,
            bool anyDeposited)
        {
            if (!matchedAnySlot)
                return StorageOperationFailure.NothingQuickStacked;

            if (!anyDeposited)
                return StorageOperationFailure.NothingDeposited;

            return StorageOperationFailure.None;
        }

        private static string GetLocalizationKeyPrefix() => "Mods.TerraStorage.UI.OperationFailed.";
    }

    // Bulk deposit sends one packet per inventory slot, so a full network denies once per slot and
    // one click became forty identical red lines. The same cause arriving again inside a second is
    // the same refusal, not new information; a different cause is never suppressed.
    //
    // This is for answers arriving off the wire, where one click produces many. A locally decided
    // refusal is already one per click and must NOT come through here - a second deliberate click
    // lands well inside the window, and swallowing it would restore the silence being fixed.
    public class StorageOperationFailureThrottle
    {
        private StorageOperationFailure _lastReported = StorageOperationFailure.None;
        private uint _lastReportedAt;

        public bool ShouldReport(StorageOperationFailure failure, uint gameUpdateCount)
        {
            bool repeatsLastReported = failure == _lastReported;
            uint elapsedTicks = unchecked(gameUpdateCount - _lastReportedAt);

            if (repeatsLastReported && elapsedTicks < GetRepeatSuppressionTicks())
                return false;

            _lastReported = failure;
            _lastReportedAt = gameUpdateCount;
            return true;
        }

        // Terraria updates 60 times a second, so one second spans a burst even when its packets
        // arrive across several network reads. It also spans a double-click, which is why only
        // wire answers come through here and a locally decided refusal never does.
        private static uint GetRepeatSuppressionTicks() => 60;
    }
}
