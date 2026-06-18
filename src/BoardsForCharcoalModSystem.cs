using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace lionfox_BoardsForCharcoal
{
    // The vanilla charcoal-pit pipeline (flood-fill, hole/seal detection, smoke, yield calc and
    // conversion to charcoalpile) reads ground storage generically. Only two spots reject
    // non-firewood with a hard `is ItemFirewood` type check, and both must be opened for planks:
    //   1. BlockFirepit.IsFirewoodPile - decides which stacks are part of the pit (seeding,
    //      flood-fill, hole detection). Postfixed below.
    //   2. ItemDryGrass.OnHeldInteractStart - lets you start the firepit-construct on top of a
    //      full ground-storage stack. Without this you can never build the firepit that becomes
    //      the charcoal pit, so the stack is left as a plain (non-ignitable) pile. Prefixed below.
    //
    // Universal: the conversion runs server-side, but the client also calls IsFirewoodPile when
    // positioning the pit's smoke particles and runs dry-grass placement client-side, so the
    // patches must be present on both sides.
    public class BoardsForCharcoalModSystem : ModSystem
    {
        const string HarmonyId = "lionfoxboardsforcharcoal";
        Harmony? harmony;

        public override void Start(ICoreAPI api)
        {
            base.Start(api);

            // Start runs once per API side; in singleplayer that is twice in one process.
            // HasAnyPatches guards against applying the patch twice.
            if (!Harmony.HasAnyPatches(HarmonyId))
            {
                harmony = new Harmony(HarmonyId);
                harmony.PatchAll(typeof(BoardsForCharcoalModSystem).Assembly);
                api.Logger.Notification("[BoardsForCharcoal] Charcoal pits now accept plank stacks as fuel.");
            }
        }

        public override void Dispose()
        {
            harmony?.UnpatchAll(HarmonyId);
            harmony = null;
            base.Dispose();
        }
    }

    [HarmonyPatch(typeof(BlockFirepit), nameof(BlockFirepit.IsFirewoodPile))]
    public static class IsFirewoodPilePatch
    {
        // If vanilla already recognised a firewood pile, leave its answer alone. Otherwise
        // accept a ground-storage stack of planks. Vanilla's ItemPlank class is internal, so
        // we match on the item code instead: plank codes are "plank-<wood>" (e.g. plank-oak,
        // plank-aged), so the first code part is always "plank". Matching the code rather than
        // the class also lets planks from other mods' woods qualify ("boards of any wood").
        // Within one block a stack is a single wood type, but a pit can span many blocks of
        // differing woods, so mixed-wood pits work for free.
        static void Postfix(IWorldAccessor world, BlockPos pos, ref bool __result)
        {
            if (__result) return;

            var be = world.BlockAccessor.GetBlockEntity<BlockEntityGroundStorage>(pos);
            if (Boards.IsPlankStack(be)) __result = true;
        }
    }

    [HarmonyPatch(typeof(ItemDryGrass), nameof(ItemDryGrass.OnHeldInteractStart))]
    public static class DryGrassFirepitOnPlanksPatch
    {
        // Vanilla only lets dry grass start a firepit-construct on top of a ground-storage pile
        // when that pile is a FULL stack of ItemFirewood. We handle exactly one extra case - a
        // full plank stack - by placing the same firepit-construct1 block vanilla would, then
        // skipping the original. Every other case (firewood, pit kilns, non-ground-storage
        // surfaces, partial stacks) returns true and falls through to vanilla untouched.
        static bool Prefix(ItemSlot itemslot, EntityAgent byEntity, BlockSelection blockSel, ref EnumHandHandling handHandling)
        {
            if (blockSel == null || byEntity?.World == null || !byEntity.Controls.ShiftKey) return true;

            IWorldAccessor world = byEntity.World;
            Block construct = world.GetBlock(new AssetLocation("firepit-construct1"));
            if (construct == null) return true;

            if (world.BlockAccessor.GetBlock(blockSel.Position) is not BlockGroundStorage) return true;
            var be = world.BlockAccessor.GetBlockEntity<BlockEntityGroundStorage>(blockSel.Position);

            // Require a single, full plank stack (slots 1-3 empty), mirroring vanilla's firewood rule
            // that the stack be complete so the firepit sits on a full block face.
            if (!Boards.IsPlankStack(be) || be.Inventory[1].Empty == false || be.Inventory[2].Empty == false
                || be.Inventory[3].Empty == false || be.Inventory[0].StackSize != be.Capacity)
            {
                return true;
            }

            BlockPos placePos = blockSel.DidOffset ? blockSel.Position : blockSel.Position.AddCopy(blockSel.Face);
            IPlayer player = world.PlayerByUid((byEntity as EntityPlayer)?.PlayerUID);
            if (!world.Claims.TryAccess(player, placePos, EnumBlockAccessFlags.BuildOrBreak)) return false;

            string failureCode = "";
            if (!construct.CanPlaceBlock(world, player, new BlockSelection { Position = placePos, Face = BlockFacing.UP }, ref failureCode))
            {
                return false;
            }

            world.BlockAccessor.SetBlock(construct.BlockId, placePos);
            if (construct.Sounds != null)
            {
                world.PlaySoundAt(construct.Sounds.Place, (double)blockSel.Position.X, (double)blockSel.Position.InternalY,
                    (double)blockSel.Position.Z, player, true, 32f, 1f);
            }

            itemslot.Itemstack.StackSize--;
            if (itemslot.Itemstack.StackSize <= 0) itemslot.Itemstack = null;
            itemslot.MarkDirty();

            handHandling = EnumHandHandling.PreventDefault;
            return false;
        }
    }

    internal static class Boards
    {
        // Single source of truth for "what counts as a board". Vanilla's ItemPlank class is
        // internal, so we match on the item code: plank codes are "plank-<wood>" (e.g. plank-oak,
        // plank-aged), so the first code part is always "plank". Matching the code rather than the
        // class also lets planks from other mods' woods qualify ("boards of any wood").
        internal static bool IsPlankStack(BlockEntityGroundStorage be)
        {
            var collectible = be?.Inventory?[0]?.Itemstack?.Collectible;
            return collectible?.ItemClass == EnumItemClass.Item && collectible.FirstCodePart() == "plank";
        }
    }
}
