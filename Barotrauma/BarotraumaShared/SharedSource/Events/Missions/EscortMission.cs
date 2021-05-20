﻿using Barotrauma.Items.Components;
using Barotrauma.Extensions;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace Barotrauma
{
    partial class EscortMission : Mission
    {
        private readonly XElement characterConfig;
        private readonly XElement itemConfig;

        private readonly List<Character> characters = new List<Character>();
        private readonly Dictionary<Character, List<Item>> characterDictionary = new Dictionary<Character, List<Item>>();

        private readonly int baseEscortedCharacters;
        private readonly float scalingEscortedCharacters;
        private readonly float terroristChance;

        private Character vipCharacter;

        private readonly List<Character> terroristCharacters = new List<Character>();
        private bool terroristsShouldAct = false;
        private float terroristDistanceSquared;
        private const string TerroristTeamChangeIdentifier = "terrorist"; 

        public EscortMission(MissionPrefab prefab, Location[] locations, Submarine sub)
            : base(prefab, locations, sub)
        {
            characterConfig = prefab.ConfigElement.Element("Characters");
            // Should reflect different escortables, prisoners, VIPs, passengers (where does this comment refer to?)

            baseEscortedCharacters = prefab.ConfigElement.GetAttributeInt("baseescortedcharacters", 1);
            scalingEscortedCharacters = prefab.ConfigElement.GetAttributeFloat("scalingescortedcharacters", 0);
            terroristChance = prefab.ConfigElement.GetAttributeFloat("terroristchance", 0);
            itemConfig = prefab.ConfigElement.Element("TerroristItems");
        }

        public override int GetReward(Submarine sub)
        {
            int multiplier = CalculateScalingEscortedCharacterCount();
            return Prefab.Reward * multiplier;
        }

        int CalculateScalingEscortedCharacterCount(bool inMission = false)
        {
            if (Submarine.MainSub == null || Submarine.MainSub.Info == null) // UI logic failing to get the correct value is not important, but the mission logic must succeed
            {
                if (inMission)
                {
                    DebugConsole.ThrowError("MainSub was null when trying to retrieve submarine size for determining escorted character count!");
                }
                return 1;
            }
            return (int)Math.Round(baseEscortedCharacters + scalingEscortedCharacters * (Submarine.MainSub.Info.RecommendedCrewSizeMin + Submarine.MainSub.Info.RecommendedCrewSizeMax) / 2);
        }

        private void InitEscort()
        {
            characters.Clear();
            characterDictionary.Clear();
            // VIP transport mission characters stay in the same location; other characters roam at will
            // could be replaced with a designated waypoint for VIPs, such as cargo or crew
            WayPoint explicitStayInHullPos = WayPoint.GetRandom(SpawnType.Human, null, Submarine.MainSub);
            Rand.RandSync randSync = Rand.RandSync.Server;

            if (terroristChance > 0f)
            {
                // in terrorist missions, reroll characters each retry to avoid confusion as to who the terrorists are
                randSync = Rand.RandSync.Unsynced; 
            }

            foreach (XElement element in characterConfig.Elements())
            {
                int count = CalculateScalingEscortedCharacterCount(inMission: true);
                for (int i = 0; i < count; i++)
                {
                    Character spawnedCharacter = CreateHuman(CreateHumanPrefabFromElement(element), characters, characterDictionary, Submarine.MainSub, CharacterTeamType.FriendlyNPC, explicitStayInHullPos, humanPrefabRandSync: randSync);
                    if (spawnedCharacter.AIController is HumanAIController humanAI)
                    {
                        humanAI.InitMentalStateManager();
                    }
                }
            }

            if (terroristChance > 0f)
            {
                int terroristCount = (int)Math.Ceiling(terroristChance * Rand.Range(0.8f, 1.2f) * characters.Count);
                terroristCount = Math.Clamp(terroristCount, 1, characters.Count);

                terroristCharacters.Clear();
                characters.GetRange(0, terroristCount).ForEach(c => terroristCharacters.Add(c));

                terroristDistanceSquared = Vector2.DistanceSquared(Level.Loaded.StartPosition, Level.Loaded.EndPosition) * Rand.Range(0.35f, 0.65f);

#if DEBUG
                DebugConsole.AddWarning("Terrorists will trigger at range  " + Math.Sqrt(terroristDistanceSquared));
                foreach (Character character in terroristCharacters)
                {
                    DebugConsole.AddWarning(character.Name + " is a terrorist.");
                }
#endif
            }
        }

        private void InitCharacters()
        {
            int scalingCharacterCount = CalculateScalingEscortedCharacterCount(inMission: true);

            if (scalingCharacterCount * characterConfig.Elements().Count() != characters.Count)
            {
                DebugConsole.AddWarning("Character count did not match expected character count in InitCharacters of EscortMission");
                return;
            }
            int i = 0;

            foreach (XElement element in characterConfig.Elements())
            {
                string escortIdentifier = element.GetAttributeString("escortidentifier", string.Empty);
                string colorIdentifier = element.GetAttributeString("color", string.Empty);
                for (int k = 0; k < scalingCharacterCount; k++)
                {
                    // for each element defined, we need to initialize that type of character equal to the scaling escorted character count
                    characters[k + i].IsEscorted = true;
                    if (escortIdentifier != string.Empty)
                    {
                        if (escortIdentifier == "vip")
                        {
                            vipCharacter = characters[k + i];
                        }
                    }
                    characters[k + i].UniqueNameColor = element.GetAttributeColor("color", Color.LightGreen);
                }
                i++;
            }
        }

        protected override void StartMissionSpecific(Level level)
        {
            if (characters.Count > 0)
            {
#if DEBUG
                throw new Exception($"characters.Count > 0 ({characters.Count})");
#else
                DebugConsole.AddWarning("Character list was not empty at the start of a escort mission. The mission instance may not have been ended correctly on previous rounds.");
                characters.Clear();            
#endif
            }

            if (characterConfig == null)
            {
                DebugConsole.ThrowError("Failed to initialize characters for escort mission (characterConfig == null)");
                return;
            }

            if (!IsClient)
            {
                InitEscort();
                InitCharacters();
            }
        }

        void TryToTriggerTerrorists()
        {
            if (terroristsShouldAct)
            {
                // decoupled from range check to prevent from weirdness if players handcuff a terrorist and move backwards
                foreach (Character character in terroristCharacters)
                {
                    if (character.HasTeamChange(TerroristTeamChangeIdentifier))
                    {
                        // already triggered
                        continue;
                    }

                    if (IsAlive(character) && !character.IsIncapacitated && !character.LockHands)
                    {
                        character.TryAddNewTeamChange(TerroristTeamChangeIdentifier, new ActiveTeamChange(CharacterTeamType.None, ActiveTeamChange.TeamChangePriorities.Willful, aggressiveBehavior: true));
                        character.Speak(TextManager.Get("dialogterroristannounce"), null, Rand.Range(0.5f, 3f));
                        XElement randomElement = itemConfig.Elements().GetRandom(e => e.GetAttributeFloat(0f, "mindifficulty") <= Level.Loaded.Difficulty);
                        if (randomElement != null)
                        {
                            HumanPrefab.InitializeItem(character, randomElement, character.Submarine, humanPrefab: null, createNetworkEvents: true);
                        }
                    }
                }
            }
            else if (Vector2.DistanceSquared(Submarine.MainSub.WorldPosition, Level.Loaded.EndPosition) < terroristDistanceSquared)
            {
                foreach (Character character in terroristCharacters)
                {
                    if (character.AIController is HumanAIController humanAI)
                    {
                        humanAI.ObjectiveManager.AddObjective(new AIObjectiveEscapeHandcuffs(character, humanAI.ObjectiveManager, shouldSwitchTeams: false, beginInstantly: true));
                    }
                }
                terroristsShouldAct = true;
            }
        }

        bool NonTerroristsStillAlive(IEnumerable<Character> characterList)
        {
            return characterList.All(c => terroristCharacters.Contains(c) || IsAlive(c));
        }

        protected override void UpdateMissionSpecific(float deltaTime)
        {
            if (!IsClient)
            {
                int newState = State;
                TryToTriggerTerrorists();
                switch (State)
                {
                    case 0: // base
                        if (!NonTerroristsStillAlive(characters))
                        {
                            newState = 1;
                        }
                        if (terroristCharacters.Any() && terroristCharacters.All(c => !IsAlive(c)))
                        {
                            newState = 2;
                        }
                        break;
                    case 1: // failure
                        break;
                    case 2: // terrorists killed
                        if (!NonTerroristsStillAlive(characters))
                        {
                            newState = 1;
                        }
                        break;
                }
                State = newState;
            }
        }

        private bool Survived(Character character)
        {
            return IsAlive(character) && character.CurrentHull != null && character.CurrentHull.Submarine == Submarine.MainSub;
        }

        private bool IsAlive(Character character)
        {
            return character != null && !character.Removed && !character.IsDead;
        }

        private bool IsCaptured(Character character)
        {
            return character.LockHands && character.HasTeamChange(TerroristTeamChangeIdentifier);
        }

        public override void End()
        {
            if (Submarine.MainSub != null && Submarine.MainSub.AtEndExit)
            {
                bool terroristsSurvived = terroristCharacters.Any(c => Survived(c) && !IsCaptured(c));
                bool friendliesSurvived = characters.Except(terroristCharacters).All(c => Survived(c));
                bool vipDied = false;

                // this logic is currently irrelevant, as the mission is failed regardless of who dies 
                if (vipCharacter != null)
                {
                    vipDied = !Survived(vipCharacter);
                }

                if (friendliesSurvived && !terroristsSurvived && !vipDied)
                {
                    GiveReward();
                    completed = true;
                }
            }

            // characters that survived will take their items with them, in case players tried to be crafty and steal them
            // this needs to run here in case players abort the mission by going back home
            // TODO: I think this might feel like a bug.
            foreach (var characterItem in characterDictionary)
            {
                if (Survived(characterItem.Key) || !completed)
                {
                    foreach (Item item in characterItem.Value)
                    {
                        if (!item.Removed)
                        {
                            item.Remove();
                        }
                    }
                }
            }

            characters.Clear();
            characterDictionary.Clear();
            failed = !completed;
        }
    }
}
