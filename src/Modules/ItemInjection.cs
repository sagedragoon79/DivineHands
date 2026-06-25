using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using MelonLoader;

namespace DivineHands.Modules
{
    /// <summary>
    /// Item Injection — operates on the CURRENTLY-SELECTED building (the one whose info window is
    /// open), not the cursor. Three capabilities, all behind the panel's "Selected Building" section:
    ///
    ///   1. ADD ITEMS — instantiate an Item by ItemID name and push it into the building's
    ///      ReservableItemStorage via AddItems(ItemBundle).
    ///   2. ADD LIVESTOCK — instantiate the animal prefab (user-editable GUID prefs, GetPrefab
    ///      null-guarded for DLC safety) and AddAnimalToHerd into a Barn/ChickenCoop/GoatBarn/Stable.
    ///   3. INFINITE STORAGE — flip ReservableItemStorage.infiniteItems on the selected building.
    ///
    /// All FF API paths VERIFIED against the Assembly-CSharp decompile and two working reference
    /// mods (AddItemMono, WickerToolbox) — no invented names:
    ///
    ///   SELECTED BUILDING — GameManager.Instance.inputManager.selectedObject : GameObject
    ///     [public, decompile 110685; WickerToolbox 273]. We also fall back to
    ///     UIBuildingInfoWindow_New.targetObject [AddItemMono 981-985] when nothing is selected via
    ///     the input manager (e.g. selection cleared but the info window is still open).
    ///
    ///   STORAGE — go.GetComponent&lt;ReservableItemStorage&gt;() [AddItemMono 1046] — the storage
    ///     component is on the building GameObject directly (it's a CEMonoBehaviour, decompile 162957).
    ///
    ///   ADD ITEMS — new ItemBundle(Item, uint quantity, uint percentIntact) [ctor 156293] then
    ///     storage.AddItems(ItemBundle) — ReservableItemStorage.AddItems at 163536 (forwards to the
    ///     inner ItemStorage.AddItems at 157252). Item instances are built reflectively:
    ///     Activator.CreateInstance(Type "Item{Name}") — Item classes map 1:1 to ItemID enum names
    ///     with an "Item" prefix (verified: ItemGoldIngot, ItemRootVegetable, ItemKnowledgeTome…).
    ///     This mirrors AddItemMono (which hard-news each ItemXxx) but stays reflection-only so DH
    ///     adds no compile-time game refs (same pattern as WickerToolbox 285-318).
    ///
    ///   LIVESTOCK — targetObject.GetComponent&lt;Barn|ChickenCoop|GoatBarn|Stable&gt;()
    ///     [AddItemMono 989-1041], all four derive LivestockBuilding (decompile 325786/336473/
    ///     341844/353020) which exposes public Herd herd at 344413 (on the abstract base
    ///     LivestockBuilding @344233). Spawn pattern:
    ///       prefab = GlobalAssets.prefabAssetMap.GetPrefab(guid)   [48987, null on miss]
    ///       go = Object.Instantiate(prefab); go.transform.localPosition = building.localPosition
    ///       ((LivestockBuilding)b).herd.AddAnimalToHerd((LivestockAnimal)go.GetComponent&lt;Kind&gt;(), false) [39810]
    ///     Animal component classes: Cow 37343 / Chicken 37248 / Goat 38134 / Horse 40089. Default
    ///     GUIDs come from AddItemMono's working hardcodes; user-overridable via Config prefs.
    ///
    /// ── SAVE-SAFETY (infinite storage) ─────────────────────────────────────────────────────────
    ///   ReservableItemStorage.infiniteItems IS SERIALIZED — ItemStorage.Save writes it [157075] and
    ///   Load reads it back behind a save-version gate [157085]; the ReservableItemStorage wrapper
    ///   serializes the same field [163179/163189]. So a flag set on a building BAKES INTO THE .sav
    ///   and survives uninstall.
    ///
    ///   There is NO persistent building identity to key a cleanup sweep on: Building save uses
    ///   gameObject.GetInstanceID() [38943], which is runtime-only and changes every load; FF has no
    ///   building GUID/saveId. A position-keyed sidecar would be fragile (buildings relocate) AND
    ///   DANGEROUS here — DivineHands' own CursorSpawners deliberately bakes storage.infiniteItems on
    ///   spawned stone/clay/sand pits and relies on it persisting; a blind map-load sweep that cleared
    ///   "unrecognised" infinite flags would wipe those legitimate infinite pits.
    ///
    ///   DECISION: SESSION-ONLY. The toggle flips infiniteItems live, and DivineHands force-clears
    ///   EVERY storage it flagged before any save can capture it, then restores it after. We track the
    ///   live ReservableItemStorage references we touched in a runtime set (cleared each map load), so
    ///   we only ever revert OUR OWN flags and never touch the spawner's pits or any other component.
    ///
    ///   THE SAVE PATH (the real hole the first draft missed): FF serializes in THREE situations and
    ///   only one of them is a scene change:
    ///     • return-to-menu / save-and-exit  → scene change → OnSceneExit fires (covered)
    ///     • manual "Save Game"              → IN-PLACE, no scene change
    ///     • periodic AUTOSAVE (default 10m) → IN-PLACE, no scene change   [AutoSaveRoutine 181848]
    ///   The two in-place saves NEVER fire OnSceneExit, so a scene-exit-only revert would let an
    ///   autosave bake infiniteItems=true into the .sav mid-session. The fix is the save event itself:
    ///   SaveManager.Save raises StartSaveGameEvent SYNCHRONOUSLY [182289] and only THEN starts
    ///   SaveRoutine, which spins numFrameSpin(=10) frames + a thumbnail coroutine before the actual
    ///   ItemStorage.Save writes [SaveRoutine 182319-182326, SaveInternal 182329]. So a listener on
    ///   StartSaveGameEvent runs guaranteed-before serialization for ALL THREE paths (manual, autosave,
    ///   exit). AutoSave() routes through the same Save() [182316], so one hook covers everything.
    ///
    ///   We therefore: (a) on StartSaveGameEvent, synchronously clear every session flag to false so
    ///   the bytes written to disk are clean; (b) queue those same storages for re-apply and flip them
    ///   back to true a comfortable margin after the save coroutine finishes (frame-counted in OnUpdate,
    ///   well past numFrameSpin + thumbnail), so the live session keeps infinite storage. The OFF window
    ///   is a fraction of a second and harmless. OnSceneExit + master-disable still hard-revert as
    ///   belt-and-suspenders. Net: infinite storage works the whole session, is NEVER serialized true,
    ///   touches only DH-flagged building storages (never the spawner's pits), and uninstalls clean.
    /// </summary>
    public static class ItemInjection
    {
        // Livestock the four FF buildings accept. Order MUST match the panel picker + Config doc.
        public enum LivestockKind { Cow, Chicken, Goat, Horse }

        // The ItemID names offered in the Add-Items picker (subset of the 88-value ItemID enum that
        // makes sense to inject as stored goods — raw materials, food, processed goods, tools, arms).
        // Names map 1:1 to Item{Name} classes (verified against the decompile). Order == picker order
        // and Config.InjectItemIndex.
        public static readonly string[] ItemNames =
        {
            "Logs", "Planks", "Firewood", "Stone", "Brick", "Clay", "Sand", "Glass",
            "IronOre", "Iron", "GoldOre", "GoldIngot", "Coal", "Tool", "HeavyTool",
            "Berries", "RootVegetable", "Beans", "Greens", "Grain", "Flour", "Bread",
            "Mushroom", "Roots", "Nuts", "Fruit", "Herbs", "Eggs", "Meat", "Fish",
            "SmokedMeat", "SmokedFish", "Preserves", "PreservedVeg", "Honey", "Milk",
            "Cheese", "Pottery", "WheatBeer", "Medicine", "Soap", "Candle", "Spice",
            "Hide", "HideCoat", "Flax", "LinenClothes", "Shoes", "Wax", "Tallow",
            "Furniture", "Basket", "Barrel", "Books", "Paper", "Hay", "Willow",
            "Weapon", "SimpleWeapon", "HeavyWeapon", "Shield", "Hauberk", "Platemail",
            "Bow", "Crossbow", "Arrow", "AnimalTrap"
        };

        // ---- cached reflection (resolved lazily, reset each map) ----
        private static Assembly? _gameAssembly;          // assembly that holds Item* classes
        private static MethodInfo? _addAnimalToHerd;     // Herd.AddAnimalToHerd(LivestockAnimal, bool)
        private static bool _resolved;

        // Live ReservableItemStorage components DivineHands flipped infinite THIS session. We revert
        // exactly these (never anything else) on save / scene-exit / disable, so the cheat never
        // serializes.
        private static readonly HashSet<Component> _sessionInfinite = new HashSet<Component>();

        // ---- save-hook state ----
        // Subscribed StartSaveGameEvent listener (kept so we can RemoveListener on map unload). Typed as
        // object/Delegate-free via a cached MethodInfo-free closure isn't needed: we hold the strongly
        // typed delegate. Stored as Delegate to avoid leaking the game type into the field signature.
        private static Delegate? _saveListener;
        private static bool _saveHookBound;

        // While a save is in flight we revert our flags to false, stash them here, and re-apply true a
        // safe margin of frames later (SaveRoutine spins numFrameSpin=10 + a thumbnail coroutine before
        // writing). Re-applying ~30 frames after the event clears that window with comfortable slack.
        private static readonly List<Component> _reapplyAfterSave = new List<Component>();
        private static int _reapplyCountdown;          // frames until we restore infinite; 0 = idle
        private const int ReapplyDelayFrames = 30;

        // =====================================================================
        // Lifecycle (called from Plugin)
        // =====================================================================

        public static void OnMapLoaded()
        {
            // Fresh map = fresh live objects; drop stale references and re-resolve reflection lazily.
            _sessionInfinite.Clear();
            _reapplyAfterSave.Clear();
            _reapplyCountdown = 0;
            _addAnimalToHerd = null;
            _gameAssembly = null;
            _resolved = false;

            // Eligibility reflection is keyed off the live game assembly; re-resolve per map.
            _storageBuildingType = null;
            _isItemAllowed = null;
            _buildingType = null;
            _eligibilityResolved = false;
            _loggedNoFilter = false;
            _consumedCacheGo = null;
            _consumedNames = null;
            // Per-frame / per-building lookup caches (selection + eligible-item set).
            _infoWindowType = null;
            _infoWindow = null;
            _selCacheGo = null;
            _selCacheFrame = -1;
            _eligSetGo = null;
            _eligibleNames = null;

            // Bind the save hook so in-place manual saves AND autosaves (which never fire OnSceneExit)
            // still strip our infinite flags before the bytes hit disk.
            BindSaveHook();
        }

        /// <summary>Force-clear every infinite flag DivineHands set, BEFORE any save can capture it.
        /// Called on scene change (always precedes save-and-exit / return to menu). The StartSaveGameEvent
        /// hook covers the in-place save paths; this covers the scene-change path and tidies up.</summary>
        public static void OnSceneExit()
        {
            ClearAllSessionInfinite();
            UnbindSaveHook();          // drop the listener so it can't accumulate across scene cycles
            _reapplyAfterSave.Clear();
            _reapplyCountdown = 0;
            // Drop stale references/reflection too, but do NOT re-bind here — OnMapLoaded re-binds when
            // the next Map scene loads. (Re-binding now would leave a dangling listener with no game.)
            _sessionInfinite.Clear();
            _addAnimalToHerd = null;
            _gameAssembly = null;
            _resolved = false;
        }

        /// <summary>Master-disable also reverts our session flags so they can't linger into a save.</summary>
        public static void OnMasterDisabled()
        {
            ClearAllSessionInfinite();
            _reapplyAfterSave.Clear();
            _reapplyCountdown = 0;
        }

        /// <summary>Per-frame tick (from Plugin.OnUpdate while in game). Drives the post-save re-apply of
        /// session-infinite flags once the save coroutine has safely written.</summary>
        public static void OnUpdate()
        {
            if (_reapplyCountdown <= 0) return;
            _reapplyCountdown--;
            if (_reapplyCountdown > 0) return;

            // Save has written by now — restore infinite on the storages we temporarily cleared.
            foreach (var storage in _reapplyAfterSave)
            {
                try
                {
                    if (storage == null) continue;        // destroyed with the scene
                    SetMember(storage, "infiniteItems", true);
                    _sessionInfinite.Add(storage);
                }
                catch { /* best-effort restore */ }
            }
            if (_reapplyAfterSave.Count > 0 && Config.DebugLog.Value)
                MelonLogger.Msg($"[DivineHands] Restored {_reapplyAfterSave.Count} session-infinite " +
                                "storage(s) after save");
            _reapplyAfterSave.Clear();
        }

        // =====================================================================
        // Selected building resolution
        // =====================================================================

        // GetSelectedBuilding is hit many times per IMGUI frame (per-item eligibility + header checks,
        // and OnGUI fires Layout+Repaint each frame). Memoize the result per frame so the resolve — and
        // especially the FindObjectOfType fallback — runs at most once per frame, not per item.
        private static GameObject? _selCacheGo;
        private static int _selCacheFrame = -1;
        private static Type? _infoWindowType;
        private static UnityEngine.Object? _infoWindow;       // cached window instance (FindObjectOfType is O(all objects))
        private static int _infoWindowProbeFrame = -1000;

        /// <summary>The building GameObject the player currently has selected (info window open), or
        /// null. Primary: inputManager.selectedObject. Fallback: UIBuildingInfoWindow_New.targetObject.
        /// Memoized per frame — cheap to call repeatedly within a frame.</summary>
        public static GameObject? GetSelectedBuilding()
        {
            int frame = Time.frameCount;
            if (frame == _selCacheFrame) return _selCacheGo;
            _selCacheFrame = frame;
            _selCacheGo = ResolveSelectedBuilding(frame);
            return _selCacheGo;
        }

        private static GameObject? ResolveSelectedBuilding(int frame)
        {
            // Fast path — the game's own selection (cheap; reliable while a building is selected).
            try
            {
                var gm = GameManager.Instance;
                var go = gm != null && gm.inputManager != null ? gm.inputManager.selectedObject : null;
                if (go != null && go.GetComponent<Building>() != null)
                    return go;
            }
            catch { /* fall through to the info-window probe */ }

            // Fallback — open info window's target. FindObjectOfType is O(all objects), so cache the
            // window instance and only re-probe every ~30 frames when we don't have a live one.
            try
            {
                _infoWindowType ??= FindType("UIBuildingInfoWindow_New");
                if (_infoWindowType == null) return null;
                if (_infoWindow == null && frame - _infoWindowProbeFrame >= 30)
                {
                    _infoWindowProbeFrame = frame;
                    _infoWindow = UnityEngine.Object.FindObjectOfType(_infoWindowType);
                }
                if (_infoWindow == null) return null;
                var target = GetMember(_infoWindow, _infoWindowType, "targetObject") as GameObject;
                if (target != null && target.GetComponent<Building>() != null)
                    return target;
            }
            catch { /* no selection available */ }

            return null;
        }

        /// <summary>Human-readable name of the selected building for the panel header, or "".</summary>
        public static string GetSelectedBuildingName()
        {
            var go = GetSelectedBuilding();
            if (go == null) return "";
            try
            {
                var b = go.GetComponent<Building>();
                var name = GetMember(b!, b!.GetType(), "displayName") as string; // Building.displayName
                if (!string.IsNullOrEmpty(name)) return name!;
            }
            catch { /* fall back to GameObject name */ }
            return go.name;
        }

        // =====================================================================
        // 1) ADD ITEMS
        // =====================================================================

        /// <summary>Add <paramref name="count"/> of the item named <paramref name="itemName"/> (an
        /// ItemID enum name) to the selected building's storage. Returns a short status string for
        /// the panel.</summary>
        public static string AddItems(string itemName, int count)
        {
            var go = GetSelectedBuilding();
            if (go == null) return "No building selected";

            var storage = go.GetComponent<ReservableItemStorage>();
            if (storage == null) return "Building has no storage";

            count = Mathf.Clamp(count, 1, 9999);

            try
            {
                var item = CreateItem(itemName);
                if (item == null) return $"Unknown item '{itemName}'";

                // Eligibility gate (3-tier): storage building -> its allow-list; production building ->
                // only the items it consumes; neither -> denied. Blocks up-front rather than dropping an
                // item into a building that won't keep or use it.
                if (!IsItemInjectableByName(go, itemName))
                    return $"{itemName} not accepted here";

                // new ItemBundle(Item, uint quantity, uint percentIntact:100) -> storage.AddItems
                var bundle = new ItemBundle(item, (uint)count, 100u);
                storage.AddItems(bundle);

                if (Config.DebugLog.Value)
                    MelonLogger.Msg($"[DivineHands] Added {count}x {itemName} to {GetSelectedBuildingName()}");
                return $"+{count} {itemName}";
            }
            catch (Exception ex)
            {
                if (Config.DebugLog.Value)
                    MelonLogger.Warning($"[DivineHands] AddItems({itemName}) failed: {ex.Message}");
                return "Add failed (see log)";
            }
        }

        // Build an Item instance by enum name via reflection. Item classes are "Item{Name}" and have
        // a public parameterless ctor (verified; mirrors WickerToolbox's Activator approach).
        private static Item? CreateItem(string itemName)
        {
            if (string.IsNullOrWhiteSpace(itemName)) return null;
            ResolveReflection();

            Type? t = null;
            // Prefer the cached game assembly; otherwise scan (Il2CPP. prefix variant too, per Wicker).
            if (_gameAssembly != null)
                t = _gameAssembly.GetType("Item" + itemName) ?? _gameAssembly.GetType("Il2CPP.Item" + itemName);
            if (t == null) t = FindType("Item" + itemName);

            if (t == null) return null;
            return Activator.CreateInstance(t) as Item;
        }

        // ====================================================================
        // Eligibility — what may be injected into the SELECTED building (3-tier):
        //   1. StorageBuilding family (granary / storehouse / depot / stockyard / root cellar /
        //      treasury / market / trading post / supply wagon) -> its own allow-list via
        //      StorageBuilding.IsItemAllowed(Item) [355450].
        //   2. Production building (Preservist, Bakery, …) -> ONLY the items it CONSUMES, read from
        //      Building.manufactureDefinitions[].sourceItems[].itemName [184600 / 185446 / 185398].
        //      Produced goods are injected at a storage building instead.
        //   3. Neither (no allow-list, no recipe inputs) -> DENY everything (never fake-allow).
        // If the StorageBuilding type itself can't be resolved (reflection broken) we fail OPEN so the
        // picker never hard-locks.
        // ====================================================================
        private static Type? _storageBuildingType;
        private static MethodInfo? _isItemAllowed;
        private static Type? _buildingType;     // FF "Building" base — exposes manufactureDefinitions
        private static bool _eligibilityResolved;
        private static bool _loggedNoFilter;

        private static void ResolveEligibility()
        {
            if (_eligibilityResolved) return;
            _eligibilityResolved = true;
            try
            {
                _storageBuildingType = FindType("StorageBuilding");
                if (_storageBuildingType != null)
                    _isItemAllowed = _storageBuildingType.GetMethod("IsItemAllowed",
                        BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(Item) }, null);
                _buildingType = FindType("Building");
            }
            catch (Exception ex)
            {
                if (Config.DebugLog.Value)
                    MelonLogger.Warning($"[DivineHands] ResolveEligibility failed: {ex.Message}");
            }
        }

        // Per-building cache of the consumed-input item names (FF "ItemX" form). null => the building has
        // no manufactureDefinitions (not a producer); empty => a producer with no inputs (denies all).
        // Recomputed only when the selected building changes, so the per-item picker check is a cheap
        // HashSet lookup rather than reflection every frame.
        private static GameObject? _consumedCacheGo;
        private static HashSet<string>? _consumedNames;

        private static HashSet<string>? GetConsumedItemNames(GameObject go)
        {
            if (ReferenceEquals(go, _consumedCacheGo)) return _consumedNames;
            _consumedCacheGo = go;
            _consumedNames = null;
            try
            {
                if (_buildingType == null) return null;
                var building = go.GetComponent(_buildingType);
                if (building == null) return null;

                if (ReflectGet(building, "manufactureDefinitions") is not System.Collections.IEnumerable defs)
                    return null;

                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var md in defs)
                {
                    if (md == null) continue;
                    if (ReflectGet(md, "sourceItems") is not System.Collections.IEnumerable srcs) continue;
                    foreach (var s in srcs)
                        if (s != null && ReflectGet(s, "itemName") is string n && !string.IsNullOrEmpty(n))
                            set.Add(n);
                }
                _consumedNames = set; // possibly empty -> denies all (producer with no inputs)
            }
            catch (Exception ex)
            {
                if (Config.DebugLog.Value)
                    MelonLogger.Warning($"[DivineHands] GetConsumedItemNames failed: {ex.Message}");
                _consumedNames = null;
            }
            return _consumedNames;
        }

        // Field-or-property reflective getter, walking base types. Cheap and tolerant.
        private static object? ReflectGet(object obj, string name)
        {
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            for (var cur = obj.GetType(); cur != null; cur = cur.BaseType)
            {
                var f = cur.GetField(name, F);
                if (f != null) return f.GetValue(obj);
                var p = cur.GetProperty(name, F);
                if (p != null && p.CanRead) return p.GetValue(obj);
            }
            return null;
        }

        /// <summary>The 3-tier eligibility decision for a DH picker item (named by ItemID enum, e.g.
        /// "RootVegetable" — FF names the same item "ItemRootVegetable"). See the section header.</summary>
        private static bool IsItemInjectableByName(GameObject go, string dhName)
        {
            try
            {
                ResolveEligibility();
                if (_storageBuildingType == null) return true; // reflection broken -> fail OPEN

                // Tier 1 — storage building: honour its allow-list (fails OPEN on its own error).
                if (_isItemAllowed != null)
                {
                    var sb = go.GetComponent(_storageBuildingType);
                    if (sb != null)
                    {
                        var item = CreateItem(dhName);
                        if (item == null) return true; // unknown name — don't grey on our own ignorance
                        try { return _isItemAllowed.Invoke(sb, new object[] { item }) is true; }
                        catch { return true; }
                    }
                }

                // Tier 2 — production building: only the items it consumes.
                var consumed = GetConsumedItemNames(go);
                if (consumed != null)
                    return consumed.Contains("Item" + dhName) || consumed.Contains(dhName);

                // Tier 3 — neither: deny (the user's fallback; never fake-allow).
                if (!_loggedNoFilter && Config.DebugLog.Value)
                {
                    _loggedNoFilter = true;
                    MelonLogger.Msg("[DivineHands] Selected building is neither storage nor a producer — " +
                                    "injection denied (inject at a storage building instead).");
                }
                return false;
            }
            catch
            {
                return false; // non-storage error -> safe side (deny)
            }
        }

        // Cache the full set of eligible picker-item names per selected building. The per-item check
        // (CreateItem + IsItemAllowed.Invoke for storage) is computed ONCE when the building changes —
        // NOT per item per frame — so the panel's grey-out loop is just a HashSet lookup.
        private static GameObject? _eligSetGo;
        private static HashSet<string>? _eligibleNames;

        private static void EnsureEligibleSet(GameObject go)
        {
            if (ReferenceEquals(go, _eligSetGo) && _eligibleNames != null) return;
            _eligSetGo = go;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var names = ItemNames;
            for (int i = 0; i < names.Length; i++)
                if (IsItemInjectableByName(go, names[i]))
                    set.Add(names[i]);
            _eligibleNames = set;
        }

        /// <summary>Picker-side eligibility for greying out buttons: storage -> accepted items;
        /// producer -> consumed inputs; neither -> not eligible. Cached per building (cheap per call);
        /// fails OPEN only when no building is selected.</summary>
        public static bool IsItemEligibleForSelectedBuilding(string itemName)
        {
            var go = GetSelectedBuilding();
            if (go == null) return true;
            EnsureEligibleSet(go);
            return _eligibleNames!.Contains(itemName);
        }

        /// <summary>True whenever a building is selected — the picker greys items by eligibility
        /// (storage allow-list, producer inputs, or deny-all). Named for the panel's existing call.</summary>
        public static bool SelectedBuildingHasAllowList()
        {
            return GetSelectedBuilding() != null;
        }

        // =====================================================================
        // 2) ADD LIVESTOCK
        // =====================================================================

        /// <summary>Add one animal of <paramref name="kind"/> into the selected livestock building,
        /// if the building type matches the animal. Returns a status string.</summary>
        public static string AddLivestock(LivestockKind kind)
        {
            var go = GetSelectedBuilding();
            if (go == null) return "No building selected";

            ResolveReflection();

            // Map kind -> (building component type name, prefab GUID, animal component type name).
            string buildingTypeName, animalTypeName, guid;
            switch (kind)
            {
                case LivestockKind.Cow:
                    buildingTypeName = "Barn"; animalTypeName = "Cow"; guid = Config.LivestockGuidCow.Value; break;
                case LivestockKind.Chicken:
                    buildingTypeName = "ChickenCoop"; animalTypeName = "Chicken"; guid = Config.LivestockGuidChicken.Value; break;
                case LivestockKind.Goat:
                    buildingTypeName = "GoatBarn"; animalTypeName = "Goat"; guid = Config.LivestockGuidGoat.Value; break;
                case LivestockKind.Horse:
                    buildingTypeName = "Stable"; animalTypeName = "Horse"; guid = Config.LivestockGuidHorse.Value; break;
                default:
                    return "Unknown livestock";
            }

            // The selected building must be the matching livestock building type.
            var buildingType = FindType(buildingTypeName);
            var building = buildingType != null ? go.GetComponent(buildingType) : null;
            if (building == null)
                return $"Select a {buildingTypeName} for {kind}";

            try
            {
                var prefab = SafeGetPrefab(guid);
                if (prefab == null)
                    return $"{kind} prefab missing (DLC?)";

                var animalGo = UnityEngine.Object.Instantiate(prefab);
                if (animalGo == null) return "Instantiate failed";

                // Match AddItemMono: place the new animal at the building's local position.
                animalGo.transform.localPosition = ((Component)building).transform.localPosition;

                // Get the LivestockAnimal-derived component (Cow/Chicken/Goat/Horse) off the prefab.
                var animalType = FindType(animalTypeName);
                var animalComp = animalType != null ? animalGo.GetComponent(animalType) : null;
                if (animalComp == null)
                {
                    UnityEngine.Object.Destroy(animalGo);
                    return $"{kind} prefab has no {animalTypeName}";
                }

                // ((LivestockBuilding)building).herd.AddAnimalToHerd((LivestockAnimal)animal, false)
                var herd = GetMember(building, building.GetType(), "herd"); // LivestockBuilding.herd
                if (herd == null)
                {
                    UnityEngine.Object.Destroy(animalGo);
                    return "Building has no herd";
                }

                if (_addAnimalToHerd == null)
                    _addAnimalToHerd = ResolveAddAnimalToHerd(herd.GetType());
                if (_addAnimalToHerd == null)
                {
                    UnityEngine.Object.Destroy(animalGo);
                    return "AddAnimalToHerd unresolved";
                }

                _addAnimalToHerd.Invoke(herd, new object[] { animalComp, false });

                if (Config.DebugLog.Value)
                    MelonLogger.Msg($"[DivineHands] Added 1x {kind} to {GetSelectedBuildingName()}");
                return $"+1 {kind}";
            }
            catch (Exception ex)
            {
                if (Config.DebugLog.Value)
                    MelonLogger.Warning($"[DivineHands] AddLivestock({kind}) failed: {ex.Message}");
                return "Livestock add failed (see log)";
            }
        }

        // =====================================================================
        // 3) INFINITE STORAGE  (SESSION-ONLY — never serialized; see class doc)
        // =====================================================================

        /// <summary>Is the selected building's storage currently flagged infinite BY DivineHands this
        /// session? (We only report our own flag so the toggle reflects DH state, not a baked save.)</summary>
        public static bool IsSelectedInfinite()
        {
            var go = GetSelectedBuilding();
            if (go == null) return false;
            var storage = go.GetComponent<ReservableItemStorage>();
            return storage != null && _sessionInfinite.Contains(storage);
        }

        /// <summary>Flip session-only infinite storage on the selected building. Returns a status
        /// string. The flag is force-cleared before any save (OnSceneExit / master-disable), so it
        /// NEVER persists to the .sav.</summary>
        public static string ToggleSelectedInfinite()
        {
            var go = GetSelectedBuilding();
            if (go == null) return "No building selected";

            var storage = go.GetComponent<ReservableItemStorage>();
            if (storage == null) return "Building has no storage";

            try
            {
                bool nowInfinite = !_sessionInfinite.Contains(storage);
                storage.infiniteItems = nowInfinite;             // public setter [162987]
                if (nowInfinite) _sessionInfinite.Add(storage);
                else _sessionInfinite.Remove(storage);

                if (Config.DebugLog.Value)
                    MelonLogger.Msg($"[DivineHands] Infinite storage {(nowInfinite ? "ON" : "OFF")} " +
                                    $"(session-only) on {GetSelectedBuildingName()}");
                return nowInfinite ? "Infinite ON (session-only)" : "Infinite OFF";
            }
            catch (Exception ex)
            {
                if (Config.DebugLog.Value)
                    MelonLogger.Warning($"[DivineHands] ToggleSelectedInfinite failed: {ex.Message}");
                return "Toggle failed (see log)";
            }
        }

        private static void ClearAllSessionInfinite()
        {
            if (_sessionInfinite.Count == 0) return;
            int cleared = 0;
            foreach (var storage in _sessionInfinite.ToArray())
            {
                try
                {
                    // The component may have been destroyed with the scene; guard the Unity null.
                    if (storage == null) continue;
                    SetMember(storage, "infiniteItems", false);
                    cleared++;
                }
                catch { /* best-effort; a destroyed storage can't serialize anyway */ }
            }
            _sessionInfinite.Clear();
            if (cleared > 0 && Config.DebugLog.Value)
                MelonLogger.Msg($"[DivineHands] Reverted {cleared} session-infinite storage(s)");
        }

        // ---- save hook: strip-before-write, restore-after ----------------------------------------

        /// <summary>Subscribe to StartSaveGameEvent so EVERY save path (manual, autosave, exit) strips
        /// our infinite flags before serialization. Idempotent.</summary>
        private static void BindSaveHook()
        {
            if (_saveHookBound) return;
            try
            {
                var em = GameManager.Instance?.eventManager;
                if (em == null) return; // not in a live game yet; OnMapLoaded re-attempts each load

                // Strongly typed: AddListener<StartSaveGameEvent>(EventDelegate<StartSaveGameEvent>).
                EventManager.EventDelegate<StartSaveGameEvent> handler = OnStartSaveGame;
                em.AddListener(handler);
                _saveListener = handler;
                _saveHookBound = true;
                if (Config.DebugLog.Value)
                    MelonLogger.Msg("[DivineHands] Save hook bound (infinite-storage save guard active)");
            }
            catch (Exception ex)
            {
                if (Config.DebugLog.Value)
                    MelonLogger.Warning($"[DivineHands] BindSaveHook failed: {ex.Message}");
            }
        }

        private static void UnbindSaveHook()
        {
            if (!_saveHookBound) return;
            try
            {
                var em = GameManager.Instance?.eventManager;
                if (em != null && _saveListener is EventManager.EventDelegate<StartSaveGameEvent> h)
                    em.RemoveListener(h);
            }
            catch { /* singleton may be gone on shutdown; nothing to clean then */ }
            _saveListener = null;
            _saveHookBound = false;
        }

        /// <summary>Runs SYNCHRONOUSLY when a save begins [SaveManager.Save raises it at 182289], before
        /// SaveRoutine spins up and writes [182319-182326]. We clear our infinite flags now so the .sav
        /// is clean, then schedule a re-apply a safe number of frames later so the live session keeps
        /// infinite storage.</summary>
        private static void OnStartSaveGame(StartSaveGameEvent _)
        {
            if (_sessionInfinite.Count == 0) return;
            try
            {
                _reapplyAfterSave.Clear();
                foreach (var storage in _sessionInfinite)
                {
                    if (storage == null) continue;
                    _reapplyAfterSave.Add(storage);
                    SetMember(storage, "infiniteItems", false); // strip before write
                }
                // NOTE: we intentionally do NOT empty _sessionInfinite — those storages are still
                // "session-infinite" from the player's POV; OnUpdate re-applies true after the write.
                _reapplyCountdown = ReapplyDelayFrames;
                if (Config.DebugLog.Value)
                    MelonLogger.Msg($"[DivineHands] Save starting — temporarily cleared " +
                                    $"{_reapplyAfterSave.Count} infinite flag(s) for clean serialization");
            }
            catch (Exception ex)
            {
                if (Config.DebugLog.Value)
                    MelonLogger.Warning($"[DivineHands] OnStartSaveGame guard failed: {ex.Message}");
            }
        }

        // =====================================================================
        // Reflection helpers
        // =====================================================================

        private static void ResolveReflection()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                // Cache the assembly that defines the game's Item* / livestock types.
                var probe = FindType("ItemLogs");
                _gameAssembly = probe?.Assembly;
            }
            catch (Exception ex)
            {
                if (Config.DebugLog.Value)
                    MelonLogger.Warning($"[DivineHands] ItemInjection reflection resolve failed: {ex.Message}");
            }
        }

        // Herd.AddAnimalToHerd(LivestockAnimal animalToAdd, bool gracePeriod=false) [39810].
        private static MethodInfo? ResolveAddAnimalToHerd(Type herdType)
        {
            const BindingFlags F = BindingFlags.Public | BindingFlags.Instance;
            foreach (var m in herdType.GetMethods(F))
            {
                if (m.Name != "AddAnimalToHerd") continue;
                var p = m.GetParameters();
                if (p.Length == 2 && p[1].ParameterType == typeof(bool))
                    return m; // first param is LivestockAnimal; we pass the derived component, which fits
            }
            // Single-arg overloads exist in some builds; accept a 1-arg fallback (gracePeriod default).
            foreach (var m in herdType.GetMethods(F))
                if (m.Name == "AddAnimalToHerd" && m.GetParameters().Length == 1)
                    return m;
            return null;
        }

        private static GameObject? SafeGetPrefab(string guid)
        {
            if (string.IsNullOrWhiteSpace(guid)) return null;
            try
            {
                var map = GlobalAssets.prefabAssetMap; // public static [96552]
                if (map == null) return null;
                return map.GetPrefab(guid.Trim());     // returns null on miss, logs, no throw [48987]
            }
            catch (Exception ex)
            {
                if (Config.DebugLog.Value)
                    MelonLogger.Warning($"[DivineHands] GetPrefab('{guid}') threw: {ex.Message}");
                return null;
            }
        }

        // ---- reflective member get/set (walk base types; field OR property) ----

        private const BindingFlags MemberFlags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private static object? GetMember(object target, Type startType, string name)
        {
            for (var t = startType; t != null; t = t.BaseType)
            {
                var f = t.GetField(name, MemberFlags);
                if (f != null) return f.GetValue(target);
                var p = t.GetProperty(name, MemberFlags);
                if (p != null && p.CanRead) return p.GetValue(target);
            }
            return null;
        }

        private static void SetMember(object target, string name, object value)
        {
            for (var t = target.GetType(); t != null; t = t.BaseType)
            {
                var f = t.GetField(name, MemberFlags);
                if (f != null) { f.SetValue(target, value); return; }
                var p = t.GetProperty(name, MemberFlags);
                if (p != null && p.CanWrite) { p.SetValue(target, value); return; }
            }
        }

        private static Type? FindType(string simpleName)
        {
            var t = Type.GetType(simpleName + ", Assembly-CSharp");
            if (t != null) return t;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!asm.GetName().Name.Contains("Assembly-CSharp")) continue;
                t = asm.GetType(simpleName) ?? asm.GetType("Il2CPP." + simpleName);
                if (t != null) return t;
            }
            return null;
        }
    }
}
