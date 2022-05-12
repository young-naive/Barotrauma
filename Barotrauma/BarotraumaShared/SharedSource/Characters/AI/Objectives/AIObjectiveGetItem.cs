﻿using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Immutable;
using System.Collections.Generic;
using System.Linq;

namespace Barotrauma
{
    class AIObjectiveGetItem : AIObjective
    {
        public override Identifier Identifier { get; set; } = "get item".ToIdentifier();

        public override bool AbandonWhenCannotCompleteSubjectives => false;
        public override bool AllowMultipleInstances => true;

        public HashSet<Item> ignoredItems = new HashSet<Item>();

        public Func<Item, float> GetItemPriority;
        public Func<Item, bool> ItemFilter;
        public float TargetCondition { get; set; } = 1;
        public bool AllowDangerousPressure { get; set; }

        public readonly ImmutableArray<Identifier> IdentifiersOrTags;

        //if the item can't be found, spawn it in the character's inventory (used by outpost NPCs)
        private bool spawnItemIfNotFound = false;

        private Item targetItem;
        private readonly Item originalTarget;
        private ISpatialEntity moveToTarget;
        private bool isDoneSeeking;
        public Item TargetItem => targetItem;
        private int currSearchIndex;
        public Identifier[] ignoredContainerIdentifiers;
        public Identifier[] ignoredIdentifiersOrTags;
        private AIObjectiveGoTo goToObjective;
        private float currItemPriority;
        private readonly bool checkInventory;

        public static float DefaultReach = 100;

        public bool AllowToFindDivingGear { get; set; } = true;
        public bool MustBeSpecificItem { get; set; }

        /// <summary>
        /// Is the character allowed to take the item from somewhere else than their own sub (e.g. an outpost)
        /// </summary>
        public bool AllowStealing { get; set; }
        public bool TakeWholeStack { get; set; }
        /// <summary>
        /// Are variants of the specified item allowed
        /// </summary>
        public bool AllowVariants { get; set; }
        public bool Equip { get; set; }
        public bool Wear { get; set; }
        public bool RequireLoaded { get; set; }
        public bool EvaluateCombatPriority { get; set; }
        public bool CheckPathForEachItem { get; set; }
        public bool SpeakIfFails { get; set; }
        public string CannotFindDialogueIdentifierOverride { get; set; }
        public Func<bool> CannotFindDialogueCondition { get; set; }

        private int _itemCount = 1;
        public int ItemCount
        {
            get { return _itemCount; }
            set
            {
                _itemCount = Math.Max(value, 1);
            }
        }

        public InvSlotType? EquipSlotType { get; set; }

        public AIObjectiveGetItem(Character character, Item targetItem, AIObjectiveManager objectiveManager, bool equip = true, float priorityModifier = 1) 
            : base(character, objectiveManager, priorityModifier)
        {
            currSearchIndex = -1;
            Equip = equip;
            originalTarget = targetItem;
            this.targetItem = targetItem;
            moveToTarget = targetItem?.GetRootInventoryOwner();
        }

        public AIObjectiveGetItem(Character character, Identifier identifierOrTag, AIObjectiveManager objectiveManager, bool equip = true, bool checkInventory = true, float priorityModifier = 1, bool spawnItemIfNotFound = false) 
            : this(character, new Identifier[] { identifierOrTag }, objectiveManager, equip, checkInventory, priorityModifier, spawnItemIfNotFound) { }

        public AIObjectiveGetItem(Character character, IEnumerable<Identifier> identifiersOrTags, AIObjectiveManager objectiveManager, bool equip = true, bool checkInventory = true, float priorityModifier = 1, bool spawnItemIfNotFound = false) 
            : base(character, objectiveManager, priorityModifier)
        {
            currSearchIndex = -1;
            Equip = equip;
            this.spawnItemIfNotFound = spawnItemIfNotFound;
            this.checkInventory = checkInventory;
            IdentifiersOrTags = ParseGearTags(identifiersOrTags).ToImmutableArray();
            ignoredIdentifiersOrTags = ParseIgnoredTags(identifiersOrTags).ToArray();
        }

        public static IEnumerable<Identifier> ParseGearTags(IEnumerable<Identifier> identifiersOrTags)
        {
            var tags = new List<Identifier>();
            foreach (Identifier tag in identifiersOrTags)
            {
                if (!tag.Contains("!"))
                {
                    tags.Add(tag);
                }
            }
            return tags;
        }

        public static IEnumerable<Identifier> ParseIgnoredTags(IEnumerable<Identifier> identifiersOrTags)
        {
            var ignoredTags = new List<Identifier>();
            foreach (Identifier tag in identifiersOrTags)
            {
                if (tag.Contains("!"))
                {
                    ignoredTags.Add(tag.Remove("!"));
                }
            }
            return ignoredTags;
        }

        private bool CheckInventory()
        {
            if (IdentifiersOrTags == null) { return false; }
            var item = character.Inventory.FindItem(i => CheckItem(i), recursive: true);
            if (item != null)
            {
                targetItem = item;
                moveToTarget = item.GetRootInventoryOwner();
            }
            return item != null;
        }

        private bool CountItems()
        {
            int itemCount = 0;
            foreach (Item it in character.Inventory.AllItems)
            {
                if (CheckItem(it))
                {
                    itemCount++;
                }
            }
            return itemCount >= ItemCount;
        }

        protected override void Act(float deltaTime)
        {
            if (character.LockHands)
            {
                Abandon = true;
                return;
            }
            if (character.Submarine == null)
            {
                Abandon = true;
                return;
            }
            if (IdentifiersOrTags != null && !isDoneSeeking)
            {
                if (checkInventory)
                {
                    if (CheckInventory())
                    {
                        isDoneSeeking = true;
                    }
                }
                if (!isDoneSeeking)
                {
                    if (!AllowDangerousPressure)
                    {
                        bool dangerousPressure = character.CurrentHull == null || character.CurrentHull.LethalPressure > 0 && character.PressureProtection <= 0;
                        if (dangerousPressure)
                        {
#if DEBUG
                            string itemName = targetItem != null ? targetItem.Name : IdentifiersOrTags.FirstOrDefault().Value;
                            DebugConsole.NewMessage($"{character.Name}: Seeking item ({itemName}) aborted, because the pressure is dangerous.", Color.Yellow);
#endif
                            Abandon = true;
                            return;
                        }
                    }
                    FindTargetItem();
                    if (!objectiveManager.IsCurrentOrder<AIObjectiveGoTo>())
                    {
                        objectiveManager.GetObjective<AIObjectiveIdle>().Wander(deltaTime);
                    }
                    return;
                }
            }
            if (targetItem == null || targetItem.Removed)
            {
#if DEBUG
                DebugConsole.NewMessage($"{character.Name}: Target null or removed. Aborting.", Color.Red);
#endif
                Abandon = true;
                return;
            }
            else if (isDoneSeeking && moveToTarget == null)
            {
#if DEBUG
                DebugConsole.NewMessage($"{character.Name}: Move target null. Aborting.", Color.Red);
#endif
                Abandon = true;
                return;
            }
            if (character.IsItemTakenBySomeoneElse(targetItem))
            {
#if DEBUG
                DebugConsole.NewMessage($"{character.Name}: Found an item, but it's already equipped by someone else.", Color.Yellow);
#endif
                if (originalTarget == null)
                {
                    // Try again
                    ignoredItems.Add(targetItem);
                    ResetInternal();
                }
                else
                {
                    Abandon = true;
                }
                return;
            }
            bool canInteract = false;
            if (moveToTarget is Character c)
            {
                if (character == c)
                {
                    canInteract = true;
                    moveToTarget = null;
                }
                else
                {
                    character.SelectCharacter(c);
                    canInteract = character.CanInteractWith(c, maxDist: DefaultReach);
                    character.DeselectCharacter();
                }
            }
            else if (moveToTarget is Item parentItem)
            {
                canInteract = character.CanInteractWith(parentItem, checkLinked: false);
            }
            if (canInteract)
            {
                var pickable = targetItem.GetComponent<Pickable>();
                if (pickable == null)
                {
#if DEBUG
                    DebugConsole.NewMessage($"{character.Name}: Target not pickable. Aborting.", Color.Yellow);
#endif
                    Abandon = true;
                    return;
                }

                Inventory itemInventory = targetItem.ParentInventory;
                var slots = itemInventory?.FindIndices(targetItem);
                if (HumanAIController.TakeItem(targetItem, character.Inventory, Equip, Wear, storeUnequipped: true))
                {
                    if (TakeWholeStack && slots != null)
                    {
                        foreach (int slot in slots)
                        {
                            foreach (Item item in itemInventory.GetItemsAt(slot).ToList())
                            {
                                HumanAIController.TakeItem(item, character.Inventory, equip: false, storeUnequipped: true);
                            }
                        }
                    }
                    if (IdentifiersOrTags == null)
                    {
                        IsCompleted = true;
                    }
                    else
                    {
                        IsCompleted = CountItems();
                        if (!IsCompleted)
                        {
                            ResetInternal();
                        }
                    }
                }
                else
                {
                    if (!Equip)
                    {
                        // Try equipping and wearing the item
                        Wear = true;
                        Equip = true;
                        return;
                    }
#if DEBUG
                    DebugConsole.NewMessage($"{character.Name}: Failed to equip/move the item '{targetItem.Name}' into the character inventory. Aborting.", Color.Red);
#endif
                    Abandon = true;
                }
            }
            else if (moveToTarget != null)
            {
                TryAddSubObjective(ref goToObjective,
                    constructor: () =>
                    {
                        return new AIObjectiveGoTo(moveToTarget, character, objectiveManager, repeat: false, getDivingGearIfNeeded: AllowToFindDivingGear, closeEnough: DefaultReach)
                        {
                            // If the root container changes, the item is no longer where it was (taken by someone -> need to find another item)
                            AbortCondition = obj => targetItem == null || targetItem.GetRootInventoryOwner() != moveToTarget,
                            SpeakIfFails = false
                        };
                    },
                    onAbandon: () =>
                    {
                        if (originalTarget == null)
                        {
                            // Try again
                            ignoredItems.Add(targetItem);
                            if (targetItem != moveToTarget && moveToTarget is Item item)
                            {
                                ignoredItems.Add(item);
                            }
                            ResetInternal();
                        }
                        else
                        {
                            Abandon = true;
                        }
                    },
                    onCompleted: () => RemoveSubObjective(ref goToObjective));
            }
        }

        private void FindTargetItem()
        {
            if (IdentifiersOrTags == null)
            {
                if (targetItem == null)
                {
#if DEBUG
                    DebugConsole.NewMessage($"{character.Name}: Cannot find an item, because neither identifiers nor item was defined.", Color.Red);
#endif
                    Abandon = true;
                }
                return;
            }

            float priority = Math.Clamp(objectiveManager.GetCurrentPriority(), 10, 100);
            if (!CheckPathForEachItem)
            {
                // While following the player, let's ensure that there's a valid path to the target before accepting it.
                // Otherwise it will take some time for us to find a valid item when there are multiple items that we can't reach and some that we can.
                // This is relatively expensive, so let's do this only when it significantly improves the behavior.
                // Only allow one path find call per frame.
                CheckPathForEachItem = priority >= AIObjectiveManager.LowestOrderPriority && (objectiveManager.IsCurrentOrder<AIObjectiveFixLeaks>() || objectiveManager.CurrentOrder is AIObjectiveGoTo gotoOrder && gotoOrder.IsFollowOrderObjective);
            }
            bool checkPath = CheckPathForEachItem;
            bool hasCalledPathFinder = false;
            int itemsPerFrame = (int)priority;
            for (int i = 0; i < itemsPerFrame && currSearchIndex < Item.ItemList.Count - 1; i++)
            {
                currSearchIndex++;
                var item = Item.ItemList[currSearchIndex];
                Submarine itemSub = item.Submarine ?? item.ParentInventory?.Owner?.Submarine;
                if (itemSub == null) { continue; }
                Submarine mySub = character.Submarine;
                if (mySub == null) { continue; }
                if (!checkInventory)
                {
                    // Ignore items in the inventory when defined not to check it.
                    if (item.IsOwnedBy(character)) { continue; }
                }
                if (!AllowStealing)
                {
                    if (character.TeamID == CharacterTeamType.FriendlyNPC != item.SpawnedInCurrentOutpost) { continue; }
                }
                if (!CheckItem(item)) { continue; }
                if (item.Container != null)
                {
                    if (item.Container.HasTag("donttakeitems")) { continue; }
                    if (ignoredItems.Contains(item.Container)) { continue; }
                    if (ignoredContainerIdentifiers != null)
                    {
                        if (ignoredContainerIdentifiers.Contains(item.ContainerIdentifier)) { continue; }
                    }
                }
                // Don't allow going into another sub, unless it's connected and of the same team and type.
                if (!character.Submarine.IsEntityFoundOnThisSub(item, includingConnectedSubs: true)) { continue; }
                if (character.IsItemTakenBySomeoneElse(item)) { continue; }
                if (item.ParentInventory is ItemInventory itemInventory)
                {
                    if (!itemInventory.Container.HasRequiredItems(character, addMessage: false)) { continue; }
                }
                float itemPriority = 1;
                if (GetItemPriority != null)
                {
                    itemPriority = GetItemPriority(item);
                }
                Entity rootInventoryOwner = item.GetRootInventoryOwner();
                if (rootInventoryOwner is Item ownerItem)
                {
                    if (!ownerItem.IsInteractable(character)) { continue; }
                    if (!(ownerItem.GetComponent<ItemContainer>()?.HasRequiredItems(character, addMessage: false) ?? true)) { continue; }
                    //the item is inside an item inside an item (e.g. fuel tank in a welding tool in a cabinet -> reduce priority to prefer items that aren't inside a tool)
                    if (ownerItem != item.Container)
                    {
                        itemPriority *= 0.1f;
                    }
                }
                Vector2 itemPos = (rootInventoryOwner ?? item).WorldPosition;
                float yDist = Math.Abs(character.WorldPosition.Y - itemPos.Y);
                yDist = yDist > 100 ? yDist * 5 : 0;
                float dist = Math.Abs(character.WorldPosition.X - itemPos.X) + yDist;
                float minDistFactor = EvaluateCombatPriority ? 0.1f : 0;
                float distanceFactor = MathHelper.Lerp(1, minDistFactor, MathUtils.InverseLerp(100, 10000, dist));
                itemPriority *= distanceFactor;
                if (EvaluateCombatPriority)
                {
                    var mw = item.GetComponent<MeleeWeapon>();
                    var rw = item.GetComponent<RangedWeapon>();
                    float combatFactor = 0;
                    if (mw != null)
                    {
                        if (mw.CombatPriority > 0)
                        {
                            combatFactor = mw.CombatPriority / 100;
                        }
                        else
                        {
                            // The combat factor of items with zero combat priority is not allowed to be greater than 0.1f
                            combatFactor = Math.Min(AIObjectiveCombat.GetLethalDamage(mw) / 1000, 0.1f);
                        }
                    }
                    else if (rw != null)
                    {
                        if (rw.CombatPriority > 0)
                        {
                            combatFactor = rw.CombatPriority / 100;
                        }
                        else
                        {
                            combatFactor = Math.Min(AIObjectiveCombat.GetLethalDamage(rw) / 1000, 0.1f);
                        }
                    }
                    else
                    {
                        combatFactor = Math.Min(item.Components.Sum(ic => AIObjectiveCombat.GetLethalDamage(ic)) / 1000, 0.1f);
                    }
                    itemPriority *= combatFactor;
                }
                else
                {
                    itemPriority *= item.Condition / item.MaxCondition;
                }
                // Ignore if the item has a lower priority than the currently selected one
                if (itemPriority < currItemPriority) { continue; }
                if (!hasCalledPathFinder && PathSteering != null && checkPath)
                {
                    hasCalledPathFinder = true;
                    var path = PathSteering.PathFinder.FindPath(character.SimPosition, item.SimPosition, character.Submarine, errorMsgStr: $"AIObjectiveGetItem {character.DisplayName}", nodeFilter: node => node.Waypoint.CurrentHull != null);
                    if (path.Unreachable) { continue; }
                }
                currItemPriority = itemPriority;
                targetItem = item;
                moveToTarget = rootInventoryOwner ?? item;
            }
            if (currSearchIndex >= Item.ItemList.Count - 1)
            {
                isDoneSeeking = true;
                if (targetItem == null)
                {
                    if (spawnItemIfNotFound)
                    {
                        ItemPrefab prefab = FindItemToSpawn();
                        if (prefab == null)
                        {
#if DEBUG
                            DebugConsole.NewMessage($"{character.Name}: Cannot find an item with the following identifier(s) or tag(s): {string.Join(", ", IdentifiersOrTags)}, tried to spawn the item but no matching item prefabs were found.", Color.Yellow);
#endif
                            Abandon = true;
                        }
                        else
                        {
                            Entity.Spawner.AddItemToSpawnQueue(prefab, character.Inventory, onSpawned: (Item spawnedItem) => 
                            {
                                targetItem = spawnedItem; 
                                if (character.TeamID == CharacterTeamType.FriendlyNPC && (character.Submarine?.Info.IsOutpost ?? false))
                                {
                                    spawnedItem.SpawnedInCurrentOutpost = true;
                                }
                            });
                        }
                    }
                    else
                    {
#if DEBUG
                        DebugConsole.NewMessage($"{character.Name}: Cannot find an item with the following identifier(s) or tag(s): {string.Join(", ", IdentifiersOrTags)}", Color.Yellow);
#endif
                        Abandon = true;
                    }
                }
            }
        }

        /// <summary>
        /// Returns the "best" item to spawn when using <see cref="spawnItemIfNotFound"/> and there's multiple suitable items.
        /// Best in this context is the one that's sold at the lowest price in stores (usually the most "basic" item)
        /// </summary>
        /// <returns></returns>
        private ItemPrefab FindItemToSpawn()
        {
            ItemPrefab bestItem = null;
            float lowestCost = float.MaxValue;
            foreach (MapEntityPrefab prefab in MapEntityPrefab.List)
            {
                if (!(prefab is ItemPrefab itemPrefab)) { continue; }
                if (IdentifiersOrTags.Any(id => id == prefab.Identifier || prefab.Tags.Contains(id)))
                {
                    float cost = itemPrefab.DefaultPrice != null && itemPrefab.CanBeBought ?
                        itemPrefab.DefaultPrice.Price :
                        float.MaxValue;
                    if (cost < lowestCost || bestItem == null)
                    {
                        bestItem = itemPrefab;
                        lowestCost = cost;
                    }
                }
            }
            return bestItem;
        }

        protected override bool CheckObjectiveSpecific()
        {
            if (IsCompleted) { return true; }
            if (targetItem == null)
            {
                // Not yet ready
                return false;
            }
            if (IdentifiersOrTags != null && ItemCount > 1)
            {
                return CountItems();
            }
            else
            {
                if (Equip && EquipSlotType.HasValue)
                {
                    return character.HasEquippedItem(targetItem, EquipSlotType.Value);
                }
                else
                {
                    return character.HasItem(targetItem, Equip);
                }
            }
        }

        private bool CheckItem(Item item)
        {
            if (!item.HasAccess(character)) { return false; }
            if (ignoredItems.Contains(item)) { return false; };
            if (ignoredIdentifiersOrTags != null && ignoredIdentifiersOrTags.Any(id => item.Prefab.Identifier == id || item.HasTag(id))) { return false; }
            if (item.Condition < TargetCondition) { return false; }
            if (ItemFilter != null && !ItemFilter(item)) { return false; }
            if (RequireLoaded && item.Components.Any(i => !i.IsLoaded(character))) { return false; }
            return IdentifiersOrTags.Any(id => id == item.Prefab.Identifier || item.HasTag(id) || (AllowVariants && !item.Prefab.VariantOf.IsEmpty && item.Prefab.VariantOf == id));
        }

        public override void Reset()
        {
            base.Reset();
            ResetInternal();
        }

        /// <summary>
        /// Does not reset the ignored items list
        /// </summary>
        private void ResetInternal()
        {
            RemoveSubObjective(ref goToObjective);
            targetItem = originalTarget;
            moveToTarget = targetItem?.GetRootInventoryOwner();
            isDoneSeeking = false;
            currSearchIndex = 0;
            currItemPriority = 0;
        }

        protected override void OnAbandon()
        {
            base.OnAbandon();
            if (moveToTarget != null)
            {
#if DEBUG
                DebugConsole.NewMessage($"{character.Name}: Get item failed to reach {moveToTarget}", Color.Yellow);
#endif
            }
            SpeakCannotFind();
        }

        private void SpeakCannotFind()
        {
            if (!SpeakIfFails) { return; }
            if (!character.IsOnPlayerTeam) { return; }
            if (objectiveManager.CurrentOrder != objectiveManager.CurrentObjective) { return; }
            if (CannotFindDialogueCondition != null && !CannotFindDialogueCondition()) { return; }
            LocalizedString msg = TextManager.Get(CannotFindDialogueIdentifierOverride, "dialogcannotfinditem");
            if (msg.IsNullOrEmpty() || !msg.Loaded) { return; }
            character.Speak(msg.Value, identifier: "dialogcannotfinditem".ToIdentifier(), minDurationBetweenSimilar: 20.0f);
        }
    }
}
