using HarmonyLib;
using System;
using System.Linq;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace archcomposermod
{
    public class archcomposermodModSystem : ModSystem
    {
        public static Type instrumentType;
        private Harmony harmony;
        ICoreClientAPI clientApi;
        public static readonly string logPrefix = "[archcomposermod] ";
        public override void Start(ICoreAPI api)
        {
            // Apply Harmony patches
            harmony = new Harmony("archcomposermod.blockentityinterceptor");
            try
            {
                // Find and patch BEMusicBlock.OnUse
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        // Look for BEMusicBlock specifically
                        if (type.Name == "BEMusicBlock" && typeof(BlockEntity).IsAssignableFrom(type))
                        {
                            var onUseMethod = type.GetMethod("OnUse",
                                BindingFlags.Public | BindingFlags.Instance,
                                null,
                                new Type[] { typeof(IPlayer) },
                                null);

                            if (onUseMethod != null)
                            {
                                var prefix = typeof(MusicBlockInterceptor).GetMethod("Prefix");
                                harmony.Patch(onUseMethod, new HarmonyMethod(prefix));

                                api.Logger.Notification("Successfully patched BEMusicBlock.OnUse");
                            }
                        }
                        
                    }
                }
            }
            catch (Exception e)
            {
                api.Logger.Error($"{logPrefix} Failed to patch BEMusicBlock: {e}");
            }

            
        }

        public override void Dispose()
        {
            base.Dispose();
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            clientApi = api;
            
            if (api.Side == EnumAppSide.Client)
            {
                bool patched = false;
                try
                {
                    // Find and patch Instrument.OnHeldInteractStart
                    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        foreach (var type in assembly.GetTypes())
                        {
                            // Look for Instrument specifically
                            if (type.Name == "InstrumentItem" && typeof(Item).IsAssignableFrom(type))
                            {
                                instrumentType = type;
                                api.Logger.Debug($"{logPrefix} Found InstrumentItem class");

                                patched = true;
                                var onHeldInteractStartMethod = type.GetMethod("OnHeldInteractStart",
                                    BindingFlags.Public | BindingFlags.Instance,
                                    null,
                                    new Type[] {
                                        typeof(ItemSlot),
                                        typeof(EntityAgent),
                                        typeof(BlockSelection),
                                        typeof(EntitySelection),
                                        typeof(bool),
                                        typeof(EnumHandHandling)
                                    },
                                    null);
                                // Get ALL public instance methods and find the right one
                                var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance);

                                foreach (var method in methods)
                                {
                                    if (method.Name == "OnHeldInteractStart")
                                    {
                                        var parameters = method.GetParameters();
                                        if (parameters.Length == 6)
                                        {
                                            var prefix = typeof(InstrumentPatcher).GetMethod("Prefix");
                                            harmony.Patch(method, new HarmonyMethod(prefix));
                                            api.Logger.Notification("{logPrefix} Successfully patched InstrumentItem.OnHeldInteractStart");
                                            break;
                                        }
                                    }
                                }
                                break;
                            }

                        }
                    }
                }
                catch (Exception e)
                {
                    api.Logger.Error($"{logPrefix} Failed to patch InstrumentItem: {e}");
                }
                api.Logger.Error($"{logPrefix} patch InstrumentItem: {patched}");
            }
        }

        public static bool IsParchmentOrTextHolder(ItemSlot slot)
        {
            // is slot valid, non empty, and is either ItemBook, subclass of ItemBook or has attribute "text" indicating book-like
            return slot != null
                && slot.Itemstack != null
                && slot.Itemstack.StackSize > 0
                && (slot.Itemstack.Item is ItemBook
                    || slot.Itemstack.Attributes.HasAttribute("text")
                );
        }
    }

    public class InstrumentPatcher
    {
        private static MethodInfo? abcSendStartMethod;
        
        //OnHeldInteractStart, Prefix
        public static bool Prefix(Item __instance, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handling)
        {
            try
            {
                var api = byEntity.World.Api;
                if (!firstEvent)
                    return true;
                var isInstrument = __instance.GetType().IsAssignableTo(archcomposermodModSystem.instrumentType);
                // will intercept the default behaviour on some condition, else allow default;
                if (isInstrument)
                {
                    IClientWorldAccessor clientWorld = GetClient(byEntity, out bool isClient);
                    if (isClient)
                    {
                        // let client send up to server the text;
                        // Call the private ABCSendStart method
                        var data = GetParchment(slot.Inventory, byEntity, api);
                        if (data != String.Empty)
                        {
                            handling = EnumHandHandling.PreventDefault;
                            CallABCSendStart(__instance, api, data, false);
                            return false;
                        }
                        else
                        {
                            return true;
                        }
                    }
                    else
                    {
                        return true; // default handle as we dont interfere with that.
                    }
                }
            }
            catch (Exception e)
            {
                byEntity.World.Api.Logger.Error(
                    $"{archcomposermodModSystem.logPrefix} Error during InstrumentItem.OnHeldInteractStart : {e.Message}"
                );
            }
            return true;
        }

        // Fetch that parchment text of first slot of inventory;
        private static string GetParchment(ItemSlot slot, EntityAgent byEntity, ICoreAPI api)
        {
            //first slot
            var inv = slot.Inventory;
            return GetParchment(inv, byEntity, api);
        }

        private static string GetParchment(InventoryBase inv, EntityAgent byEntity, ICoreAPI api)
        {
            //first slot
            var findSlot = inv.FirstOrDefault(x => archcomposermodModSystem.IsParchmentOrTextHolder(x));
            if (findSlot != null)
            {
                var text = findSlot.Itemstack.Attributes.GetString("text", String.Empty);
                return text;
            }
            return String.Empty;
        }

        private static void CallABCSendStart(Item instrument, ICoreAPI api, string fileData, bool isServerOwned)
        {
            // Cache the method info for performance
            if (abcSendStartMethod == null)
            {
                abcSendStartMethod = archcomposermodModSystem.instrumentType.GetMethod("ABCSendStart",
                    BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
            }

            if (abcSendStartMethod != null)
            {
                api?.Logger.Error($"{archcomposermodModSystem.logPrefix} invoked ABCSendStart method on Instrument");
                abcSendStartMethod.Invoke(instrument, new object[] { fileData, isServerOwned });
            }
            else
            {
                // Log error if method not found
                api?.Logger.Error($"{archcomposermodModSystem.logPrefix} Could not find ABCSendStart method on Instrument");
            }
        }


        // copied from the instruments mod;
        private static IClientWorldAccessor GetClient(EntityAgent entity, out bool isClient)
        {
            isClient = entity.World.Side == EnumAppSide.Client;
            return isClient ? entity.World as IClientWorldAccessor : null;
        }
    }

    public class MusicBlockInterceptor
    {
        private static FieldInfo? songDataProperty;

        public static bool Prefix(BlockEntity __instance, IPlayer byPlayer)
        {
            // Verify it's actually a BEMusicBlock (should always be true due to patch target)
            if (__instance.GetType().Name == "BEMusicBlock")
            {
                try
                {
                    var slot = byPlayer.InventoryManager?.ActiveHotbarSlot;
                    if (!archcomposermodModSystem.IsParchmentOrTextHolder(slot))
                    {
                        return true; // not a valid parchment item; do not alter behaviour
                    }
                    // Cache the property info for performance
                    if (songDataProperty == null)
                    {
                        songDataProperty = __instance.GetType().GetField("songData",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    }

                    if (songDataProperty != null)
                    {
                        // Fetch the songdata from item
                        var newSongData = slot.Itemstack.Attributes.GetString("text");
                        // Write back to the property
                        songDataProperty.SetValue(__instance, newSongData);
                    }
                    else
                    {
                        byPlayer.Entity.World.Api.Logger.Warning(
                            $"{archcomposermodModSystem.logPrefix} Could not find songdata property on BEMusicBlock"
                        );
                    }
                }
                catch (Exception e)
                {
                    byPlayer.Entity.World.Api.Logger.Error(
                        $"{archcomposermodModSystem.logPrefix} Error modifying songdata: {e.Message}"
                    );
                }
                return true;
            }

            return true;
        }
    }
}
